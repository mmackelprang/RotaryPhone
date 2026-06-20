using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Result of an SMS message list call. Succeeded=false means a non-200/parse failure — callers must
/// NOT treat the empty Messages list as "no messages" (the poller would otherwise seed an empty
/// high-water mark on a failed first poll and then flood RadioConsole with all history on the first
/// successful poll). Mirrors GvVoicemailListResult / GvThreadListResult.
/// </summary>
public record GvSmsListResult(IReadOnlyList<GvSmsNode> Messages, bool Succeeded)
{
    public static GvSmsListResult Empty(bool succeeded) =>
        new(Array.Empty<GvSmsNode>(), succeeded);
}

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

    /// <summary>
    /// List messages for a single thread by filtering the SMS-folder list. Succeeded=false on a
    /// non-200/parse failure (so the controller can return 502 instead of masking it as an empty 200).
    /// </summary>
    public async Task<GvSmsListResult> ListMessagesAsync(
        string threadId, int count = 50, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Sms, count, pageToken: null, ct);
        if (doc is null) return GvSmsListResult.Empty(succeeded: false);

        var all = _parser.ParseSmsMessages(doc.RootElement);
        return new GvSmsListResult(all.Where(m => m.ThreadId == threadId).ToList(), Succeeded: true);
    }

    /// <summary>
    /// List ALL recent SMS messages across threads (used by the poller's diff). Succeeded=false on a
    /// non-200/parse failure — the poller MUST check this before seeding the high-water mark.
    /// </summary>
    public async Task<GvSmsListResult> ListRecentMessagesAsync(
        int count = 50, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Sms, count, pageToken: null, ct);
        return doc is null
            ? GvSmsListResult.Empty(succeeded: false)
            : new GvSmsListResult(_parser.ParseSmsMessages(doc.RootElement), Succeeded: true);
    }
}
