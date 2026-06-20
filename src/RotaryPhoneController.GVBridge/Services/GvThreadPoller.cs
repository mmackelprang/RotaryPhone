using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Background poller (ADR §5.3) — the shipped SMS-read transport. Polls api2thread/list (SMS +
/// voicemail folders), diffs against a per-thread high-water mark, and raises OnSmsReceived /
/// OnVoicemailReceived for each NEW inbound item. A Server-side bridge forwards those to RotaryHub as
/// "SmsReceived"/"VoicemailReceived" — mirroring IncomingCall. The poll-vs-signaler choice lives
/// entirely behind IGvMessageEventSource (ADR §5.2, §9).
/// </summary>
public class GvThreadPoller : BackgroundService, IGvMessageEventSource
{
    private readonly GvSmsClient _smsClient;
    private readonly GvVoicemailClient _voicemailClient;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GvThreadPoller> _logger;

    private readonly GvHighWaterMark _smsHwm = new();
    private readonly GvHighWaterMark _vmHwm = new();
    private bool _smsSeeded;
    private bool _vmSeeded;
    private DateTime _lastActivityUtc = DateTime.MinValue;
    private int _consecutiveFailures;

    public event Action<SmsMessageDto>? OnSmsReceived;
    public event Action<VoicemailItemDto>? OnVoicemailReceived;

    public GvThreadPoller(GvSmsClient smsClient, GvVoicemailClient voicemailClient,
        IOptions<GVBridgeConfig> config, ILogger<GvThreadPoller> logger)
    {
        _smsClient = smsClient;
        _voicemailClient = voicemailClient;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.EnableThreadPoller)
        {
            _logger.LogInformation("GvThreadPoller disabled by config");
            return;
        }

        _logger.LogInformation("GvThreadPoller started");
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            try { await Task.Delay(NextDelay(), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One poll cycle (SMS folder + voicemail folder). Public for deterministic testing.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            await PollSmsAsync(ct);
            await PollVoicemailAsync(ct);
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "GvThreadPoller poll cycle failed (#{Count})", _consecutiveFailures);
        }
    }

    private async Task PollSmsAsync(CancellationToken ct)
    {
        var result = await _smsClient.ListRecentMessagesAsync(count: 50, ct);

        // First SUCCESSFUL poll seeds the high-water marks without raising events, so we never flood
        // RadioConsole with SMS history on startup (ADR §5.3). Critically, the seed is gated on
        // Succeeded (exactly like the voicemail path): a failed first poll (e.g. cookieless startup)
        // must NOT seed an empty mark — that would make every historical message look "new" and flood
        // RadioConsole on the first successful poll. Subsequent polls diff.
        if (!_smsSeeded)
        {
            if (!result.Succeeded) return;
            _smsHwm.Seed(result.Messages
                .Where(m => m.MessageId is not null && m.ThreadId is not null && m.SentEpochMs is not null)
                .Select(m => (m.ThreadId!, m.MessageId!, m.SentEpochMs!.Value)));
            _smsSeeded = true;
            return;
        }

        foreach (var m in result.Messages)
        {
            if (m.MessageId is null || m.ThreadId is null || m.SentEpochMs is not { } epoch) continue;
            var isNew = _smsHwm.IsNewMessage(m.ThreadId, m.MessageId, epoch);
            if (isNew && m.Direction == "Inbound")
            {
                _lastActivityUtc = DateTime.UtcNow;
                OnSmsReceived?.Invoke(ToSmsDto(m));
                _logger.LogInformation("Poller: new inbound SMS {Id} on {Thread}", m.MessageId, m.ThreadId);
            }
        }
    }

    private async Task PollVoicemailAsync(CancellationToken ct)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count: 50, pageToken: null, ct);
        if (!result.Succeeded) return;

        // First successful poll seeds without raising events (ADR §5.3). Subsequent polls diff.
        if (!_vmSeeded)
        {
            _vmHwm.Seed(result.Items
                .Where(v => v.MessageId is not null && v.ThreadId is not null && v.ReceivedEpochMs is not null)
                .Select(v => (v.ThreadId!, v.MessageId!, v.ReceivedEpochMs!.Value)));
            _vmSeeded = true;
            return;
        }

        foreach (var v in result.Items)
        {
            if (v.MessageId is null || v.ThreadId is null || v.ReceivedEpochMs is not { } epoch) continue;
            if (_vmHwm.IsNewMessage(v.ThreadId, v.MessageId, epoch))
            {
                _lastActivityUtc = DateTime.UtcNow;
                OnVoicemailReceived?.Invoke(ToVoicemailDto(v));
                _logger.LogInformation("Poller: new voicemail {Id}", v.MessageId);
            }
        }
    }

    private TimeSpan NextDelay()
    {
        if (_consecutiveFailures > 0)
            return TimeSpan.FromSeconds(_config.ThreadPollBackoffSeconds);

        var active = (DateTime.UtcNow - _lastActivityUtc).TotalMinutes
                     < _config.ThreadPollActiveWindowMinutes;
        return TimeSpan.FromSeconds(active ? _config.ThreadPollActiveSeconds : _config.ThreadPollIdleSeconds);
    }

    private static SmsMessageDto ToSmsDto(GvSmsNode m) => new(
        Id: m.MessageId ?? "",
        ThreadId: m.ThreadId ?? "",
        Direction: m.Direction ?? "Inbound",
        CounterpartyNumber: m.CounterpartyNumber ?? "",
        Text: m.Text,
        SentAt: m.SentEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
        IsRead: m.IsRead ?? false);

    private static VoicemailItemDto ToVoicemailDto(GvVoicemailNode v) => new(
        Id: v.MessageId ?? "",
        ThreadId: v.ThreadId ?? "",
        FromNumber: v.FromNumber ?? "",
        FromName: v.FromName,
        ReceivedAt: v.ReceivedEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
        DurationSeconds: v.DurationSeconds ?? 0,
        IsRead: v.IsRead ?? false,
        Transcript: v.Transcript,
        AudioUrl: $"/api/gvbridge/voicemail/{v.MessageId}/audio");
}
