using System.Collections.Concurrent;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Sliding-window send limiter (ADR §4.2 #4): rejects more than <paramref name="maxSends"/> sends per
/// window so a looping bug or hostile LAN caller cannot spray the owner's GV number. The controller
/// returns 429 when TryAcquire() is false — the UI's "Sending too fast" affordance (handoff) relies on
/// this being a real server response, not a UI-only guess. Process-wide (single GV account).
/// </summary>
public class SmsSendRateLimiter
{
    private readonly int _maxSends;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentQueue<DateTime> _stamps = new();
    private readonly object _gate = new();

    public SmsSendRateLimiter(int maxSends, TimeSpan window, Func<DateTime>? clock = null)
    {
        _maxSends = maxSends;
        _window = window;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True if a send is permitted now (and records it); false if the window is full.</summary>
    public bool TryAcquire()
    {
        var now = _clock();
        lock (_gate)
        {
            // Strictly-closed window: a stamp at exactly the window boundary is still IN-window, so the
            // limiter matches its documented "reject > N sends per window" contract (a 6th send at exactly
            // t=window is rejected, not admitted). Use > rather than >= for eviction (review MEDIUM-1).
            while (_stamps.TryPeek(out var oldest) && now - oldest > _window)
                _stamps.TryDequeue(out _);

            if (_stamps.Count >= _maxSends) return false;
            _stamps.Enqueue(now);
            return true;
        }
    }
}
