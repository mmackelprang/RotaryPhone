using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Sip;
using RotaryPhoneController.GVBridge.Tests.Sip;
using RotaryPhoneController.GVBridge.Tests.Support;
using RotaryPhoneController.Core;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

public class GVApiAdapterTests
{
    [Fact]
    public void Mode_IsGVApi()
    {
        var adapter = CreateAdapter();
        Assert.Equal(CallAdapterMode.GVApi, adapter.Mode);
    }

    [Fact]
    public void IsAvailable_DefaultsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsAvailable);
    }

    [Fact]
    public void IsSipRegistered_BeforeActivate_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsSipRegistered);
    }

    [Fact]
    public void AreCookiesValid_BeforeActivate_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.AreCookiesValid);
    }

    [Fact]
    public async Task PlaceCallAsync_ThrowsWhenNotAvailable()
    {
        var adapter = CreateAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.PlaceCallAsync("+15551234567"));
    }

    [Fact]
    public void IsWebSocketConnected_BeforeActivate_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsWebSocketConnected);
    }

    [Fact]
    public void SipLastConnectedAt_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.SipLastConnectedAt);
    }

    [Fact]
    public void PsidtsAgeSeconds_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.PsidtsAgeSeconds);
    }

    [Fact]
    public void Degraded_BeforeActivate_IsFalse()
    {
        // Not activated/available → not "degraded" (that's conveyed by available:false instead);
        // Degraded is gated on IsAvailable so an inactive adapter doesn't raise a false alarm.
        var adapter = CreateAdapter();
        Assert.False(adapter.IsAvailable);
        Assert.False(adapter.Degraded);
    }

    [Fact]
    public void LastHealthyAt_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.LastHealthyAt);
    }

    // --- IGvAuthenticatedClientProvider seam (PR1) ---

    [Fact]
    public void GetAuthenticatedClient_BeforeActivate_ReturnsNull()
    {
        // Seam gates on IsAvailable (false before activation) so PR2/PR3 read clients get null
        // rather than a half-initialized client; they handle null by reporting unavailable.
        IGvAuthenticatedClientProvider adapter = CreateAdapter();
        Assert.Null(adapter.GetAuthenticatedClient());
    }

    [Fact]
    public void ApiBaseUrl_ReturnsConfiguredValue()
    {
        IGvAuthenticatedClientProvider adapter = CreateAdapter();
        Assert.Equal("https://clients6.google.com/voice/v1/voiceclient", adapter.ApiBaseUrl);
    }

    [Fact]
    public void ApiKey_ReturnsConfiguredValue()
    {
        IGvAuthenticatedClientProvider adapter = CreateAdapter();
        Assert.Equal("test", adapter.ApiKey);
    }

    // --- Task 3 (spec F6/F7): ActivateAsync is re-entrant ---
    //
    // The external cookie refresher re-enters ActivateAsync on the LIVE adapter every ~20 min
    // (GvCookieManager → CallAdapterRegistry.SwitchModeAsync, whose DeactivateAsync is skipped
    // because the mode is unchanged). Every pass used to leak an armed 30-min Timer, an HttpClient
    // and a whole GvSipTransport — ~72/day — and starved the watchdog, the only timed entry into
    // the recovery ladder.
    //
    // A full activation needs a real cookie file plus a network round-trip, so these drive the
    // re-entrancy guard directly: a previous generation is installed via the private fields, then
    // ActivateAsync is called. The teardown tests deliberately give the adapter NO usable
    // encryption key, so ActivateAsync runs the guard at the top and then returns early at key
    // resolution — isolating the teardown from the construction that follows it.

    [Fact]
    public async Task ActivateAsync_CalledTwice_DisposesPreviousTransport()
    {
        var logger = new CapturingLogger<GVApiAdapter>();
        var adapter = CreateAdapter(NoKeyConfig(), logger);
        var (transport, _) = NewFakeTransport();
        GVApiAdapterRecoveryTests.SetField(adapter, "_sipTransport", transport);

        await adapter.ActivateAsync();

        Assert.True(GVApiAdapterRecoveryTests.GetField<bool>(transport, "_disposed"));
        Assert.Null(GVApiAdapterRecoveryTests.GetField<GvSipTransport>(adapter, "_sipTransport"));
        Assert.Contains(logger.Entries,
            e => e.Message.Contains("re-activating — tearing down the previous generation first"));
    }

    [Fact]
    public async Task ActivateAsync_CalledTwice_DoesNotLeakHealthTimer()
    {
        var adapter = CreateAdapter(NoKeyConfig());
        var previous = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        GVApiAdapterRecoveryTests.SetField(adapter, "_healthCheckTimer", previous);

        await adapter.ActivateAsync();

        // The previous generation's timer is dead, and no orphan was left behind in the field.
        Assert.True(TimerIsDead(previous));
        Assert.Null(GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer"));
    }

    [Fact]
    public async Task ActivateAsync_DuringActiveCall_KeepsTransport()
    {
        // Tearing the transport down mid-call would drop the call, so the guard takes the
        // cookies-only branch instead.
        var logger = new CapturingLogger<GVApiAdapter>();
        var adapter = CreateAdapter(NoKeyConfig(), logger);
        var (transport, channel) = NewFakeTransport();
        var timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        GVApiAdapterRecoveryTests.SetField(adapter, "_sipTransport", transport);
        GVApiAdapterRecoveryTests.SetField(adapter, "_healthCheckTimer", timer);
        GVApiAdapterRecoveryTests.SetField(adapter, "_activeCallId", "call-1");
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore",
            await NewStoreWithCookies("SAPISID-NEW"));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);

        await adapter.ActivateAsync();

        Assert.Same(transport, GVApiAdapterRecoveryTests.GetField<GvSipTransport>(adapter, "_sipTransport"));
        Assert.Same(timer, GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer"));
        Assert.False(GVApiAdapterRecoveryTests.GetField<bool>(transport, "_disposed"));
        Assert.Equal(0, channel.ConnectCount);
        Assert.Contains(logger.Entries,
            e => e.Message.Contains("re-activating during an active call"));
        // Cookies were still adopted — the refresher's contract holds even mid-call.
        Assert.Equal("SAPISID-NEW", adapter.CurrentCookieSet!.Sapisid);
    }

    [Fact]
    public async Task ActivateAsync_Reentrant_StillAdoptsNewCookies()
    {
        // The POINT of the fix: the external refresher's intent — adopt new cookies into the live
        // adapter — must keep working. The cookie swap happens before the health probe, so it is
        // observable regardless of the probe outcome.
        var adapter = CreateAdapter(NoKeyConfig());
        var (transport, _) = NewFakeTransport();
        GVApiAdapterRecoveryTests.SetField(adapter, "_sipTransport", transport);
        GVApiAdapterRecoveryTests.SetField(adapter, "_activeCallId", "call-1");
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore",
            await NewStoreWithCookies("SAPISID-ADOPTED"));
        adapter.HealthProbeOverride = _ => Task.FromResult(false);   // probe fails; adoption still happens

        await adapter.ActivateAsync();

        Assert.Equal("SAPISID-ADOPTED", adapter.CurrentCookieSet!.Sapisid);
    }

    // --- helpers ---

    /// <summary>
    /// A config with NO usable encryption key, so ActivateAsync runs the re-entrancy teardown at the
    /// top and then returns early at key resolution — no cookie file and no network needed.
    /// </summary>
    private static GVBridgeConfig NoKeyConfig() => new()
    {
        GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
        GvApiKey = "test",
        CookieFilePath = "test.enc",
        CookieKeyFilePath = "",
        CookieEncryptionKey = "",
    };

    private static GVApiAdapter CreateAdapter(GVBridgeConfig config, ILogger<GVApiAdapter>? logger = null)
        => new(Options.Create(config), logger ?? NullLogger<GVApiAdapter>.Instance,
               NullLoggerFactory.Instance, cookieRotator: null);

    private static (GvSipTransport Transport, FakeSipWebSocketChannel Channel) NewFakeTransport()
        => GVApiAdapterRecoveryTests.NewFakeTransport();

    private static async Task<GvCookieStore> NewStoreWithCookies(string sapisid)
    {
        var path = Path.Combine(Path.GetTempPath(), "gv-b2-tests",
            Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies(sapisid));
        return store;
    }

    /// <summary>
    /// True when the timer has been disposed. .NET Core returns false from Change() on a disposed
    /// timer where .NET Framework throws — accept either so the assertion is about the disposal,
    /// not the runtime.
    /// </summary>
    private static bool TimerIsDead(Timer timer)
    {
        try { return !timer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { return true; }
    }

    private static GVApiAdapter CreateAdapter()
    {
        var config = Options.Create(new GVBridgeConfig
        {
            GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
            GvApiKey = "test",
            CookieFilePath = "test.enc",
            CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
        });

        return new GVApiAdapter(
            config,
            NullLogger<GVApiAdapter>.Instance,
            NullLoggerFactory.Instance);
    }
}
