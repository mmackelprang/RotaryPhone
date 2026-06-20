using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using RotaryPhoneController.GVTrunk.Services;
using Serilog;
using Xunit;

namespace RotaryPhoneController.GVTrunk.Tests;

public class TrunkRegistrationServiceTests
{
    private static ILogger SilentLogger => new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task StartAsync_SkipsRegistration_WhenCredentialsMissing()
    {
        var trunk = new Mock<ITrunkAdapter>();
        var config = Options.Create(new TrunkConfig { SipUsername = "", SipPassword = "" });
        var svc = new TrunkRegistrationService(trunk.Object, SilentLogger, config);

        await svc.StartAsync(CancellationToken.None);

        trunk.Verify(t => t.StartListening(), Times.Never);
        trunk.Verify(t => t.RegisterAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_Registers_WhenCredentialsPresent()
    {
        var trunk = new Mock<ITrunkAdapter>();
        trunk.Setup(t => t.RegisterAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var config = Options.Create(new TrunkConfig { SipUsername = "100000_sub", SipPassword = "secret" });
        var svc = new TrunkRegistrationService(trunk.Object, SilentLogger, config);

        await svc.StartAsync(CancellationToken.None);

        trunk.Verify(t => t.StartListening(), Times.Once);
        trunk.Verify(t => t.RegisterAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_DoesNotUnregister_WhenSkipped()
    {
        var trunk = new Mock<ITrunkAdapter>();
        var config = Options.Create(new TrunkConfig { SipUsername = "", SipPassword = "" });
        var svc = new TrunkRegistrationService(trunk.Object, SilentLogger, config);

        await svc.StartAsync(CancellationToken.None); // skipped (no creds)
        await svc.StopAsync(CancellationToken.None);

        trunk.Verify(t => t.UnregisterAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
