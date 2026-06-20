using System.Collections.Concurrent;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Per-thread high-water mark for the poller's diff (ADR §5.3). Tracks the max message timestamp seen
/// per thread; a message strictly newer than the mark is "new" and advances it. The FIRST poll seeds
/// the marks WITHOUT raising events (so we don't flood RadioConsole with history on startup). In-memory
/// for v1 (ADR §5.3 notes optional SQLite durability — out of scope here; a restart re-seeds silently).
/// </summary>
public class GvHighWaterMark
{
    private readonly ConcurrentDictionary<string, long> _maxEpochByThread = new();
    private bool _seeded;

    /// <summary>Seed marks from the first poll's messages without treating any as new.</summary>
    public void Seed(IEnumerable<(string ThreadId, string MessageId, long EpochMs)> messages)
    {
        foreach (var (threadId, _, epoch) in messages)
            Advance(threadId, epoch);
        _seeded = true;
    }

    /// <summary>
    /// True if this message is newer than the thread's mark (and advances the mark). Before the first
    /// Seed, returns false and seeds the mark — so the very first poll never raises events.
    /// </summary>
    public bool IsNewMessage(string threadId, string messageId, long epochMs)
    {
        if (!_seeded)
        {
            Advance(threadId, epochMs);
            return false;
        }

        var current = _maxEpochByThread.GetValueOrDefault(threadId, long.MinValue);
        if (epochMs > current)
        {
            Advance(threadId, epochMs);
            return true;
        }
        return false;
    }

    private void Advance(string threadId, long epochMs)
        => _maxEpochByThread.AddOrUpdate(threadId, epochMs, (_, prev) => Math.Max(prev, epochMs));
}
