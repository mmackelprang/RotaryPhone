using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;
using Serilog;

namespace RotaryPhoneController.GVTrunk.Services;

public class GmailSmsService : ISmsProvider
{
    private readonly TrunkConfig _config;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private GmailService? _gmail;
    private readonly HashSet<string> _processedIds = new();

    public event Action<SmsNotification>? OnSmsReceived;
    public event Action<SmsNotification>? OnMissedCallReceived;

    public GmailSmsService(IOptions<TrunkConfig> config, ILogger logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public static (SmsType type, string number)? ParseGvSubject(string subject)
    {
        var smsMatch = Regex.Match(subject, @"New text message from (.+)$");
        if (smsMatch.Success)
            return (SmsType.Sms, smsMatch.Groups[1].Value.Trim());

        var missedMatch = Regex.Match(subject, @"Missed call from (.+)$");
        if (missedMatch.Success)
            return (SmsType.MissedCall, missedMatch.Groups[1].Value.Trim());

        return null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.GmailCredentialsPath))
        {
            _logger.Warning("Gmail credentials path not configured — SMS service disabled");
            return;
        }

        try
        {
            var credential = await AuthorizeAsync(ct);
            _gmail = new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "RotaryPhoneController-GVTrunk"
            });

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = PollLoopAsync(_cts.Token);
            _logger.Information("GmailSmsService started — polling every {Interval}s", _config.GmailPollIntervalSeconds);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start Gmail SMS service");
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _logger.Information("GmailSmsService stopped");
        return Task.CompletedTask;
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken ct)
    {
        using var stream = new FileStream(_config.GmailCredentialsPath, FileMode.Open, FileAccess.Read);
        var credPath = Path.GetDirectoryName(_config.GmailCredentialsPath) ?? ".";
        var tokenPath = Path.Combine(credPath, "token");

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            new[] { GmailService.Scope.GmailModify },
            "user",
            ct,
            new FileDataStore(tokenPath, true));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Gmail poll error");
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.GmailPollIntervalSeconds), ct);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (_gmail == null) return;

        var request = _gmail.Users.Messages.List("me");
        request.Q = "from:voice-noreply@google.com is:unread";
        request.MaxResults = 10;

        var response = await request.ExecuteAsync(ct);
        if (response.Messages == null) return;

        foreach (var msgRef in response.Messages)
        {
            if (!_processedIds.Add(msgRef.Id)) continue;

            var msg = await _gmail.Users.Messages.Get("me", msgRef.Id).ExecuteAsync(ct);
            var subject = msg.Payload?.Headers?.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "";
            var snippet = msg.Snippet ?? "";

            var parsed = ParseGvSubject(subject);
            if (parsed == null) continue;

            var notification = new SmsNotification(
                parsed.Value.number,
                parsed.Value.type == SmsType.Sms ? snippet : null,
                DateTime.UtcNow,
                parsed.Value.type);

            if (parsed.Value.type == SmsType.Sms)
                OnSmsReceived?.Invoke(notification);
            else
                OnMissedCallReceived?.Invoke(notification);

            var modReq = new ModifyMessageRequest { RemoveLabelIds = new[] { "UNREAD" } };
            await _gmail.Users.Messages.Modify(modReq, "me", msgRef.Id).ExecuteAsync(ct);

            if (_processedIds.Count > 500)
                _processedIds.Clear();

            _logger.Information("Processed GV notification: {Type} from {Number}", parsed.Value.type, parsed.Value.number);
        }
    }
}
