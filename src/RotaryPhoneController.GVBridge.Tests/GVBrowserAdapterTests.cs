using Xunit;
using Moq;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace RotaryPhoneController.GVBridge.Tests;

public class GVBrowserAdapterTests
{
    private GVBrowserAdapter CreateAdapter()
    {
        var config = Options.Create(new GVBridgeConfig { WebSocketPort = 0 });
        var serilogLogger = new Mock<Serilog.ILogger>().Object;
        var bridgeService = new GVBridgeService(config, serilogLogger);
        var audioConfig = Options.Create(new GVBridgeConfig { LocalRtpPort = 0 });
        var audioBridgeLogger = new Mock<ILogger<GVAudioBridgeService>>().Object;
        var audioBridge = new GVAudioBridgeService(bridgeService, audioConfig, audioBridgeLogger);
        var logger = new Mock<ILogger<GVBrowserAdapter>>().Object;
        return new GVBrowserAdapter(bridgeService, audioBridge, logger);
    }

    [Fact]
    public void Mode_IsGVBrowser()
    {
        var adapter = CreateAdapter();
        Assert.Equal(CallAdapterMode.GVBrowser, adapter.Mode);
    }

    [Fact]
    public void InitialState_IsNotAvailable()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsAvailable);
    }

    [Fact]
    public async Task PlaceCallAsync_ThrowsWhenNotAvailable()
    {
        var adapter = CreateAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.PlaceCallAsync("+15551234567"));
    }

    [Fact]
    public void OnIncomingCall_EventCanBeSubscribed()
    {
        var adapter = CreateAdapter();
        bool fired = false;
        adapter.OnIncomingCall += _ => fired = true;
        Assert.False(fired);
    }
}
