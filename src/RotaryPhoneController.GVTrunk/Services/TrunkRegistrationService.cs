using Microsoft.Extensions.Hosting;
using RotaryPhoneController.GVTrunk.Interfaces;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Services;

public class TrunkRegistrationService : IHostedService
{
    private readonly ITrunkAdapter _trunk;
    private readonly ILogger _logger;

    public TrunkRegistrationService(ITrunkAdapter trunk, ILogger logger)
    {
        _trunk = trunk;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Information("TrunkRegistrationService starting");
        _trunk.StartListening();
        await _trunk.RegisterAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("TrunkRegistrationService stopping");
        await _trunk.UnregisterAsync(cancellationToken);
    }
}
