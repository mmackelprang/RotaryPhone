using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Bell;

namespace RotaryPhoneController.Core.Diagnostics;

/// <summary>
/// Central SIP diagnostics aggregator that maintains a ring buffer of SIP messages,
/// tracks HT801 registration state, detects INVITE timeouts, and generates diagnostic alerts.
/// </summary>
public class SipDiagnosticService : IHostedService, IDisposable
{
    private readonly ILogger<SipDiagnosticService> _logger;
    private readonly object _lock = new();

    // Ring buffer for SIP message log
    private const int MaxBufferSize = 200;
    private readonly LinkedList<SipMessageEntry> _messageBuffer = new();

    // INVITE tracking: callId → (when it was sent, where it was sent). The target is carried so a
    // failure can name the address the INVITE actually went to — the single most useful fact when
    // the bell does not ring.
    private readonly Dictionary<string, (DateTime SentAt, string? Target)> _pendingInvites = new();
    private static readonly TimeSpan InviteTimeout = TimeSpan.FromSeconds(5);

    // HT801 registration state
    private bool _isRegistered;
    private DateTime? _lastRegisterReceived;
    private int? _registrationExpiresIn;
    private static readonly TimeSpan RegistrationStaleThreshold = TimeSpan.FromHours(2);

    // Call timeline
    private readonly LinkedList<CallTimelineEntry> _timeline = new();
    private const int MaxTimelineSize = 200;

    // Periodic timer
    private Timer? _timer;

    // Events
    public event Action<SipMessageEntry>? OnSipMessageLogged;
    public event Action<string, string[]>? OnDiagnosisGenerated;
    public event Action<Ht801HealthStatus>? OnHt801HealthUpdate;
    public event Action<CallTimelineEntry>? OnCallTimelineEvent;

    /// <summary>
    /// A sent INVITE failed. The ONLY sent INVITEs in this system are bell rings to the HT801
    /// (SIPSorceryAdapter is the only source wired into HandleSipMessage), so this is a bell failure.
    /// Args: (callId, reason, target, detail).
    /// </summary>
    public event Action<string, BellFailureReason, string?, string?>? OnSentInviteFailed;

    /// <summary>A sent INVITE was answered with 180 Ringing or 200 OK — the bell demonstrably rang.</summary>
    public event Action<string>? OnSentInviteSucceeded;

