using Serilog;

namespace RotaryPhoneController.Core;

public class RotaryDecoder : IDisposable
{
    private const int INTER_DIGIT_TIMEOUT_MS = 500;
    private int _pulseCount = 0;
    private Timer? _digitTimer;
    private readonly object _lock = new();
    private bool _disposed = false;
    
    public event Action<int>? DigitDecoded;

    public void HandlePulseInterrupt()
    {
        lock (_lock)
        {
            _pulseCount++;
            Log.Debug("RotaryDecoder: Pulse received, count = {PulseCount}", _pulseCount);
            
            // Reset the digit timer
            StopDigitTimer();
            StartDigitTimer();
        }
    }

    private void StartDigitTimer()
    {
        _digitTimer = new Timer(OnDigitTimerElapsed, null, INTER_DIGIT_TIMEOUT_MS, Timeout.Infinite);
        Log.Debug("RotaryDecoder: Digit timer started");
    }

    private void StopDigitTimer()
    {
        if (_digitTimer != null)
        {
            _digitTimer.Dispose();
            _digitTimer = null;
            Log.Debug("RotaryDecoder: Digit timer stopped");
        }
    }

    private void OnDigitTimerElapsed(object? state)
    {
        lock (_lock)
        {
            if (_pulseCount > 0)
            {
                // Convert pulse count to digit (10 pulses = 0)
                int digit = _pulseCount == 10 ? 0 : _pulseCount;
                Log.Information("RotaryDecoder: Digit decoded: {Digit} (from {PulseCount} pulses)", digit, _pulseCount);
                
                DigitDecoded?.Invoke(digit);
                _pulseCount = 0;
            }
            
            StopDigitTimer();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            StopDigitTimer();
            _pulseCount = 0;
            Log.Debug("RotaryDecoder: Reset");
        }
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
                lock (_lock)
                {
                    StopDigitTimer();
                }
            }
            _disposed = true;
        }
    }
}
