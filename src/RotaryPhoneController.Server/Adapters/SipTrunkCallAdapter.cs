using Microsoft.Extensions.Logging;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVTrunk.Interfaces;

namespace RotaryPhoneController.Server.Adapters;

/// <summary>
/// ICallAdapter wrapper for the SIP Trunk (VoIP.ms) call path.
/// Delegates to ITrunkAdapter from the GVTrunk project.
/// Lives in Server (not Core) to avoid circular dependency: Core → GVTrunk → Core.
/// </summary>
public class SipTrunkCallAdapter : ICallAdapter
{
    private readonly ITrunkAdapter _trunk;
    private readonly ILogger<SipTrunkCallAdapter> _logger;

    public CallAdapterMode Mode => CallAdapterMode.SipTrunk;
    public bool IsAvailable => _trunk.IsRegistered;

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public SipTrunkCallAdapter(ITrunkAdapter trunk, ILogger<SipTrunkCallAdapter> logger)
    {
        _trunk = trunk;
        _logger = logger;
    }

    public Task ActivateAsync(CancellationToken ct = default)
    {
        _trunk.OnRegistrationChanged += registered => OnAvailabilityChanged?.Invoke(registered);
        _trunk.OnIncomingCall += () => OnIncomingCall?.Invoke("Unknown");
        _trunk.OnHookChange += isOffHook =>
        {
            if (!isOffHook) OnCallEnded?.Invoke();
        };

        _trunk.StartListening();
        _ = _trunk.RegisterAsync(ct);
        _logger.LogInformation("SipTrunkCallAdapter activated");
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        await _trunk.UnregisterAsync(ct);
        _logger.LogInformation("SipTrunkCallAdapter deactivated");
    }

    public async Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default)
    {
        return await _trunk.PlaceOutboundCallAsync(e164Number);
    }

    public Task AnswerCallAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("SipTrunkCallAdapter.AnswerCallAsync — delegating to HT801 SIP adapter");
        return Task.CompletedTask;
    }

    public Task HangUpAsync(CancellationToken ct = default)
    {
        _trunk.CancelPendingInvite();
        return Task.CompletedTask;
    }
}
