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
    private readonly int _rtpPort;
    private CallState _currentState;
    private string _dialedNumber = string.Empty;
    private CallHistoryEntry? _currentCallHistory;

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

    public CallManager(
        ISipAdapter sipAdapter, 
        IBluetoothHfpAdapter bluetoothAdapter,
        IRtpAudioBridge rtpBridge,
        ILogger<CallManager> logger,
        RotaryPhoneConfig phoneConfig,
        int rtpPort = 49000,
        ICallHistoryService? callHistoryService = null)
    {
        _sipAdapter = sipAdapter;
        _bluetoothAdapter = bluetoothAdapter;
        _rtpBridge = rtpBridge;
        _logger = logger;
        _phoneConfig = phoneConfig;
        _rtpPort = rtpPort;
        _callHistoryService = callHistoryService;
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
                    // User picked up handset - transition to dialing
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
        
        if (CurrentState != CallState.Idle)
        {
            _logger.LogWarning("Cannot handle incoming call - not in Idle state. Current state: {State}", CurrentState);
            return;
        }

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
    
    private void HandleCallAnsweredOnCellPhone()
    {
        _logger.LogInformation("Call answered on cell phone device");
        
        if (CurrentState != CallState.Ringing)
        {
            _logger.LogWarning("Call answered on cell phone but not in Ringing state. Current state: {State}", CurrentState);
            return;
        }
        
        // Audio automatically routes to cell phone without user intervention
        _logger.LogInformation("Automatically routing audio to cell phone");
        
        // Start RTP bridge with cell phone audio route (use actual RTP port, not SIP extension)
        var rtpEndpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
        _ = _rtpBridge.StartBridgeAsync(rtpEndpoint, AudioRoute.CellPhone);
        
        // Update call history
        if (_currentCallHistory != null)
        {
            _currentCallHistory.AnsweredOn = CallAnsweredOn.CellPhone;
            _callHistoryService?.UpdateCallHistory(_currentCallHistory);
        }
        
        CurrentState = CallState.InCall;
        _logger.LogInformation("Call answered on cell phone - audio routed to cell phone");
    }
    
    private void HandleBluetoothCallEnded()
    {
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
        
        // Answer call via Bluetooth HFP with audio routed to rotary phone
        _ = _bluetoothAdapter.AnswerCallAsync(AudioRoute.RotaryPhone);
        
        // Start RTP audio bridge (use actual RTP port, not SIP extension)
        var rtpEndpoint = $"{_phoneConfig.HT801IpAddress}:{_rtpPort}";
        _ = _rtpBridge.StartBridgeAsync(rtpEndpoint, AudioRoute.RotaryPhone);
        
        // Update call history
        if (_currentCallHistory != null)
        {
            _currentCallHistory.AnsweredOn = CallAnsweredOn.RotaryPhone;
            _callHistoryService?.UpdateCallHistory(_currentCallHistory);
        }
        
        CurrentState = CallState.InCall;
        _logger.LogInformation("Call answered successfully - audio routed through rotary phone");
    }

    public void StartCall(string number)
    {
        _logger.LogInformation("Starting call to: {Number}", number);
        
        if (CurrentState != CallState.Dialing)
        {
            _logger.LogWarning("Cannot start call - not in Dialing state. Current state: {State}", CurrentState);
            return;
        }

        // Initiate call via Bluetooth HFP
        _logger.LogInformation("Initiating call via Bluetooth HFP for: {Number}", number);
        _ = _bluetoothAdapter.InitiateCallAsync(number);
        
        // Create call history entry
        _currentCallHistory = new CallHistoryEntry
        {
            PhoneNumber = number,
            Direction = CallDirection.Outgoing,
            PhoneId = _phoneConfig.Id,
            StartTime = DateTime.Now
        };
        _callHistoryService?.AddCallHistory(_currentCallHistory);
        
        CurrentState = CallState.InCall;
        DialedNumber = number;
        _logger.LogInformation("Call started successfully");
    }

    public void HangUp()
    {
        _logger.LogInformation("Hanging up");
        
        var previousState = CurrentState;
        
        // Terminate Bluetooth call
        _ = _bluetoothAdapter.TerminateCallAsync();
        
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
        
        _logger.LogInformation("Call terminated. State reset. Previous state was: {PreviousState}", previousState);
    }
}
