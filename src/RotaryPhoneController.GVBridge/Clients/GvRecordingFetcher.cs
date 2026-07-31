using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Default recording fetcher.
/// <para>
/// VERIFIED (2026-07-31 capture): the media reference at message index 11 is a FULLY ABSOLUTE URL —
/// <c>https://www.google.com/voice/media/svm/&lt;acct&gt;/&lt;token&gt;</c>, no query string. That is a
/// DIFFERENT host from the API base (clients6.google.com), and a browser <c>fetch()</c> from the
/// voice.google.com origin is CORS-blocked, so the bytes must be proxied server-side — which is
/// exactly what this class plus GvVoicemailController's /audio route already do.
/// </para>
/// <para>
/// The absolute-URL branch below is therefore now the production path. The relative
/// <c>recording/get?id=...&amp;key=...</c> fallback is retained only for non-URL references; no
/// captured voicemail has ever used it.
/// </para>
/// </summary>
/// <remarks>
/// Two construction paths exist:
/// <list type="bullet">
/// <item>A plain <see cref="HttpClient"/> + baseUrl/apiKey — used by hermetic unit tests.</item>
/// <item>An <see cref="IGvAuthenticatedClientProvider"/> — used by DI. The live authenticated client
/// is resolved <b>per call</b> so this fetcher does NOT capture a client at container-build time
/// (the adapter activates later, after cookies load). When the provider has no client yet, the
/// fetch degrades to a failure result instead of throwing — see ADR §1.3 activation-order note.</item>
/// </list>
/// </remarks>
public class GvRecordingFetcher : IGvRecordingFetcher
{
    private readonly IGvAuthenticatedClientProvider? _provider;
    private readonly HttpClient? _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvRecordingFetcher> _logger;

    /// <summary>Test-facing constructor: a fixed HttpClient + base URL + key.</summary>
    public GvRecordingFetcher(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvRecordingFetcher> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// DI-facing constructor: resolves the live authenticated HttpClient from the provider on each
    /// call so nothing is captured at construction (avoids startup crash when the adapter is inactive).
    /// </summary>
    public GvRecordingFetcher(IGvAuthenticatedClientProvider provider,
        ILogger<GvRecordingFetcher> logger)
    {
        _provider = provider;
        _baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        _apiKey = provider.ApiKey;
        _logger = logger;
    }

    public async Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
    {
        // Resolve the live client per call when provider-backed; the test path uses the captured one.
        var http = _http ?? _provider?.GetAuthenticatedClient();
        if (http is null)
        {
            _logger.LogWarning("recording fetch skipped — authenticated client unavailable for {MediaRef}", mediaRef);
            return new GvRecordingFetchResult(false, null, "audio/mpeg");
        }

        try
        {
            // VERIFIED production path: mediaRef is an absolute www.google.com media URL — GET it
            // directly (auth rides the shared handler, which is cookie-based and so applies across
            // google.com hosts). The relative form is a legacy fallback only.
            var url = Uri.TryCreate(mediaRef, UriKind.Absolute, out _)
                ? mediaRef
                : $"{_baseUrl}/recording/get?id={Uri.EscapeDataString(mediaRef)}&key={_apiKey}";

            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("recording fetch returned {Status} for {MediaRef}",
                    response.StatusCode, mediaRef);
                return new GvRecordingFetchResult(false, null, "audio/mpeg");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            return new GvRecordingFetchResult(true, bytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "recording fetch failed for {MediaRef}", mediaRef);
            return new GvRecordingFetchResult(false, null, "audio/mpeg");
        }
    }
}
