using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.Core;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

public class GVApiAdapterTests
{
    [Fact]
    public void Mode_IsGVApi()
    {
        var adapter = CreateAdapter();
        Assert.Equal(CallAdapterMode.GVApi, adapter.Mode);
    }

    [Fact]
    public void IsAvailable_DefaultsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsAvailable);
    }

    [Fact]
    public void IsSipRegistered_BeforeActivate_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsSipRegistered);
    }

    [Fact]
    public void AreCookiesValid_BeforeActivate_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.AreCookiesValid);
    }

    [Fact]
    public async Task PlaceCallAsync_ThrowsWhenNotAvailable()
    {
        var adapter = CreateAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.PlaceCallAsync("+15551234567"));
    }

    [Fact]
    public void IsWebSocketConnected_BeforeActivate_ReturnsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsWebSocketConnected);
    }

    [Fact]
    public void SipLastConnectedAt_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.SipLastConnectedAt);
    }

    [Fact]
    public void PsidtsAgeSeconds_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.PsidtsAgeSeconds);
    }

    [Fact]
    public void Degraded_BeforeActivate_IsFalse()
    {
        // Not activated/available → not "degraded" (that's conveyed by available:false instead);
        // Degraded is gated on IsAvailable so an inactive adapter doesn't raise a false alarm.
        var adapter = CreateAdapter();
        Assert.False(adapter.IsAvailable);
        Assert.False(adapter.Degraded);
    }

    [Fact]
    public void LastHealthyAt_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.LastHealthyAt);
    }

    // --- IGvAuthenticatedClientProvider seam (PR1) ---

    [Fact]
    public void GetAuthenticatedClient_BeforeActivate_ReturnsNull()
    {
        // Seam gates on IsAvailable (false before activation) so PR2/PR3 read clients get null
        // rather than a half-initialized client; they handle null by reporting unavailable.
        IGvAuthenticatedClientProvider adapter = CreateAdapter();
        Assert.Null(adapter.GetAuthenticatedClient());
    }

    [Fact]
    public void ApiBaseUrl_ReturnsConfiguredValue()
    {
        IGvAuthenticatedClientProvider adapter = CreateAdapter();
        Assert.Equal("https://clients6.google.com/voice/v1/voiceclient", adapter.ApiBaseUrl);
    }

    [Fact]
    public void ApiKey_ReturnsConfiguredValue()
    {
        IGvAuthenticatedClientProvider adapter = CreateAdapter();
        Assert.Equal("test", adapter.ApiKey);
    }

    private static GVApiAdapter CreateAdapter()
    {
        var config = Options.Create(new GVBridgeConfig
        {
            GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
            GvApiKey = "test",
            CookieFilePath = "test.enc",
            CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
        });

        return new GVApiAdapter(
            config,
            NullLogger<GVApiAdapter>.Instance,
            NullLoggerFactory.Instance);
    }
}
