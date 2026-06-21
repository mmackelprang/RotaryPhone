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
        _eventSource.OnSmsSent += BroadcastSmsSent;
        _eventSource.OnReadStateChanged += BroadcastReadStateChanged;
        _logger.LogInformation("GvMessagePushBridge subscribed to GV message events");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSource.OnSmsReceived -= BroadcastSms;
        _eventSource.OnVoicemailReceived -= BroadcastVoicemail;
        _eventSource.OnSmsSent -= BroadcastSmsSent;
        _eventSource.OnReadStateChanged -= BroadcastReadStateChanged;
        return Task.CompletedTask;
    }

    private void BroadcastSms(GVBridge.Api.SmsMessageDto dto)
    {
        _logger.LogInformation("Broadcasting SmsReceived {Id} from {Number}", dto.Id, dto.CounterpartyNumber);
        FireAndLog(_hubContext.Clients.All.SendAsync("SmsReceived", dto), "SmsReceived", dto.Id);
    }

    private void BroadcastSmsSent(GVBridge.Api.SmsMessageDto dto)
    {
        _logger.LogInformation("Broadcasting SmsSent {Id} to {Number}", dto.Id, dto.CounterpartyNumber);
        FireAndLog(_hubContext.Clients.All.SendAsync("SmsSent", dto), "SmsSent", dto.Id);
    }

    private void BroadcastVoicemail(GVBridge.Api.VoicemailItemDto dto)
    {
        _logger.LogInformation("Broadcasting VoicemailReceived {Id} from {Number}", dto.Id, dto.FromNumber);
        FireAndLog(_hubContext.Clients.All.SendAsync("VoicemailReceived", dto), "VoicemailReceived", dto.Id);
    }

    private void BroadcastReadStateChanged(GVBridge.Api.ReadStateChangedDto dto)
    {
        _logger.LogInformation("Broadcasting ReadStateChanged {Kind} {Id}/{Thread} isRead={IsRead}",
            dto.Kind, dto.Id, dto.ThreadId, dto.IsRead);
        FireAndLog(_hubContext.Clients.All.SendAsync("ReadStateChanged", dto),
            "ReadStateChanged", dto.Id ?? dto.ThreadId ?? "");
    }

    // Fire-and-forget the SignalR broadcast but observe the task so a SendAsync fault (hub startup
    // race, serialization error) is logged instead of silently swallowed as an unobserved exception.
    // Pass an explicit TaskScheduler.Default (don't capture TaskScheduler.Current) and flatten the
    // AggregateException so all inner faults are logged, not just the first (review MEDIUM-2).
    private void FireAndLog(Task sendTask, string eventName, string id)
        => _ = sendTask.ContinueWith(
            t => _logger.LogWarning(t.Exception!.Flatten().InnerException,
                "{Event} broadcast failed for {Id}", eventName, id),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
