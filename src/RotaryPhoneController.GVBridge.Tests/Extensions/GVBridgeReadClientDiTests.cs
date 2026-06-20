using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Extensions;

/// <summary>
/// Guards the ADR §1.3 activation-order seam: the GV adapter activates AFTER startup (when cookies
/// load) and <see cref="IGvAuthenticatedClientProvider.GetAuthenticatedClient"/> returns null until
/// then. The read-client DI factories must NOT resolve a live HttpClient at container-build/resolve
/// time, and a request that lands before activation must degrade (Succeeded=false / null path) rather
/// than throw. This test registers the read clients exactly as AddGVBridge does, against a provider
/// that always returns null, and proves the container builds, resolves, and serves without throwing.
/// </summary>
public class GVBridgeReadClientDiTests
{
    private sealed class NullClientProvider : IGvAuthenticatedClientProvider
    {
        public HttpClient? GetAuthenticatedClient() => null;
        public string ApiBaseUrl => "https://clients6.google.com/voice/v1/voiceclient";
        public string ApiKey => "test-key";
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<GVBridgeConfig>(_ => { });
        services.AddSingleton<IGvAuthenticatedClientProvider, NullClientProvider>();

        // Mirror of AddGVBridge's read-side registrations (provider-backed, per-call resolution).
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

        return services.BuildServiceProvider();
    }

    [Fact]
    public void ResolvingReadClients_WhenAdapterInactive_DoesNotThrow()
    {
        using var sp = BuildProvider();

        // None of these may resolve (or eagerly call) a live HttpClient at construction.
        var fetcher = sp.GetRequiredService<IGvRecordingFetcher>();
        var threadClient = sp.GetRequiredService<GvThreadClient>();
        var vmClient = sp.GetRequiredService<GvVoicemailClient>();
        var cache = sp.GetRequiredService<GvVoicemailCache>();

        Assert.NotNull(fetcher);
        Assert.NotNull(threadClient);
        Assert.NotNull(vmClient);
        Assert.NotNull(cache);
    }

    [Fact]
    public async Task ReadCalls_WhenClientUnavailable_DegradeWithoutThrowing()
    {
        using var sp = BuildProvider();
        var vmClient = sp.GetRequiredService<GvVoicemailClient>();
        var fetcher = sp.GetRequiredService<IGvRecordingFetcher>();

        var list = await vmClient.ListVoicemailsAsync(count: 20);
        var fetch = await fetcher.FetchAsync("media-1");

        Assert.False(list.Succeeded);
        Assert.Empty(list.Items);
        Assert.False(fetch.Success);
        Assert.Null(fetch.Bytes);
    }
}
