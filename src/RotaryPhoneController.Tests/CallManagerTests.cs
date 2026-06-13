using Microsoft.Extensions.Logging;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;
using Xunit;

namespace RotaryPhoneController.Tests;

public class CallManagerTests
{
    private readonly Mock<ISipAdapter> _mockSipAdapter;
    private readonly Mock<IBluetoothHfpAdapter> _mockBluetoothAdapter;
    private readonly Mock<IRtpAudioBridge> _mockRtpBridge;
    private readonly Mock<ICallHistoryService> _mockCallHistory;
    private readonly Mock<ILogger<CallManager>> _mockLogger;
    private readonly RotaryPhoneConfig _phoneConfig;
    private readonly CallManager _callManager;

    public CallManagerTests()
    {
        _mockSipAdapter = new Mock<ISipAdapter>();
        _mockBluetoothAdapter = new Mock<IBluetoothHfpAdapter>();
        _mockRtpBridge = new Mock<IRtpAudioBridge>();
        _mockCallHistory = new Mock<ICallHistoryService>();
        _mockLogger = new Mock<ILogger<CallManager>>();
        
        _phoneConfig = new RotaryPhoneConfig 
        { 
            Id = "test-phone",
            Name = "Test Phone",
            HT801IpAddress = "127.0.0.1",
            HT801Extension = "1000"
        };

        _callManager = new CallManager(
            _mockSipAdapter.Object,
            _mockBluetoothAdapter.Object,
            _mockRtpBridge.Object,
            _mockLogger.Object,
            _phoneConfig,
            49000,
            _mockCallHistory.Object
        );
        
        _callManager.Initialize();
    }

    [Fact]
    public void InitialState_ShouldBeIdle()
    {
        Assert.Equal(CallState.Idle, _callManager.CurrentState);
    }

    [Fact]
    public void HandleHookChange_OffHook_WhenIdle_ShouldTransitionToDialing()
    {
        // Act
        _callManager.HandleHookChange(true); // Off-hook

        // Assert
        Assert.Equal(CallState.Dialing, _callManager.CurrentState);
    }

    [Fact]
    public void HandleHookChange_OnHook_ShouldAlwaysTransitionToIdle()
    {
        // Arrange
        _mockRtpBridge.Setup(x => x.IsActive).Returns(true);
        _callManager.HandleHookChange(true); // Go to Dialing
        Assert.Equal(CallState.Dialing, _callManager.CurrentState);

        // Act
        _callManager.HandleHookChange(false); // On-hook

        // Assert
        Assert.Equal(CallState.Idle, _callManager.CurrentState);
        _mockBluetoothAdapter.Verify(x => x.TerminateCallAsync(), Times.Once);
        _mockRtpBridge.Verify(x => x.StopBridgeAsync(), Times.Once);
    }

    [Fact]
    public void SimulateIncomingCall_ShouldTransitionToRinging_AndSendInvite()
    {
        // Act
        _callManager.SimulateIncomingCall();

        // Assert
        Assert.Equal(CallState.Ringing, _callManager.CurrentState);
        _mockSipAdapter.Verify(x => x.SendInviteToHT801(_phoneConfig.HT801Extension, _phoneConfig.HT801IpAddress), Times.Once);
        _mockCallHistory.Verify(x => x.AddCallHistory(It.Is<CallHistoryEntry>(e => e.Direction == CallDirection.Incoming)), Times.Once);
    }

    [Fact]
    public void HandleHookChange_OffHook_WhenRinging_ShouldAnswerCall()
    {
        // Arrange
        _callManager.SimulateIncomingCall();
        Assert.Equal(CallState.Ringing, _callManager.CurrentState);

        // Act
        _callManager.HandleHookChange(true); // Off-hook (Answer)

        // Assert
        Assert.Equal(CallState.InCall, _callManager.CurrentState);
        // Verify audio routed to RotaryPhone
        _mockBluetoothAdapter.Verify(x => x.AnswerCallAsync(AudioRoute.RotaryPhone), Times.Once);
        _mockRtpBridge.Verify(x => x.StartBridgeAsync(It.IsAny<string>(), AudioRoute.RotaryPhone), Times.Once);
    }

