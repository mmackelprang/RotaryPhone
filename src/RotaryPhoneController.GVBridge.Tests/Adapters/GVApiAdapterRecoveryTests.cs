using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Sip;
using RotaryPhoneController.GVBridge.Tests.Sip;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

/// <summary>
/// Covers the B2 auth-blackout fix (docs/plans/gv-auth-blackout-b2-design.md):
///  - Task 1: the recovery ladder is awaitable, single-flight via a SHARED task, reports its
///    outcome, arms a failure-only cooldown, and marks the adapter available again on success.
///
/// Every test drives the adapter through its internal seams — an injected <see cref="ICookieRotator"/>
/// and the <c>HealthProbeOverride</c> health-probe seam — so the ladder is exercised end-to-end
/// WITHOUT talking to Google. Nothing here sleeps for real time or opens a socket.
/// </summary>
public class GVApiAdapterRecoveryTests
{
    // ---------------------------------------------------------------- Task 1: awaitable ladder

    [Fact]
    public async Task TryRecoverAuthAsync_ConcurrentCallers_ShareOneRun()
    {
        // During a blackout the poller and several RadioConsole requests hit 401 within
        // milliseconds. They must all ride ONE refresh, not stampede RotateCookies.
        var gate = new TaskCompletionSource<CookieRotationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotator = new FakeCookieRotator(_ => gate.Task);
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());

        var first = adapter.TryRecoverAuthAsync("first");
        var second = adapter.TryRecoverAuthAsync("second");

        Assert.Same(first, second);
        Assert.Equal(1, rotator.Calls);

