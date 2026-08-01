using System.Diagnostics;
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

    [Fact]
    public async Task ActivateAsync_CallStartsDuringTeardown_KeepsTransportAndReArmsTimers()
    {
        // The TOCTOU the guard used to lose. It reads _activeCallId ONCE and then commits to a
        // teardown whose first step is `await _healthCheckTimer.DisposeAsync()` — which does not
        // complete until an ALREADY-RUNNING callback finishes. That callback is RunHealthCheckAsync,
        // which makes a live HTTP call with a 30-second client timeout, so the window is seconds
        // wide. IncomingCallReceived fires when the INVITE arrives, i.e. while the phone is still
        // RINGING: a call landing in that window used to have its transport disposed out from under
        // it and be silently dropped.
        //
        // This test parks the teardown at exactly that point using a health-check callback that
        // blocks, sets _activeCallId while it is parked, and then releases it.
        var logger = new CapturingLogger<GVApiAdapter>();
        var adapter = CreateAdapter(NoKeyConfig(), logger);
        var (transport, channel) = NewFakeTransport();
        GVApiAdapterRecoveryTests.SetField(adapter, "_sipTransport", transport);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore",
            await NewStoreWithCookies("SAPISID-NEW"));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);

        using var callbackEntered = new ManualResetEventSlim(false);
        using var releaseCallback = new ManualResetEventSlim(false);
        var previousTimer = new Timer(
            _ => { callbackEntered.Set(); releaseCallback.Wait(TimeSpan.FromSeconds(30)); },
            null, dueTime: 0, period: Timeout.Infinite);
        GVApiAdapterRecoveryTests.SetField(adapter, "_healthCheckTimer", previousTimer);
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(30)), "health-check callback never ran");

        var activation = Task.Run(() => adapter.ActivateAsync());

        // Wait until ActivateAsync is demonstrably PAST the guard's _activeCallId check and inside
        // the teardown — otherwise this would race into the mid-call branch instead of the one
        // under test.
        Assert.True(await GVApiAdapterRecoveryTests.WaitForAsync(
            () => logger.Entries.Any(e => e.Message.Contains("GVApiAdapter deactivating"))),
            "teardown never started");

        // ...the phone starts ringing while the teardown is parked on the timer disposal.
        GVApiAdapterRecoveryTests.SetField(adapter, "_activeCallId", "call-mid-teardown");
        releaseCallback.Set();
        await activation;

        // The transport the ringing call depends on survived, untouched.
        Assert.Same(transport, GVApiAdapterRecoveryTests.GetField<GvSipTransport>(adapter, "_sipTransport"));
        Assert.False(GVApiAdapterRecoveryTests.GetField<bool>(transport, "_disposed"));
        Assert.Equal(0, channel.ConnectCount);
        Assert.Equal("call-mid-teardown", GVApiAdapterRecoveryTests.GetField<string>(adapter, "_activeCallId"));

        // The timers — the only thing the abandoned teardown destroyed — were re-armed.
        var health = GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer");
        Assert.NotNull(health);
        Assert.NotSame(previousTimer, health);
        Assert.NotNull(GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_cookieRefreshTimer"));

        // And the refresher's whole point — adopting the new cookies — still happened.
        Assert.Equal("SAPISID-NEW", adapter.CurrentCookieSet!.Sapisid);
        Assert.Contains(logger.Entries, e => e.Message.Contains("re-activation aborted"));
    }

    [Fact]
    public async Task TryDeactivateForReactivation_WithActiveCall_AbortsAndKeepsTransport()
    {
        // The teardown variant in isolation: with a call active it must report failure and leave
        // the transport alone, having disposed only the (restorable) timers.
        var adapter = CreateAdapter(NoKeyConfig());
        var (transport, _) = NewFakeTransport();
        var timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        GVApiAdapterRecoveryTests.SetField(adapter, "_sipTransport", transport);
        GVApiAdapterRecoveryTests.SetField(adapter, "_healthCheckTimer", timer);
        GVApiAdapterRecoveryTests.SetField(adapter, "_activeCallId", "call-1");

        var torndown = await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(
            adapter, "TryDeactivateForReactivationAsync", CancellationToken.None)!;

        Assert.False(torndown);
        Assert.Same(transport, GVApiAdapterRecoveryTests.GetField<GvSipTransport>(adapter, "_sipTransport"));
        Assert.False(GVApiAdapterRecoveryTests.GetField<bool>(transport, "_disposed"));
        Assert.Null(GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer"));

        // ...and StartPeriodicTimers — what the caller runs on abort — puts them back.
        GVApiAdapterRecoveryTests.Invoke(adapter, "StartPeriodicTimers");
        Assert.NotNull(GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer"));
        Assert.NotNull(GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_cookieRefreshTimer"));
    }

    [Fact]
    public async Task ActivateAsync_TwoFullActivations_LeaveExactlyOneTransportAndOneOfEachTimer()
    {
        // ACCEPTANCE CRITERION #6 — the hard merge gate (design §8.5). After two
        // refresh-from-browser calls, exactly ONE live health-check timer and ONE live
        // GvSipTransport must remain, and the new cookies must still be adopted.
        //
        // The sibling re-entrancy tests deliberately abort at key resolution, so they prove the
        // ORPHAN is disposed but not that a fresh generation is REBUILT. This one runs two genuine,
        // complete activations.
        //
        // Hermetic, and it must stay that way:
        //  - HealthProbeOverride makes the health probe offline and deterministic;
        //  - the cookie store is a real temp file with a real key, so key resolution passes;
        //  - GvApiBaseUrl points at an unroutable loopback port, so the ONE remaining impurity —
        //    EnsureRegisteredAsync's sipregisterinfo/get — fails with connection-refused instantly.
        //    ActivateAsync catches that ("SIP registration failed — will retry on first call"), so
        //    activation still completes normally. No network, no sleeping on real time.
        var cookiePath = Path.Combine(Path.GetTempPath(), "gv-b2-tests",
            Guid.NewGuid().ToString("n") + ".enc");
        var key = Convert.ToBase64String(new byte[32]);
        var store = new GvCookieStore(cookiePath, key);
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-GEN1"));

        var config = new GVBridgeConfig
        {
            GvApiBaseUrl = "http://127.0.0.1:1/voice/v1/voiceclient",
            GvApiKey = "test",
            CookieFilePath = cookiePath,
            CookieKeyFilePath = "",
            CookieEncryptionKey = key,
        };

        var logger = new CapturingLogger<GVApiAdapter>();
        using var adapter = CreateAdapter(config, logger);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);

        var sw = Stopwatch.StartNew();

        // --- activation #1 (the first refresh-from-browser) ---
        await adapter.ActivateAsync();

        var transport1 = GVApiAdapterRecoveryTests.GetField<GvSipTransport>(adapter, "_sipTransport");
        var health1 = GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer");
        var refresh1 = GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_cookieRefreshTimer");
        Assert.NotNull(transport1);
        Assert.NotNull(health1);
        Assert.NotNull(refresh1);
        Assert.True(adapter.IsAvailable);
        Assert.Equal("SAPISID-GEN1", adapter.CurrentCookieSet!.Sapisid);

        // --- activation #2 (the second refresh-from-browser, on the LIVE adapter) ---
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-GEN2"));
        await adapter.ActivateAsync();
        sw.Stop();

        var transport2 = GVApiAdapterRecoveryTests.GetField<GvSipTransport>(adapter, "_sipTransport");
        var health2 = GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_healthCheckTimer");
        var refresh2 = GVApiAdapterRecoveryTests.GetField<Timer>(adapter, "_cookieRefreshTimer");

        // Exactly one live transport: a NEW one, with the first generation's disposed.
        Assert.NotNull(transport2);
        Assert.NotSame(transport1, transport2);
        Assert.True(GVApiAdapterRecoveryTests.GetField<bool>(transport1, "_disposed"));
        Assert.False(GVApiAdapterRecoveryTests.GetField<bool>(transport2, "_disposed"));

        // Exactly one live timer of each kind, likewise rebuilt with the first generation's dead.
        Assert.NotNull(health2);
        Assert.NotSame(health1, health2);
        Assert.True(TimerIsDead(health1));
        Assert.NotNull(refresh2);
        Assert.NotSame(refresh1, refresh2);
        Assert.True(TimerIsDead(refresh1));
        // Checked last: probing liveness disarms them, which is fine at end of test.
        Assert.False(TimerIsDead(health2));
        Assert.False(TimerIsDead(refresh2));

        // The refresher's contract still works — generation 2 adopted the new cookie set.
        Assert.Equal("SAPISID-GEN2", adapter.CurrentCookieSet!.Sapisid);
        Assert.True(adapter.IsAvailable);
        Assert.Contains(logger.Entries,
            e => e.Message.Contains("re-activating — tearing down the previous generation first"));

        // Guard the hermeticity claim: a real network attempt would either succeed (and do real
        // work) or block on the 30-second HttpClient timeout. Two full activations offline are
        // sub-second.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"two activations took {sw.Elapsed} — did this reach the network?");
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
