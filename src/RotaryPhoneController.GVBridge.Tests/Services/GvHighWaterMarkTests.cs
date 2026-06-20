using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvHighWaterMarkTests
{
    [Fact]
    public void FirstObservation_IsNotNew_ToAvoidStartupFlood()
    {
        var hwm = new GvHighWaterMark();
        // On the very first poll we seed the marks and DO NOT raise events for history.
        var firstSeed = hwm.IsNewMessage("t.1", "m.1", epochMs: 1000);
        Assert.False(firstSeed); // seeding, not "new"
    }

    [Fact]
    public void NewerMessage_AfterSeed_IsNew()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 1000L) });

        Assert.True(hwm.IsNewMessage("t.1", "m.2", 2000));
    }

    [Fact]
    public void OlderOrEqualMessage_AfterSeed_IsNotNew()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 2000L) });

        Assert.False(hwm.IsNewMessage("t.1", "m.0", 1000));
        Assert.False(hwm.IsNewMessage("t.1", "m.1", 2000)); // same id+ts
    }

    [Fact]
    public void SameMessageTwice_IsNewOnlyOnce()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 1000L) });

        Assert.True(hwm.IsNewMessage("t.1", "m.2", 2000));
        Assert.False(hwm.IsNewMessage("t.1", "m.2", 2000)); // mark advanced, no double-fire
    }

    [Fact]
    public void UnknownThread_AfterSeed_TreatsNewMessageAsNew()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 1000L) });

        Assert.True(hwm.IsNewMessage("t.2", "m.9", 500)); // brand-new thread → its first inbound is new
    }
}