    public SipDiagnosticService(ILogger<SipDiagnosticService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Process an incoming SIP message entry: log it, update state, track INVITEs.
    /// </summary>
    public void HandleSipMessage(SipMessageEntry entry)
    {
        lock (_lock)
        {
            // Add to ring buffer
            _messageBuffer.AddLast(entry);
            while (_messageBuffer.Count > MaxBufferSize)
                _messageBuffer.RemoveFirst();
        }

        // Re-emit for SignalR broadcasting
        OnSipMessageLogged?.Invoke(entry);

        // Update registration state for REGISTER messages
        if (string.Equals(entry.Method, "REGISTER", StringComparison.OrdinalIgnoreCase))
        {
            HandleRegister(entry);
        }

        // Track sent INVITEs.
        // A DiagnosticNote on a sent INVITE means the send itself failed. The immediate path already
        // reported that failure; tracking it here would fire a SECOND, wrongly-reasoned failure when the
        // response that can never arrive times out.
        if (string.Equals(entry.Method, "INVITE", StringComparison.OrdinalIgnoreCase)
            && entry.Direction == SipDirection.Sent
            && entry.CallId is not null
            && entry.DiagnosticNote is null)
        {
            lock (_lock)
            {
                _pendingInvites[entry.CallId] = (entry.Timestamp, entry.ToAddress);
            }

            AddTimelineEvent("INVITE_SENT", $"INVITE sent to {entry.ToAddress}", entry.CallId);
        }

        // Handle responses that resolve pending INVITEs
        if (entry.StatusCode.HasValue && entry.CallId is not null)
        {
            HandleResponseForInvite(entry);
        }
    }

    private void HandleRegister(SipMessageEntry entry)
    {
        _isRegistered = true;
        _lastRegisterReceived = entry.Timestamp;

        var health = GetHt801Health();
        _logger.LogDebug("HT801 registration updated: {Health}", health);
        OnHt801HealthUpdate?.Invoke(health);

        AddTimelineEvent("REGISTER", $"REGISTER from {entry.FromAddress}", null);
    }

    private void HandleResponseForInvite(SipMessageEntry entry)
    {
        int code = entry.StatusCode!.Value;
        string? callId = entry.CallId;

        if (callId is null) return;

        // What to do once the lock is released. The bell events MUST be raised outside the lock —
        // their subscribers take the BellFailureTracker's lock and broadcast over SignalR, and
        // holding both locks in this order would be a lock-ordering hazard.
        //
        // A 180/200 to an INVITE is proof the bell rang, so success is raised UNCONDITIONALLY —
        // deliberately NOT gated on the _pendingInvites lookup. InviteTimeout is 5s, so a 180
        // arriving at 5.5s finds the entry already evicted by CheckInviteTimeouts; gating success on
        // the lookup would leave the (false) failure alert stuck until some later call happened to
        // answer inside 5s. BellFailureTracker.RecordSuccess is a no-op when nothing is stored, so
        // raising for an untracked call-id is safe.
        //
        // The INVITE-method qualifier is load-bearing: responses are logged with their CSeq method,
        // and a CANCEL or BYE for the same dialog carries the INVITE's Call-ID. Without it, the 200
        // acknowledging a CANCEL — sent precisely BECAUSE the phone never rang — would clear the
        // alert. The _pendingInvites lookup used to provide this filtering implicitly.
        bool succeeded = (code == 180 || code == 200)
            && string.Equals(entry.Method, "INVITE", StringComparison.OrdinalIgnoreCase);
        bool failed = false;
        BellFailureReason reason = BellFailureReason.Unknown;
        string? target = null;
        string? detail = null;
        string? diagnosisIssue = null;
        string[]? diagnosisSuggestions = null;
        string? timelineEventType = null;
        string? timelineDescription = null;

        // Timeline events stay gated on the pending lookup, exactly as before.
        lock (_lock)
        {
            if (_pendingInvites.TryGetValue(callId, out var pending))
            {
                // 180 Ringing or 200 OK — INVITE is progressing, remove from tracking
                if (code == 180 || code == 200)
                {
                    _pendingInvites.Remove(callId);
                    timelineEventType = code == 180 ? "RINGING" : "CALL_ANSWERED";
                    timelineDescription = $"{code} response for {callId}";
                }
                // 4xx+ error responses — remove and generate diagnosis
                else if (code >= 400)
                {
                    _pendingInvites.Remove(callId);
                    failed = true;
                    // 404/480 mean the registrar has no usable binding for the URI we rang; anything
                    // else 4xx/5xx/6xx is the device actively refusing the call.
                    reason = code is 404 or 480
                        ? BellFailureReason.NotRegistered
                        : BellFailureReason.Rejected;
                    target = entry.ToAddress ?? pending.Target;
                    detail = $"{code} {entry.StatusText}";
                    diagnosisSuggestions = GetSuggestionsForStatusCode(code);
                    diagnosisIssue = $"INVITE to {entry.ToAddress} failed with {code} {entry.StatusText}";
                    timelineEventType = "INVITE_FAILED";
                    timelineDescription = $"{code} {entry.StatusText} for {callId}";
                }
                // 1xx/2xx-other/3xx: the INVITE is still in flight — leave it pending so a
                // later timeout can still fire.
            }
        }

        if (diagnosisIssue is not null)
        {
            _logger.LogWarning("SIP diagnosis: {Issue}", diagnosisIssue);
            OnDiagnosisGenerated?.Invoke(diagnosisIssue, diagnosisSuggestions!);
        }

        if (timelineEventType is not null)
        {
            AddTimelineEvent(timelineEventType, timelineDescription!, callId);
        }

        if (succeeded)
        {
            OnSentInviteSucceeded?.Invoke(callId);
        }
        else if (failed)
        {
            OnSentInviteFailed?.Invoke(callId, reason, target, detail);
        }
    }

    /// <summary>
    /// Check all pending INVITEs for timeout (no 180 Ringing within 5s).
    /// </summary>
    public void CheckInviteTimeouts()
    {
        List<(string CallId, string? Target)> timedOut;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            timedOut = _pendingInvites
                .Where(kv => now - kv.Value.SentAt > InviteTimeout)
                .Select(kv => (kv.Key, kv.Value.Target))
                .ToList();

            foreach (var (callId, _) in timedOut)
                _pendingInvites.Remove(callId);
        }

        foreach (var (callId, target) in timedOut)
        {
            string issue = $"INVITE timeout: no response for call {callId} after {InviteTimeout.TotalSeconds}s";
            string[] suggestions = new[]
            {
                "Check HT801 registration status",
                "Verify extension number is correct",
                "Check codec configuration (G.711 recommended)",
                "Verify network connectivity to HT801",
                "Check SDP port availability"
            };

            _logger.LogWarning("SIP diagnosis: {Issue}", issue);
            OnDiagnosisGenerated?.Invoke(issue, suggestions);
            AddTimelineEvent("INVITE_TIMEOUT", $"No response for {callId}", callId);

            // This is the failure that actually fires in production: a UDP send to a
            // dead-but-routable address succeeds at the socket level, so the only evidence the
            // bell did not ring is the absence of a 180/200.
            // F0, not N0: this string crosses a service boundary into Radio.Web's diagnostics card,
            // and N0 would render a culture-dependent thousands separator ("5,000 ms").
            OnSentInviteFailed?.Invoke(callId, BellFailureReason.Timeout, target,
                $"no response to INVITE after {InviteTimeout.TotalMilliseconds:F0} ms");
        }
    }

