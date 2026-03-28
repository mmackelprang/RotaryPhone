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

    /// <summary>
    /// Called by CallManager when the call is answered on the rotary phone (SIP 200 OK).
    /// Adapters that manage their own audio bridge (e.g., GVBrowser) should start it here.
    /// Default implementation is a no-op for adapters that don't need it.
    /// </summary>
    Task OnCallAnsweredOnRotaryPhoneAsync() => Task.CompletedTask;

    /// <summary>
    /// Called by CallManager on hangup. Adapters should stop any audio bridge here.
    /// </summary>
    Task OnCallHungUpAsync() => Task.CompletedTask;
}