        gate.SetResult(new CookieRotationResult(true, "psidts1-new", "psidts3-new"));

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, rotator.Calls);   // the ladder body ran exactly once
    }

    [Fact]
    public async Task TryRecoverAuthAsync_ReturnsTrue_WhenRungSucceeds()
    {
        var rotator = new FakeCookieRotator(
            _ => Task.FromResult(new CookieRotationResult(true, "psidts1-new", "psidts3-new")));
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());

        Assert.True(await adapter.TryRecoverAuthAsync("rung-1 success"));
    }

    [Fact]
    public async Task TryRecoverAuthAsync_ReturnsFalse_WhenAllRungsFail()
    {
        // Rung 1 does not rotate, rung 2 has no cookie store, rung 3 has no CDP extractor.
        var adapter = CreateAdapter(new FakeCookieRotator(_ => Task.FromResult(CookieRotationResult.NotRotated)));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());

        Assert.False(await adapter.TryRecoverAuthAsync("all rungs fail"));
    }

    [Fact]
    public async Task TryRecoverAuthAsync_FailureArmsCooldown()
    {
        // A real Google outage 401s every poll. Without the failure-only cooldown that would drive
        // RotateCookies at the poll rate — the shape of the 2026-06-19 storm.
        var rotator = new FakeCookieRotator(_ => Task.FromResult(CookieRotationResult.NotRotated));
        var adapter = CreateAdapter(rotator, NewConfig(cooldownSeconds: 60));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());

        Assert.False(await adapter.TryRecoverAuthAsync("first failure"));
        Assert.Equal(1, rotator.Calls);

        Assert.False(await adapter.TryRecoverAuthAsync("immediately after"));
        Assert.Equal(1, rotator.Calls);   // suppressed — the ladder did NOT run again
    }

    [Fact]
    public async Task TryRecoverAuthAsync_SuccessDoesNotArmCooldown()
    {
        var rotator = new FakeCookieRotator(
            _ => Task.FromResult(new CookieRotationResult(true, "p1", "p3")));
        var adapter = CreateAdapter(rotator, NewConfig(cooldownSeconds: 600));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());

        Assert.True(await adapter.TryRecoverAuthAsync("first"));
        Assert.True(await adapter.TryRecoverAuthAsync("second"));
        Assert.Equal(2, rotator.Calls);   // success arms nothing
    }

    [Fact]
    public async Task TryRecoverAuthAsync_Success_SetsAvailable()
    {
        // The deferred PR1 review HIGH-2: GetAuthenticatedClient() gates on IsAvailable, so a
        // successful rotation that left IsAvailable=false would silently defeat the read retry.
        var rotator = new FakeCookieRotator(
            _ => Task.FromResult(new CookieRotationResult(true, "p1", "p3")));
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());
        Assert.False(adapter.IsAvailable);

        Assert.True(await adapter.TryRecoverAuthAsync("high-2"));

        Assert.True(adapter.IsAvailable);
        Assert.NotNull(((IGvAuthenticatedClientProvider)adapter).GetAuthenticatedClient());
    }

    [Fact]
    public void TriggerCookieRecovery_StillFireAndForget()
    {
        // The SIP AuthenticationFailed handler must not block on the ladder.
        var gate = new TaskCompletionSource<CookieRotationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = CreateAdapter(new FakeCookieRotator(_ => gate.Task));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());

        Invoke(adapter, "TriggerCookieRecovery", "sip auth failed");

        // Returned while the ladder is still suspended inside RotateAsync.
        Assert.False(gate.Task.IsCompleted);
        Assert.True(IsRecoveryInFlight(adapter));

        gate.SetResult(CookieRotationResult.NotRotated);
    }

    // ------------------------------------------------- Task 4: real proactive PSIDTS refresh

    [Fact]
    public void Config_CookieRefreshIntervalMinutes_IsRead()
    {
        // Regression guard for F1: this knob was declared and read by NOTHING. Binding it must now
        // produce a real timer — and the spec's decided default is 8 minutes.
        Assert.Equal(8, new GVBridgeConfig().CookieRefreshIntervalMinutes);

        var adapter = CreateAdapter(config: NewConfig(refreshIntervalMinutes: 8));
        Invoke(adapter, "StartPeriodicTimers");

        Assert.NotNull(GetField<Timer>(adapter, "_cookieRefreshTimer"));
        Assert.NotNull(GetField<Timer>(adapter, "_healthCheckTimer"));
    }

    [Fact]
    public void ProactiveRefresh_IntervalZero_InstallsNoTimer()
    {
        // Kill switch: restores today's behaviour without a redeploy.
        var adapter = CreateAdapter(config: NewConfig(refreshIntervalMinutes: 0));
        Invoke(adapter, "StartPeriodicTimers");

        Assert.Null(GetField<Timer>(adapter, "_cookieRefreshTimer"));
        Assert.NotNull(GetField<Timer>(adapter, "_healthCheckTimer"));   // watchdog is unaffected
    }

    [Fact]
    public async Task ProactiveRefresh_SkipsWhenThrottled()
    {
        // A 603/403 account cooldown means STOP TALKING TO GOOGLE.
        var rotator = new FakeCookieRotator(_ => Task.FromResult(new CookieRotationResult(true, "p1", "p3")));
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());
        SetAvailable(adapter, true);
        var (transport, _) = NewFakeTransport();
        Throttle(transport);
        SetField(adapter, "_sipTransport", transport);

        await RunProactiveRefresh(adapter);

        Assert.Equal(0, rotator.Calls);
    }

    [Fact]
    public async Task ProactiveRefresh_SkipsWhenRecoveryInFlight()
    {
        // The proactive tick shares the reactive single-flight guard, so it can never race a recovery.
        var gate = new TaskCompletionSource<CookieRotationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rotator = new FakeCookieRotator(_ => gate.Task);
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());
        SetAvailable(adapter, true);

        var recovery = adapter.TryRecoverAuthAsync("reactive");
        Assert.Equal(1, rotator.Calls);
        Assert.True(IsRecoveryInFlight(adapter));

        await RunProactiveRefresh(adapter);

        Assert.Equal(1, rotator.Calls);   // the tick stood down

        gate.SetResult(CookieRotationResult.NotRotated);
        await recovery;
    }

    [Fact]
    public async Task ProactiveRefresh_SkipsWhenRotationDisabled()
    {
        var rotator = new FakeCookieRotator(_ => Task.FromResult(new CookieRotationResult(true, "p1", "p3")));
        var adapter = CreateAdapter(rotator, NewConfig(enableCookieRotation: false));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());
        SetAvailable(adapter, true);

        await RunProactiveRefresh(adapter);

        Assert.Equal(0, rotator.Calls);
    }

    [Fact]
    public async Task ProactiveRefresh_DoesNotReRegister()
    {
        // A successful PSIDTS rotation does NOT invalidate a live SIP registration. Re-registering
        // every 8 minutes would re-create the 2026-06-19 REGISTER-storm risk (spec §4.1).
        var rotator = new FakeCookieRotator(_ => Task.FromResult(new CookieRotationResult(true, "p1", "p3")));
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());
        SetAvailable(adapter, true);
        var (transport, channel) = NewFakeTransport();
        SetField(adapter, "_sipTransport", transport);

        await RunProactiveRefresh(adapter);

        Assert.Equal(1, rotator.Calls);        // it DID rotate
        Assert.Equal(0, channel.ConnectCount); // and did NOT re-register
        Assert.Empty(channel.Sends);
    }

    // --------------------------------------- Task 5: health derived from the last REAL call

    [Fact]
    public void AuthBlackout_TrueAfterAuthFailure()
    {
        // The 2026-07-31 blackout reported cookiesValid:true while api2thread/list was 401ing,
        // because the probe had last run up to 30 minutes earlier against a DIFFERENT endpoint.
        //
        // A REGISTERED transport is attached deliberately. Degraded is
        // IsAvailable && !(AreCookiesValid && IsRegistered), so with no transport at all it is
        // already true before RecordApiOutcome runs and asserting it afterwards proves nothing.
        // With a registered transport the adapter starts provably healthy, and the flip to
        // degraded is attributable to the auth blackout alone — which is acceptance criterion 3.
        var adapter = CreateAdapter();
        var (transport, _) = NewRegisteredTransport();
        SetField(adapter, "_sipTransport", transport);
        SetField(adapter, "_areCookiesValid", true);
        SetAvailable(adapter, true);
        Assert.False(adapter.AuthBlackout);
        Assert.True(adapter.AreCookiesValid);
        Assert.False(adapter.Degraded);          // provably healthy BEFORE the outcome is recorded

        adapter.RecordApiOutcome(success: false, authFailure: true);

        Assert.True(adapter.AuthBlackout);
        Assert.False(adapter.AreCookiesValid);   // cookiesValid goes false on the FIRST real rejection
        Assert.True(adapter.Degraded);           // degraded follows for free
        Assert.True(transport.IsRegistered);     // ...and NOT because SIP dropped
        Assert.NotNull(adapter.LastApiAuthFailureAt);
    }

    [Fact]
    public void AuthBlackout_ClearsAfterSuccess()
    {
        var adapter = CreateAdapter();
        SetField(adapter, "_areCookiesValid", true);
        SetAvailable(adapter, true);

        adapter.RecordApiOutcome(success: false, authFailure: true);
        Assert.True(adapter.AuthBlackout);

        adapter.RecordApiOutcome(success: true, authFailure: false);

        Assert.False(adapter.AuthBlackout);
        Assert.True(adapter.AreCookiesValid);
        Assert.NotNull(adapter.LastApiSuccessAt);
    }

    [Fact]
    public void AuthBlackout_NotSetByNonAuthFailure()
    {
        // A 429 or a 5xx is not an auth blackout. Throttling is falsified for this defect.
        var adapter = CreateAdapter();
        SetField(adapter, "_areCookiesValid", true);
        SetAvailable(adapter, true);

        adapter.RecordApiOutcome(success: false, authFailure: false);

        Assert.False(adapter.AuthBlackout);
        Assert.True(adapter.AreCookiesValid);
        Assert.Null(adapter.LastApiAuthFailureAt);
    }

    [Fact]
    public void Available_StaysTrueDuringBlackout()
    {
        // The deliberate deviation from RadioConsole's literal ask (spec §4.3, §8.3), locked in by a
        // test: IsAvailable gates GetAuthenticatedClient(), so flipping it during a transient
        // data-plane 401 would make the adapter refuse its OWN recovery retry — turning a ~9-minute
        // blackout into a hard stop. degraded / authBlackout carry the fact instead.
        // A REGISTERED transport is attached for the same reason as in
        // AuthBlackout_TrueAfterAuthFailure: without one, Degraded is already true before
        // RecordApiOutcome runs, so asserting it afterwards would prove nothing. The point of
        // THIS test is the contrast — degraded flips, available does NOT.
        var adapter = CreateAdapter();
        var (transport, _) = NewRegisteredTransport();
        SetField(adapter, "_sipTransport", transport);
        SetField(adapter, "_areCookiesValid", true);
        SetAvailable(adapter, true);
        Assert.True(adapter.IsAvailable);
        Assert.False(adapter.Degraded);          // provably healthy BEFORE the outcome is recorded

        adapter.RecordApiOutcome(success: false, authFailure: true);

        Assert.True(adapter.IsAvailable);        // THE DEVIATION: available stays true, by design
        Assert.True(adapter.AuthBlackout);
        Assert.True(adapter.Degraded);           // degraded is what carries the fact instead

        // The reason available must stay true: the seam that hands out the authenticated client
        // gates on it, so flipping it would make the adapter refuse its own recovery retry.
        SetField(adapter, "_httpClient", new HttpClient());
        Assert.NotNull(((IGvAuthenticatedClientProvider)adapter).GetAuthenticatedClient());
    }

    [Fact]
    public async Task Deactivate_ClearsDataPlaneOutcomeTimestamps()
    {
        // The outcome timestamps are per-generation. Left set, a stale authBlackout:true carries
        // from a torn-down generation into a freshly re-activated one and misleads the dashboard
        // until the next real GV call happens to land.
        var adapter = CreateAdapter();
        SetField(adapter, "_areCookiesValid", true);
        SetAvailable(adapter, true);
        adapter.RecordApiOutcome(success: false, authFailure: true);
        Assert.True(adapter.AuthBlackout);

        await adapter.DeactivateAsync();

        Assert.False(adapter.AuthBlackout);
        Assert.Null(adapter.LastApiAuthFailureAt);
        Assert.Null(adapter.LastApiSuccessAt);
    }

    // ------------------------------------------------------- F7: the periodic health watchdog
    //
    // RunHealthCheckAsync is the ONLY timed entry into the recovery ladder, and F7 is the headline
    // finding of this work — but until now nothing exercised it. It is reachable offline because
    // ProbeHealthAsync routes through HealthProbeOverride; its early-out is
    // `_accountClient == null && HealthProbeOverride == null`, which no longer short-circuits past
    // that seam.

    [Fact]
    public async Task Watchdog_ProbeFails_MarksUnavailableAndRunsRecoveryLadder()
    {
        // Google rejected the cookies → availability goes false and the full ladder runs.
        var rotator = new FakeCookieRotator(_ => Task.FromResult(CookieRotationResult.NotRotated));
        var adapter = CreateAdapter(rotator);
        adapter.HealthProbeOverride = _ => Task.FromResult(false);
        SetField(adapter, "_cookieSet", NewCookies());
        SetField(adapter, "_areCookiesValid", true);
        SetAvailable(adapter, true);

        await RunHealthCheck(adapter);
        await AwaitRecoveryAsync(adapter);

        Assert.False(adapter.IsAvailable);
        Assert.False(adapter.AreCookiesValid);
        Assert.NotNull(adapter.LastValidatedAt);
        Assert.Equal(1, rotator.Calls);          // the ladder really was entered
        Assert.Null(adapter.LastHealthyAt);
    }

    [Fact]
    public async Task Watchdog_ProbeOkAndRegistered_RecordsHealthyAndMarksAvailable()
    {
        var adapter = CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        var (transport, channel) = NewRegisteredTransport();
        SetField(adapter, "_sipTransport", transport);
        Assert.False(adapter.IsAvailable);
        Assert.Null(adapter.LastHealthyAt);

        await RunHealthCheck(adapter);

        Assert.NotNull(adapter.LastHealthyAt);
        Assert.True(adapter.IsAvailable);        // recovered from a previous down state
        Assert.True(adapter.AreCookiesValid);
        Assert.False(adapter.Degraded);
        Assert.Empty(channel.Sends);             // already registered — no REGISTER was forced
    }

    [Fact]
    public async Task Watchdog_ProbeOkNotRegistered_ForcesReRegister()
    {
        // Positive control for the two "does NOT re-register" tests below — without it they could
        // pass for the wrong reason. This is the 2026-06-19 shape: cookies fine, SIP stuck.
        var logger = new CapturingLogger<GVApiAdapter>();
        var adapter = CreateAdapter(logger: logger);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        var (transport, channel) = NewFakeTransport();
        channel.FailNextSend = true;             // let the forced REGISTER fail fast, not hang
        SetField(adapter, "_sipTransport", transport);
        SetAvailable(adapter, true);

        await RunHealthCheck(adapter);

        Assert.Contains(logger.Entries,
            e => e.Message.Contains("cookies valid but SIP not registered, forcing re-register"));
        // ForceReRegisterAsync is fire-and-forget, so poll rather than assert instantly.
        Assert.True(await WaitForAsync(() => channel.ConnectCount == 1));
    }

    [Fact]
    public async Task Watchdog_ProbeOkButThrottled_DoesNotForceReRegister()
    {
        // The 2026-06-19 storm guard: re-registering inside a 603/403 account cooldown re-arms the
        // storm every health-check interval. The transport's own gate would suppress the REGISTER,
        // but the ADAPTER must stand down first — hence the assertion on the deferral log.
        var logger = new CapturingLogger<GVApiAdapter>();
        var adapter = CreateAdapter(logger: logger);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        var (transport, channel) = NewFakeTransport();
        Throttle(transport);
        SetField(adapter, "_sipTransport", transport);
        SetAvailable(adapter, true);

        await RunHealthCheck(adapter);

        Assert.Contains(logger.Entries,
            e => e.Message.Contains("deferring re-register: throttle cooldown active"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("forcing re-register"));
        Assert.Equal(0, channel.ConnectCount);
        Assert.Empty(channel.Sends);
        Assert.True(adapter.Degraded);           // and it says so honestly
    }

    [Fact]
    public async Task Watchdog_ProbeOkNotRegisteredRecoveryInFlight_DoesNotForceReRegister()
    {
        // A ladder run already in flight re-registers on its own once a rung succeeds; a second
        // forced REGISTER from the watchdog would just double up on Google.
        var gate = new TaskCompletionSource<CookieRotationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CapturingLogger<GVApiAdapter>();
        var adapter = CreateAdapter(new FakeCookieRotator(_ => gate.Task), logger: logger);
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        SetField(adapter, "_cookieSet", NewCookies());
        var (transport, channel) = NewFakeTransport();
        SetField(adapter, "_sipTransport", transport);
        SetAvailable(adapter, true);

        var recovery = adapter.TryRecoverAuthAsync("reactive");
        Assert.True(IsRecoveryInFlight(adapter));

        await RunHealthCheck(adapter);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("forcing re-register"));
        Assert.Equal(0, channel.ConnectCount);

        gate.SetResult(CookieRotationResult.NotRotated);
        await recovery;
    }

    // ---------------------------------------------------------------- shared test scaffolding

    internal static Task RunProactiveRefresh(GVApiAdapter adapter)
        => (Task)Invoke(adapter, "RunProactiveCookieRefreshAsync")!;

    internal static Task RunHealthCheck(GVApiAdapter adapter)
        => (Task)Invoke(adapter, "RunHealthCheckAsync")!;

    /// <summary>
    /// Awaits the ladder run the watchdog kicked off fire-and-forget, so assertions about it are
    /// deterministic rather than a race against a background task.
    /// </summary>
    internal static async Task AwaitRecoveryAsync(GVApiAdapter adapter)
    {
        if (GetField<Task<bool>>(adapter, "_recoveryTask") is { } task)
            await task;
    }

    internal static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    internal static (GvSipTransport Transport, FakeSipWebSocketChannel Channel) NewFakeTransport()
    {
        var channel = new FakeSipWebSocketChannel();
        var transport = new GvSipTransport(
            NullLogger<GvSipTransport>.Instance,
            () => Task.FromResult(new SipCredentials(
                SipUsername: "sip-token", BearerToken: "crypto-key",
                PhoneNumber: "+15551234567", ExpirySeconds: 3600)),
            loggerFactory: null,
            channelFactory: (_, _) => channel,
            options: null,
            timeProvider: null);
        return (transport, channel);
    }

    /// <summary>
    /// A fake transport that reports <c>IsRegistered == true</c> (which is <c>_registered</c> AND a
    /// connected channel) without driving a real REGISTER exchange. Needed wherever a test must
    /// watch <c>Degraded</c> FLIP: with no transport attached, Degraded is already true and the
    /// post-condition assertion would be vacuous.
    /// </summary>
    internal static (GvSipTransport Transport, FakeSipWebSocketChannel Channel) NewRegisteredTransport()
    {
        var (transport, channel) = NewFakeTransport();
        channel.ConnectAsync().GetAwaiter().GetResult();
        SetField(transport, "_wsChannel", channel);
        SetField(transport, "_registered", true);
        Assert.True(transport.IsRegistered);
        return (transport, channel);
    }

    /// <summary>Puts the transport into a 603/403 cooldown without driving a real REGISTER exchange.</summary>
    internal static void Throttle(GvSipTransport transport)
    {
        SetField(transport, "_throttledUntilTimestamp",
            Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3600);
        SetField(transport, "_throttledUntilUtc", DateTime.UtcNow.AddHours(1));
        SetField(transport, "_throttleReason", "test cooldown");
        Assert.True(transport.IsThrottled);
    }


    internal static GVBridgeConfig NewConfig(int cooldownSeconds = 60, int refreshIntervalMinutes = 8,
        bool enableCookieRotation = true) => new()
        {
            GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
            GvApiKey = "test",
            CookieFilePath = "test.enc",
            CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
            AuthRecoveryFailureCooldownSeconds = cooldownSeconds,
            CookieRefreshIntervalMinutes = refreshIntervalMinutes,
            EnableCookieRotation = enableCookieRotation,
        };

    internal static GVApiAdapter CreateAdapter(
        ICookieRotator? rotator = null,
        GVBridgeConfig? config = null,
        ILogger<GVApiAdapter>? logger = null)
        => new(Options.Create(config ?? NewConfig()),
               logger ?? NullLogger<GVApiAdapter>.Instance,
               NullLoggerFactory.Instance,
               rotator);

    internal static GvCookieSet NewCookies(string sapisid = "SAPISID-A") => new()
    {
        Sapisid = sapisid,
        Sid = "sid",
        Hsid = "hsid",
        Ssid = "ssid",
        Apisid = "apisid",
        Secure1Psid = "secure-1psid",
        Secure3Psid = "secure-3psid",
    };

    internal static void SetField(object target, string name, object? value)
        => FieldOf(target, name).SetValue(target, value);

    internal static T? GetField<T>(object target, string name)
        => (T?)FieldOf(target, name).GetValue(target);

    private static FieldInfo FieldOf(object target, string name)
    {
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f is not null) return f;
        }
        throw new InvalidOperationException($"Field {name} not found on {target.GetType().Name}");
    }

    internal static object? Invoke(object target, string method, params object?[] args)
        => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    internal static bool IsRecoveryInFlight(GVApiAdapter adapter)
        => (bool)typeof(GVApiAdapter)
            .GetProperty("IsRecoveryInFlight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(adapter)!;

    internal static void SetAvailable(GVApiAdapter adapter, bool available)
        => Invoke(adapter, "SetAvailable", available);

    /// <summary>Counts rotate attempts and lets a test suspend one mid-ladder.</summary>
    internal sealed class FakeCookieRotator(Func<GvCookieSet, Task<CookieRotationResult>> impl)
        : ICookieRotator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<CookieRotationResult> RotateAsync(GvCookieSet current, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return impl(current);
        }
    }
}
