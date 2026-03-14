using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.CallHistory;
using RotaryPhoneController.Core.Configuration;

namespace RotaryPhoneController.Core;

public class CallManager
{
    private readonly ISipAdapter _sipAdapter;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly IRtpAudioBridge _rtpBridge;
    private readonly ICallHistoryService? _callHistoryService;
    private readonly ILogger<CallManager> _logger;
    private readonly RotaryPhoneConfig _phoneConfig;
    private readonly IBluetoothDeviceManager? _deviceManager;
    private readonly int _rtpPort;
    private CallState _currentState;
    private string _dialedNumber = string.Empty;
    private CallHistoryEntry? _currentCallHistory;
    private bool _isHangingUp;
    private string? _activeDeviceAddress;

    public event Action? StateChanged;

    public CallState CurrentState
    {
        get => _currentState;
        private set
        {
            if (_currentState != value)
            {
                _currentState = value;
                _logger.LogInformation("State changed to: {State}", _currentState);
                StateChanged?.Invoke();
            }
        }
    }

    public string DialedNumber
    {
        get => _dialedNumber;
        private set
        {
            _dialedNumber = value;
            _logger.LogInformation("Dialed number updated: {Number}", _dialedNumber);
        }
    }

    /// <summary>
    /// Phone number of the current incoming call (set during Ringing state, cleared on Idle).
    /// </summary>
    public string? IncomingPhoneNumber { get; private set; }

    /// <summary>
    /// UTC timestamp when the current call entered InCall state. Null when idle.
    /// </summary>
    public DateTime? CallStartedAtUtc { get; private set; }

    public CallManager(
        ISipAdapter sipAdapter,
        IBluetoothHfpAdapter bluetoothAdapter,
        IRtpAudioBridge rtpBridge,
        ILogger<CallManager> logger,
        RotaryPhoneConfig phoneConfig,
        int rtpPort = 49000,
        ICallHistoryService? callHistoryService = null,
        IBluetoothDeviceManager? deviceManager = null)
    {
        _sipAdapter = sipAdapter;
        _bluetoothAdapter = bluetoothAdapter;
        _rtpBridge = rtpBridge;
        _logger = logger;
        _phoneConfig = phoneConfig;
        _rtpPort = rtpPort;
        _callHistoryService = callHistoryService;
        _deviceManager = deviceManager;
        _currentState = CallState.Idle;
    }

    public void Initialize()
    {
        _logger.LogInformation("Initializing CallManager for phone: {PhoneId} ({PhoneName})", 
            _phoneConfig.Id, _phoneConfig.Name);
        
        // Subscribe to SIP adapter events
        _sipAdapter.OnHookChange += HandleHookChange;
        _sipAdapter.OnDigitsReceived += HandleDigitsReceived;
        _sipAdapter.OnIncomingCall += HandleIncomingCall;
        
        // Subscribe to Bluetooth HFP events
        _bluetoothAdapter.OnIncomingCall += HandleBluetoothIncomingCall;
        _bluetoothAdapter.OnCallAnsweredOnCellPhone += HandleCallAnsweredOnCellPhone;
        _bluetoothAdapter.OnCallEnded += HandleBluetoothCallEnded;
        _bluetoothAdapter.OnAudioRouteChanged += HandleAudioRouteChanged;

        // Subscribe to multi-device BT manager events (if available)
        if (_deviceManager != null)
        {
            _deviceManager.OnIncomingCall += HandleDeviceIncomingCall;
            _deviceManager.OnCallAnsweredOnPhone += HandleDeviceCallAnsweredOnPhone;
            _deviceManager.OnCallActive += HandleDeviceCallActive;
            _deviceManager.OnCallEnded += HandleDeviceCallEnded;
            _deviceManager.OnScoAudioConnected += HandleScoConnected;
            _deviceManager.OnScoAudioDisconnected += HandleScoDisconnected;
        }

        _logger.LogInformation("CallManager initialized successfully");
    }

    public void HandleHookChange(bool isOffHook)
    {
        _logger.LogInformation("Hook change: {State}", isOffHook ? "OFF-HOOK" : "ON-HOOK");

        if (isOffHook)
        {
            // Handset lifted
            switch (CurrentState)
            {
                case CallState.Idle:
                    // Check for available BT devices before allowing dialing
                    if (_deviceManager != null)
                    {
                        var connected = _deviceManager.ConnectedDevices;
                        if (connected.Count == 0)
                        {
                            _logger.LogWarning("No BT devices connected — cannot make calls");
                            return;
                        }
                        _activeDeviceAddress = connected[0].Address;
                    }
                    CurrentState = CallState.Dialing;
                    DialedNumber = string.Empty;
                    break;

                case CallState.Ringing:
                    // User answered incoming call
                    AnswerCall();
                    break;
            }
        }
        else
        {
            // Handset placed on hook - always hang up
            HangUp();
        }
    }

    public void HandleDigitsReceived(string number)
    {
        _logger.LogInformation("Digits received: {Number}", number);
        DialedNumber = number;

        if (CurrentState == CallState.Dialing)
        {
            // Transition to InCall and start the call
            StartCall(number);
        }
    }

