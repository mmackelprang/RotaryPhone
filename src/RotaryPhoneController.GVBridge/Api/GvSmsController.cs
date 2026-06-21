using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge/sms")]
public class GvSmsController : ControllerBase
{
    private readonly GvSmsClient _smsClient;
    private readonly SmsSendRateLimiter _rateLimiter;
    private readonly ISmsThreadIdResolver _threadIdResolver;
    private readonly IGvOutboundSmsSink _outboundSink;
    private readonly GVBridgeConfig _config;       // for the EnableSmsSend feature flag (Task 7)
    private readonly GvReadStateClient _readStateClient;
    private readonly IGvReadStateSink _readStateSink;
    private readonly ILogger<GvSmsController> _logger;
    private HttpClient? _testSendClient;   // test-only; null in production (uses GvSmsClient.SendAsync(threadId,text))
    private HttpClient? _testReadStateClient;   // test-only

    public GvSmsController(GvSmsClient smsClient, SmsSendRateLimiter rateLimiter,
        ISmsThreadIdResolver threadIdResolver, IGvOutboundSmsSink outboundSink,
        GvReadStateClient readStateClient, IGvReadStateSink readStateSink,
        IOptions<GVBridgeConfig> config, ILogger<GvSmsController> logger)
    {
        _smsClient = smsClient;
        _rateLimiter = rateLimiter;
        _threadIdResolver = threadIdResolver;
        _outboundSink = outboundSink;
        _readStateClient = readStateClient;
        _readStateSink = readStateSink;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>Test seam: inject the HttpClient used as the "authenticated client" for the write path.</summary>
    internal void SetSendClientForTest(HttpClient client) => _testSendClient = client;

    /// <summary>Test seam: inject the HttpClient used as the authenticated client for the updateread write.</summary>
    internal void SetReadStateClientForTest(HttpClient client) => _testReadStateClient = client;

    /// <summary>
    /// Stable client-correlation id for an outbound echo (id-consistency rule, Task 1 Step 1b). The SAME
    /// formula is used by the PR3 poller's outbound surface (Task 1 Step 1c) via the shared
    /// <see cref="SmsCorrelationId"/> so the UI collapses the optimistic bubble against the re-surfaced
    /// copy on an exact Id match. Form: csid:{threadId}:{sha1(text)[..12]}:{sentEpochMs}.
    /// </summary>
    internal static string CorrelationId(string threadId, string text, long sentEpochMs)
        => SmsCorrelationId.For(threadId, text, sentEpochMs);

    [HttpGet("threads")]
    public async Task<IActionResult> GetThreads([FromQuery] int count = 20, CancellationToken ct = default)
    {
        var result = await _smsClient.ListThreadsAsync(count, pageToken: null, ct);
        // Surface upstream failure as 502 rather than masking it as a 200 with an empty list, which
        // RadioConsole could not distinguish from "no threads" (a silent-status hazard).
        if (!result.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch SMS threads from Google" });
        var threads = result.Threads.Select(ToThreadDto).ToList();
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

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendSmsRequest request, CancellationToken ct = default)
    {
        // 0. FEATURE FLAG (Task 7, ADR §12 #1). Default FALSE — the write code ships DARK. Return a coded
        //    "send disabled" with NO GV call. Independent of RadioConsole's own EnableSmsSend (defense in depth).
        if (!_config.EnableSmsSend)
        {
            _logger.LogInformation("SMS send rejected — EnableSmsSend is false (dark)");
            return StatusCode(409, new SendSmsResponse(
                Queued: false, Code: "send_disabled", ThreadId: null,
                Error: "SMS send is disabled on this server", Message: null));
        }

        // 1. Rate-limit (ADR §4.2 #4) — cheap, and a real 429 backs the UI's "Sending too fast".
        if (!_rateLimiter.TryAcquire())
        {
            _logger.LogWarning("SMS send rejected by rate limiter");
            return StatusCode(429, new SendSmsResponse(
                Queued: false, Code: "rate_limited", ThreadId: null,
                Error: "Sending too fast — wait a moment", Message: null));
        }

        // 2. Validate text → 400 invalid_text.
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new SendSmsResponse(
                false, "invalid_text", null, "Message text is required", null));

        // 3. Normalize the recipient to E.164 (ADR §4.2 #2) → 400 invalid_number. Never guess.
        if (!PhoneNumberNormalizer.TryNormalize(request.ToNumber, out var e164) || e164 is null)
            return BadRequest(new SendSmsResponse(
                false, "invalid_number", null, $"Invalid or unsupported number: {request.ToNumber}", null));

        // 4. Resolve the thread id (ADR §4.2 #1): reply id verbatim, else synthesized t.+<E164> (UNVERIFIED).
        var threadId = _threadIdResolver.Resolve(e164, request.ThreadId);

        // 5. Send. In production GvSmsClient resolves the live authenticated client per call; the test seam
        //    injects an explicit client.
        var sendResult = _testSendClient is not null
            ? await _smsClient.SendAsync(_testSendClient, threadId, request.Text, ct)
            : await _smsClient.SendAsync(threadId, request.Text, ct);

        // 6. Honest mapping (ADR §4.2 #3, §"Error taxonomy"): NO auto-retry. Map the classified outcome to
        //    a distinct (status, code) so the UI picks the right copy without parsing prose.
        if (!sendResult.Queued)
        {
            var (status, code) = sendResult.Outcome switch
            {
                GvSendOutcome.InvalidArgument    => (400, "invalid_number"),
                GvSendOutcome.AdapterUnavailable => (502, "auth_unavailable"),
                GvSendOutcome.UpstreamError      => (502, "upstream_error"),
                GvSendOutcome.Timeout            => (504, "timeout"),
                _                                 => (500, "error"),
            };
            return StatusCode(status, new SendSmsResponse(false, code, threadId, sendResult.Error, null));
        }

        // 7. Build the optimistic OUTBOUND echo (NOT a parse of Google's ack — it returns no message). The Id
        //    is the STABLE csid: correlation id (or the UI-supplied ClientCorrelationId), so the bubble
        //    collapses against the re-surfaced copy with no visual jump (id-consistency rule, Task 1 Step 1b).
        var sentEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = string.IsNullOrWhiteSpace(request.ClientCorrelationId)
            ? CorrelationId(threadId, request.Text, sentEpochMs)
            : request.ClientCorrelationId;
        var echo = new SmsMessageDto(
            Id: id,
            ThreadId: threadId,
            Direction: "Outbound",
            CounterpartyNumber: e164,
            Text: request.Text,
            SentAt: DateTimeOffset.FromUnixTimeMilliseconds(sentEpochMs).UtcDateTime,
            IsRead: true);

        // 8. Broadcast so other connected clients converge (decision in Task 6 header). Distinct SmsSent event.
        _outboundSink.NotifySent(echo);

        return Ok(new SendSmsResponse(Queued: true, Code: "queued", ThreadId: threadId, Error: null, Message: echo));
    }

    [HttpPost("threads/{threadId}/read")]
    public async Task<IActionResult> MarkThreadRead(
        string threadId, [FromBody] MarkReadRequest request, CancellationToken ct = default)
    {
        // 0. FEATURE FLAG (ADR §8) — checked FIRST → 409 markread_disabled, NO GV call.
        if (!_config.EnableMarkRead)
        {
            _logger.LogInformation("Mark-thread-read rejected — EnableMarkRead is false (dark)");
            return StatusCode(409, new { error = "markread_disabled" });
        }

        // 1. Mark-unread gate (ADR §6.1).
        if (!request.IsRead && !_config.AllowMarkUnread)
            return BadRequest(new { error = "unread_unsupported" });

        // 2. Resolve the thread summary (404 if unknown) — same list+filter GetThreads does.
        var threadsResult = await _smsClient.ListThreadsAsync(count: 100, pageToken: null, ct);
        if (!threadsResult.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch SMS threads from Google" });
        var thread = threadsResult.Threads.FirstOrDefault(t => t.ThreadId == threadId);
        if (thread is null) return NotFound(new { error = $"SMS thread {threadId} not found" });

        // 3. Idempotent no-op (ADR §4.3): thread already in the target state → 200, no GV call. isRead:true
        //    means target hasUnread=false; so "already in target state" is HasUnread == !isRead.
        var alreadyInTargetState = (thread.HasUnread ?? false) == !request.IsRead;
        if (alreadyInTargetState)
            return Ok(ToThreadDto(thread));

        // 4. Per-thread grain (ADR §4.2 Q4): mark every message in the thread. Resolve the message ids.
        //    If we cannot enumerate the thread's messages (auth blip / GV 5xx), do NOT attempt a wrong/partial
        //    mark — return 502 so RadioConsole reconciles on the next list (honest-status discipline, ADR §3.2).
        var messagesResult = await _smsClient.ListMessagesAsync(threadId, count: 200, ct);
        if (!messagesResult.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch SMS messages for mark-read" });
        var messageIds = messagesResult.Messages
            .Where(m => m.MessageId is not null).Select(m => m.MessageId!).ToList();

        // 5. Write through to GV (honest — 200 means accepted; partial = failure). No auto-retry (§8).
        var write = _testReadStateClient is not null
            ? await _readStateClient.MarkSmsThreadReadAsync(_testReadStateClient, threadId, messageIds, request.IsRead, ct)
            : await _readStateClient.MarkSmsThreadReadAsync(threadId, messageIds, request.IsRead, ct);
        if (write.Outcome != GvUpdateReadOutcome.Applied)
            return StatusCode(502, new { error = write.Error ?? "Failed to update read-state in Google" });

        // 6. Build the authoritative thread DTO: hasUnread = !isRead (a mark-read clears unread).
        var dto = ToThreadDto(thread) with { HasUnread = !request.IsRead };

        // 7. Broadcast path-a ReadStateChanged (ADR §5). For SMS, IsRead = "thread fully read" (!hasUnread).
        _readStateSink.NotifyReadStateChanged(new ReadStateChangedDto(
            Kind: "Sms", Id: null, ThreadId: threadId,
            IsRead: request.IsRead, ChangedAtUtc: DateTime.UtcNow));

        return Ok(dto);
    }

    // Map a parsed thread node to the public SmsThreadDto (same projection GetThreads uses inline).
    private static SmsThreadDto ToThreadDto(GvThreadNode t) => new(
        ThreadId: t.ThreadId ?? "",
        CounterpartyNumber: t.CounterpartyNumber ?? "",
        CounterpartyName: t.CounterpartyName,
        LastMessageAt: t.LastMessageEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
        HasUnread: t.HasUnread ?? false,
        LastMessagePreview: t.LastMessagePreview);
}
