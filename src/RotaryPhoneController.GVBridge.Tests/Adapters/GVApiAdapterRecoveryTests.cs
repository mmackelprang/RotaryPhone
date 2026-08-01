using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
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

    // ---------------------------------------------------------------- shared test scaffolding

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