    private void HandleIncomingCall()
    {
        _logger.LogInformation("Incoming call detected");
        
        if (CurrentState == CallState.Idle)
        {
            SimulateIncomingCall();
        }
    }

    public void SimulateIncomingCall()
    {
        _logger.LogInformation("Simulating incoming call");

        if (CurrentState != CallState.Idle)
        {
            _logger.LogWarning("Cannot simulate incoming call - not in Idle state. Current state: {State}", CurrentState);
            return;
        }

        IncomingPhoneNumber = "Unknown";
        CurrentState = CallState.Ringing;
        
        // Send INVITE to HT801 to trigger ring
        _sipAdapter.SendInviteToHT801(_phoneConfig.HT801Extension, _phoneConfig.HT801IpAddress);
        
        // Create call history entry for incoming call
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = "Unknown",
            Direction = CallDirection.Incoming,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);
        
        _logger.LogInformation("Incoming call simulation initiated - HT801 should ring");
    }
    
    private void HandleBluetoothIncomingCall(string phoneNumber)
    {
        _logger.LogInformation("Bluetooth incoming call from: {PhoneNumber}", phoneNumber);

        // If already ringing and we get an updated caller ID (e.g., +CLIP arrived after +CIEV),
        // update the phone number and re-broadcast
        if (CurrentState == CallState.Ringing && phoneNumber != "Unknown" && IncomingPhoneNumber == "Unknown")
        {
            IncomingPhoneNumber = phoneNumber;
            if (_currentCallHistory != null)
                _currentCallHistory.PhoneNumber = phoneNumber;
            _logger.LogInformation("Caller ID updated to: {PhoneNumber}", phoneNumber);
            StateChanged?.Invoke(); // Re-broadcast with updated number
            return;
        }

        if (CurrentState != CallState.Idle)
        {
            _logger.LogWarning("Cannot handle incoming call - not in Idle state. Current state: {State}", CurrentState);
            return;
        }

        IncomingPhoneNumber = phoneNumber;
        CurrentState = CallState.Ringing;
        
        // Send INVITE to HT801 to trigger ring
        _sipAdapter.SendInviteToHT801(_phoneConfig.HT801Extension, _phoneConfig.HT801IpAddress);
        
        // Create call history entry
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = phoneNumber,
            Direction = CallDirection.Incoming,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);
        
        _logger.LogInformation("Incoming call - ringing rotary phone");
    }

    /// <summary>
    /// Called by Radio.API (via SignalR) when it resolves a caller's name from PBAP contacts.
    /// </summary>
    public void SetResolvedCallerName(string phoneNumber, string displayName)
    {
        if (_currentCallHistory != null && _currentCallHistory.PhoneNumber == phoneNumber)
        {
            _currentCallHistory.CallerName = displayName;
            _logger.LogInformation("Caller resolved: {PhoneNumber} → {DisplayName}", phoneNumber, displayName);
        }
    }

    private void HandleCallAnsweredOnCellPhone()
    {
        _logger.LogInformation("Call answered on cell phone device");

        if (CurrentState != CallState.Ringing)
        {
            _logger.LogWarning("Call answered on cell phone but not in Ringing state. Current state: {State}", CurrentState);
            return;
        }

        // Cancel the SIP INVITE so the rotary phone stops ringing
        _sipAdapter.CancelPendingInvite();

        // Audio stays on the cell phone — no RTP bridge needed
        _logger.LogInformation("Call answered on cell phone — audio stays on phone, rotary ring cancelled");

        // Update call history
        if (_currentCallHistory != null)
        {
            _currentCallHistory.AnsweredOn = CallAnsweredOn.CellPhone;
            _callHistoryService?.UpdateCallHistory(_currentCallHistory);
        }

        CallStartedAtUtc = DateTime.UtcNow;
        CurrentState = CallState.InCall;
    }
    
    private void HandleBluetoothCallEnded()
    {
        if (CurrentState == CallState.Idle) return;
        _logger.LogInformation("Bluetooth call ended");
        HangUp();
    }
    
    private void HandleAudioRouteChanged(AudioRoute route)
    {
        _logger.LogInformation("Audio route changed to: {Route}", route);

        // Update RTP bridge routing if active
        if (_rtpBridge.IsActive && CurrentState == CallState.InCall)
        {
            _ = _rtpBridge.ChangeAudioRouteAsync(route);
        }
    }

    #region Device-Aware Call Handlers (IBluetoothDeviceManager)

    private void HandleDeviceIncomingCall(BluetoothDevice device, string number)
    {
        _activeDeviceAddress = device.Address;
        HandleBluetoothIncomingCall(number);
    }

    private void HandleDeviceCallAnsweredOnPhone(BluetoothDevice device)
    {
        if (_activeDeviceAddress == device.Address)
            HandleCallAnsweredOnCellPhone();
    }

    private void HandleDeviceCallActive(BluetoothDevice device)
    {
        if (_activeDeviceAddress == device.Address && CurrentState == CallState.Dialing)
        {
            // Outgoing call connected — transition to InCall
            CallStartedAtUtc = DateTime.UtcNow;
            CurrentState = CallState.InCall;
            _logger.LogInformation("Outgoing call connected on {Device}", device.Address);
        }
    }

    private void HandleDeviceCallEnded(BluetoothDevice device)
    {
        if (_activeDeviceAddress == device.Address)
        {
            HandleBluetoothCallEnded();
            _activeDeviceAddress = null;
        }
    }

    private void HandleScoConnected(BluetoothDevice device)
    {
        if (_activeDeviceAddress != device.Address) return;

        _logger.LogInformation("SCO audio connected for {Device} — starting RTP bridge", device.Address);
        var rtpEndpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
        _ = _rtpBridge.StartBridgeAsync(rtpEndpoint, AudioRoute.RotaryPhone);
    }

    private void HandleScoDisconnected(BluetoothDevice device)
    {
        if (_activeDeviceAddress != device.Address) return;

        _logger.LogInformation("SCO audio disconnected for {Device} — stopping RTP bridge", device.Address);
        if (_rtpBridge.IsActive)
        {
            _ = _rtpBridge.StopBridgeAsync();
        }
    }

    #endregion

    public void AnswerCall()
    {
        _logger.LogInformation("Answering call");
        
        if (CurrentState != CallState.Ringing)
        {
            _logger.LogWarning("Cannot answer call - not in Ringing state. Current state: {State}", CurrentState);
            return;
        }

        // Call answered on rotary phone (handset lifted) - route audio through rotary phone
        _logger.LogInformation("Call answered on rotary phone - routing audio through rotary phone");

        if (_deviceManager != null && _activeDeviceAddress != null)
        {
            // Answer via device manager — sends ATA over RFCOMM, SCO opens,
            // HandleScoConnected will start the RTP bridge
            _ = _deviceManager.AnswerCallAsync(_activeDeviceAddress);
        }
        else
        {
            // Legacy path
            _ = _bluetoothAdapter.AnswerCallAsync(AudioRoute.RotaryPhone);
            var rtpEndpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
            _ = _rtpBridge.StartBridgeAsync(rtpEndpoint, AudioRoute.RotaryPhone);
        }

        // Update call history
        if (_currentCallHistory != null)
        {
            _currentCallHistory.AnsweredOn = CallAnsweredOn.RotaryPhone;
            _callHistoryService?.UpdateCallHistory(_currentCallHistory);
        }

        CallStartedAtUtc = DateTime.UtcNow;
        CurrentState = CallState.InCall;
        _logger.LogInformation("Call answered on rotary phone");
    }

    public void StartCall(string number)
    {
        _logger.LogInformation("Starting call to: {Number}", number);

        if (CurrentState != CallState.Dialing)
        {
            _logger.LogWarning("Cannot start call - not in Dialing state. Current state: {State}", CurrentState);
            return;
        }

        // Create call history entry
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = number,
            Direction = CallDirection.Outgoing,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);

        DialedNumber = number;

        if (_deviceManager != null && _activeDeviceAddress != null)
        {
            // Use device manager — dial via the active BT device, stay in Dialing
            // until call_active event arrives (HandleDeviceCallActive transitions to InCall)
            _logger.LogInformation("Dialing {Number} via device {Device}", number, _activeDeviceAddress);
            _ = _deviceManager.DialAsync(_activeDeviceAddress, number);
        }
        else
        {
            // Legacy path — use old single-device adapter
            _logger.LogInformation("Initiating call via Bluetooth HFP for: {Number}", number);
            _ = _bluetoothAdapter.InitiateCallAsync(number);
            CurrentState = CallState.InCall;
        }

        _logger.LogInformation("Call initiated for {Number}", number);
    }

    public void HangUp()
    {
        if (_isHangingUp)
        {
            _logger.LogDebug("HangUp re-entry detected; ignoring to prevent recursion");
            return;
        }

        _isHangingUp = true;
        _logger.LogInformation("Hanging up");
        var previousState = CurrentState;

        try
        {
            // Cancel any pending SIP INVITE so the HT801 stops ringing
            _sipAdapter.CancelPendingInvite();

            // Terminate Bluetooth call
            if (_deviceManager != null && _activeDeviceAddress != null)
            {
                _ = _deviceManager.HangupCallAsync(_activeDeviceAddress);
            }
            else
            {
                _ = _bluetoothAdapter.TerminateCallAsync();
            }

            // Stop RTP bridge
            if (_rtpBridge.IsActive)
            {
                _ = _rtpBridge.StopBridgeAsync();
            }

            // Update call history
            if (_currentCallHistory != null)
            {
                _currentCallHistory.EndTime = DateTime.Now;
                _callHistoryService?.UpdateCallHistory(_currentCallHistory);
                _currentCallHistory = null;
            }

            CurrentState = CallState.Idle;
            DialedNumber = string.Empty;
            IncomingPhoneNumber = null;
            CallStartedAtUtc = null;
            _activeDeviceAddress = null;
            _logger.LogInformation("Call terminated. State reset. Previous state was: {PreviousState}", previousState);
        }
        finally
        {
            _isHangingUp = false;
        }
    }
}
