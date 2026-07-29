using RotaryPhoneController.Core.Bell;

namespace RotaryPhoneController.Tests;

/// <summary>
/// The tracker is the single convergence point for "the bell did not ring", so its counting,
/// clearing and acknowledgement semantics are what every consumer depends on.
/// </summary>
public class BellFailureTrackerTests
{
    private const string PhoneId = "default";

    private static BellFailureRecord Fail(BellFailureTracker tracker, string phoneId = PhoneId,
        BellFailureReason reason = BellFailureReason.Timeout) =>
        tracker.RecordFailure(phoneId, reason, "5551234567", "call-1", "192.0.2.240",
            "no response to INVITE", DateTime.UtcNow);

    [Fact]
    public void RecordFailure_IncrementsFailureCount_AcrossConsecutiveFailures()
    {
        var tracker = new BellFailureTracker();

        Assert.Equal(1, Fail(tracker).FailureCount);
        Assert.Equal(2, Fail(tracker).FailureCount);
        Assert.Equal(3, Fail(tracker).FailureCount);
        Assert.Equal(3, tracker.Get(PhoneId)!.FailureCount);
    }

    [Fact]
    public void RecordSuccess_ClearsRecord_AndResetsCount()
    {
        var tracker = new BellFailureTracker();
        Fail(tracker);
        Fail(tracker);

        tracker.RecordSuccess(PhoneId);

        Assert.Null(tracker.Get(PhoneId));
        // A demonstrated ring is the strongest recovery evidence there is — the count starts over.
        Assert.Equal(1, Fail(tracker).FailureCount);
    }

    [Fact]
    public void RecordSuccess_RaisesRecovered_OnlyWhenSomethingWasCleared()
    {
        var tracker = new BellFailureTracker();
        var recovered = 0;
        tracker.OnBellRecovered += _ => recovered++;

        // Nothing stored — a healthy ring must not emit a spurious "recovered".
        tracker.RecordSuccess(PhoneId);
        Assert.Equal(0, recovered);

        Fail(tracker);
        tracker.RecordSuccess(PhoneId);
        Assert.Equal(1, recovered);

        // Already cleared — no second event.
        tracker.RecordSuccess(PhoneId);
        Assert.Equal(1, recovered);
    }

    [Fact]
    public void Acknowledge_SetsFlag_WithoutResettingFailureCount()
    {
        var tracker = new BellFailureTracker();
        Fail(tracker);
        Fail(tracker);

        Assert.True(tracker.Acknowledge(PhoneId));

        var record = tracker.Get(PhoneId)!;
        Assert.True(record.Acknowledged);
        // Dismissing the note does not mean the bell started working.
        Assert.Equal(2, record.FailureCount);
    }

    [Fact]
    public void Acknowledge_ReturnsFalse_WhenNothingToAcknowledge()
    {
        var tracker = new BellFailureTracker();

        Assert.False(tracker.Acknowledge(PhoneId));

        Fail(tracker);
        Assert.True(tracker.Acknowledge(PhoneId));
        // Idempotent — a second ack is a no-op, not an error.
        Assert.False(tracker.Acknowledge(PhoneId));
    }

    [Fact]
    public void OnBellFailure_FiresExactlyOncePerRecordFailure()
    {
        var tracker = new BellFailureTracker();
        var events = new List<BellFailureRecord>();
        tracker.OnBellFailure += (_, record) => events.Add(record);

        Fail(tracker);
        Fail(tracker);

        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].FailureCount);
        Assert.Equal(2, events[1].FailureCount);
    }

    [Fact]
    public void Failures_ForDifferentPhoneIds_AreIndependent()
    {
        var tracker = new BellFailureTracker();

        Fail(tracker, "hall");
        Fail(tracker, "hall");
        Fail(tracker, "kitchen");

        Assert.Equal(2, tracker.Get("hall")!.FailureCount);
        Assert.Equal(1, tracker.Get("kitchen")!.FailureCount);

        tracker.RecordSuccess("hall");

        Assert.Null(tracker.Get("hall"));
        Assert.Equal(1, tracker.Get("kitchen")!.FailureCount);
    }

    [Fact]
    public void RecordFailure_CarriesReasonAndContext()
    {
        var tracker = new BellFailureTracker();
        var at = new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);

        var record = tracker.RecordFailure(PhoneId, BellFailureReason.NotRegistered,
            "5551234567", "call-abc", "192.0.2.240", "404 Not Found", at);

        Assert.Equal(BellFailureReason.NotRegistered, record.Reason);
        Assert.Equal("5551234567", record.CallerNumber);
        Assert.Equal("call-abc", record.CallId);
        Assert.Equal("192.0.2.240", record.Target);
        Assert.Equal("404 Not Found", record.Detail);
        Assert.Equal(at, record.OccurredAtUtc);
        Assert.False(record.Acknowledged);
    }
}
