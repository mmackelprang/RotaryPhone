using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// SMS read client (ADR §5.3, §6.2). Composes GvThreadClient — voicemail/SMS/threads all ride the
/// same api2thread/list call + parser seam. SendAsync (the account write) is PR4, intentionally absent.
/// </summary>
public class GvSmsClient
{
    private readonly GvThreadClient _threadClient;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvSmsClient> _logger;

    public GvSmsClient(GvThreadClient threadClient, IGvThreadParser parser, ILogger<GvSmsClient> logger)
    {
        _threadClient = threadClient;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>List SMS threads (folder = Sms).</summary>
    public Task<GvThreadListResult> ListThreadsAsync(
        int count = 20, string? pageToken = null, CancellationToken ct = default)
        => _threadClient.ListThreadsAsync(GvThreadFolder.Sms, count, pageToken, ct);

    /// <summary>List messages for a single thread by filtering the SMS-folder list.</summary>
    public async Task<IReadOnlyList<GvSmsNode>> ListMessagesAsync(
        string threadId, int count = 50, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Sms, count, pageToken: null, ct);
        if (doc is null) return Array.Empty<GvSmsNode>();

        var all = _parser.ParseSmsMessages(doc.RootElement);
        return all.Where(m => m.ThreadId == threadId).ToList();
    }

    /// <summary>List ALL recent SMS messages across threads (used by the poller's diff).</summary>
    public async Task<IReadOnlyList<GvSmsNode>> ListRecentMessagesAsync(
        int count = 50, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Sms, count, pageToken: null, ct);
        return doc is null ? Array.Empty<GvSmsNode>() : _parser.ParseSmsMessages(doc.RootElement);
    }
}
