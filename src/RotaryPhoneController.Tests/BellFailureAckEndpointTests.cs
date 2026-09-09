using System.Text.Json;
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
/// POST /api/phone/bell-failure/ack is IDEMPOTENT, and these tests are the executable form of that
/// promise.
///
/// <para>
/// docs/handoffs/radioconsole-bell-failure-reply.md §5 tells Radio Console that acking an
/// already-acknowledged failure, or a phone with no failure at all, both return
/// <c>200 {"acknowledged": true}</c>, and invites them in those words to
/// <b>"retry freely on a flaky network"</b>. The code did not do that — it returned
/// <c>acknowledged: false</c> in both cases, which a retrying client reads as a failed dismissal.
/// Given the choice between retracting the promise and making it true, the owner chose to make it
/// true: the published semantics are a POST-CONDITION ("the failure is acknowledged"), not a delta
/// ("you were the one who changed it"), and the idempotent reading is the one they were told to
/// rely on.
/// </para>
///
/// <para>
/// ⚠ Do not "simplify" these back to reporting the delta. <see cref="IBellFailureTracker.Acknowledge"/>
/// still returns whether THIS CALL changed state — that is useful internal information, it is pinned
/// by <see cref="BellFailureTrackerTests"/>, and the controller logs it. What must never come back is
/// that bool reaching the wire.
/// </para>
/// </summary>
public class BellFailureAckEndpointTests
{
    private const string PhoneId = "default";

    private readonly Ht801ReachabilityCache _cache = new();
    private readonly Mock<IHT801ConfigService> _ht801Service = new();
    private readonly BellFailureTracker _tracker = new();

    [Fact]
    public void Ack_OfALiveFailure_Returns200AndAcknowledgedTrue()
    {
        var controller = CreateController();
        RecordFailure();

        Assert.True(AcknowledgedFlagOf(controller.AcknowledgeBellFailure(PhoneId)));

        // ...and the ack actually did its job, not merely reported that it had. The stored note is
        // what survives a browser reload, so this is the assertion that keeps a dismissed failure
        // dismissed.
        Assert.True(StatusAcknowledgedFlag(controller));
    }

    [Fact]
    public void Ack_Repeated_StillReturns200AndAcknowledgedTrue()
    {
        // THE regression. This is the call that used to answer acknowledged:false — a client that
        // retried after a dropped response was told its dismissal had failed, on the exact flaky
        // network the reply told them to retry over.
        var controller = CreateController();
        RecordFailure();

        Assert.True(AcknowledgedFlagOf(controller.AcknowledgeBellFailure(PhoneId)));
        Assert.True(AcknowledgedFlagOf(controller.AcknowledgeBellFailure(PhoneId)));

        Assert.True(StatusAcknowledgedFlag(controller));
    }

    [Fact]
    public void Ack_OfAPhoneWithNoStoredFailure_StillReturns200AndAcknowledgedTrue()
    {
        // The other half of the same promise: the post-condition "no unacknowledged failure is
        // showing" already holds, so the honest answer is true. It was false.
        var controller = CreateController();

        Assert.True(AcknowledgedFlagOf(controller.AcknowledgeBellFailure(PhoneId)));
    }

    // --- Helpers ---

    private void RecordFailure() =>
        _tracker.RecordFailure(PhoneId, BellFailureReason.Timeout, "5551234567", "call-1",
            "192.0.2.240", "no response to INVITE", DateTime.UtcNow);

    /// <summary>Reads <c>acknowledged</c> off the endpoint's anonymous 200 body.</summary>
    private static bool AcknowledgedFlagOf(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return doc.RootElement.GetProperty("acknowledged").GetBoolean();
    }

    /// <summary>Reads <c>LastBellFailure.acknowledged</c> off GET /api/phone/status.</summary>
    private static bool StatusAcknowledgedFlag(PhoneController controller)
    {
        var ok = Assert.IsType<OkObjectResult>(controller.GetStatus());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return doc.RootElement.GetProperty("LastBellFailure").GetProperty("acknowledged").GetBoolean();
    }

    private PhoneController CreateController()
    {
        // One configured phone, so GetStatus resolves a CallManager and serves the stored note
        // rather than the all-nulls "no phone registered" shape.
        var config = new AppConfiguration();
        config.Phones.Add(new RotaryPhoneConfig { Id = PhoneId, HT801IpAddress = "192.0.2.240" });

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
            _tracker);
    }
}
