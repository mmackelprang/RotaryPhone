using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>Result of a thread list call. Succeeded=false means a non-200/parse failure (caller
/// should not treat empty as "no threads" — the poller distinguishes them, ADR §5.3).</summary>
public record GvThreadListResult(IReadOnlyList<GvThreadNode> Threads, string? NextPageToken, bool Succeeded)
{
    public static GvThreadListResult Empty(bool succeeded) =>
        new(Array.Empty<GvThreadNode>(), null, succeeded);
}

/// <summary>
/// Lists GV threads via api2thread/list. Thin wrapper over the shared authenticated HttpClient
/// (ADR §1.3, §7) — gets auth/cookies/PSIDTS freshness for free. All field parsing is delegated to
/// <see cref="IGvThreadParser"/> so UNVERIFIED positions live in exactly one place.
/// </summary>
public class GvThreadClient
{
    private readonly HttpClient? _http;
    private readonly IGvAuthenticatedClientProvider? _provider;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvThreadClient> _logger;

    /// <summary>Test-facing constructor: a fixed HttpClient + base URL + key.</summary>
    public GvThreadClient(HttpClient http, string baseUrl, string apiKey,
        IGvThreadParser parser, ILogger<GvThreadClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>
    /// DI-facing constructor: resolves the live authenticated HttpClient from the provider on each
    /// call so nothing is captured at construction (avoids startup crash when the adapter is inactive,
    /// ADR §1.3 activation-order note). When the provider has no client yet, list calls return a
    /// failure result instead of throwing.
    /// </summary>
    public GvThreadClient(IGvAuthenticatedClientProvider provider, IGvThreadParser parser,
        ILogger<GvThreadClient> logger)
    {
        _provider = provider;
        _baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        _apiKey = provider.ApiKey;
        _parser = parser;
        _logger = logger;
    }

    public async Task<GvThreadListResult> ListThreadsAsync(
        GvThreadFolder folder, int count = 20, string? pageToken = null, CancellationToken ct = default)
    {
        using var root = await ListRawAsync(folder, count, pageToken, ct);
        if (root is null) return GvThreadListResult.Empty(succeeded: false);

        var threads = _parser.ParseThreadList(root.RootElement);
        var rawThreads = _parser.CountThreads(root.RootElement);

        // Wire-shape drift guard: GV sent threads but our positional indices produced nothing.
        // Report failure so callers surface it, instead of it looking like an empty folder.
        if (rawThreads > 0 && threads.Count == 0)
        {
            _logger.LogError(
                "api2thread/list wire-shape drift for folder {Folder}: {RawThreads} raw threads " +
                "parsed to 0 nodes. Positional indices no longer match Google's response — re-capture " +
                "and update PositionalGvThreadParser.", folder, rawThreads);
            return GvThreadListResult.Empty(succeeded: false);
        }

        _logger.LogInformation("Listed {Count} threads from {RawThreads} raw threads for folder {Folder}",
            threads.Count, rawThreads, folder);
        var token = _parser.ParseNextPageToken(root.RootElement);
        return new GvThreadListResult(threads, token, Succeeded: true);
    }

    /// <summary>
    /// Raw list call shared by thread/voicemail/SMS read paths — returns the parsed JsonDocument or
    /// null on failure. Caller is responsible for disposing the returned JsonDocument.
    /// <para>
    /// Request body is VERIFIED against a live capture (2026-07-31):
    /// <c>[folder, count, 15, null, null, [null,1,1,1]]</c>. Note <c>count</c> is at index 1 — the
    /// previous <c>[folder, pageToken, count]</c> shape was synthesized and wrong.
    /// </para>
    /// </summary>
    public async Task<JsonDocument?> ListRawAsync(
        GvThreadFolder folder, int count, string? pageToken, CancellationToken ct = default)
    {
        // Paging is UNVERIFIED: the capture was a single un-paged request, so we know neither which
        // body position carries a page token nor whether root[2]'s version cursor is one. Rather than
        // guess a position, we ignore the token and say so — silently dropping it would make a caller
        // believe it was paging while it re-read page 1 forever.
        if (pageToken is not null)
        {
            _logger.LogWarning(
                "api2thread/list ignoring pageToken for folder {Folder} — the request body's paging " +
                "field position is UNVERIFIED (no paged capture exists). Returning the first page.",
                folder);
        }

        var url = $"{_baseUrl}/api2thread/list?alt=protojson&key={_apiKey}";
        // VERIFIED body: [folder, count, 15, null, null, [null,1,1,1]].
        // Index 2's constant 15 and the trailing flags array are sent verbatim as captured; their
        // meanings are unknown, so they are reproduced rather than reinterpreted.
        var payload = GvProtobuf.BuildArray(
            folder.ToWireValue(), count, 15, null, null,
            new object?[] { null, 1, 1, 1 });

        // Attempt 1, then — on 401/403 only, and only when provider-backed — recover and replay ONCE.
        var (doc, authFailed) = await TrySendAsync(url, payload, folder, ct);
        if (doc is not null || !authFailed || _provider is null)
            return doc;

        _logger.LogInformation(
            "api2thread/list auth-failed for folder {Folder} — recovering cookies and retrying once", folder);

        if (!await _provider.TryRecoverAuthAsync($"api2thread/list 401 ({folder})", ct))
        {
            _logger.LogWarning("api2thread/list retry skipped for folder {Folder} — recovery failed", folder);
            return null;
        }

        (doc, _) = await TrySendAsync(url, payload, folder, ct);
        return doc;
    }

    /// <summary>
    /// One attempt. Returns (document, authFailed). The client is RE-RESOLVED per attempt: recovery
    /// rungs 1 and 2 dispose and re-create the adapter's HttpClient, so a captured instance would
    /// throw ObjectDisposedException on the retry.
    /// <para>
    /// Only 401/403 sets authFailed. A 429, a 5xx, or a network fault must NOT trigger recovery —
    /// throttling is falsified for this defect and replaying into a 429 is exactly the wrong move.
    /// </para>
    /// </summary>
    private async Task<(JsonDocument? Doc, bool AuthFailed)> TrySendAsync(
        string url, string payload, GvThreadFolder folder, CancellationToken ct)
    {
        // Resolve the live client per call when provider-backed; the test path uses the captured one.
        var http = _http ?? _provider?.GetAuthenticatedClient();
        if (http is null)
        {
            _logger.LogWarning("api2thread/list skipped — authenticated client unavailable for folder {Folder}",
                folder);
            return (null, false);
        }

        try
        {
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("api2thread/list returned {Status} for folder {Folder}",
                    response.StatusCode, folder);
                var authFailed = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                                     or System.Net.HttpStatusCode.Forbidden;
                _provider?.RecordApiOutcome(success: false, authFailure: authFailed);
                return (null, authFailed);
            }
            var raw = await response.Content.ReadAsStringAsync(ct);

            // Record the success only once the body has actually parsed. A 200 carrying
            // unparseable content is NOT a data-plane success — this method returns null for it —
            // and recording one would clear an authBlackout that nothing has really resolved. It is
            // not an auth failure either, so it records no outcome at all: the throw below lands in
            // the catch, which deliberately reports nothing.
            var doc = JsonDocument.Parse(raw);
            _provider?.RecordApiOutcome(success: true, authFailure: false);
            return (doc, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "api2thread/list failed for folder {Folder}", folder);
            return (null, false);
        }
    }
}
