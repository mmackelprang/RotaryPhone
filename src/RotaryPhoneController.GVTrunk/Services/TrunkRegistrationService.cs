using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Services;

public class TrunkRegistrationService : IHostedService
{
    private readonly ITrunkAdapter _trunk;
    private readonly ILogger _logger;
    private readonly TrunkConfig _config;
    private bool _started;

    public TrunkRegistrationService(ITrunkAdapter trunk, ILogger logger, IOptions<TrunkConfig> config)
    {
        _trunk = trunk;
        _logger = logger;
        _config = config.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The VoIP.ms trunk is optional/legacy — the active path is the GV API adapter. When no
        // credentials are configured it would register as `sip:@sip.voip.ms` and get a 403 Forbidden
        // every RegisterIntervalSeconds (endless WRN spam). Skip it entirely until creds are set.
        if (string.IsNullOrWhiteSpace(_config.SipUsername) || string.IsNullOrWhiteSpace(_config.SipPassword))
        {
            _logger.Information(
                "GVTrunk credentials not configured (GVTrunk:SipUsername/SipPassword empty) — skipping VoIP.ms registration");
            return;
        }

        _logger.Information("TrunkRegistrationService starting");
        _started = true;
        _trunk.StartListening();
        await _trunk.RegisterAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            return;

        _logger.Information("TrunkRegistrationService stopping");
        await _trunk.UnregisterAsync(cancellationToken);
    }
}
