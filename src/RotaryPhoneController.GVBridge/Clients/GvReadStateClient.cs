using System.Text;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Classified outcome of a GV updateread call (ADR §4.3, §7). The CLIENT only knows what it observed at
/// the GV boundary; the CONTROLLER owns the HTTP mapping (all non-Applied outcomes collapse to 502 on the
/// wire per the reply's single-502 table — the distinction here is for logging). Applied is true ONLY for
/// <see cref="Applied"/>.
/// </summary>
public enum GvUpdateReadOutcome
{
    Applied,            // HTTP 200 from GV — the mark was accepted (NOT a guess)
    AdapterUnavailable, // no authenticated client (cookie decay / recovery window) → 502
    UpstreamError,      // GV returned a non-200 (incl. a partial thread mark) → 502
    Timeout             // timeout / network exception, no response observed → 502
}

/// <summary>
/// Result of a GV updateread call. Outcome==Applied ONLY when Google returned HTTP 200 — an HONEST mark,
/// never a false success (ADR §3.2 honesty constraint; cf. the 603 incident). Error is populated on any
/// failure. Callers MUST NOT auto-retry on failure (ADR §8) — return 502, let RadioConsole reconcile.
/// </summary>
public record GvUpdateReadResult(GvUpdateReadOutcome Outcome, string? Error)
{
    public static GvUpdateReadResult Ok() => new(GvUpdateReadOutcome.Applied, null);
    public static GvUpdateReadResult Fail(GvUpdateReadOutcome outcome, string error) => new(outcome, error);
}

/// <summary>
/// GV read-state write client (ADR §3 write-through). POSTs api2thread/updateread over the shared
/// authenticated HttpClient (same IGvAuthenticatedClientProvider seam GvSmsClient.SendAsync rides — cookie
/// rotation + recovery ladder for free, ADR §1.3, §7). The wire format is delegated to
/// IUpdateReadPayloadBuilder so the UNVERIFIED positions/grain live in exactly one place. Never throws.
/// </summary>
public class GvReadStateClient
{
    private readonly IUpdateReadPayloadBuilder _payloadBuilder;
    private readonly ILogger<GvReadStateClient> _logger;

    private readonly IGvAuthenticatedClientProvider? _provider;
    // Defaults keep the explicit-client overloads usable from the read-only test constructor (tests assert
    // on the path substring, not the host). The provider constructor overwrites both from the live adapter.
    private string _baseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private string _apiKey = "";

    /// <summary>Test-facing constructor (no provider — use the explicit-client overloads).</summary>
    public GvReadStateClient(IUpdateReadPayloadBuilder payloadBuilder, ILogger<GvReadStateClient> logger)
    {
        _payloadBuilder = payloadBuilder;
        _logger = logger;
    }

    /// <summary>DI-facing constructor: adds the auth provider so production resolves the live client.</summary>
    public GvReadStateClient(IUpdateReadPayloadBuilder payloadBuilder,
        IGvAuthenticatedClientProvider provider, ILogger<GvReadStateClient> logger)
        : this(payloadBuilder, logger)
    {
        _provider = provider;
        _baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        _apiKey = provider.ApiKey;
    }

    // ----- Voicemail -----

    /// <summary>Production: resolve the live client per call, mark one voicemail read/unread.</summary>
    public Task<GvUpdateReadResult> MarkVoicemailReadAsync(
        string messageId, string threadId, bool isRead, CancellationToken ct = default)
        => MarkVoicemailReadAsync(_provider?.GetAuthenticatedClient(), messageId, threadId, isRead, ct);

    /// <summary>Core (test-facing explicit client). Applied only on HTTP 200; never throws.</summary>
    public Task<GvUpdateReadResult> MarkVoicemailReadAsync(
        HttpClient? authenticatedClient, string messageId, string threadId, bool isRead,
        CancellationToken ct = default)
        => PostOneAsync(authenticatedClient,
            _payloadBuilder.BuildVoicemail(messageId, threadId, isRead), ct);

    // ----- SMS thread (per-thread grain — ADR §4.2 Q4) -----

    /// <summary>Production: resolve the live client per call, mark a whole SMS thread read/unread.</summary>
    public Task<GvUpdateReadResult> MarkSmsThreadReadAsync(
        string threadId, IReadOnlyList<string> messageIds, bool isRead, CancellationToken ct = default)
        => MarkSmsThreadReadAsync(_provider?.GetAuthenticatedClient(), threadId, messageIds, isRead, ct);

    /// <summary>
    /// Core (test-facing explicit client). POSTs one updateread per payload from the builder (per-message
    /// grain = one per message id; thread-level = one). Applied ONLY if EVERY post returns 200 — a partial
    /// thread mark is an honest failure (no false "applied"), and short-circuits on the first failure.
    /// </summary>
    public async Task<GvUpdateReadResult> MarkSmsThreadReadAsync(
        HttpClient? authenticatedClient, string threadId, IReadOnlyList<string> messageIds, bool isRead,
        CancellationToken ct = default)
    {
        var payloads = _payloadBuilder.BuildSmsThread(threadId, messageIds, isRead);
        foreach (var payload in payloads)
        {
            var result = await PostOneAsync(authenticatedClient, payload, ct);
            if (result.Outcome != GvUpdateReadOutcome.Applied)
                return result;   // honest: partial mark = failure, RadioConsole reconciles on next list
        }
        return GvUpdateReadResult.Ok();
    }

    // ----- shared POST (mirrors GvSmsClient.SendAsync's body exactly) -----

    private async Task<GvUpdateReadResult> PostOneAsync(
        HttpClient? authenticatedClient, string payload, CancellationToken ct)
    {
        if (authenticatedClient is null)
        {
            _logger.LogWarning("updateread skipped — authenticated client unavailable");
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.AdapterUnavailable,
                "GV adapter unavailable (no authenticated client)");
        }
        try
        {
            var url = $"{_baseUrl}/api2thread/updateread?alt=protojson&key={_apiKey}";
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await authenticatedClient.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("updateread applied");
                return GvUpdateReadResult.Ok();   // 200 = GV accepted the mark (honest)
            }
            _logger.LogWarning("updateread returned {Status}", response.StatusCode);
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.UpstreamError,
                $"Google returned {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException
                                      or HttpRequestException)
        {
            _logger.LogWarning(ex, "updateread timed out / network error");
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.Timeout,
                "updateread request timed out (no response)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "updateread failed");
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.UpstreamError,
                "updateread request failed (exception)");
        }
    }
}
