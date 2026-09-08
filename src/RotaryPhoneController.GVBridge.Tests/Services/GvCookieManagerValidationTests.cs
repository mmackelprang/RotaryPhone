using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Adapters;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

/// <summary>
/// Task 5 of docs/plans/gv-auth-first-refresh-anchor-and-cookie-lineage.md — the cron path.
///
/// The box-side cron POSTs /api/gvbridge/cookies/refresh-from-browser every 20 minutes, landing in
/// <see cref="GvCookieManager.SetCookiesAsync"/>. That method used to save unconditionally on its
/// first statement and then return true if nothing threw — and ActivateCoreAsync handles a failed
/// probe with SetAvailable(false) and a plain return rather than an exception. So it reported success
/// for cookie sets Google had just rejected, every 20 minutes, for two days.
/// </summary>
public class GvCookieManagerValidationTests
{
    private static (GvCookieManager Manager, GVApiAdapter Adapter, GvCookieStore Store, string Path)
        NewHotPathManager(bool probePasses)
    {
        var path = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".enc");
        var config = GVApiAdapterRecoveryTests.NewConfig();
        config.CookieFilePath = path;
        config.CookieEncryptionKey = Convert.ToBase64String(new byte[32]);

        var store = new GvCookieStore(path, config.CookieEncryptionKey);
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(config: config);
        adapter.HealthProbeOverride = _ => Task.FromResult(probePasses);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD"));
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);

        var manager = new GvCookieManager(
            Options.Create(config), adapter, new Mock<ICallAdapterRegistry>().Object,
            NullLogger<GvCookieManager>.Instance);

        return (manager, adapter, store, path);
    }

    [Fact]
    public async Task SetCookiesAsync_GoodCookiesHeld_DeadOnesOffered_ReturnsFalseAndKeepsTheGoodSet()
    {
        // The 20-minute cron's exact shape. On main this returns TRUE — SwitchModeAsync does not throw
        // on a failed probe — and the controller then logs "extracted and activated" at INF. That is how
        // two days of total failure produced no warning at all.
        var (manager, adapter, store, path) = NewHotPathManager(probePasses: false);
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD"));

        var saved = await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"));

        Assert.False(saved);                                              // fails on main: returns true
        Assert.Equal("SAPISID-GOOD", adapter.CurrentCookieSet!.Sapisid);  // in-memory set rolled back
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-GOOD", onDisk!.Sapisid);                    // fails on main: SAPISID-DEAD

        File.Delete(path);
    }

    [Fact]
    public async Task SetCookiesAsync_GoodCookiesHeld_WorkingOnesOffered_AdoptsAndPersistsThem()
    {
        // The paired positive: the guard must not block a genuine refresh.
        var (manager, adapter, store, path) = NewHotPathManager(probePasses: true);
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD"));

        var saved = await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-FRESH"));

        Assert.True(saved);
        Assert.Equal("SAPISID-FRESH", adapter.CurrentCookieSet!.Sapisid);
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-FRESH", onDisk!.Sapisid);
        Assert.NotNull(onDisk.BrowserSessionValidatedAtUtc);

        File.Delete(path);
    }

    [Fact]
    public async Task SetCookiesAsync_ColdStart_StillSavesSoTheBootstrapPathKeepsWorking()
    {
        // With no validated set to protect, an unproven save is correct — otherwise a box with no
        // cookies at all could never be seeded, and the recovery procedure in KNOWN-ISSUES would break.
        var path = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".enc");
        var keyPath = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".bin");
        var config = GVApiAdapterRecoveryTests.NewConfig();
        config.CookieFilePath = path;
        config.CookieKeyFilePath = keyPath;
        config.CookieEncryptionKey = Convert.ToBase64String(new byte[32]);

        // A never-activated adapter: CurrentCookieSet is null, so the cold path must be taken.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(config: config);
        var registry = new Mock<ICallAdapterRegistry>();
        registry.Setup(r => r.SwitchModeAsync(It.IsAny<CallAdapterMode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var manager = new GvCookieManager(
            Options.Create(config), adapter, registry.Object, NullLogger<GvCookieManager>.Instance);

        await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-SEED"));

        // The seed reached disk even though nothing validated it — that is the intended cold-path
        // behaviour. (The return value is false here because the un-activated adapter reports
        // AreCookiesValid == false; the assertion under test is that the file was written.)
        var store = new GvCookieStore(path, config.CookieEncryptionKey);
        var onDisk = await store.LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Equal("SAPISID-SEED", onDisk!.Sapisid);

        File.Delete(path);
    }

    [Fact]
    public async Task SetCookiesAsync_ColdStart_ReportsFalseWhenGoogleRejectsTheSeed()
    {
        // The second half of defect 3b. The old code returned true whenever SwitchModeAsync did not
        // throw, and ActivateCoreAsync handles a failed probe with SetAvailable(false) and a plain
        // return — so "saved and activated" was reported for a set Google had already refused. The
        // return value now tracks whether the cookies WORK.
        var path = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".enc");
        var keyPath = Path.Combine(Path.GetTempPath(), "gv-mgr-tests", Guid.NewGuid().ToString("n") + ".bin");
        var config = GVApiAdapterRecoveryTests.NewConfig();
        config.CookieFilePath = path;
        config.CookieKeyFilePath = keyPath;
        config.CookieEncryptionKey = Convert.ToBase64String(new byte[32]);

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(config: config);
        var registry = new Mock<ICallAdapterRegistry>();
        registry.Setup(r => r.SwitchModeAsync(It.IsAny<CallAdapterMode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);   // does NOT throw — exactly the two-day shape
        var manager = new GvCookieManager(
            Options.Create(config), adapter, registry.Object, NullLogger<GvCookieManager>.Instance);

        var saved = await manager.SetCookiesAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"));

        Assert.False(saved);   // fails on main: returns true for a cookie set Google rejected

        File.Delete(path);
    }
}
