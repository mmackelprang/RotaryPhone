using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge/voicemail")]
public class GvVoicemailController : ControllerBase
{
    private readonly GvVoicemailClient _voicemailClient;
    private readonly GvVoicemailCache _cache;
    private readonly GvReadStateClient _readStateClient;
    private readonly IGvReadStateSink _readStateSink;
    private readonly GVBridgeConfig _config;
    private HttpClient? _testReadStateClient;   // test-only; null in production
    private readonly ILogger<GvVoicemailController> _logger;

    public GvVoicemailController(GvVoicemailClient voicemailClient, GvVoicemailCache cache,
        GvReadStateClient readStateClient, IGvReadStateSink readStateSink,
        IOptions<GVBridgeConfig> config,
        ILogger<GvVoicemailController> logger)
    {
        _voicemailClient = voicemailClient;
        _cache = cache;
        _readStateClient = readStateClient;
        _readStateSink = readStateSink;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>Test seam: inject the HttpClient used as the authenticated client for the updateread write.</summary>
    internal void SetReadStateClientForTest(HttpClient client) => _testReadStateClient = client;

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] int count = 20, [FromQuery] string? pageToken = null, CancellationToken ct = default)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count, pageToken, ct);
        // Do not mask an auth/transport failure as "no voicemails" — RadioConsole cannot tell the
        // difference from an empty 200, and silent-failure is a known hazard for this integration.
        if (!result.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch voicemail list from Google" });
        // Skip nodes with no message id — they cannot produce a valid audio URL (ADR §7 possibly-null).
        var items = result.Items.Where(n => n.MessageId is not null).Select(ToDto).ToList();
        return Ok(new VoicemailListDto(items, result.NextPageToken, DateTime.UtcNow));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetItem(string id, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(id, ct);
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });
        return Ok(ToDto(node));
    }

    [HttpGet("{id}/audio")]
    public async Task<IActionResult> GetAudio(string id, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(id, ct);
        if (node?.MediaId is null)
            return NotFound(new { error = $"Voicemail {id} has no recording" });

        var path = await _cache.GetOrFetchAsync(id, node.MediaId, ct);
        if (path is null)
            return StatusCode(502, new { error = "Failed to fetch recording from Google" });

        // PhysicalFileResult + EnableRangeProcessing gives Accept-Ranges so the HTML5 <audio>
        // scrubber works (ADR §6.4). All bytes flow Google→RotaryPhone→RadioConsole; never a redirect.
        return new PhysicalFileResult(path, "audio/mpeg") { EnableRangeProcessing = true };
    }

    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkRead(
        string id, [FromBody] MarkReadRequest request, CancellationToken ct = default)
    {
        // 0. FEATURE FLAG (ADR §8) — DEFAULT FALSE → 409 markread_disabled, NO GV call. Checked FIRST.
        if (!_config.EnableMarkRead)
        {
            _logger.LogInformation("Mark-read rejected — EnableMarkRead is false (dark)");
            return StatusCode(409, new { error = "markread_disabled" });
        }

        // 1. Mark-unread gate (ADR §6.1). v1 honors isRead:true; isRead:false only when AllowMarkUnread.
        if (!request.IsRead && !_config.AllowMarkUnread)
            return BadRequest(new { error = "unread_unsupported" });

        // 2. Find the node (also needed to build the response DTO — same list+filter the read routes do).
        var node = await FindNodeAsync(id, ct);
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });

        // 3. Idempotent no-op (ADR §4.3): already in the target state → 200 with the true DTO, no GV call.
        if ((node.IsRead ?? false) == request.IsRead)
            return Ok(ToDto(node));

        // 4. Write through to GV (honest status — 200 means GV accepted, ADR §3.2). No auto-retry (§8).
        var write = _testReadStateClient is not null
            ? await _readStateClient.MarkVoicemailReadAsync(
                _testReadStateClient, node.MessageId ?? id, node.ThreadId ?? "", request.IsRead, ct)
            : await _readStateClient.MarkVoicemailReadAsync(
                node.MessageId ?? id, node.ThreadId ?? "", request.IsRead, ct);
        if (write.Outcome != GvUpdateReadOutcome.Applied)
            return StatusCode(502, new { error = write.Error ?? "Failed to update read-state in Google" });

        // 5. Re-read so the response DTO reflects GV's truth (ADR §4.4). Fall back to the optimistic node
        //    if the re-read can't find it (rare race) — but with the applied IsRead.
        var fresh = await FindNodeAsync(id, ct) ?? node;
        var dto = ToDto(fresh) with { IsRead = request.IsRead };

        // 6. Broadcast path-a ReadStateChanged (ADR §5). Unconditional; RadioConsole de-dupes.
        _readStateSink.NotifyReadStateChanged(new ReadStateChangedDto(
            Kind: "Voicemail", Id: dto.Id, ThreadId: dto.ThreadId,
            IsRead: request.IsRead, ChangedAtUtc: DateTime.UtcNow));

        return Ok(dto);
    }

    // Voicemail is a thread/message subtype — there is no per-id GET on GV; we list and filter.
    // Lists are small (tens of items); a future optimization could cache the last list.
    private async Task<GvVoicemailNode?> FindNodeAsync(string id, CancellationToken ct)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count: 100, pageToken: null, ct);
        return result.Items.FirstOrDefault(v => v.MessageId == id);
    }

    private static VoicemailItemDto ToDto(GvVoicemailNode n) => new(
        Id: n.MessageId ?? "",
        ThreadId: n.ThreadId ?? "",
        FromNumber: n.FromNumber ?? "",
        FromName: n.FromName,
        ReceivedAt: n.ReceivedEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UnixEpoch,
        DurationSeconds: n.DurationSeconds ?? 0,
        IsRead: n.IsRead ?? false,
        Transcript: n.Transcript,
        AudioUrl: n.MessageId is not null
            ? $"/api/gvbridge/voicemail/{n.MessageId}/audio"
            : "");
}
