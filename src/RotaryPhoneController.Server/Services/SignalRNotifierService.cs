using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Core;
using RotaryPhoneController.Server.Hubs;

namespace RotaryPhoneController.Server.Services;

public class SignalRNotifierService : IHostedService
{
    private readonly PhoneManagerService _phoneManager;
    private readonly IHubContext<RotaryHub> _hubContext;
    private readonly ILogger<SignalRNotifierService> _logger;

    public SignalRNotifierService(
        PhoneManagerService phoneManager, 
        IHubContext<RotaryHub> hubContext,
        ILogger<SignalRNotifierService> logger)
    {
        _phoneManager = phoneManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SignalR Notifier Service");

        foreach (var (phoneId, manager) in _phoneManager.GetAllPhones())
        {
            _logger.LogInformation("Subscribing to events for phone: {PhoneId}", phoneId);
            manager.StateChanged += () => OnStateChanged(phoneId, manager);
        }

        return Task.CompletedTask;
    }

    private void OnStateChanged(string phoneId, CallManager manager)
    {
        _logger.LogInformation("Broadcasting state change for {PhoneId}: {State}", phoneId, manager.CurrentState);
        _hubContext.Clients.All.SendAsync("CallStateChanged", phoneId, manager.CurrentState.ToString());
        
        // If ringing, we could also broadcast incoming call details if we had them easily accessible
        // For now, StateChanged is the main driver
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
