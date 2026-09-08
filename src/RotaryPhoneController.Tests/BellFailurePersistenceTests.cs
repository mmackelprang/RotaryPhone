using RotaryPhoneController.Core.Bell;

namespace RotaryPhoneController.Tests;

/// <summary>
/// docs/handoffs/radioconsole-bell-failure-reply.md §5 tells RadioConsole the acknowledged flag
/// "survives a service restart", and that their Q4 concern — a kiosk that restarts nightly
/// resurrecting a note the operator already dismissed — "is addressed". The tracker was in-memory
/// when that was written, so it was not true. These tests are what make it true and keep it true.
///
/// <para>
/// <b>Every test here constructs a SECOND tracker over the same file.</b> A test that writes and
/// then reads back through the same instance proves only that a dictionary works. The claim being
/// defended is specifically about a process boundary, so the second construction — a simulated
/// restart — is the whole test, not an incidental detail.
/// </para>
/// </summary>
public class BellFailurePersistenceTests : IDisposable
{
    private const string PhoneId = "default";

    private readonly string _dir;
    private readonly string _path;

    public BellFailurePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bell-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "bell-failure-state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A tracker over the shared file. Calling it twice simulates a restart.</summary>
    private BellFailureTracker NewTracker() => new(new JsonBellFailureStore(_path));

    private static BellFailureRecord Fail(BellFailureTracker tracker, string phoneId = PhoneId) =>
        tracker.RecordFailure(phoneId, BellFailureReason.Timeout, "5551234567", "call-1",
            "192.0.2.240", "no response to INVITE", DateTime.UtcNow);

    [Fact]
    public void Acknowledge_SurvivesRestart()
    {
        var before = NewTracker();
        Fail(before);
        Assert.True(before.Acknowledge(PhoneId));

        // Restart.
        var after = NewTracker();

        // The headline assertion — this is the promise made to RadioConsole in writing, and the
        // exact scenario their nightly kiosk restart produces.
        Assert.True(after.Get(PhoneId)!.Acknowledged);
    }

    [Fact]
    public void RecordedFailure_SurvivesRestart_WithFailureCountIntact()
    {
        var before = NewTracker();
        Fail(before);
        Fail(before);
        Fail(before);

        var after = NewTracker();

        // A restart is not evidence the bell started working, so the consecutive count carries over
        // rather than starting again at one.
        Assert.Equal(3, after.Get(PhoneId)!.FailureCount);
        Assert.Equal(4, Fail(after).FailureCount);
    }

    [Fact]
    public void RecordSuccess_ClearsPersistedState()
    {
        var before = NewTracker();
        Fail(before);
        before.RecordSuccess(PhoneId);

        var after = NewTracker();

        // A demonstrated ring is the strongest recovery evidence there is. If the clear were not
        // persisted, a restart would resurrect a failure that has since been disproved — a ghost.
        Assert.Null(after.Get(PhoneId));
    }

    [Fact]
    public void MissingStateFile_StartsEmpty()
    {
        Assert.False(File.Exists(_path));

        var tracker = NewTracker();

        Assert.Null(tracker.Get(PhoneId));
        // First boot must not litter data/ with an empty file just by starting up.
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void CorruptStateFile_StartsEmpty_AndDoesNotThrow()
    {
        File.WriteAllText(_path, "{ not json");

        // Construction happens while DI builds a singleton the whole server depends on. Throwing
        // here would mean a bad file could stop the appliance from booting — a phone that will not
        // ring, to protect a dismissal flag.
        var tracker = NewTracker();
        Assert.Null(tracker.Get(PhoneId));

        // And it recovers: the bad file is overwritten by the next mutation rather than poisoning
        // the state permanently.
        Fail(tracker);
        Assert.Equal(1, NewTracker().Get(PhoneId)!.FailureCount);
    }

    [Fact]
    public void AllFieldsRoundTrip()
    {
        var occurredAt = new DateTime(2026, 7, 28, 12, 34, 56, DateTimeKind.Utc);

        var before = NewTracker();
        var original = before.RecordFailure(PhoneId, BellFailureReason.Rejected, "5551234567",
            "call-abc", "192.0.2.240", "486 Busy Here", occurredAt);
        before.Acknowledge(PhoneId);

        var restored = NewTracker().Get(PhoneId)!;

        Assert.Equal(original.CallerNumber, restored.CallerNumber);
        Assert.Equal(original.CallId, restored.CallId);
        Assert.Equal(original.Target, restored.Target);
        Assert.Equal(original.Detail, restored.Detail);
        Assert.Equal(original.FailureCount, restored.FailureCount);
        Assert.True(restored.Acknowledged);

        // Rejected is deliberately not the zero value: an integer encoding that silently defaulted
        // would still pass a test that used Timeout.
        Assert.Equal(BellFailureReason.Rejected, restored.Reason);

        // ... and the round-trip alone does NOT prove the encoding, because an integer Reason
        // round-trips perfectly well within one build. What it fails to survive is someone inserting
        // a member into BellFailureReason, at which point a persisted `2` silently reloads as a
        // different reason and looks entirely valid. So assert the on-disk form directly.
        Assert.Contains("\"Rejected\"", File.ReadAllText(_path));

        // A UTC timestamp that reloads as Unspecified — or shifted by the machine's local offset —
        // would make every restored note's age wrong, and wrong by an amount that depends on where
        // the box is. Both the value and the Kind have to survive.
        Assert.Equal(occurredAt, restored.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Utc, restored.OccurredAtUtc.Kind);
    }

    [Fact]
    public void NoTempFileLeftBehind()
    {
        var tracker = NewTracker();
        Fail(tracker);
        tracker.Acknowledge(PhoneId);

        // The write goes via <path>.tmp + rename so a crash mid-write cannot truncate the real file.
        // The rename must consume the temp file, not leave debris beside the state it guards.
        Assert.Equal(new[] { "bell-failure-state.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void InMemoryTracker_WritesNoFile()
    {
        // No store means no disk, which is what the eight pre-existing BellFailureTrackerTests rely
        // on and what keeps `new BellFailureTracker()` a valid construction.
        var tracker = new BellFailureTracker();

        Fail(tracker);
        tracker.Acknowledge(PhoneId);

        Assert.True(tracker.Get(PhoneId)!.Acknowledged);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
