using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.Bell;

/// <summary>
/// The single convergence point for "the bell did not ring". Both detection paths — the immediate
/// socket-level INVITE failure (CallManager) and the delayed INVITE-outcome signal
/// (SipDiagnosticService: timeout / 4xx) — feed this tracker, and exactly one hub event is emitted
/// from its <see cref="OnBellFailure"/> subscription. Consumers therefore never have to reconcile
/// two competing notions of failure.
/// </summary>
public interface IBellFailureTracker
{
    /// <summary>Records a failed ring attempt, incrementing the consecutive-failure count. Raises OnBellFailure.</summary>
    BellFailureRecord RecordFailure(string phoneId, BellFailureReason reason, string? callerNumber,
        string? callId, string? target, string? detail, DateTime occurredAtUtc);

    /// <summary>
    /// Records that a ring demonstrably succeeded (the HT801 answered the INVITE with 180 or 200).
    /// A successful ring is the strongest possible recovery evidence — stronger than a reachability
    /// probe — so it clears the stored failure and resets the consecutive count. Raises OnBellRecovered
    /// only if a failure was actually cleared.
    /// </summary>
    void RecordSuccess(string phoneId);

    /// <summary>Marks the stored failure acknowledged (the user dismissed the note). Idempotent; returns false if there was nothing to acknowledge.</summary>
    bool Acknowledge(string phoneId);

    BellFailureRecord? Get(string phoneId);

    event Action<string, BellFailureRecord>? OnBellFailure;   // (phoneId, record)
    event Action<string>? OnBellRecovered;                    // (phoneId)
}

/// <summary>
/// Per-phone, thread-safe bell-failure state, persisted through an optional
/// <see cref="IBellFailureStore"/>.
///
/// <para>
/// <b>This REVERSES plan decision D5, which said the tracker was deliberately not persisted.</b>
/// D5's argument was sound as far as it went: a failure recorded before a restart says nothing about
/// the current state of the hardware, so restoring it risks a second source of untruth about the
/// bell — the exact class of problem this work exists to remove. That concern is not being
/// dismissed. It is being answered.
/// </para>
///
/// <para>
/// <b>What answers it: this record was never the live health signal.</b> Live reachability is
/// <c>Ht801Reachable</c>, re-probed within ~30 s of boot and now reported identically over REST and
/// SignalR. What is stored here is a timestamped HISTORICAL note carrying its own
/// <see cref="BellFailureRecord.OccurredAtUtc"/>, and acknowledging it explicitly does not touch
/// reachability — the reply's phrasing is "acking clears the note, not the fault". A note dated an
/// hour ago is not a claim about now, and it is rendered as what it is.
/// </para>
///
/// <para>
/// <b>The overriding reason, though, is that we already promised this in writing.</b>
/// docs/handoffs/radioconsole-bell-failure-reply.md §5 tells RadioConsole the acknowledged flag
/// "survives a service restart" and that their Q4 concern — a nightly-restarting kiosk resurrecting
/// a note the operator already dismissed — "is addressed". It was not; the tracker was in-memory.
/// Given the choice between retracting the claim and making it true, the owner chose to make it
/// true. A dismissal that silently undismisses itself every night is worse than a note that is one
/// restart stale, and a document asserting more than the code does is the specific failure this
/// project keeps tripping over.
/// </para>
///
/// <para>
/// <b>FailureCount survives too, and that is intended.</b> It counts consecutive failures since the
/// last DEMONSTRATED success; a restart demonstrates nothing. Resetting it on boot would quietly
/// downgrade a bell that has failed five times in a row to a bell that has failed once.
/// </para>
///
/// <para>
/// Constructed with no store, the tracker is in-memory only and touches no disk — which is what the
/// unit tests use.
/// </para>
/// </summary>
public sealed class BellFailureTracker : IBellFailureTracker
{
    private readonly ConcurrentDictionary<string, BellFailureRecord> _failures;

    // Guards the read-modify-write sequences (increment the consecutive count, flip Acknowledged).
    // BellFailureRecord is immutable, so every mutation is a replace and must not race another.
    private readonly object _lock = new();

