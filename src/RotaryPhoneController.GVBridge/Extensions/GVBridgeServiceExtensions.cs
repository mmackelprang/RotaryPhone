using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        services.AddSingleton<GVAudioBridgeService>();
        services.AddSingleton<GVApiAdapter>();
        services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVApiAdapter>());
        services.AddSingleton<IGvCookieManager, GvCookieManager>();
        services.AddSingleton<ICdpCookieExtractor, CdpCookieExtractor>();

        // HttpClientFactory for CDP cookie extraction (localhost-only, no special config)
        services.AddHttpClient();

        // Wire audio bridge into adapter at startup
        services.AddHostedService(sp =>
        {
            var adapter = sp.GetRequiredService<GVApiAdapter>();
            var audioBridge = sp.GetRequiredService<GVAudioBridgeService>();
            adapter.SetAudioBridge(audioBridge);
            adapter.SetCookieExtractor(sp.GetRequiredService<ICdpCookieExtractor>());
            return new GvApiAdapterSetup();
        });

        return services;
    }

    public static IEndpointRouteBuilder MapGVBridge(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        return endpoints;
    }

    private class GvApiAdapterSetup : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
