using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RotaryPhoneController.GVTrunk.Adapters;
using RotaryPhoneController.GVTrunk.Api;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using RotaryPhoneController.GVTrunk.Services;

namespace RotaryPhoneController.GVTrunk.Extensions;

public static class GVTrunkServiceExtensions
{
    public static IServiceCollection AddGVTrunk(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TrunkConfig>(configuration.GetSection("GVTrunk"));

        services.AddSingleton<GVTrunkAdapter>();
        services.AddSingleton<ITrunkAdapter>(sp => sp.GetRequiredService<GVTrunkAdapter>());

        services.AddSingleton<CallLogService>(sp =>
        {
            var config = configuration.GetSection("GVTrunk").Get<TrunkConfig>() ?? new TrunkConfig();
            return new CallLogService(config.CallLogDbPath);
        });
        services.AddSingleton<ICallLogService>(sp => sp.GetRequiredService<CallLogService>());
        services.AddHostedService<CallLogInitializer>();

        services.AddSingleton<ISmsProvider, GmailSmsService>();
        services.AddHostedService<SmsServiceStarter>();
        services.AddHostedService<TrunkRegistrationService>();
        services.AddHostedService<GVTrunkEventBridge>();

        return services;
    }

    public static IEndpointRouteBuilder MapGVTrunk(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<GVTrunkHub>("/hubs/gvtrunk");
        endpoints.MapControllers();
        return endpoints;
    }
}

public class SmsServiceStarter : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ISmsProvider _sms;
    public SmsServiceStarter(ISmsProvider sms) => _sms = sms;
    public Task StartAsync(CancellationToken ct) => _sms.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _sms.StopAsync(ct);
}

public class CallLogInitializer : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly CallLogService _callLog;
    public CallLogInitializer(CallLogService callLog) => _callLog = callLog;
    public async Task StartAsync(CancellationToken cancellationToken) => await _callLog.InitializeAsync();
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public class GVTrunkEventBridge : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ITrunkAdapter _trunk;
    private readonly ISmsProvider _sms;
    private readonly IHubContext<GVTrunkHub> _hub;
    private readonly RotaryPhoneController.Core.PhoneManagerService _phoneManager;

    public GVTrunkEventBridge(
        ITrunkAdapter trunk,
        ISmsProvider sms,
        IHubContext<GVTrunkHub> hub,
        RotaryPhoneController.Core.PhoneManagerService phoneManager)
    {
        _trunk = trunk;
        _sms = sms;
        _hub = hub;
        _phoneManager = phoneManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _trunk.OnRegistrationChanged += async (registered) =>
            await _hub.Clients.All.SendAsync("RegistrationChanged", new { isRegistered = registered });

        _sms.OnSmsReceived += async (notification) =>
        {
            GVTrunkSmsCache.Add(notification);
            await _hub.Clients.All.SendAsync("SmsReceived", notification);
        };

        _sms.OnMissedCallReceived += async (notification) =>
        {
            GVTrunkSmsCache.Add(notification);
            await _hub.Clients.All.SendAsync("MissedCallReceived", notification);
        };

        foreach (var (phoneId, callManager) in _phoneManager.GetAllPhones())
        {
            callManager.StateChanged += async () =>
                await _hub.Clients.All.SendAsync("CallStateChanged", new
                {
                    phoneId,
                    callState = callManager.CurrentState.ToString()
                });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
