using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge/voicemail")]
public class GvVoicemailController : ControllerBase
{
    private readonly GvVoicemailClient _voicemailClient;
    private readonly GvVoicemailCache _cache;
    private readonly ILogger<GvVoicemailController> _logger;

    public GvVoicemailController(GvVoicemailClient voicemailClient, GvVoicemailCache cache,
        ILogger<GvVoicemailController> logger)
    {
        _voicemailClient = voicemailClient;
        _cache = cache;
        _logger = logger;
    }

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
