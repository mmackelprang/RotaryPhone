using System.Collections.Concurrent;

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
/// Per-phone, in-memory, thread-safe bell-failure state.
///
/// Deliberately NOT persisted, for the same reason registrar bindings are not (plan D5): a failure
/// recorded before a restart says nothing about the current state of the hardware, and a restored
/// stale alert would be a second source of untruth about the bell — the exact class of problem this
/// work exists to remove. The next ring attempt re-establishes the truth within seconds.
/// </summary>
public sealed class BellFailureTracker : IBellFailureTracker
{
    private readonly ConcurrentDictionary<string, BellFailureRecord> _failures =
        new(StringComparer.OrdinalIgnoreCase);

    // Guards the read-modify-write sequences (increment the consecutive count, flip Acknowledged).
    // BellFailureRecord is immutable, so every mutation is a replace and must not race another.
    private readonly object _lock = new();

    public event Action<string, BellFailureRecord>? OnBellFailure;
    public event Action<string>? OnBellRecovered;

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
            return true;
        }
    }

    public BellFailureRecord? Get(string phoneId) =>
        _failures.TryGetValue(phoneId, out var record) ? record : null;
}
