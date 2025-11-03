namespace RotaryPhoneController.Core;

public interface IGpioService
{
    bool ReadHookState();
    void SetRingControl(bool active);
    void SetInterruptHandler(int pin, Action callback);
}