    /// <summary>
    /// Returns recent SIP messages, optionally filtered by method.
    /// </summary>
    public List<SipMessageEntry> GetRecentMessages(int count, string? methodFilter = null)
    {
        lock (_lock)
        {
            IEnumerable<SipMessageEntry> query = _messageBuffer;

            if (!string.IsNullOrEmpty(methodFilter))
                query = query.Where(m => string.Equals(m.Method, methodFilter, StringComparison.OrdinalIgnoreCase));

            return query
                .Reverse()
                .Take(count)
                .Reverse()
                .ToList();
        }
    }

    /// <summary>
    /// Returns recent call timeline events.
    /// </summary>
    public List<CallTimelineEntry> GetTimeline(int count)
    {
        lock (_lock)
        {
            return _timeline
                .Reverse()
                .Take(count)
                .Reverse()
                .ToList();
        }
    }

    /// <summary>
    /// Returns current HT801 health snapshot.
    /// Derives registration state from both the event flag AND the message buffer
    /// (in case the REGISTER event was missed during a restart).
    /// </summary>
    public Ht801HealthStatus GetHt801Health()
    {
        // If we haven't caught a REGISTER event, scan the buffer for recent ones
        if (!_isRegistered || _lastRegisterReceived == null)
        {
            RefreshRegistrationFromBuffer();
        }

        // Consider registration stale if we haven't seen a REGISTER in a long time
        var isRegistered = _isRegistered
            && _lastRegisterReceived.HasValue
            && (DateTime.UtcNow - _lastRegisterReceived.Value) < RegistrationStaleThreshold;

        return new Ht801HealthStatus(
            IsReachable: _lastRegisterReceived.HasValue,
            PingMs: null,
            IsRegistered: isRegistered,
            RegistrationExpiresIn: _registrationExpiresIn,
            LastRegisterReceived: _lastRegisterReceived,
            HookState: null,
            FirmwareVersion: null
        );
    }

    /// <summary>
    /// Scan the message buffer for REGISTER messages we may have missed as events.
    /// This handles the case where the HT801 registered before our event wiring was set up,
    /// or during a service restart.
    /// </summary>
    private void RefreshRegistrationFromBuffer()
    {
        lock (_lock)
        {
            var lastRegister = _messageBuffer
                .Where(m => string.Equals(m.Method, "REGISTER", StringComparison.OrdinalIgnoreCase)
                         && m.Direction == SipDirection.Received)
                .LastOrDefault();

            if (lastRegister != null)
            {
                _isRegistered = true;
                _lastRegisterReceived = lastRegister.Timestamp;
            }
        }
    }

    private void AddTimelineEvent(string eventType, string description, string? callId)
    {
        var metadata = callId is not null
            ? new Dictionary<string, string> { ["callId"] = callId }
            : null;

        var timelineEntry = new CallTimelineEntry(DateTime.UtcNow, eventType, description, metadata);

        lock (_lock)
        {
            _timeline.AddLast(timelineEntry);
            while (_timeline.Count > MaxTimelineSize)
                _timeline.RemoveFirst();
        }

        OnCallTimelineEvent?.Invoke(timelineEntry);
    }

    private static string[] GetSuggestionsForStatusCode(int code)
    {
        return code switch
        {
            401 => new[] { "Check SIP authentication credentials on HT801" },
            403 => new[] { "Check extension number and domain configuration for mismatch" },
            408 => new[] { "HT801 not responding — check network connectivity" },
            480 => new[] { "Device not registered — verify HT801 registration" },
            486 => new[] { "Phone is busy — try again later" },
            503 => new[] { "Device overloaded — check HT801 status and restart if needed" },
            _ => new[] { $"Unexpected SIP error {code} — check HT801 logs" }
        };
    }

    // IHostedService implementation

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SipDiagnosticService starting — periodic checks every 3s");
        _timer = new Timer(_ =>
        {
            CheckInviteTimeouts();
            RefreshRegistrationFromBuffer();
        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SipDiagnosticService stopping");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
