using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Classified outcome of a GV sendsms call, so the controller can map to a distinct HTTP status + Code
/// (§"Error taxonomy"). The CLIENT only knows what it observed at the GV boundary; the controller owns
/// the HTTP mapping. Queued is true ONLY for <see cref="GvSendOutcome.Queued"/>.
/// </summary>
public enum GvSendOutcome
{
    Queued,            // HTTP 200 from GV — accepted/queued (NOT delivered)
    InvalidArgument,   // GV returned INVALID_ARGUMENT — bad number → controller maps to 400 invalid_number
    AdapterUnavailable,// no authenticated client (cookie decay / recovery window) → 502 auth_unavailable
    UpstreamError,     // GV returned some other non-200 → 502 upstream_error
    Timeout            // timeout / network exception, no response observed → 504 timeout
}

/// <summary>
/// Result of a GV sendsms call. Queued=true ONLY when Outcome==Queued and Google returned HTTP 200 —
/// NOT confirmed delivery (ADR §4.2 #3, sendsms returns a transaction ack). Error is populated on any
/// failure. Callers MUST NOT auto-retry on failure (ADR §4.2 #4).
/// </summary>
public record GvSmsSendResult(bool Queued, GvSendOutcome Outcome, string? Error)
{
    public static GvSmsSendResult Ok() => new(true, GvSendOutcome.Queued, null);
    public static GvSmsSendResult Fail(GvSendOutcome outcome, string error) => new(false, outcome, error);
}

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

    // Optional — only needed for the write path (SendAsync). Null on the read-only test constructor.
    private readonly IGvAuthenticatedClientProvider? _provider;
    // Defaults here keep the explicit-client SendAsync overload usable from the read-only test
    // constructor (the test asserts on the path substring, not the host). The provider constructor
    // overwrites both from the live adapter.
    private string _baseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private string _apiKey = "";

    public GvSmsClient(GvThreadClient threadClient, IGvThreadParser parser, ILogger<GvSmsClient> logger)
    {
        _threadClient = threadClient;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>DI-facing constructor: adds the auth provider so SendAsync can resolve the live client.</summary>
    public GvSmsClient(GvThreadClient threadClient, IGvThreadParser parser,
        IGvAuthenticatedClientProvider provider, ILogger<GvSmsClient> logger)
        : this(threadClient, parser, logger)
    {
        _provider = provider;
        _baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        _apiKey = provider.ApiKey;
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

    /// <summary>
    /// Production send: resolves the live authenticated client per call (cookie rotation + recovery
    /// ladder, ADR §1.3) and posts api2thread/sendsms. threadId must already be resolved
    /// (ISmsThreadIdResolver) and the recipient already normalized (PhoneNumberNormalizer) by the caller.
    /// </summary>
    public Task<GvSmsSendResult> SendAsync(string threadId, string text, CancellationToken ct = default)
        => SendAsync(_provider?.GetAuthenticatedClient(), threadId, text, ct);

    /// <summary>
    /// Core send (test-facing overload takes an explicit client). Returns Queued only on HTTP 200;
    /// never throws — an exception or non-200 becomes Queued=false + Error (honest status, ADR §4.2 #3).
    /// Builds the EXACT ADR §4.1 payload [null,null,null,null,text,threadId] via GvProtobuf.BuildArray.
    /// </summary>
    public async Task<GvSmsSendResult> SendAsync(
        HttpClient? authenticatedClient, string threadId, string text, CancellationToken ct = default)
    {
        if (authenticatedClient is null)
        {
            _logger.LogWarning("sendsms skipped — authenticated client unavailable");
            return GvSmsSendResult.Fail(GvSendOutcome.AdapterUnavailable,
                "GV adapter unavailable (no authenticated client)");
        }
        try
        {
            var url = $"{_baseUrl}/api2thread/sendsms?alt=protojson&key={_apiKey}";
            // ADR §4.1 payload. The thread-id form is UNVERIFIED (ADR §11 step 4) — resolved upstream
            // by ISmsThreadIdResolver, passed in here verbatim.
            var payload = GvProtobuf.BuildArray(null, null, null, null, text, threadId);
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await authenticatedClient.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("sendsms queued for thread {ThreadId}", threadId);
                return GvSmsSendResult.Ok();   // 200 = QUEUED, not delivered (honest)
            }

            // Distinguish a bad-number rejection from a generic upstream failure so the controller can
            // return 400 invalid_number (UI: "number doesn't look right") vs 502 upstream_error
            // (§"Error taxonomy"). GV signals a bad recipient with INVALID_ARGUMENT in the body.
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("sendsms INVALID_ARGUMENT for thread {ThreadId}", threadId);
                return GvSmsSendResult.Fail(GvSendOutcome.InvalidArgument,
                    "Google rejected the recipient (INVALID_ARGUMENT)");
            }
            _logger.LogWarning("sendsms returned {Status} for thread {ThreadId}",
                response.StatusCode, threadId);
            return GvSmsSendResult.Fail(GvSendOutcome.UpstreamError,
                $"Google returned {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException
                                      or HttpRequestException)
        {
            // No response observed → honest "timeout/no response" (504), distinct from an upstream non-200.
            _logger.LogWarning(ex, "sendsms timed out / network error for thread {ThreadId}", threadId);
            return GvSmsSendResult.Fail(GvSendOutcome.Timeout, "Send request timed out (no response)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sendsms failed for thread {ThreadId}", threadId);
            return GvSmsSendResult.Fail(GvSendOutcome.UpstreamError, "Send request failed (exception)");
        }
    }
}
