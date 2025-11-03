using Serilog;

namespace RotaryPhoneController.Core;

public class MockGpioService : IGpioService
{
    private bool _hookState = false; // false = on-hook, true = off-hook
    private bool _ringControl = false;
    private Dictionary<int, Action> _interruptHandlers = new();

    public bool ReadHookState()
    {
        Log.Debug("MockGpioService: ReadHookState called, returning {HookState}", _hookState);
        return _hookState;
    }

    public void SetRingControl(bool active)
    {
        _ringControl = active;
        Log.Information("MockGpioService: Ring control set to {Active}", active);
    }

    public void SetInterruptHandler(int pin, Action callback)
    {
        _interruptHandlers[pin] = callback;
        Log.Debug("MockGpioService: Interrupt handler set for pin {Pin}", pin);
    }

    // Mock methods for testing
    public void SimulateHookChange(bool isOffHook)
    {
        _hookState = isOffHook;
        Log.Information("MockGpioService: Simulated hook change to {State}", isOffHook ? "OFF-HOOK" : "ON-HOOK");
    }

    public void SimulatePulse(int pin)
    {
        if (_interruptHandlers.TryGetValue(pin, out var callback))
        {
            Log.Debug("MockGpioService: Simulating pulse on pin {Pin}", pin);
            callback();
        }
    }

    public void SimulateDigit(int digit, int pulsePin)
    {
        if (digit < 0 || digit > 9)
        {
            Log.Warning("MockGpioService: Invalid digit {Digit}, must be 0-9", digit);
            return;
        }

        int pulseCount = digit == 0 ? 10 : digit;
        Log.Information("MockGpioService: Simulating digit {Digit} with {PulseCount} pulses", digit, pulseCount);

        for (int i = 0; i < pulseCount; i++)
        {
            SimulatePulse(pulsePin);
            Thread.Sleep(60); // Simulate pulse spacing
        }
    }
}