    private readonly IBellFailureStore? _store;
    private readonly ILogger<BellFailureTracker>? _logger;

    public event Action<string, BellFailureRecord>? OnBellFailure;
    public event Action<string>? OnBellRecovered;

    public BellFailureTracker(IBellFailureStore? store = null, ILogger<BellFailureTracker>? logger = null)
    {
        _store = store;
        _logger = logger;

        // Load is contractually incapable of throwing — see JsonBellFailureStore. That matters here
        // specifically: this runs while the DI container is building a singleton the whole server
        // depends on, and a bad state file must not be able to stop the appliance from booting.
        var restored = store?.Load();

        _failures = restored is null
            ? new ConcurrentDictionary<string, BellFailureRecord>(StringComparer.OrdinalIgnoreCase)
            : new ConcurrentDictionary<string, BellFailureRecord>(restored, StringComparer.OrdinalIgnoreCase);

        if (_failures.Count > 0)
        {
            _logger?.LogInformation(
                "Restored bell-failure state for {Count} phone(s) across restart", _failures.Count);
        }
    }

    public BellFailureRecord RecordFailure(string phoneId, BellFailureReason reason, string? callerNumber,
        string? callId, string? target, string? detail, DateTime occurredAtUtc)
    {
        BellFailureRecord record;

        lock (_lock)
        {
            // FailureCount counts CONSECUTIVE failures since the last demonstrated success. It is not
            // reset by Acknowledge — dismissing the note does not mean the bell started working.
            var previousCount = _failures.TryGetValue(phoneId, out var existing) ? existing.FailureCount : 0;

            record = new BellFailureRecord(
                occurredAtUtc, reason, callerNumber, callId, target, detail,
                previousCount + 1, Acknowledged: false);

            _failures[phoneId] = record;
            Persist();
        }

        // Raise outside the lock: subscribers broadcast over SignalR and must never run under it.
        OnBellFailure?.Invoke(phoneId, record);
        return record;
    }

    public void RecordSuccess(string phoneId)
    {
        bool cleared;

        lock (_lock)
        {
            cleared = _failures.TryRemove(phoneId, out _);

            // Only on an actual clear. A healthy bell rings all day and reports success every time;
            // rewriting an unchanged file on each of those would be pure disk churn.
            if (cleared) Persist();
        }

        // Only announce recovery when something was actually cleared — otherwise every successful ring
        // on a healthy system would emit a spurious "recovered" event.
        if (cleared)
        {
            OnBellRecovered?.Invoke(phoneId);
        }
    }

    public bool Acknowledge(string phoneId)
    {
        lock (_lock)
        {
            if (!_failures.TryGetValue(phoneId, out var existing) || existing.Acknowledged)
            {
                // Nothing to acknowledge (or already acknowledged). Idempotent by design — the caller
                // returns 200 with acknowledged=false rather than 404.
                return false;
            }

            _failures[phoneId] = existing with { Acknowledged = true };

            // The one persist that was promised to another team by name. Only on the actual flip —
            // the early return above already covers the idempotent second ack.
            Persist();
            return true;
        }
    }

    public BellFailureRecord? Get(string phoneId) =>
        _failures.TryGetValue(phoneId, out var record) ? record : null;

    /// <summary>
    /// Writes the current state through the store. <b>Call sites must already hold _lock.</b>
    ///
    /// Persisting inside the lock rather than after it costs a file write on a path that runs at
    /// most a few times per call, and buys the guarantee that the file's write order can never
    /// disagree with the in-memory order: two concurrent mutations serialized in one order but
    /// flushed in the other would leave the durable state contradicting the state the UI is showing,
    /// which is the failure mode this whole feature exists to eliminate. The store swallows its own
    /// I/O errors, so this cannot throw out of a mutation.
    /// </summary>
    private void Persist()
    {
        if (_store is null) return;

        _store.Save(new Dictionary<string, BellFailureRecord>(_failures, StringComparer.OrdinalIgnoreCase));
    }
}
