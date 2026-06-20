using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Clients;
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
        services.AddSingleton<IGvAuthenticatedClientProvider>(
            sp => sp.GetRequiredService<GVApiAdapter>());
        services.AddSingleton<IGvCookieManager, GvCookieManager>();
        services.AddSingleton<ICdpCookieExtractor, CdpCookieExtractor>();

        // Read-side voicemail clients ride the adapter's authenticated HttpClient (PR1 seam) so they
        // inherit cookie rotation + the recovery ladder. CRITICAL (ADR §1.3 activation-order): the
        // adapter activates AFTER startup (when cookies load) and GetAuthenticatedClient() returns
        // null until then. These factories must NOT resolve a live HttpClient at container-build time
        // — they pass the IGvAuthenticatedClientProvider into provider-backed constructors that fetch
        // the live client PER CALL (and degrade to a failure result when it is still null), so the
        // container builds and the app starts even with the adapter inactive.
        services.AddSingleton<IGvThreadParser, PositionalGvThreadParser>();

        services.AddSingleton<IGvRecordingFetcher>(sp => new GvRecordingFetcher(
            sp.GetRequiredService<IGvAuthenticatedClientProvider>(),
            sp.GetRequiredService<ILogger<GvRecordingFetcher>>()));

        services.AddSingleton(sp => new GvThreadClient(
            sp.GetRequiredService<IGvAuthenticatedClientProvider>(),
            sp.GetRequiredService<IGvThreadParser>(),
            sp.GetRequiredService<ILogger<GvThreadClient>>()));

        services.AddSingleton(sp => new GvVoicemailClient(
            sp.GetRequiredService<GvThreadClient>(),
            sp.GetRequiredService<IGvThreadParser>(),
            sp.GetRequiredService<IGvRecordingFetcher>(),
            sp.GetRequiredService<ILogger<GvVoicemailClient>>()));

        services.AddSingleton<GvVoicemailCache>();

        // SMS read client + thread poller (PR3). Both ride the same provider-backed GvThreadClient /
        // GvVoicemailClient (per-call auth resolution from PR2), so registering them does NOT resolve a
        // live HttpClient at container-build time — the poller starts even with the adapter inactive and
        // simply raises nothing until cookies load (ADR §1.3 activation-order, §5.3).
        // SMS send (PR4 — ships dark behind EnableSmsSend). Provider-backed GvSmsClient so SendAsync resolves the live
        // authenticated client per call (cookie rotation + recovery ladder, ADR §1.3, §7).
        services.AddSingleton<GvSmsClient>(sp => new GvSmsClient(
            sp.GetRequiredService<GvThreadClient>(),
            sp.GetRequiredService<IGvThreadParser>(),
            sp.GetRequiredService<IGvAuthenticatedClientProvider>(),
            sp.GetRequiredService<ILogger<GvSmsClient>>()));

        services.AddSingleton<GvThreadPoller>();
        services.AddSingleton<IGvMessageEventSource>(sp => sp.GetRequiredService<GvThreadPoller>());
        services.AddHostedService(sp => sp.GetRequiredService<GvThreadPoller>());

        services.AddSingleton<ISmsThreadIdResolver, SmsThreadIdResolver>();
        services.AddSingleton<IGvOutboundSmsSink>(sp => sp.GetRequiredService<GvThreadPoller>());
        services.AddSingleton<SmsSendRateLimiter>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<GVBridgeConfig>>().Value;
            return new SmsSendRateLimiter(cfg.SmsSendMaxPerWindow,
                TimeSpan.FromSeconds(cfg.SmsSendWindowSeconds));
        });

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
