namespace RotaryPhoneController.Core;

/// <summary>
/// Universal adapter for a remote call path (BT phone, SIP trunk, GV browser).
/// Note: This does NOT replace ISipAdapter. ISipAdapter handles the local HT801
/// phone interface. ICallAdapter handles the remote side of the call.
/// </summary>
public interface ICallAdapter
{
    CallAdapterMode Mode { get; }
    bool IsAvailable { get; }
    event Action<bool>? OnAvailabilityChanged;
    event Action<string>? OnIncomingCall;
    event Action? OnCallAnswered;
    event Action? OnCallEnded;
    event Action<string>? OnDtmfReceived;
    Task ActivateAsync(CancellationToken ct = default);
    Task DeactivateAsync(CancellationToken ct = default);
    Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default);
    Task AnswerCallAsync(CancellationToken ct = default);
    Task HangUpAsync(CancellationToken ct = default);
}
