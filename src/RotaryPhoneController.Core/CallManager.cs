using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core;

public class CallManager
{
    private readonly ISipAdapter _sipAdapter;
    private readonly ILogger<CallManager> _logger;
    private CallState _currentState;
    private string _dialedNumber = string.Empty;

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

    public CallManager(ISipAdapter sipAdapter, ILogger<CallManager> logger)
    {
        _sipAdapter = sipAdapter;
        _logger = logger;
        _currentState = CallState.Idle;
    }

    public void Initialize()
    {
        _logger.LogInformation("Initializing CallManager");
        
        // Subscribe to SIP adapter events
        _sipAdapter.OnHookChange += HandleHookChange;
        _sipAdapter.OnDigitsReceived += HandleDigitsReceived;
        _sipAdapter.OnIncomingCall += HandleIncomingCall;
        
        // TODO: When HFP implementation is complete, subscribe to Bluetooth HFP events
        // to detect when calls are answered on the cell phone device and automatically
        // route audio to the cell phone without user intervention
        
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
        // Using default HT801 IP (192.168.1.10) and extension
        _sipAdapter.SendInviteToHT801("1000", "192.168.1.10");
        
        _logger.LogInformation("Incoming call simulation initiated - HT801 should ring");
    }

    public void AnswerCall()
    {
        _logger.LogInformation("Answering call");
        
        if (CurrentState != CallState.Ringing)
        {
            _logger.LogWarning("Cannot answer call - not in Ringing state. Current state: {State}", CurrentState);
            return;
        }

        // Mock function - simulate audio bridge connection
        _logger.LogInformation("Simulating RTP/HFP Audio Bridge connection.");
        
        // TODO: When HFP implementation is complete, ensure audio routing is based on where call was answered:
        // - If answered on rotary phone (handset lifted), route audio through rotary phone
        // - If answered on cell phone device, automatically route all audio to cell phone
        //   without any user intervention to select microphone/speaker
        
        CurrentState = CallState.InCall;
        _logger.LogInformation("Call answered successfully");
    }

    public void StartCall(string number)
    {
        _logger.LogInformation("Starting call to: {Number}", number);
        
        if (CurrentState != CallState.Dialing)
        {
            _logger.LogWarning("Cannot start call - not in Dialing state. Current state: {State}", CurrentState);
            return;
        }

        // Mock function - simulate Bluetooth HFP call initiation
        _logger.LogInformation("Simulating Bluetooth HFP call initiation for: {Number}", number);
        
        CurrentState = CallState.InCall;
        DialedNumber = number;
        _logger.LogInformation("Call started successfully");
    }

    public void HangUp()
    {
        _logger.LogInformation("Hanging up");
        
        var previousState = CurrentState;
        CurrentState = CallState.Idle;
        DialedNumber = string.Empty;
        
        _logger.LogInformation("Call terminated. State reset. Previous state was: {PreviousState}", previousState);
    }
}
