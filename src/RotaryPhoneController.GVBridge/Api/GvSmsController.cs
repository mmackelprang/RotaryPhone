using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Clients;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge/sms")]
public class GvSmsController : ControllerBase
{
    private readonly GvSmsClient _smsClient;
    private readonly ILogger<GvSmsController> _logger;

    public GvSmsController(GvSmsClient smsClient, ILogger<GvSmsController> logger)
    {
        _smsClient = smsClient;
        _logger = logger;
    }

    [HttpGet("threads")]
    public async Task<IActionResult> GetThreads([FromQuery] int count = 20, CancellationToken ct = default)
    {
        var result = await _smsClient.ListThreadsAsync(count, pageToken: null, ct);
        // Surface upstream failure as 502 rather than masking it as a 200 with an empty list, which
        // RadioConsole could not distinguish from "no threads" (a silent-status hazard).
        if (!result.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch SMS threads from Google" });
        var threads = result.Threads.Select(t => new SmsThreadDto(
            ThreadId: t.ThreadId ?? "",
            CounterpartyNumber: t.CounterpartyNumber ?? "",
            CounterpartyName: t.CounterpartyName,
            LastMessageAt: t.LastMessageEpochMs is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
            HasUnread: t.HasUnread ?? false,
            LastMessagePreview: t.LastMessagePreview)).ToList();
        return Ok(new SmsThreadListDto(threads, DateTime.UtcNow));
    }

    [HttpGet("threads/{threadId}")]
    public async Task<IActionResult> GetThreadMessages(
        string threadId, [FromQuery] int count = 50, CancellationToken ct = default)
    {
        var result = await _smsClient.ListMessagesAsync(threadId, count, ct);
        if (!result.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch SMS messages from Google" });
        var messages = result.Messages.Select(m => new SmsMessageDto(
            Id: m.MessageId ?? "",
            ThreadId: m.ThreadId ?? "",
            Direction: m.Direction ?? "Inbound",
            CounterpartyNumber: m.CounterpartyNumber ?? "",
            Text: m.Text,
            SentAt: m.SentEpochMs is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
            IsRead: m.IsRead ?? false)).ToList();
        return Ok(new SmsThreadMessagesDto(threadId, messages, DateTime.UtcNow));
    }
}
