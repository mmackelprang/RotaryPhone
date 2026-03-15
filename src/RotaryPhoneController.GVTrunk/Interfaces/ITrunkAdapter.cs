using RotaryPhoneController.Core;

namespace RotaryPhoneController.GVTrunk.Interfaces;

public interface ITrunkAdapter : ISipAdapter
{
    bool IsRegistered { get; }
    event Action<bool> OnRegistrationChanged;
    event Action<string>? OnDtmfReceived;
    void StartListening();
    Task RegisterAsync(CancellationToken ct = default);
    Task UnregisterAsync(CancellationToken ct = default);
    Task<string> PlaceOutboundCallAsync(string e164Number);
}
