using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Extensions;

public static class GVBridgeServiceExtensions
{
    public static IServiceCollection AddGVBridge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GVBridgeConfig>(configuration.GetSection("GVBridge"));

        services.AddSingleton<GVBridgeService>();
        services.AddHostedService(sp => sp.GetRequiredService<GVBridgeService>());
        services.AddSingleton<GVAudioBridgeService>();

        services.AddSingleton<GVBrowserAdapter>();
        services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVBrowserAdapter>());

        services.AddSingleton<GVSmsService>();

        services.AddHostedService<GVBridgeEventBridge>();

        return services;
    }

    public static IEndpointRouteBuilder MapGVBridge(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<GVBridgeHub>("/hubs/gvbridge");
        endpoints.MapControllers();
        return endpoints;
    }
}

public class GVBridgeEventBridge : IHostedService
{
    private readonly GVBridgeService _bridge;
    private readonly ICallAdapterRegistry _registry;
    private readonly IHubContext<GVBridgeHub> _hub;

    public GVBridgeEventBridge(
        GVBridgeService bridge,
        ICallAdapterRegistry registry,
        IHubContext<GVBridgeHub> hub)
    {
        _bridge = bridge;
        _registry = registry;
        _hub = hub;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _bridge.OnConnectionChanged += async connected =>
            await _hub.Clients.All.SendAsync("ExtensionConnectionChanged", new { connected });

        _registry.OnModeChanged += async mode =>
            await _hub.Clients.All.SendAsync("ModeChanged", new { activeMode = mode.ToString() });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
