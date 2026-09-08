using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Server.Controllers;

namespace RotaryPhoneController.Tests;

/// <summary>
/// GET /api/phone/system-status and the SystemStatusChanged hub event carry the same
/// <see cref="SystemStatus"/> type, and until 2026-09 they filled its three HT801 fields from two
/// different mechanisms with two different meanings: SignalR from a 30-second background probe of
/// the RESOLVED registrar binding, REST from a synchronous 3-second ping of the CONFIGURED address
/// stamped with DateTime.UtcNow.
///
/// RadioConsole polls the REST path every 15 seconds and derives its predictive-degrade rule from
/// it, so their input was a ping of the address that stayed green throughout the 2026-07 outage,
/// carrying a timestamp that could never look stale. These tests pin the converged behaviour: one
/// probe, one meaning, on both transports.
///
/// See docs/architecture/decisions/2026-09-08-bell-health-contract-ratification.md §4.
/// </summary>
public class SystemStatusTransportParityTests
{
    private const string ConfiguredAddress = "192.0.2.1";
    private const string ResolvedAddress = "192.0.2.240";

    private readonly Ht801ReachabilityCache _cache = new();
    private readonly Mock<IHT801ConfigService> _ht801Service = new();

    public SystemStatusTransportParityTests()
    {
        // The configured address the endpoint used to report — and must now ignore.
        _ht801Service.Setup(s => s.GetConfig(It.IsAny<string>()))
            .Returns(new HT801Config { IpAddress = ConfiguredAddress, Extension = "1000" });
        _ht801Service.Setup(s => s.TestConnectionAsync(It.IsAny<string>()))
            .ReturnsAsync(new HT801ConnectionTestResult { Success = true });
    }

    [Fact]
    public async Task GetSystemStatus_ReturnsSameLastCheckedUtc_AcrossCallsSeparatedInTime()
    {
        _cache.Update(true, ResolvedAddress, DateTime.UtcNow);
        var controller = CreateController();

        var first = Status(controller);
        await Task.Delay(25);
        var second = Status(controller);

        // The defect in one assertion. The old implementation set Ht801LastCheckedUtc =
        // DateTime.UtcNow inside the request, so two calls 10 ms apart returned different values on
        // the live box (measured 15:55:45.6352455Z / 15:55:45.6452160Z) and the field could never
        // look stale — which silently killed RadioConsole's "mark a stale probe" affordance.
        // Asserting merely that the field is non-null would have PASSED on the broken code.
        Assert.Equal(first.Ht801LastCheckedUtc, second.Ht801LastCheckedUtc);
    }

    [Fact]
    public void GetSystemStatus_LastCheckedUtcAdvances_OnlyWhenTheProbeRuns()
    {
        var firstProbeAt = new DateTime(2026, 9, 8, 15, 54, 0, DateTimeKind.Utc);
        _cache.Update(true, ResolvedAddress, firstProbeAt);
        var controller = CreateController();

        Assert.Equal(firstProbeAt, Status(controller).Ht801LastCheckedUtc);

        var secondProbeAt = firstProbeAt.AddSeconds(30);
        _cache.Update(true, ResolvedAddress, secondProbeAt);

        // Paired with the test above, this is the full contract: the timestamp changes if and only
        // if a probe actually ran. That is what makes it a probe age rather than a response age.
        Assert.Equal(secondProbeAt, Status(controller).Ht801LastCheckedUtc);
    }

    [Fact]
    public void GetSystemStatus_DoesNotPingInRequest()
    {
        _cache.Update(true, ResolvedAddress, DateTime.UtcNow);
        var controller = CreateController();

        controller.GetSystemStatus();

        // TestConnectionAsync awaits SendPingAsync(ip, 3000). Inline, that blocked the request for
        // up to three seconds when the ATA was unreachable — for every polling client, every 15
        // seconds, precisely when the bell was broken. GetConfig is verified too because it is the
        // other half of the removed block: it is a last-wins projection of the CONFIGURED address,
        // which this endpoint must no longer consult at all.
        _ht801Service.Verify(s => s.TestConnectionAsync(It.IsAny<string>()), Times.Never);
        _ht801Service.Verify(s => s.GetConfig(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GetSystemStatus_ReportsResolvedProbeAddress_NotConfiguredAddress()
    {
        _cache.Update(true, ResolvedAddress, DateTime.UtcNow);
        var controller = CreateController();

        // The outage-relevant assertion. The configured address reported correct for the entire
        // 2026-07 outage while every INVITE went somewhere else; only the resolved binding says
        // anything about where a ring actually lands.
        Assert.Equal(ResolvedAddress, Status(controller).Ht801IpAddress);
        Assert.NotEqual(ConfiguredAddress, Status(controller).Ht801IpAddress);
    }

    [Fact]
    public void GetSystemStatus_BeforeFirstProbe_ReportsAllThreeAsNull()
    {
        // Cold start: up to 30 seconds before the background probe first completes. All three null
        // is the contracted "Unknown" (SystemStatus.cs:53-61) — never "offline", and deliberately
        // not the fast-but-possibly-wrong `true` the in-request ping used to produce.
        var status = Status(CreateController());

        Assert.Null(status.Ht801Reachable);
        Assert.Null(status.Ht801LastCheckedUtc);
        Assert.Null(status.Ht801IpAddress);
    }

    // --- Helpers ---

    private static SystemStatus Status(PhoneController controller) =>
        Assert.IsType<SystemStatus>(
            Assert.IsType<OkObjectResult>(controller.GetSystemStatus()).Value);

    private PhoneController CreateController()
    {
        var config = new AppConfiguration();

        var phoneManager = new PhoneManagerService(
            Mock.Of<ILogger<PhoneManagerService>>(),
            config,
            Mock.Of<ISipAdapter>(),
            Mock.Of<IBluetoothHfpAdapter>(),
            Mock.Of<IRtpAudioBridge>(),
            Mock.Of<ILogger<CallManager>>());

        return new PhoneController(
            phoneManager,
            NullLogger<PhoneController>.Instance,
            Mock.Of<IBluetoothHfpAdapter>(),
            Mock.Of<ISipAdapter>(),
            config,
            _ht801Service.Object,
            _cache,
            new BellFailureTracker());
    }
}
