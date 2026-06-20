using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvThreadClient> _logger;

    public GvThreadClient(HttpClient http, string baseUrl, string apiKey,
        IGvThreadParser parser, ILogger<GvThreadClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _parser = parser;
        _logger = logger;
    }

    public async Task<GvThreadListResult> ListThreadsAsync(
        GvThreadFolder folder, int count = 20, string? pageToken = null, CancellationToken ct = default)
    {
        using var root = await ListRawAsync(folder, count, pageToken, ct);
        if (root is null) return GvThreadListResult.Empty(succeeded: false);

        var threads = _parser.ParseThreadList(root.RootElement);
        var token = _parser.ParseNextPageToken(root.RootElement);
        return new GvThreadListResult(threads, token, Succeeded: true);
    }

    /// <summary>
    /// Raw list call shared by thread/voicemail/SMS read paths — returns the parsed JsonDocument or
    /// null on failure. Request body positions (folder, pageToken, count) are UNVERIFIED — ADR §11
    /// step 1. Built via GvProtobuf.BuildArray so the positional shape is in one obvious place.
    /// Caller is responsible for disposing the returned JsonDocument.
    /// </summary>
    public async Task<JsonDocument?> ListRawAsync(
        GvThreadFolder folder, int count, string? pageToken, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/api2thread/list?alt=protojson&key={_apiKey}";
            // UNVERIFIED positional body — ADR §11 step 1 (candidate: [folder, pageToken?, count?]).
            var payload = GvProtobuf.BuildArray(folder.ToWireValue(), pageToken, count);
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("api2thread/list returned {Status} for folder {Folder}",
                    response.StatusCode, folder);
                return null;
            }
            var raw = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "api2thread/list failed for folder {Folder}", folder);
            return null;
        }
    }
}
