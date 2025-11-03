using Serilog;

namespace RotaryPhoneController.Core;

public class CallManager : IDisposable
{
    private const int PULSE_PIN = 17; // Example GPIO pin number
    private const int RING_CONTROL_PIN = 27; // Example GPIO pin number
    
    private readonly IGpioService _gpioService;
    private readonly RotaryDecoder _rotaryDecoder;
    private CallState _currentState = CallState.Idle;
    private string _dialedNumber = string.Empty;
    private readonly object _stateLock = new();
    private Task? _ringerTask;
    private CancellationTokenSource? _ringerCts;
    private bool _disposed = false;

    public event Action? StateChanged;

    public CallState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                if (_currentState != value)
                {
                    var oldState = _currentState;
                    _currentState = value;
                    Log.Information("CallManager: State transition: {OldState} -> {NewState}", oldState, value);
                    StateChanged?.Invoke();
                }
            }
        }
    }

    public string DialedNumber
    {
        get
        {
            lock (_stateLock)
            {
                return _dialedNumber;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _dialedNumber = value;
                StateChanged?.Invoke();
            }
        }
    }

    public CallManager(IGpioService gpioService)
    {
        _gpioService = gpioService;
        _rotaryDecoder = new RotaryDecoder();
        _rotaryDecoder.DigitDecoded += OnDigitDecoded;
        
        Log.Information("CallManager: Initialized");
    }

    public void Initialize()
    {
        // Set up GPIO interrupt handlers
        _gpioService.SetInterruptHandler(PULSE_PIN, _rotaryDecoder.HandlePulseInterrupt);
        Log.Information("CallManager: GPIO interrupt handlers configured");
    }

    public void HandleHookChange(bool isOffHook)
    {
        Log.Information("CallManager: Hook change detected: {State}", isOffHook ? "OFF-HOOK" : "ON-HOOK");

        if (isOffHook && CurrentState == CallState.Idle)
        {
            // Transition to DIALING
            CurrentState = CallState.Dialing;
            DialedNumber = string.Empty;
            _rotaryDecoder.Reset();
            Log.Information("CallManager: Started dialing mode");
        }
        else if (isOffHook && CurrentState == CallState.Ringing)
        {
            // Answer incoming call
            StopRinger();
            CurrentState = CallState.InCall;
            Log.Information("CallManager: Call answered");
        }
        else if (!isOffHook && CurrentState != CallState.Idle)
        {
            // Hang up
            HangUp();
        }
    }

    private void OnDigitDecoded(int digit)
    {
        if (CurrentState == CallState.Dialing)
        {
            DialedNumber += digit.ToString();
            Log.Information("CallManager: Digit added: {Digit}, Current number: {DialedNumber}", digit, DialedNumber);
            
            // Note: In production, dialing completion would be determined by user action or timeout
            // Auto-dial is disabled in this mock implementation to allow testing
        }
    }

    public void SimulateIncomingCall()
    {
        Log.Information("CallManager: Simulating incoming call");
        
        if (CurrentState == CallState.Idle)
        {
            CurrentState = CallState.Ringing;
            ActivateRinger();
        }
        else
        {
            Log.Warning("CallManager: Cannot simulate incoming call in state {State}", CurrentState);
        }
    }

    private void ActivateRinger()
    {
        Log.Information("CallManager: Activating ringer");
        
        // Stop any existing ringer task before starting a new one
        StopRinger();
        
        lock (_stateLock)
        {
            _ringerCts = new CancellationTokenSource();
            var token = _ringerCts.Token;
            
            _ringerTask = Task.Run(async () =>
            {
                try
                {
                    while (CurrentState == CallState.Ringing && !token.IsCancellationRequested)
                    {
                        Log.Debug("CallManager: Ring cycle - ON");
                        _gpioService.SetRingControl(true);
                        await Task.Delay(1500, token); // 1.5s on
                        
                        if (CurrentState != CallState.Ringing || token.IsCancellationRequested)
                            break;
                        
                        Log.Debug("CallManager: Ring cycle - OFF");
                        _gpioService.SetRingControl(false);
                        await Task.Delay(3000, token); // 3s off
                    }
                }
                catch (TaskCanceledException)
                {
                    Log.Debug("CallManager: Ringer task cancelled");
                }
                finally
                {
                    _gpioService.SetRingControl(false);
                    Log.Information("CallManager: Ringer deactivated");
                }
            }, token);
        }
    }

    private void StopRinger()
    {
        lock (_stateLock)
        {
            if (_ringerCts != null)
            {
                Log.Information("CallManager: Stopping ringer");
                _ringerCts.Cancel();
                _ringerCts.Dispose();
                _ringerCts = null;
            }
            
            if (_ringerTask != null)
            {
                // Don't wait for the task to complete to avoid blocking
                _ringerTask = null;
            }
        }
    }

    public void StartCall(string number)
    {
        Log.Information("CallManager: Starting call to {Number}", number);
        
        if (CurrentState == CallState.Dialing)
        {
            CurrentState = CallState.InCall;
            // Mock Bluetooth HFP call initiation
            Log.Information("CallManager: [MOCK] Bluetooth HFP call initiated to {Number}", number);
        }
    }

    public void HangUp()
    {
        Log.Information("CallManager: Hanging up");
        
        StopRinger();
        
        // Mock Bluetooth HFP termination
        if (CurrentState == CallState.InCall)
        {
            Log.Information("CallManager: [MOCK] Bluetooth HFP connection terminated");
        }
        
        CurrentState = CallState.Idle;
        DialedNumber = string.Empty;
        _rotaryDecoder.Reset();
        
        Log.Information("CallManager: Returned to idle state");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                StopRinger();
                _rotaryDecoder.Dispose();
                _ringerCts?.Dispose();
            }
            _disposed = true;
        }
    }
}
