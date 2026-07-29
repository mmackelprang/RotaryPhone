using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Sip;
using Serilog;

namespace RotaryPhoneController.Tests;

public class RegistrarBindingTests
{
    private const string ConfiguredIp = "192.0.2.22";   // stale, as shipped in config
    private const string LearnedIp = "192.0.2.240";     // where the device actually is

    private static RegistrarBinding Fresh(string aor, string address, int expires = 3600) =>
        new(aor, address, 5060, address, DateTime.UtcNow, expires);

    private static SIPSorceryAdapter AdapterWith(IRegistrarBindingStore store) =>
        new(Mock.Of<ILogger>(), "0.0.0.0", 5060, store);

    [Fact]
    public void Resolve_PrefersFreshLearnedBinding_OverConfiguredAddress()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("1000", LearnedIp));

        Assert.Equal(LearnedIp, AdapterWith(store).ResolveHt801Address("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_FallsBackToConfigured_WhenNothingLearnedYet()
    {
        Assert.Equal(ConfiguredIp,
            AdapterWith(new RegistrarBindingStore()).ResolveHt801Address("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_FallsBackToConfigured_WhenBindingIsStale()
    {
        var store = new RegistrarBindingStore();
        // Expiry 60s, learned 2h ago — well beyond expiry + StaleGrace.
        store.Record(new RegistrarBinding("1000", LearnedIp, 5060, LearnedIp,
            DateTime.UtcNow.AddHours(-2), 60));

        Assert.Equal(ConfiguredIp, AdapterWith(store).ResolveHt801Address("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_UsesSingleBinding_WhenExtensionDoesNotMatchRegisteredAor()
    {
        // The HT801 registers as "rotaryphone" but is rung at extension "1000".
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", LearnedIp));

        Assert.Equal(LearnedIp, AdapterWith(store).ResolveHt801Address("1000", ConfiguredIp));
    }

    [Fact]
    public void Resolve_DoesNotGuess_WhenMultipleBindingsAndNoAorMatch()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", LearnedIp));
        store.Record(Fresh("kitchen", "192.0.2.241"));

        Assert.Equal(ConfiguredIp, AdapterWith(store).ResolveHt801Address("1000", ConfiguredIp));
    }

    [Fact]
    public void Record_RefreshesExistingBinding_WhenDeviceMoves()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", "192.0.2.99"));
        store.Record(Fresh("rotaryphone", LearnedIp));

        Assert.Single(store.All());
        Assert.Equal(LearnedIp, store.Get("rotaryphone")!.Address);
    }

    [Fact]
    public void Remove_DropsBinding_OnDeRegistration()
    {
        var store = new RegistrarBindingStore();
        store.Record(Fresh("rotaryphone", LearnedIp));
        store.Remove("rotaryphone");

        Assert.Null(store.Get("rotaryphone"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void IsFresh_HonoursExpiryPlusGrace()
    {
        var now = DateTime.UtcNow;
        var binding = new RegistrarBinding("rotaryphone", LearnedIp, 5060, LearnedIp,
            now.AddSeconds(-3600), 3600);

        Assert.True(binding.IsFresh(now));                      // exactly at expiry, within grace
        Assert.False(binding.IsFresh(now.AddMinutes(6)));       // beyond expiry + 5 min grace
    }

    [Fact]
    public void IsFresh_IsInclusive_AtExactlyExpiryPlusGrace()
    {
        // Pins the boundary: the comparison is <=, so a binding learned exactly expiry + StaleGrace
        // ago is still fresh. Timestamps are derived from a fixed `now` — no wall-clock sleeps.
        var now = DateTime.UtcNow;
        var learnedAt = now - TimeSpan.FromSeconds(3600) - RegistrarBinding.StaleGrace;
        var binding = new RegistrarBinding("rotaryphone", LearnedIp, 5060, LearnedIp, learnedAt, 3600);

        Assert.True(binding.IsFresh(now));                        // exactly on the boundary
        Assert.False(binding.IsFresh(now.AddSeconds(1)));         // one second past it
    }
}
