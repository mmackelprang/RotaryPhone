using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

public record GvVoicemailListResult(IReadOnlyList<GvVoicemailNode> Items, string? NextPageToken, bool Succeeded)
{
    public static GvVoicemailListResult Empty(bool succeeded) =>
        new(Array.Empty<GvVoicemailNode>(), null, succeeded);
}

/// <summary>
/// Lists voicemails by reading the voicemail folder of api2thread/list (ADR §3.1: voicemail is a
/// thread/message subtype, not a separate product). Composes <see cref="GvThreadClient"/> so the
/// authenticated HTTP call + parser seam are shared. Audio fetch is PR2 — intentionally absent here.
/// </summary>
public class GvVoicemailClient
{
    private readonly GvThreadClient _threadClient;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvVoicemailClient> _logger;

    public GvVoicemailClient(GvThreadClient threadClient, IGvThreadParser parser,
        ILogger<GvVoicemailClient> logger)
    {
        _threadClient = threadClient;
        _parser = parser;
        _logger = logger;
    }

    public async Task<GvVoicemailListResult> ListVoicemailsAsync(
        int count = 20, string? pageToken = null, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Voicemail, count, pageToken, ct);
        if (doc is null) return GvVoicemailListResult.Empty(succeeded: false);

        var items = _parser.ParseVoicemailList(doc.RootElement);
        var token = _parser.ParseNextPageToken(doc.RootElement);
        _logger.LogDebug("Listed {Count} voicemails", items.Count);
        return new GvVoicemailListResult(items, token, Succeeded: true);
    }
}