    [Fact]
    public void Bluetooth_OnIncomingCall_ShouldTriggerRinging()
    {
        // Act
        // Simulate event from Bluetooth adapter
        _mockBluetoothAdapter.Raise(x => x.OnIncomingCall += null, "1234567890");

        // Assert
        Assert.Equal(CallState.Ringing, _callManager.CurrentState);
        _mockSipAdapter.Verify(x => x.SendInviteToHT801(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Bluetooth_OnCallAnsweredOnCellPhone_ShouldCancelInviteAndNotBridge()
    {
        // Arrange
        _callManager.SimulateIncomingCall();

        // Act
        _mockBluetoothAdapter.Raise(x => x.OnCallAnsweredOnCellPhone += null);

        // Assert
        Assert.Equal(CallState.InCall, _callManager.CurrentState);
        // INVITE should be cancelled so rotary stops ringing
        _mockSipAdapter.Verify(x => x.CancelPendingInvite(), Times.Once);
        // No RTP bridge — audio stays on cell phone
        _mockRtpBridge.Verify(x => x.StartBridgeAsync(It.IsAny<string>(), It.IsAny<AudioRoute>()), Times.Never);
    }

    [Fact]
    public void Dialing_ShouldInitiateBluetoothCall()
    {
        // Arrange
        _callManager.HandleHookChange(true); // Go to Dialing
        var numberToDial = "5551234";

        // Act
        _callManager.HandleDigitsReceived(numberToDial);
        _callManager.StartCall(numberToDial); // Usually triggered by timeout or explicit call, testing public method here

        // Assert
        Assert.Equal(CallState.InCall, _callManager.CurrentState);
        Assert.Equal(numberToDial, _callManager.DialedNumber);
        _mockBluetoothAdapter.Verify(x => x.InitiateCallAsync(numberToDial), Times.Once);
    }

    [Fact]
    public void SetResolvedCallerName_WhenRinging_ShouldUpdateCallHistory()
    {
        // Arrange - simulate incoming BT call
        _mockBluetoothAdapter.Raise(x => x.OnIncomingCall += null, "5551234567");
        Assert.Equal(CallState.Ringing, _callManager.CurrentState);

        // Act
        _callManager.SetResolvedCallerName("5551234567", "John Smith");

        // Assert - hang up to flush call history update
        _callManager.HangUp();

        _mockCallHistory.Verify(x => x.UpdateCallHistory(
            It.Is<CallHistoryEntry>(e => e.CallerName == "John Smith" && e.PhoneNumber == "5551234567")),
            Times.Once);
    }

    [Fact]
    public void SetResolvedCallerName_WrongNumber_ShouldNotUpdate()
    {
        // Arrange
        _mockBluetoothAdapter.Raise(x => x.OnIncomingCall += null, "5551234567");

        // Act - different number should be ignored
        _callManager.SetResolvedCallerName("9999999999", "Wrong Person");

        // Assert - hang up and verify CallerName is NOT set
        _callManager.HangUp();

        _mockCallHistory.Verify(x => x.UpdateCallHistory(
            It.Is<CallHistoryEntry>(e => e.CallerName == null && e.PhoneNumber == "5551234567")),
            Times.Once);
    }

    [Fact]
    public void HandleDigitsReceived_NonNumeric_ShouldBeIgnored()
    {
        // Act — HT801 sometimes sends its registration name as first INVITE
        _callManager.HandleDigitsReceived("rotaryphone");

        // Assert — state stays Idle, DialedNumber not set to the junk value
        Assert.Equal(CallState.Idle, _callManager.CurrentState);
        Assert.Equal(string.Empty, _callManager.DialedNumber);
    }

    [Fact]
    public void HandleDigitsReceived_WhileIdle_ShouldImplicitOffHookAndStartCall()
    {
        // Arrange — create a CallManager with a GV adapter so BT device isn't required
        var mockAdapterRegistry = new Mock<ICallAdapterRegistry>();
        var mockAdapter = new Mock<ICallAdapter>();
        mockAdapter.Setup(a => a.Mode).Returns(CallAdapterMode.GVApi);
        mockAdapter.Setup(a => a.PlaceCallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("call-123");
        mockAdapterRegistry.Setup(r => r.ActiveAdapter).Returns(mockAdapter.Object);

        var cm = new CallManager(
            _mockSipAdapter.Object,
            _mockBluetoothAdapter.Object,
            _mockRtpBridge.Object,
            _mockLogger.Object,
            _phoneConfig,
            49000,
            _mockCallHistory.Object,
            adapterRegistry: mockAdapterRegistry.Object
        );
        cm.Initialize();

        // Act — digits arrive before hook change (HT801 INVITE ordering)
        cm.HandleDigitsReceived("9193718044");

        // Assert — should have transitioned through Dialing into the GV call flow
        Assert.Equal("9193718044", cm.DialedNumber);
        Assert.NotEqual(CallState.Idle, cm.CurrentState);
        mockAdapter.Verify(a => a.PlaceCallAsync("9193718044", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HandleDigitsReceived_WhileIdle_NoBtDevice_ShouldNotStartCall()
    {
        // Arrange — CallManager with a device manager but no connected devices
        var mockDeviceManager = new Mock<IBluetoothDeviceManager>();
        mockDeviceManager.Setup(d => d.ConnectedDevices)
            .Returns(new List<BluetoothDevice>());

        var cm = new CallManager(
            _mockSipAdapter.Object,
            _mockBluetoothAdapter.Object,
            _mockRtpBridge.Object,
            _mockLogger.Object,
            _phoneConfig,
            49000,
            _mockCallHistory.Object,
            deviceManager: mockDeviceManager.Object
        );
        cm.Initialize();

        // Act — digits arrive while Idle but no BT device connected
        cm.HandleDigitsReceived("5551234567");

        // Assert — should stay Idle (no device to route through)
        Assert.Equal(CallState.Idle, cm.CurrentState);
    }

    [Theory]
    [InlineData("+15551234567")]
    [InlineData("*67")]
    [InlineData("#31#5551234567")]
    [InlineData("911")]
    public void HandleDigitsReceived_ValidDialStrings_ShouldNotBeFiltered(string number)
    {
        // Act — go to Dialing first, then send digits
        _callManager.HandleHookChange(true);
        _callManager.HandleDigitsReceived(number);

        // Assert — number should be accepted (not filtered by non-numeric guard)
        Assert.Equal(number, _callManager.DialedNumber);
    }

    #region Outbound GV InCall-ordering (defer bridge-start/InCall until cell answers)

    /// <summary>
    /// Builds a CallManager bound to a mock GV adapter, plus the registry/adapter mocks
    /// so tests can drive the outbound answer event (OnCallAnswered).
    /// outboundDialingTimeout lets the no-answer timeout test run fast.
    /// </summary>
    private (CallManager Cm, Mock<ICallAdapter> Adapter, Mock<ICallAdapterRegistry> Registry) BuildGvCallManager(
        TimeSpan? outboundDialingTimeout = null)
    {
        var mockAdapterRegistry = new Mock<ICallAdapterRegistry>();
        var mockAdapter = new Mock<ICallAdapter>();
        mockAdapter.Setup(a => a.Mode).Returns(CallAdapterMode.GVApi);
        mockAdapter.Setup(a => a.PlaceCallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("call-123");
        mockAdapter.Setup(a => a.OnCallAnsweredOnRotaryPhoneAsync()).Returns(Task.CompletedTask);
        mockAdapterRegistry.Setup(r => r.ActiveAdapter).Returns(mockAdapter.Object);

        var cm = new CallManager(
            _mockSipAdapter.Object,
            _mockBluetoothAdapter.Object,
            _mockRtpBridge.Object,
            _mockLogger.Object,
            _phoneConfig,
            49000,
            _mockCallHistory.Object,
            adapterRegistry: mockAdapterRegistry.Object,
            outboundDialingTimeout: outboundDialingTimeout
        );
        cm.Initialize();
        return (cm, mockAdapter, mockAdapterRegistry);
    }

    [Fact]
    public void Outbound_AfterPlaceCall_StaysDialing_UntilAnswered()
    {
        // Arrange
        var (cm, adapter, _) = BuildGvCallManager();

        // Act — off-hook then dial (digits-while-idle path also routes here)
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");

        // Assert — PlaceCallAsync was invoked, but we remain in Dialing and the
        // audio bridge has NOT started yet (deferred to the GV answer).
        adapter.Verify(a => a.PlaceCallAsync("9193718044", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(CallState.Dialing, cm.CurrentState);
        Assert.Null(cm.CallStartedAtUtc);
        adapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Never);
    }

    [Fact]
    public void Outbound_OnGvActive_StartsBridge_AndGoesInCall()
    {
        // Arrange — place the call first (stays Dialing)
        var (cm, adapter, _) = BuildGvCallManager();
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");
        Assert.Equal(CallState.Dialing, cm.CurrentState);

        // Act — GV signals the cell answered (CallStatusType.Active → OnCallAnswered)
        adapter.Raise(a => a.OnCallAnswered += null);

        // Assert — now we start the bridge and go InCall
        Assert.Equal(CallState.InCall, cm.CurrentState);
        Assert.NotNull(cm.CallStartedAtUtc);
        adapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Once);
    }

    [Fact]
    public void Outbound_OnGvActive_DoesNotCancelInvite()
    {
        // Arrange
        var (cm, adapter, _) = BuildGvCallManager();
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");

        // Act — answer
        adapter.Raise(a => a.OnCallAnswered += null);

        // Assert — the HT801 INVITE leg must stay up (it was already 200-OK'd);
        // CancelPendingInvite is the inbound-stop-ringing action and must NOT fire here.
        _mockSipAdapter.Verify(s => s.CancelPendingInvite(), Times.Never);
    }

    [Fact]
    public void Outbound_DuplicateGvActive_StartsBridgeOnce()
    {
        // Arrange
        var (cm, adapter, _) = BuildGvCallManager();
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");

        // Act — GV emits Active more than once (e.g. re-INVITE 200 OK)
        adapter.Raise(a => a.OnCallAnswered += null);
        adapter.Raise(a => a.OnCallAnswered += null);

        // Assert — bridge starts exactly once, state stays InCall
        Assert.Equal(CallState.InCall, cm.CurrentState);
        adapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Once);
    }

    [Fact]
    public async Task Outbound_PlacementFailure_ResetsToIdle()
    {
        // Arrange — adapter throws on PlaceCallAsync
        var mockAdapterRegistry = new Mock<ICallAdapterRegistry>();
        var mockAdapter = new Mock<ICallAdapter>();
        mockAdapter.Setup(a => a.Mode).Returns(CallAdapterMode.GVApi);
        mockAdapter.Setup(a => a.PlaceCallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        mockAdapterRegistry.Setup(r => r.ActiveAdapter).Returns(mockAdapter.Object);

        var cm = new CallManager(
            _mockSipAdapter.Object,
            _mockBluetoothAdapter.Object,
            _mockRtpBridge.Object,
            _mockLogger.Object,
            _phoneConfig,
            49000,
            _mockCallHistory.Object,
            adapterRegistry: mockAdapterRegistry.Object
        );
        cm.Initialize();

        // Act
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");

        // PlaceGvCallAsync is fire-and-forget; its catch resets to Idle on a continuation
        // thread. Poll briefly for the state change rather than asserting synchronously.
        await WaitForStateAsync(cm, CallState.Idle, TimeSpan.FromSeconds(2));

        // Assert — existing catch resets to Idle; bridge never started
        Assert.Equal(CallState.Idle, cm.CurrentState);
        mockAdapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Never);
    }

    /// <summary>Polls until the CallManager reaches the expected state or the deadline elapses.</summary>
    private static async Task WaitForStateAsync(CallManager cm, CallState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (cm.CurrentState != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Outbound_NoAnswer_TimesOutToIdle()
    {
        // Arrange — short outbound-dialing timeout so the test is fast
        var (cm, adapter, _) = BuildGvCallManager(outboundDialingTimeout: TimeSpan.FromMilliseconds(150));
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");
        Assert.Equal(CallState.Dialing, cm.CurrentState);

        // Act — never raise OnCallAnswered; wait past the timeout
        await Task.Delay(500);

        // Assert — clean return to Idle, bridge never started
        Assert.Equal(CallState.Idle, cm.CurrentState);
        adapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Never);
    }

    [Fact]
    public async Task Outbound_AnswerBeforeTimeout_DoesNotResetToIdle()
    {
        // Arrange — short timeout, but answer arrives first
        var (cm, adapter, _) = BuildGvCallManager(outboundDialingTimeout: TimeSpan.FromMilliseconds(300));
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");

        // Act — answer well before the timeout, then wait past it
        adapter.Raise(a => a.OnCallAnswered += null);
        Assert.Equal(CallState.InCall, cm.CurrentState);
        await Task.Delay(600);

        // Assert — timeout was cancelled on answer; stays InCall
        Assert.Equal(CallState.InCall, cm.CurrentState);
    }

    [Fact]
    public void Outbound_HangUpMidDialing_ResetsToIdle_AndClearsPending()
    {
        // Arrange — place call (Dialing), then hang up before answer
        var (cm, adapter, _) = BuildGvCallManager();
        cm.HandleHookChange(true);
        cm.HandleDigitsReceived("9193718044");
        Assert.Equal(CallState.Dialing, cm.CurrentState);

        // Act — on-hook before the cell answers
        cm.HandleHookChange(false);
        Assert.Equal(CallState.Idle, cm.CurrentState);

        // A late/duplicate GV Active after hangup must NOT start a bridge or
        // mis-route the next call (pending flag cleared on hangup).
        adapter.Raise(a => a.OnCallAnswered += null);

        // Assert
        Assert.Equal(CallState.Idle, cm.CurrentState);
        adapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Never);
    }

    #endregion

    #region Adapter OnCallEnded teardown (inbound CANCEL → stop HT801 ringing)

    [Fact]
    public void AdapterOnCallEnded_WhileRinging_ResetsToIdle_AndCancelsInvite()
    {
        // Arrange — inbound call ringing the HT801 (SimulateIncomingCall sends the INVITE).
        var (cm, adapter, _) = BuildGvCallManager();
        cm.SimulateIncomingCall();
        Assert.Equal(CallState.Ringing, cm.CurrentState);
        Assert.Equal("Unknown", cm.IncomingPhoneNumber);

        // Act — GV signals the call ended (inbound CANCEL → CallStatusChanged(Completed)
        // → GVApiAdapter.OnCallEnded). This is the event the new CANCEL branch produces.
        adapter.Raise(a => a.OnCallEnded += null);

        // Assert — state returns to Idle, incoming number cleared, and the pending HT801
        // INVITE is cancelled exactly once so the rotary phone stops ringing.
        Assert.Equal(CallState.Idle, cm.CurrentState);
        Assert.Null(cm.IncomingPhoneNumber);
        _mockSipAdapter.Verify(s => s.CancelPendingInvite(), Times.Once);
    }

    [Fact]
    public void AdapterOnCallEnded_WhenAlreadyIdle_IsNoOp()
    {
        // Arrange — bound GV adapter, no active call (state Idle).
        var (cm, adapter, _) = BuildGvCallManager();
        Assert.Equal(CallState.Idle, cm.CurrentState);

        // Act — a stray/duplicate OnCallEnded while Idle (e.g. CANCEL then BYE for the same
        // already-torn-down call) must short-circuit without re-running teardown.
        adapter.Raise(a => a.OnCallEnded += null);

        // Assert — stays Idle and does NOT fire CancelPendingInvite again.
        Assert.Equal(CallState.Idle, cm.CurrentState);
        _mockSipAdapter.Verify(s => s.CancelPendingInvite(), Times.Never);
    }

    [Fact]
    public void AdapterOnCallEnded_AfterAnswered_SingleTeardown_NoDoubleFire()
    {
        // Arrange — inbound call answered on the rotary phone (InCall).
        var (cm, adapter, _) = BuildGvCallManager();
        cm.SimulateIncomingCall();
        Assert.Equal(CallState.Ringing, cm.CurrentState);
        cm.HandleHookChange(true); // Off-hook = answer
        Assert.Equal(CallState.InCall, cm.CurrentState);

        // Act — call ends (remote hangup). A CANCEL-then-BYE pair (or a single BYE) both route
        // here as OnCallEnded; raise it twice to prove the HangUp re-entry / Idle guard collapses
        // them into a single teardown.
        adapter.Raise(a => a.OnCallEnded += null);
        adapter.Raise(a => a.OnCallEnded += null);

        // Assert — one clean teardown to Idle; CancelPendingInvite fired once, not twice.
        Assert.Equal(CallState.Idle, cm.CurrentState);
        Assert.Null(cm.IncomingPhoneNumber);
        _mockSipAdapter.Verify(s => s.CancelPendingInvite(), Times.Once);
    }

    #endregion
}
