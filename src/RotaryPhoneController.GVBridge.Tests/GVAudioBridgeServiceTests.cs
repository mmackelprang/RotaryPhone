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
        var serilogLogger = new Mock<Serilog.ILogger>().Object;
        var bridgeService = new GVBridgeService(config, serilogLogger);
        var logger = new Mock<ILogger<GVAudioBridgeService>>().Object;
        return new GVAudioBridgeService(bridgeService, Options.Create(new GVBridgeConfig { LocalRtpPort = 0 }), logger);
    }

    [Fact]
    public void IsActive_FalseByDefault()
    {
        var service = CreateService();
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task StartAsync_SetsIsActiveTrue()
    {
        using var service = CreateService();
        await service.StartAsync();
        Assert.True(service.IsActive);
        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_SetsIsActiveFalse()
    {
        using var service = CreateService();
        await service.StartAsync();
        await service.StopAsync();
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyActive_IsNoOp()
    {
        using var service = CreateService();
        await service.StartAsync();
        await service.StartAsync(); // second start — should not throw
        Assert.True(service.IsActive);
        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenNotActive_IsNoOp()
    {
        using var service = CreateService();
        await service.StopAsync(); // stop without start — should not throw
        Assert.False(service.IsActive);
    }
}
