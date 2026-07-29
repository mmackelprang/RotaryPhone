using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Configuration;
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
        // The bridge now resolves its fallback HT801 address through the one resolver
        // (ISipAdapter.ResolveHt801Address: learned binding when fresh, then the configured phone)
        // instead of its own GVBridge:HT801Ip copy or a local copy of that precedence.
        var appConfig = new AppConfiguration();
        appConfig.Phones.Add(new RotaryPhoneConfig { Id = "default", HT801IpAddress = "192.0.2.240" });
        var sipAdapter = new Mock<ISipAdapter>();
        sipAdapter
            .Setup(x => x.ResolveHt801Address(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string configured, bool _) => configured);
        return new GVAudioBridgeService(config, logger, appConfig, sipAdapter.Object);
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
