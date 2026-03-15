using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
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

        services.AddSingleton<GVBrowserAdapter>();
        services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVBrowserAdapter>());

        services.AddSingleton<GVSmsService>();

        return services;
    }

    public static IEndpointRouteBuilder MapGVBridge(this IEndpointRouteBuilder endpoints)
    {
        // Hub and controller endpoints added in Phase D
        return endpoints;
    }
}
