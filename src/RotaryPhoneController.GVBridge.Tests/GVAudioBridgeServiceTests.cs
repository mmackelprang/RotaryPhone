using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests;

public class GVAudioBridgeServiceTests
{
    private GVAudioBridgeService CreateService()
    {
        var config = Options.Create(new GVBridgeConfig { LocalRtpPort = 0 });
        var logger = new Mock<ILogger<GVAudioBridgeService>>().Object;
        return new GVAudioBridgeService(config, logger);
    }

    [Fact]
    public void IsActive_FalseByDefault()
    {
        var service = CreateService();
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task StartAsync_WithoutSipTransport_DoesNotSetActive()
    {
        using var service = CreateService();
        await service.StartAsync();
        // Without calling SetSipTransport first, StartAsync logs error and returns
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task StopAsync_WhenNotActive_IsNoOp()
    {
        using var service = CreateService();
        await service.StopAsync(); // stop without start — should not throw
        Assert.False(service.IsActive);
    }
}
