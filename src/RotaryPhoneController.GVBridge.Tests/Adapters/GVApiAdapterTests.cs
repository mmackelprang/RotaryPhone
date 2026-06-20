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
    public void Degraded_BeforeActivate_IsTrue()
    {
        // Not yet activated: cookies invalid and not registered → degraded (honest).
        var adapter = CreateAdapter();
        Assert.True(adapter.Degraded);
    }

    [Fact]
    public void LastHealthyAt_BeforeActivate_ReturnsNull()
    {
        var adapter = CreateAdapter();
        Assert.Null(adapter.LastHealthyAt);
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
