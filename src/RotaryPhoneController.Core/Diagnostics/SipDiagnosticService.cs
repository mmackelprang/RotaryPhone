using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    // INVITE tracking: callId → sentTime
    private readonly Dictionary<string, DateTime> _pendingInvites = new();
    private static readonly TimeSpan InviteTimeout = TimeSpan.FromSeconds(5);

    // HT801 registration state
    private bool _isRegistered;
    private DateTime? _lastRegisterReceived;
    private int? _registrationExpiresIn;

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

        // Track sent INVITEs
        if (string.Equals(entry.Method, "INVITE", StringComparison.OrdinalIgnoreCase)
            && entry.Direction == SipDirection.Sent
            && entry.CallId is not null)
        {
            lock (_lock)
            {
                _pendingInvites[entry.CallId] = entry.Timestamp;
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

        lock (_lock)
        {
            if (!_pendingInvites.ContainsKey(callId))
                return;

            // 180 Ringing or 200 OK — INVITE is progressing, remove from tracking
            if (code == 180 || code == 200)
            {
                _pendingInvites.Remove(callId);
                string eventType = code == 180 ? "RINGING" : "CALL_ANSWERED";
                AddTimelineEvent(eventType, $"{code} response for {callId}", callId);
                return;
            }

            // 4xx+ error responses — remove and generate diagnosis
            if (code >= 400)
            {
                _pendingInvites.Remove(callId);
                string[] suggestions = GetSuggestionsForStatusCode(code);
                string issue = $"INVITE to {entry.ToAddress} failed with {code} {entry.StatusText}";
                _logger.LogWarning("SIP diagnosis: {Issue}", issue);
                OnDiagnosisGenerated?.Invoke(issue, suggestions);
                AddTimelineEvent("INVITE_FAILED", $"{code} {entry.StatusText} for {callId}", callId);
            }
        }
    }

    /// <summary>
    /// Check all pending INVITEs for timeout (no 180 Ringing within 5s).
    /// </summary>
    public void CheckInviteTimeouts()
    {
        List<(string callId, DateTime sentTime)> timedOut;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            timedOut = _pendingInvites
                .Where(kv => now - kv.Value > InviteTimeout)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            foreach (var (callId, _) in timedOut)
                _pendingInvites.Remove(callId);
        }

        foreach (var (callId, sentTime) in timedOut)
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
    /// </summary>
    public Ht801HealthStatus GetHt801Health()
    {
        return new Ht801HealthStatus(
            IsReachable: _isRegistered,
            PingMs: null,
            IsRegistered: _isRegistered,
            RegistrationExpiresIn: _registrationExpiresIn,
            LastRegisterReceived: _lastRegisterReceived,
            HookState: null,
            FirmwareVersion: null
        );
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
        _logger.LogInformation("SipDiagnosticService starting — INVITE timeout check every 3s");
        _timer = new Timer(_ => CheckInviteTimeouts(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
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
