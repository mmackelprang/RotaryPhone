using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

/// <summary>
/// Regression tests for docs/plans/gv-auth-first-refresh-anchor-and-cookie-lineage.md — the four
/// defects behind the 83-minute guest-facing outage of 2026-09-08.
///
/// Reuses the reflection scaffolding on <see cref="GVApiAdapterRecoveryTests"/> (CreateAdapter /
/// NewConfig / NewCookies / SetField / Invoke / SetAvailable), which is already shared cross-file.
/// </summary>
public class GVApiAdapterCookieLineageTests
{
    // ------------------------------------------- §2.2 the persisted mint time survives a restart

    [Fact]
    public async Task PsidtsMintTime_SurvivesAProcessRestart_AndIsReportedHonestly()
    {
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));

        // --- process 1: a genuine rotation mints, stamps, and persists ---
        var minted = DateTime.UtcNow.AddMinutes(-42);
        await store.SaveAsync(
            GVApiAdapterRecoveryTests.NewCookies().WithRefreshedPsidts("p1", "p3", minted));

        // --- process 2: a brand-new adapter instance inherits it ---
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);

        Assert.True(await adapter.ReloadCookiesAsync());

        // THE assertion. The mint time crossed the restart boundary intact, so a fresh process can
        // finally know the age of the credential it inherited. On main there is no such field at all.
        Assert.NotNull(adapter.PsidtsMintedAtUtc);
        Assert.True(
            Math.Abs((adapter.PsidtsMintedAtUtc!.Value - minted).TotalSeconds) < 1,
            $"expected the persisted mint time {minted:O}, got {adapter.PsidtsMintedAtUtc:O}");

        // ⚠ FREEZE PIN. psidtsAgeSeconds is a live cross-repo contract (Radio Console binds published
        // bands to it) and its observable behaviour is deliberately unchanged: a mere LOAD still
        // restamps it, so it reads ~0 here for a credential that is genuinely 42 minutes old. That is
        // the documented, frozen lie. Changing this assertion must be a conscious contract decision,
        // not a drive-by "fix" — the honest value is PsidtsMintedAtUtc, asserted above.
        Assert.NotNull(adapter.PsidtsAgeSeconds);
        Assert.InRange(adapter.PsidtsAgeSeconds!.Value, 0, 5);

        File.Delete(path);
    }

    [Fact]
    public async Task LegacyCookieFileWithNoMintTime_LoadsFine_AndReportsMintTimeAsUnknown()
    {
        // Backward compatibility: an existing gv-cookies.enc has neither new field. It must still load
        // (a JsonException here would be swallowed into a null and take the adapter down silently), and
        // it must report UNKNOWN rather than a reassuring small number.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies());   // no timestamps

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);

        Assert.True(await adapter.ReloadCookiesAsync());

        // Unknown is null and null is self-describing — a consumer cannot mistake it for "fresh".
        Assert.Null(adapter.PsidtsMintedAtUtc);

        // ...while the FROZEN field is NOT null after a load, exactly as it has always been.
        Assert.NotNull(adapter.PsidtsAgeSeconds);

        File.Delete(path);
    }

    [Fact]
    public async Task PsidtsAgeSeconds_IsFrozen_AndStillRestampsOnEveryLoad()
    {
        // ⛔ THE GUARD ON A DELIBERATE DECISION. This test's entire job is to FAIL if someone later
        // "corrects" psidtsAgeSeconds to report the true credential age.
        //
        // psidtsAgeSeconds is a published cross-repo contract: Radio Console uses it as a blackout
        // predictor with bands (<660 healthy, 660-1200 blackout) and RotaryPhone promised in writing
        // that it "stays exactly as it is". So the field's observable behaviour is frozen — including
        // the fact that a mere LOAD restamps it and hides a credential's real age.
        //
        // If you are here because this test failed, you did not find a bug; you changed a contract.
        // Ship the honest value under PsidtsMintedAtUtc (already present, already derived from the
        // cookie set) and take the contract change to the consuming repo first.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));

        // A credential minted two days ago — the exact 2026-09-06 -> 2026-09-08 shape.
        var minted = DateTime.UtcNow.AddDays(-2);
        await store.SaveAsync(
            GVApiAdapterRecoveryTests.NewCookies().WithRefreshedPsidts("p1", "p3", minted));

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);

        Assert.True(await adapter.ReloadCookiesAsync());

        // Frozen behaviour: reads ~0 for a two-day-old credential, because the LOAD restamped it.
        Assert.NotNull(adapter.PsidtsAgeSeconds);
        Assert.InRange(adapter.PsidtsAgeSeconds!.Value, 0, 5);

        // ...and the honest field tells the truth about the same credential at the same instant.
        Assert.NotNull(adapter.PsidtsMintedAtUtc);
        Assert.InRange(
            (DateTime.UtcNow - adapter.PsidtsMintedAtUtc!.Value).TotalHours, 47.5, 48.5);

        // Load it a SECOND time: the frozen field restamps again, the mint time does not move.
        var mintedAfterFirstLoad = adapter.PsidtsMintedAtUtc!.Value;
        await Task.Delay(1100);
        Assert.True(await adapter.ReloadCookiesAsync());

        Assert.InRange(adapter.PsidtsAgeSeconds!.Value, 0, 5);          // restamped, as designed
        Assert.Equal(mintedAfterFirstLoad, adapter.PsidtsMintedAtUtc!.Value);   // immovable

        File.Delete(path);
    }
}
