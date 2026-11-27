using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Adapters;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

/// <summary>
/// The in-process retry after Google rejects the bridge Chrome's cookies. Every observed rejection
/// (2026-10-16..11-26) healed on the next cron fire with no sign-in; this asks again sooner, boundedly,
/// and gets out of the way the moment anything else has changed the outcome.
/// </summary>
public class StaleBrowserRetrySchedulerTests
{
    private static readonly GvCookieSet Extracted = new()
    {
        Sapisid = "SAPISID-EXTRACTED", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
    };

    private sealed class CountingExtractor(CdpExtractionResult result) : ICdpCookieExtractor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public int? LastPort { get; private set; }

        public Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
        {
            LastPort = cdpPort;
            Interlocked.Increment(ref _calls);
            return Task.FromResult(result);
        }
    }

    private sealed record Rig(
        StaleBrowserRetryScheduler Scheduler,
        GVApiAdapter Adapter,
        Mock<IGvCookieManager> CookieManager,
        CountingExtractor Extractor,
        System.Collections.Concurrent.ConcurrentQueue<TimeSpan> Waits)
    {
        /// <summary>Minutes of every wait the chain asked for, in order.</summary>
        public int[] WaitMinutes => Waits.Select(w => (int)w.TotalMinutes).ToArray();

        public Task ChainEnded()
            => (Scheduler.CurrentChain ?? throw new InvalidOperationException("no chain started"))
                .WaitAsync(TimeSpan.FromSeconds(3));
    }

    /// <param name="gate">When given, every wait blocks until it completes, so a test can observe the
    /// chain mid-flight; otherwise each wait returns at once and the chain runs straight through.</param>
    private static Rig Build(
        Queue<SetCookiesOutcome> outcomes,
        string delays = "2,5,10",
        CdpExtractionResult? extraction = null,
        Task? gate = null)
    {
        var config = Options.Create(new GVBridgeConfig
        {
            GvApiKey = "test",
            CookieFilePath = "test.enc",
            CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
            ChromeCdpPort = 9224,
            StaleBrowserRetryDelaysMinutes = delays,
        });

        var adapter = new GVApiAdapter(config, NullLogger<GVApiAdapter>.Instance, NullLoggerFactory.Instance);
        GVApiAdapterRecoveryTests.SetField(adapter, "_lastBrowserRefreshOutcome",
            GVApiAdapter.BrowserRefreshOutcome.Stale);
        // A live, activated adapter: the scheduler only ever retries on the hot path.
        AdoptBrowserSetAt(adapter, new DateTime(2026, 11, 26, 1, 0, 0, DateTimeKind.Utc));

        var cookieManager = new Mock<IGvCookieManager>();
        cookieManager.Setup(m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => outcomes.Count > 1 ? outcomes.Dequeue() : outcomes.Peek());

        var extractor = new CountingExtractor(
            extraction ?? new CdpExtractionResult(CdpExtractionStatus.Success, Extracted, 20, null));
        var waits = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        var scheduler = new StaleBrowserRetryScheduler(
            adapter, cookieManager.Object, extractor, config,
            NullLogger<StaleBrowserRetryScheduler>.Instance, new FakeTimeProvider(),
            delay: (d, ct) =>
            {
                waits.Enqueue(d);
                return gate?.WaitAsync(ct) ?? Task.CompletedTask;
            });

        return new Rig(scheduler, adapter, cookieManager, extractor, waits);
    }

    /// <summary>What a successful browser adoption leaves behind: a live set stamped with its validation time.</summary>
    private static void AdoptBrowserSetAt(GVApiAdapter adapter, DateTime validatedAtUtc)
        => GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet",
            Extracted.WithBrowserSessionValidatedAt(validatedAtUtc));

    private static void VerifyAdoptions(Rig rig, int times)
        => rig.CookieManager.Verify(
            m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()), Times.Exactly(times));

    [Fact]
    public async Task Heals_OnASecondAttempt_AndStops()
    {
        var rig = Build(new Queue<SetCookiesOutcome>(
            [SetCookiesOutcome.RejectedByGoogle, SetCookiesOutcome.Adopted]));

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();

        VerifyAdoptions(rig, 2);
        Assert.Equal(9224, rig.Extractor.LastPort);
        // +2 then +5 to the healing attempt; the +10 wait was never entered.
        Assert.Equal([2, 5], rig.WaitMinutes);
    }

    [Fact]
    public async Task GivesUp_AfterTheConfiguredAttempts()
    {
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.RejectedByGoogle]));

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();

        VerifyAdoptions(rig, 3);
        // Last attempt at +17: before the box cron's next */20 fire, which is the point of the defaults.
        Assert.Equal([2, 5, 10], rig.WaitMinutes);
        Assert.Equal(17, rig.WaitMinutes.Sum());
    }

    [Fact]
    public async Task Stops_WithoutProbing_WhenSomethingElseAlreadyHealedIt()
    {
        var gate = new TaskCompletionSource();
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.RejectedByGoogle]), gate: gate.Task);

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        // During the first wait, the cron fired and Google accepted Chrome's cookies.
        GVApiAdapterRecoveryTests.SetField(rig.Adapter, "_lastBrowserRefreshOutcome",
            GVApiAdapter.BrowserRefreshOutcome.Succeeded);
        gate.SetResult();
        await rig.ChainEnded();

        Assert.Equal(0, rig.Extractor.Calls);
        VerifyAdoptions(rig, 0);
    }

    [Fact]
    public async Task Stops_WhenChromeCannotBeRead_AndHandsNothingToThePipeline()
    {
        var rig = Build(
            new Queue<SetCookiesOutcome>([SetCookiesOutcome.RejectedByGoogle]),
            extraction: CdpExtractionResult.Fail(CdpExtractionStatus.MissingRequiredCookies, "no session"));

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();

        Assert.Equal(1, rig.Extractor.Calls);
        VerifyAdoptions(rig, 0);
    }

    [Fact]
    public async Task OnlyOneChain_AtATime()
    {
        var gate = new TaskCompletionSource();
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.Adopted]), gate: gate.Task);

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        Assert.False(rig.Scheduler.ScheduleAfterRejection());
        gate.SetResult();
        await rig.ChainEnded();

        // Once the first chain has ended, a later rejection may start a new one.
        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();
    }

    [Fact]
    public async Task Dispose_CancelsAWaitingChain_WithoutAnAttempt()
    {
        var never = new TaskCompletionSource();
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.Adopted]), gate: never.Task);

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        // Wait until the chain is parked in its first wait, then shut down.
        while (rig.Waits.IsEmpty) await Task.Yield();
        rig.Scheduler.Dispose();
        await rig.ChainEnded();

        Assert.Equal(0, rig.Extractor.Calls);
    }

    [Fact]
    public async Task OneChainPerStaleEpisode_ARealSignOutDoesNotRestartItEveryCronFire()
    {
        // Review M1: a dead session is rejected by every cron fire. Without the episode bound each one
        // started a new chain — 4 probes per 20 min for the whole outage instead of 1.
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.RejectedByGoogle]));

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();
        VerifyAdoptions(rig, 3);

        // The next cron fire is rejected too: same episode, no new chain, no extra probes.
        Assert.False(rig.Scheduler.ScheduleAfterRejection());
        VerifyAdoptions(rig, 3);

        // A browser set is adopted (the owner re-logged in); a later rejection is a NEW episode.
        AdoptBrowserSetAt(rig.Adapter, new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc));
        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();
        VerifyAdoptions(rig, 6);
    }

    [Fact]
    public async Task NeverTakesTheColdPath_WhenTheAdapterHoldsNoLiveSet()
    {
        // Review L1: with no live set, SetCookiesAsync seeds disk unprobed and switches mode. A
        // background retry must leave that to whoever is re-activating.
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.Adopted]));
        GVApiAdapterRecoveryTests.SetField(rig.Adapter, "_cookieSet", null);

        Assert.True(rig.Scheduler.ScheduleAfterRejection());
        await rig.ChainEnded();

        Assert.Equal(1, rig.Extractor.Calls);
        VerifyAdoptions(rig, 0);
    }

    [Fact]
    public void AfterDispose_ARejectionStartsNothing_AndDoesNotThrow()
    {
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.Adopted]));
        rig.Scheduler.Dispose();

        Assert.False(rig.Scheduler.ScheduleAfterRejection());
        rig.Scheduler.Dispose();
    }

    [Fact]
    public void IsRegistered_SoTheControllerActuallyGetsOne()
    {
        // The controller's parameter is optional and null-tolerant, so a lost registration would turn
        // the retries off with every other test still green.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        RotaryPhoneController.GVBridge.Extensions.GVBridgeServiceExtensions.AddGVBridge(
            services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var d = Assert.Single(services, s => s.ServiceType == typeof(IStaleBrowserRetryScheduler));
        Assert.Equal(typeof(StaleBrowserRetryScheduler), d.ImplementationType);
        Assert.Equal(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton, d.Lifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0,-3,x")]
    public void Disabled_WhenNoUsableDelaysAreConfigured(string delays)
    {
        var rig = Build(new Queue<SetCookiesOutcome>([SetCookiesOutcome.Adopted]), delays);

        Assert.False(rig.Scheduler.ScheduleAfterRejection());
        Assert.Null(rig.Scheduler.CurrentChain);
    }

    [Fact]
    public void ParseDelays_KeepsPositiveIntegers_InOrder()
        => Assert.Equal([2, 5, 10], StaleBrowserRetryScheduler.ParseDelays(" 2, 5,x,-1,0,10 "));

    // --- the trigger: only a bridge-Chrome rejection starts a chain --------------------------------

    private static (GVBridgeController Controller, Mock<IStaleBrowserRetryScheduler> Retry) ControllerRejecting()
    {
        var registry = new Mock<ICallAdapterRegistry>();
        registry.Setup(r => r.ActiveMode).Returns(CallAdapterMode.GVApi);
        var config = Options.Create(new GVBridgeConfig
        {
            GvApiKey = "test",
            CookieFilePath = "test.enc",
            CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
            ChromeCdpPort = 9224,
        });
        var adapter = new GVApiAdapter(config, NullLogger<GVApiAdapter>.Instance, NullLoggerFactory.Instance);
        var cookieManager = new Mock<IGvCookieManager>();
        cookieManager.Setup(m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetCookiesOutcome.RejectedByGoogle);
        var retry = new Mock<IStaleBrowserRetryScheduler>();
        var controller = new GVBridgeController(
            registry.Object, adapter, cookieManager.Object, config,
            new CountingExtractor(new CdpExtractionResult(CdpExtractionStatus.Success, Extracted, 20, null)),
            NullLogger<GVBridgeController>.Instance, retry.Object);
        return (controller, retry);
    }

    [Fact]
    public async Task Controller_SchedulesRetries_WhenTheBridgeChromeIsRejected()
    {
        var (controller, retry) = ControllerRejecting();

        var result = await controller.RefreshCookiesFromBrowser(null);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        retry.Verify(r => r.ScheduleAfterRejection(), Times.Once);
    }

    [Fact]
    public async Task Controller_DoesNotSchedule_ForAnotherPort()
    {
        var (controller, retry) = ControllerRejecting();

        await controller.RefreshCookiesFromBrowser(new RefreshFromBrowserRequest(CdpPort: 9333));

        retry.Verify(r => r.ScheduleAfterRejection(), Times.Never);
    }
}
