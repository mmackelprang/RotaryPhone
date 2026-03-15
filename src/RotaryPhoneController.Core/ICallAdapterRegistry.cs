namespace RotaryPhoneController.Core;

/// <summary>
/// Runtime mode switcher for call adapters. Holds all registered ICallAdapter
/// implementations and routes calls through the currently active one.
/// </summary>
public interface ICallAdapterRegistry
{
    CallAdapterMode ActiveMode { get; }
    ICallAdapter ActiveAdapter { get; }
    IReadOnlyList<CallAdapterMode> AvailableModes { get; }
    event Action<CallAdapterMode>? OnModeChanged;
    Task SwitchModeAsync(CallAdapterMode mode, CancellationToken ct = default);
    void Register(ICallAdapter adapter);
}
