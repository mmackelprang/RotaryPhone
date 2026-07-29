using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Sip;

namespace RotaryPhoneController.Tests;

/// <summary>
/// The bell and the audio must target the SAME host.
///
/// Before the single resolver existed, <c>SendInviteToHT801</c> resolved the learned registrar
/// binding internally while the legacy Bluetooth/SipTrunk RTP bridge used the raw configured value.
/// Under a config/learned mismatch — precisely the self-healing case this work exists to support —
/// that produced a call that RANG at the learned address while streaming audio to the stale
/// configured one: a connected call with no audio. It is the same split-brain class as the original
/// bug, just moved one leg over.
///
/// These tests pin the invariant at the CallManager level, using the real resolver and a real
/// binding store so the precedence under test is the production precedence.
/// </summary>
public class BellAndAudioTargetAgreementTests
{
    private const string ConfiguredIp = "192.0.2.22";   // stale, as shipped in config
    private const string LearnedIp = "192.0.2.240";     // where the device actually is
    private const int RtpPort = 49000;

    private sealed record Harness(
        CallManager CallManager,
        Mock<IRtpAudioBridge> RtpBridge,
        Func<string?> InviteTarget);

    /// <summary>
    /// Builds a CallManager whose ISipAdapter delegates address resolution to a REAL
    /// <see cref="SIPSorceryAdapter"/> backed by <paramref name="store"/>, and records the address the
    /// INVITE was actually aimed at. The adapter needs no live SIP transport: only resolution is
    /// exercised, and the INVITE itself is captured rather than sent.
    /// </summary>
    private static Harness BuildHarness(IRegistrarBindingStore store)
    {
        var resolver = new SIPSorceryAdapter(Mock.Of<Serilog.ILogger>(), "0.0.0.0", 5060, store);

        string? inviteTarget = null;
        var sipAdapter = new Mock<ISipAdapter>();

        sipAdapter
            .Setup(x => x.ResolveHt801Address(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string ext, string configured) => resolver.ResolveHt801Address(ext, configured));

        sipAdapter
            .Setup(x => x.SendInviteToHT801(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback((string _, string target, int _) => inviteTarget = target)
            .Returns(true);

        var rtpBridge = new Mock<IRtpAudioBridge>();

        var phoneConfig = new RotaryPhoneConfig
        {
            Id = "default",
            Name = "Rotary Phone",
            HT801IpAddress = ConfiguredIp,
            HT801Extension = "1000"
        };

        var callManager = new CallManager(
            sipAdapter.Object,
            Mock.Of<IBluetoothHfpAdapter>(),
            rtpBridge.Object,
            Mock.Of<ILogger<CallManager>>(),
            phoneConfig,
            RtpPort);
        callManager.Initialize();

        return new Harness(callManager, rtpBridge, () => inviteTarget);
    }

    [Fact]
    public void BellAndRtpBridge_TargetTheSameAddress_WhenConfigIsStaleAndABindingIsLearned()
    {
        // The device registered from .240; configuration still says .22.
        var store = new RegistrarBindingStore();
        store.Record(new RegistrarBinding("rotaryphone", LearnedIp, 5060, LearnedIp,
            DateTime.UtcNow, 3600));

        var h = BuildHarness(store);

        h.CallManager.SimulateIncomingCall();     // rings the bell
        h.CallManager.HandleHookChange(true);     // handset lifted -> legacy RTP bridge starts

        // The bell rang at the LEARNED address...
        Assert.Equal(LearnedIp, h.InviteTarget());

        // ...and the audio must go to the same place, NOT to the stale configured address.
        h.RtpBridge.Verify(
            x => x.StartBridgeAsync($"{LearnedIp}:{RtpPort}", AudioRoute.RotaryPhone), Times.Once);
        h.RtpBridge.Verify(
            x => x.StartBridgeAsync($"{ConfiguredIp}:{RtpPort}", AudioRoute.RotaryPhone), Times.Never);
    }

    [Fact]
    public void BellAndRtpBridge_BothFallBackToConfigured_WhenNothingIsLearned()
    {
        // Cold start: no REGISTER has arrived yet, so both legs use configuration — still in agreement.
        var h = BuildHarness(new RegistrarBindingStore());

        h.CallManager.SimulateIncomingCall();
        h.CallManager.HandleHookChange(true);

        Assert.Equal(ConfiguredIp, h.InviteTarget());
        h.RtpBridge.Verify(
            x => x.StartBridgeAsync($"{ConfiguredIp}:{RtpPort}", AudioRoute.RotaryPhone), Times.Once);
    }

    [Fact]
    public void BellAndRtpBridge_BothFallBackToConfigured_WhenTheBindingIsStale()
    {
        // Learned long ago with a short expiry — beyond expiry + StaleGrace, so it must not be used.
        var store = new RegistrarBindingStore();
        store.Record(new RegistrarBinding("rotaryphone", LearnedIp, 5060, LearnedIp,
            DateTime.UtcNow.AddHours(-2), 60));

        var h = BuildHarness(store);

        h.CallManager.SimulateIncomingCall();
        h.CallManager.HandleHookChange(true);

        Assert.Equal(ConfiguredIp, h.InviteTarget());
        h.RtpBridge.Verify(
            x => x.StartBridgeAsync($"{ConfiguredIp}:{RtpPort}", AudioRoute.RotaryPhone), Times.Once);
    }

    [Fact]
    public void RtpBridge_UsesTheAddressResolvedAtRingTime_EvenIfTheBindingChangesBeforeAnswer()
    {
        // Resolution happens ONCE, when the bell is rung. If the binding moved (or expired) between the
        // ring and the handset being lifted, the audio must still follow the bell — otherwise the two
        // legs diverge exactly as they used to.
        var store = new RegistrarBindingStore();
        store.Record(new RegistrarBinding("rotaryphone", LearnedIp, 5060, LearnedIp,
            DateTime.UtcNow, 3600));

        var h = BuildHarness(store);

        h.CallManager.SimulateIncomingCall();
        Assert.Equal(LearnedIp, h.InviteTarget());

        // The device moves mid-ring. The in-flight call must not be re-pointed underneath itself.
        store.Record(new RegistrarBinding("rotaryphone", "192.0.2.99", 5060, "192.0.2.99",
            DateTime.UtcNow, 3600));

        h.CallManager.HandleHookChange(true);

        h.RtpBridge.Verify(
            x => x.StartBridgeAsync($"{LearnedIp}:{RtpPort}", AudioRoute.RotaryPhone), Times.Once);
    }
}
