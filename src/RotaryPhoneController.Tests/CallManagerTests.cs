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
}
