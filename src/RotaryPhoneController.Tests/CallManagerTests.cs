using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;

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
    public void Bluetooth_OnCallAnsweredOnCellPhone_ShouldRouteAudioToCellPhone()
    {
        // Arrange
        _callManager.SimulateIncomingCall();
        
        // Act
        // Simulate event from Bluetooth adapter
        _mockBluetoothAdapter.Raise(x => x.OnCallAnsweredOnCellPhone += null);

        // Assert
        Assert.Equal(CallState.InCall, _callManager.CurrentState);
        // Verify audio routed to CellPhone
        _mockRtpBridge.Verify(x => x.StartBridgeAsync(It.IsAny<string>(), AudioRoute.CellPhone), Times.Once);
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
}
