using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Support;
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
    // -------------------------------------------------- §2.1 ⭐ the restart-simulation tests
    //
    // Defect 1 is INVISIBLE in a long-running process: each rotation resets the timer and mints the
    // credential in the same instant, so the two stay locked. ONLY A RESTART decouples them. A test
    // that does not simulate a restart passes against the bug and proves nothing.

    [Theory]
    // A process that loads a PSIDTS of age N must schedule its FIRST refresh at (interval - N).
    [InlineData(0,       480_000)]  // brand new            -> a full interval
    [InlineData(55_000,  425_000)]  // THE 2026-09-08 SHAPE -> 7m05s, not 8m00s. Off by 52s = the outage.
    [InlineData(420_000,  60_000)]  // 7 min old (KNOWN-ISSUES L2) -> 1 min, not 8
    [InlineData(479_000,   5_000)]  // 7m59s old            -> the floor, not 1s
    [InlineData(600_000,   5_000)]  // already past due     -> the floor, never negative
    public void ComputeFirstRefreshDelay_AnchorsToInheritedCredentialAge(int ageMs, int expectedMs)
    {
        var now = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);

        var delay = GVApiAdapter.ComputeFirstRefreshDelayMs(
            refreshIntervalMs: 8 * 60 * 1000, psidtsMintedAtUtc: now.AddMilliseconds(-ageMs), nowUtc: now);

        Assert.Equal(expectedMs, delay);
    }

    [Fact]
    public void ComputeFirstRefreshDelay_UnknownMintTime_RefreshesAtTheFloor()
    {
        // A credential we cannot date must NOT be trusted for a full interval — that is the bug.
        var delay = GVApiAdapter.ComputeFirstRefreshDelayMs(
            8 * 60 * 1000, psidtsMintedAtUtc: null, nowUtc: DateTime.UtcNow);

        Assert.Equal(GVApiAdapter.MinFirstRefreshDelayMs, delay);
    }

    [Fact]
    public void ComputeFirstRefreshDelay_MintTimeInTheFuture_IsClampedToOneInterval()
    {
        // Clock skew or a restored backup must not push the first refresh past a full interval.
        var now = new DateTime(2026, 9, 8, 18, 1, 0, DateTimeKind.Utc);

        var delay = GVApiAdapter.ComputeFirstRefreshDelayMs(
            8 * 60 * 1000, psidtsMintedAtUtc: now.AddHours(3), nowUtc: now);

        Assert.Equal(8 * 60 * 1000, delay);
    }

    [Fact]
    public void StartPeriodicTimers_OnRestart_SchedulesFromThePersistedMintTime_NotFromProcessStart()
    {
        // THE RESTART SIMULATION, end to end through the production wiring.
        // A brand-new adapter instance stands in for a brand-new process: it knows nothing about the
        // credential except what the cookie set carries.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(
            config: GVApiAdapterRecoveryTests.NewConfig(refreshIntervalMinutes: 8));

        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", new GvCookieSet
        {
            Sapisid = "SAPISID-A", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
            PsidtsMintedAtUtc = DateTime.UtcNow.AddMinutes(-7),   // inherited, 7 minutes old
        });

        GVApiAdapterRecoveryTests.Invoke(adapter, "StartPeriodicTimers");

        // ~1 minute remains of the 8-minute interval.
        Assert.NotNull(adapter.LastFirstRefreshDelayMs);
        Assert.InRange(adapter.LastFirstRefreshDelayMs!.Value, 55_000, 65_000);

        // The assertion that fails on main: main schedules a FULL interval regardless of age.
        Assert.NotEqual(8 * 60 * 1000, adapter.LastFirstRefreshDelayMs!.Value);
    }

    [Fact]
    public void StartPeriodicTimers_FreshCredential_StillUsesTheFullInterval()
    {
        // The paired negative: the fix must not turn every activation into an immediate rotation.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(
            config: GVApiAdapterRecoveryTests.NewConfig(refreshIntervalMinutes: 8));

        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", new GvCookieSet
        {
            Sapisid = "SAPISID-A", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
            PsidtsMintedAtUtc = DateTime.UtcNow,
        });

        GVApiAdapterRecoveryTests.Invoke(adapter, "StartPeriodicTimers");

        Assert.InRange(adapter.LastFirstRefreshDelayMs!.Value, 475_000, 480_000);
    }

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
    // ------------- §2.3 ⭐ a failed recovery must not destroy the last known-good cookie set

    private sealed class FakeCdpExtractor(CdpExtractionResult result) : ICdpCookieExtractor
    {
        public int Calls { get; private set; }

        public Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CdpRefresh_WhenExtractedCookiesAreRejected_LeavesTheStoredGoodSetIntact()
    {
        // The 2026-08-01 and 2026-09-06 mechanism: a signed-out Chrome returns a full, well-formed,
        // completely dead cookie set, and the old code persisted it BEFORE discovering that.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        var good = GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD");
        await store.SaveAsync(good);

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        adapter.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(false);   // Google rejects the dead set

        var refreshed = await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;

        Assert.False(refreshed);

        // THE assertion. On main the on-disk set is SAPISID-DEAD and the good one is gone forever.
        var onDisk = await store.LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Equal("SAPISID-GOOD", onDisk!.Sapisid);

        // ...and the in-memory set rolled back too, so the adapter is not left holding proven-bad creds.
        Assert.Equal("SAPISID-GOOD", adapter.CurrentCookieSet!.Sapisid);
        Assert.True(adapter.BrowserSessionStale);

        File.Delete(path);
    }

    [Fact]
    public async Task CdpRefresh_WhenExtractedCookiesValidate_PersistsThemAndStampsTheBrowserSession()
    {
        // The paired positive: the guard must not block a genuine recovery.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-OLD"));
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        adapter.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-FRESH"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(true);

        var refreshed = await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;

        Assert.True(refreshed);
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-FRESH", onDisk!.Sapisid);
        Assert.NotNull(onDisk.BrowserSessionValidatedAtUtc);
        Assert.False(adapter.BrowserSessionStale);
        Assert.NotNull(adapter.BrowserSessionAgeSeconds);

        File.Delete(path);
    }

    [Fact]
    public async Task CdpRefresh_RejectedCandidate_DoesNotDisturbTheFrozenPsidtsAgeSeconds()
    {
        // The freeze, on the rollback path. A rejected candidate must leave psidtsAgeSeconds exactly
        // where it was — the field is a published cross-repo contract and a failed refresh is not an
        // event a consumer should see in it.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        var good = GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD");
        await store.SaveAsync(good);

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetField(adapter, "_psidtsRefreshedAt", DateTime.UtcNow.AddSeconds(-300));
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        adapter.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(false);

        await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;

        // Untouched: still ~300 s, not reset to 0 and not made null.
        Assert.NotNull(adapter.PsidtsAgeSeconds);
        Assert.InRange(adapter.PsidtsAgeSeconds!.Value, 295, 310);

        File.Delete(path);
    }

    // ------------- the validation window: a rollback must not undo a concurrent recovery
    //
    // TryValidateCandidateAsync publishes an UNVALIDATED candidate into _cookieSet and then awaits a
    // live HTTP probe (30 s client timeout) with nothing excluded. Every writer in this class used to
    // move state FORWARD, so last-writer-wins was benign; the rollback is the first BACKWARD write, and
    // that is what made the window dangerous rather than merely untidy.

    /// <summary>
    /// An adapter holding a known-good set, with a probe a test can suspend mid-flight.
    /// </summary>
    private static (GVApiAdapter Adapter, GvCookieStore Store, string Path,
                    TaskCompletionSource ProbeEntered, TaskCompletionSource<bool> ReleaseProbe)
        NewAdapterWithSuspendableProbe(GVApiAdapterRecoveryTests.FakeCookieRotator? rotator = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        var good = GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD");
        store.SaveAsync(good).GetAwaiter().GetResult();

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(rotator: rotator);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.HealthProbeOverride = async _ =>
        {
            entered.TrySetResult();
            return await release.Task;
        };

        return (adapter, store, path, entered, release);
    }

    [Fact]
    public async Task RejectedCandidate_DoesNotRollBackOverASetPublishedWhileTheProbeWasInFlight()
    {
        // ⛔ THE HIGH-2 REGRESSION. previousSet/previousValid are captured, then a live HTTP call runs
        // with nothing excluded. During that window the recovery ladder can complete a rung and publish
        // a WORKING set — and a blind snapshot restore then throws that recovery away and disposes the
        // client the ladder just published. The restore has to be a compare-and-swap: only undo the
        // state we are still the owner of.
        var (adapter, store, path, entered, release) = NewAdapterWithSuspendableProbe();
        using var _ = adapter;

        var adopting = adapter.TryAdoptAndPersistCookiesAsync(
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), "test");
        await entered.Task;

        // The candidate really is published — this is the window, not a hypothetical.
        Assert.Equal("SAPISID-DEAD", adapter.CurrentCookieSet!.Sapisid);

        // A concurrent recovery finishes and publishes a working set. Written by reflection precisely
        // BECAUSE it bypasses the mutation gate: the compare-and-swap has to hold on its own, for
        // whatever the gate does not serialize.
        var recovered = GVApiAdapterRecoveryTests.NewCookies("SAPISID-RECOVERED");
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", recovered);
        await store.SaveAsync(recovered);

        release.SetResult(false);                 // ...and only now does Google reject the candidate
        Assert.False(await adopting);

        // THE assertion. Without the compare-and-swap this reads SAPISID-GOOD: the stale snapshot was
        // restored over a live, working recovery.
        Assert.Equal("SAPISID-RECOVERED", adapter.CurrentCookieSet!.Sapisid);

        // ...and nothing candidate-derived ever reached disk.
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-RECOVERED", onDisk!.Sapisid);

        File.Delete(path);
    }

    [Fact]
    public async Task ARotationCannotStartWhileAnUnvalidatedCandidateSitsInTheCookieSet()
    {
        // The other half of HIGH-2, and the worse one. Rung 1 reads _cookieSet — the CANDIDATE during
        // the validation window — rotates from it, and calls _cookieStore.SaveAsync. That writes a
        // candidate-derived set over the known-good one on disk: the very invariant this PR exists to
        // establish, defeated through the window this PR opens. The gate is what closes it.
        GvCookieSet? rotatedFrom = null;
        var rotator = new GVApiAdapterRecoveryTests.FakeCookieRotator(current =>
        {
            rotatedFrom = current;
            return Task.FromResult(new CookieRotationResult(true, "fresh-1psidts", "fresh-3psidts"));
        });

        var (adapter, store, path, entered, release) = NewAdapterWithSuspendableProbe(rotator);
        using var _ = adapter;

        var adopting = adapter.TryAdoptAndPersistCookiesAsync(
            GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), "test");
        await entered.Task;
        Assert.Equal("SAPISID-DEAD", adapter.CurrentCookieSet!.Sapisid);

        // Rung 1 / the proactive timer fires straight into the window.
        var rotating = (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryRotateCookiesAsync")!;

        Assert.False(await GVApiAdapterRecoveryTests.WaitForAsync(() => rotator.Calls > 0, timeoutMs: 250));
        Assert.Equal(0, rotator.Calls);   // it has not even READ _cookieSet yet

        release.SetResult(false);
        Assert.False(await adopting);
        await rotating;

        // It ran only after the rollback, so it rotated from the GOOD set — never from the rejected
        // candidate. Without the gate this is "SAPISID-DEAD".
        Assert.Equal(1, rotator.Calls);
        Assert.Equal("SAPISID-GOOD", rotatedFrom!.Sapisid);

        // ...and therefore what landed on disk is still derived from the good lineage.
        var onDisk = await store.LoadAsync();
        Assert.Equal("SAPISID-GOOD", onDisk!.Sapisid);

        File.Delete(path);
    }

    // ---------------------- §Task 6: the browser session's true age, and the stale-session alarm

    [Fact]
    public async Task BrowserSessionAge_SurvivesARestart_AndKeepsClimbing()
    {
        // THE SIGNAL WHOSE ABSENCE COST TWO DAYS. The service mints its own PSIDTS and can look
        // perfectly healthy on a lineage it regenerates from itself, while the Chrome it bootstraps
        // from has been signed out since Sep 6. Nothing on status reported that. This does — and
        // because the stamp rides the cookie set, it does not reset when the process restarts.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));

        var validatedTwoDaysAgo = DateTime.UtcNow.AddDays(-2);
        await store.SaveAsync(GVApiAdapterRecoveryTests.NewCookies()
            .WithBrowserSessionValidatedAt(validatedTwoDaysAgo));

        // A brand-new adapter instance — i.e. a restarted process.
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        adapter.HealthProbeOverride = _ => Task.FromResult(true);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);

        Assert.True(await adapter.ReloadCookiesAsync());

        Assert.NotNull(adapter.BrowserSessionValidatedAt);
        Assert.NotNull(adapter.BrowserSessionAgeSeconds);
        Assert.InRange(adapter.BrowserSessionAgeSeconds!.Value, 172_000, 173_600);   // ~2 days, not ~0

        File.Delete(path);
    }

    [Fact]
    public void BrowserSessionAge_IsNullWhenNoBrowserSetWasEverValidated()
    {
        // Null means UNKNOWN, and unknown is not healthy. It must not read as 0 / "fresh".
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", GVApiAdapterRecoveryTests.NewCookies());

        Assert.Null(adapter.BrowserSessionValidatedAt);
        Assert.Null(adapter.BrowserSessionAgeSeconds);
        Assert.False(adapter.BrowserSessionStale);   // nothing was attempted, so nothing is stale
    }

    [Fact]
    public async Task BrowserSessionStale_DistinguishesADeadLoginFromAnUnreachableChrome()
    {
        // "Chrome handed us cookies and Google rejected them" and "we never reached Chrome" call for
        // OPPOSITE operator actions. Conflating them is what produced an unearned "your Google login
        // is dead" message on a run where the browser was never consulted (Task 7).
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        var good = GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD");
        await store.SaveAsync(good);

        // Chrome is reachable but signed out: a full, well-formed, dead cookie set.
        var stale = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(stale, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(stale, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetAvailable(stale, true);
        stale.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), 20, null)));
        stale.HealthProbeOverride = _ => Task.FromResult(false);

        await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(stale, "TryCdpRefreshAsync")!;
        Assert.True(stale.BrowserSessionStale);

        // Chrome is down: extraction never produced a cookie set, so the login was never tested.
        var unreachable = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(unreachable, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(unreachable, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetAvailable(unreachable, true);
        unreachable.SetCookieExtractor(new FakeCdpExtractor(
            CdpExtractionResult.Fail(CdpExtractionStatus.ChromeUnreachable, "connection refused")));

        await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(unreachable, "TryCdpRefreshAsync")!;
        Assert.False(unreachable.BrowserSessionStale);   // NOT stale — nothing tested the login

        File.Delete(path);
    }

    [Fact]
    public async Task Deactivate_ClearsTheStaleBrowserSessionFlag()
    {
        // Per-generation state, like the data-plane outcome timestamps. Carrying a stale flag from a
        // torn-down generation into a fresh one would keep a resolved alarm lit on the dashboard.
        var path = Path.Combine(Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".enc");
        var store = new GvCookieStore(path, Convert.ToBase64String(new byte[32]));
        var good = GVApiAdapterRecoveryTests.NewCookies("SAPISID-GOOD");
        await store.SaveAsync(good);

        var adapter = GVApiAdapterRecoveryTests.CreateAdapter();
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieStore", store);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", good);
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        adapter.SetCookieExtractor(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(false);

        await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;
        Assert.True(adapter.BrowserSessionStale);

        await adapter.DeactivateAsync();

        Assert.False(adapter.BrowserSessionStale);

        File.Delete(path);
    }

    // ------------- §Task 7: the exhausted-ladder message must only assert what was TESTED

    /// <summary>
    /// A ladder wired to fail every rung, so the final operator message is the only thing under test.
    /// The cookie store points at a path that does not exist, which is what makes rung 2 fail.
    /// </summary>
    private static (GVApiAdapter Adapter, CapturingLogger<GVApiAdapter> Log) NewExhaustedLadder(
        ICdpCookieExtractor? extractor, bool wireCookieStore = true)
    {
        var log = new CapturingLogger<GVApiAdapter>();
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(
            rotator: new GVApiAdapterRecoveryTests.FakeCookieRotator(
                _ => Task.FromResult(CookieRotationResult.NotRotated)),   // rung 1 fails
            logger: log);

        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", GVApiAdapterRecoveryTests.NewCookies());
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);

        if (wireCookieStore)
        {
            var missing = Path.Combine(
                Path.GetTempPath(), "gv-lineage-tests", Guid.NewGuid().ToString("n") + ".missing.enc");
            GVApiAdapterRecoveryTests.SetField(
                adapter, "_cookieStore", new GvCookieStore(missing, Convert.ToBase64String(new byte[32])));
        }

        if (extractor != null) adapter.SetCookieExtractor(extractor);
        return (adapter, log);
    }

    [Fact]
    public async Task ExhaustedLadder_StaleBrowserSession_SaysSoAndClaimsItWasTested()
    {
        // Chrome answered and Google refused what it handed over. Asserting the login is dead is EARNED
        // here, and only here.
        var (adapter, log) = NewExhaustedLadder(new FakeCdpExtractor(new CdpExtractionResult(
            CdpExtractionStatus.Success, GVApiAdapterRecoveryTests.NewCookies("SAPISID-DEAD"), 20, null)));
        adapter.HealthProbeOverride = _ => Task.FromResult(false);

        Assert.False(await adapter.TryRecoverAuthAsync("test"));

        var errors = log.AtLevel(LogLevel.Error);
        Assert.Contains(errors, e => e.Message.Contains("BROWSER SESSION IS STALE"));
        Assert.Contains(errors, e => e.Message.Contains("TESTED, not inferred"));
        Assert.Contains(errors, e => e.Message.Contains("re-login at voice.google.com"));
    }

    [Fact]
    public async Task ExhaustedLadder_ChromeUnreachable_DoesNotAccuseTheGoogleLogin()
    {
        // ⚠ THE POINT OF TASK 7. The old single message told the operator their Google login was
        // probably dead even on runs where Chrome was never successfully consulted. On 2026-09-08 the
        // owner confirmed the browser page WAS authenticated while the message claimed otherwise. An
        // untested assertion sends the operator to re-login when the real fault is a dead Chrome.
        var (adapter, log) = NewExhaustedLadder(new FakeCdpExtractor(
            CdpExtractionResult.Fail(CdpExtractionStatus.ChromeUnreachable, "connection refused")));

        Assert.False(await adapter.TryRecoverAuthAsync("test"));

        var errors = log.AtLevel(LogLevel.Error);
        Assert.Contains(errors, e => e.Message.Contains("CHROME WAS UNREACHABLE"));
        Assert.Contains(errors, e => e.Message.Contains("never tested"));
        Assert.Contains(errors, e => e.Message.Contains("the session may be perfectly fine"));

        // ...and it must NOT assert the login is dead.
        Assert.DoesNotContain(errors, e => e.Message.Contains("BROWSER SESSION IS STALE"));
    }

    [Fact]
    public async Task ExhaustedLadder_BrowserNeverConsulted_SaysTheLoginWasNotTested()
    {
        // No extractor and no store: rung 3 never ran at all. Nothing whatsoever tested the login.
        var (adapter, log) = NewExhaustedLadder(extractor: null, wireCookieStore: false);

        Assert.False(await adapter.TryRecoverAuthAsync("test"));

        var errors = log.AtLevel(LogLevel.Error);
        Assert.Contains(errors, e => e.Message.Contains("NEVER CONSULTED"));
        Assert.Contains(errors, e => e.Message.Contains("do NOT assume the login is dead"));
    }

    [Fact]
    public async Task ExhaustedLadder_LogsAtError_NotWarning()
    {
        // An exhausted recovery ladder means the phone is about to be down. That is not a warning, and
        // on an appliance whose journald is watched by eye the level is what gets it noticed.
        var (adapter, log) = NewExhaustedLadder(new FakeCdpExtractor(
            CdpExtractionResult.Fail(CdpExtractionStatus.ChromeUnreachable, "connection refused")));

        await adapter.TryRecoverAuthAsync("test");

        Assert.Contains(log.AtLevel(LogLevel.Error),
            e => e.Message.Contains("all cookie-recovery rungs failed"));
        Assert.DoesNotContain(log.AtLevel(LogLevel.Warning),
            e => e.Message.Contains("all cookie-recovery rungs failed"));
    }
}
