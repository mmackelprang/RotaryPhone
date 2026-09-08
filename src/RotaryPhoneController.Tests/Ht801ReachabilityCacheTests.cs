using RotaryPhoneController.Core.HT801;

namespace RotaryPhoneController.Tests;

/// <summary>
/// The cache is now shared by the SignalR probe (writer) and the REST system-status endpoint
/// (reader), so its change-detection contract is load-bearing in two places at once: it decides
/// whether a hub broadcast goes out, and it is the sole source of what the REST endpoint reports.
/// The distinction these tests pin is that "nothing worth broadcasting changed" and "nothing
/// changed" are not the same statement — the timestamp always moves.
/// </summary>
public class Ht801ReachabilityCacheTests
{
    private const string Address = "192.0.2.240";

    private static readonly DateTime T0 = new(2026, 9, 8, 15, 54, 0, DateTimeKind.Utc);

    [Fact]
    public void Current_BeforeAnyProbe_IsNotYetProbed()
    {
        var cache = new Ht801ReachabilityCache();

        Assert.Same(Ht801ProbeSnapshot.NotYetProbed, cache.Current);
        Assert.Null(cache.Current.Reachable);
        Assert.Null(cache.Current.LastCheckedUtc);
        Assert.Null(cache.Current.ProbedAddress);
    }

    [Fact]
    public void Update_FirstRealValue_ReportsChanged()
    {
        var cache = new Ht801ReachabilityCache();

        // The null -> bool transition is what turns "Unknown" into an answer in the UI. Treating it
        // as "no change" would leave the very first successful probe unbroadcast.
        Assert.True(cache.Update(true, Address, T0));
        Assert.True(cache.Current.Reachable);
        Assert.Equal(T0, cache.Current.LastCheckedUtc);
        Assert.Equal(Address, cache.Current.ProbedAddress);
    }

    [Fact]
    public void Update_SameResultLaterTimestamp_ReportsUnchanged_ButTimestampStillAdvances()
    {
        var cache = new Ht801ReachabilityCache();
        cache.Update(true, Address, T0);

        var later = T0.AddSeconds(30);

        // No broadcast — a healthy bell must not emit a SystemStatusChanged every 30 seconds ...
        Assert.False(cache.Update(true, Address, later));
        // ... but the probe age is exactly what the "last checked" affordance renders, so
        // suppressing the broadcast must not suppress the timestamp.
        Assert.Equal(later, cache.Current.LastCheckedUtc);
    }

    [Fact]
    public void Update_AddressChangedButStillReachable_ReportsChanged()
    {
        var cache = new Ht801ReachabilityCache();
        cache.Update(true, Address, T0);

        // The registrar binding moved. Reachability reads the same, but it is now a statement about
        // a different device address — which is the whole 2026-07 outage in one line.
        Assert.True(cache.Update(true, "192.0.2.99", T0.AddSeconds(30)));
        Assert.Equal("192.0.2.99", cache.Current.ProbedAddress);
    }

    [Fact]
    public void Update_ReachableToUnknown_ReportsChanged()
    {
        var cache = new Ht801ReachabilityCache();
        cache.Update(true, Address, T0);

        // true -> null is "we no longer know", which is a different claim from "reachable" and has
        // to reach the UI as a state change.
        Assert.True(cache.Update(null, Address, T0.AddSeconds(30)));
        Assert.Null(cache.Current.Reachable);
    }
}
