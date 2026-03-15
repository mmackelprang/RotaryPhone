using Xunit;
using Moq;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVTrunk.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RotaryPhoneController.GVBridge.Tests;

public class GVSmsServiceTests
{
    [Fact]
    public void GetRecent_ReturnsEmptyInitially()
    {
        var config = Options.Create(new GVBridgeConfig { WebSocketPort = 0 });
        var serilogLogger = new Mock<Serilog.ILogger>().Object;
        var bridge = new GVBridgeService(config, serilogLogger);
        var logger = new Mock<ILogger<GVSmsService>>().Object;
        var service = new GVSmsService(bridge, logger);

        var recent = service.GetRecent(20);
        Assert.Empty(recent);
    }
}
