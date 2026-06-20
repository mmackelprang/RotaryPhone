using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.Server.Hubs;

namespace RotaryPhoneController.Server.Services;

/// <summary>
/// Bridges the GVBridge message-event seam (IGvMessageEventSource — fed by GvThreadPoller, or later a
/// cracked signaler) to RotaryHub, broadcasting "SmsReceived"/"VoicemailReceived" to all connected
/// clients. Mirrors SignalRNotifierService's IncomingCall pattern (ADR §6.3). RadioConsole already
/// holds the hub connection; this is the only new wiring needed for SMS/voicemail push.
/// </summary>
public class GvMessagePushBridge : IHostedService
{
    private readonly IGvMessageEventSource _eventSource;
    private readonly IHubContext<RotaryHub> _hubContext;
    private readonly ILogger<GvMessagePushBridge> _logger;

    public GvMessagePushBridge(
        IGvMessageEventSource eventSource,
        IHubContext<RotaryHub> hubContext,
        ILogger<GvMessagePushBridge> logger)
    {
        _eventSource = eventSource;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eventSource.OnSmsReceived += BroadcastSms;
        _eventSource.OnVoicemailReceived += BroadcastVoicemail;
        _logger.LogInformation("GvMessagePushBridge subscribed to GV message events");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSource.OnSmsReceived -= BroadcastSms;
        _eventSource.OnVoicemailReceived -= BroadcastVoicemail;
        return Task.CompletedTask;
    }

    private void BroadcastSms(GVBridge.Api.SmsMessageDto dto)
    {
        _logger.LogInformation("Broadcasting SmsReceived {Id} from {Number}", dto.Id, dto.CounterpartyNumber);
        _ = _hubContext.Clients.All.SendAsync("SmsReceived", dto);
    }

    private void BroadcastVoicemail(GVBridge.Api.VoicemailItemDto dto)
    {
        _logger.LogInformation("Broadcasting VoicemailReceived {Id} from {Number}", dto.Id, dto.FromNumber);
        _ = _hubContext.Clients.All.SendAsync("VoicemailReceived", dto);
    }
}
