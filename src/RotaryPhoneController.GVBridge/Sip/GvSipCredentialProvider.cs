using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Sip;

/// <summary>
/// Fetches SIP credentials from the GV sipregisterinfo/get endpoint.
/// Returns Bearer token, SIP username, phone number, and expiry.
/// </summary>
public sealed class GvSipCredentialProvider
{
    private static readonly Action<ILogger, Exception?> LogFetching =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, "FetchingSipCreds"),
            "Fetching SIP credentials from sipregisterinfo/get...");

    private static readonly Action<ILogger, int, Exception?> LogFetched =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(2, "SipCredsFetched"),
            "SIP credentials fetched, expires in {Expiry}s");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GvSipCredentialProvider> _logger;

    public GvSipCredentialProvider(
        IHttpClientFactory httpClientFactory,
        GVBridgeConfig config,
        ILogger<GvSipCredentialProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<SipCredentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        LogFetching(_logger, null);

        var client = _httpClientFactory.CreateClient("GvApi");
        // sipregisterinfo/get requires [3,"<deviceId>"]
        var deviceId = $"gvresearch-{Environment.MachineName}";
        var requestBody = $"[3,\"{deviceId}\"]";

        var queryString = string.IsNullOrEmpty(_config.GvApiKey)
            ? "alt=protojson"
            : $"alt=protojson&key={Uri.EscapeDataString(_config.GvApiKey)}";

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json+protobuf");
        var response = await client
            .PostAsync(
                new Uri($"voice/v1/voiceclient/sipregisterinfo/get?{queryString}", UriKind.Relative),
                content, ct)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
#pragma warning disable CA1848, CA1873 // Debug logging for troubleshooting
            _logger.LogError("sipregisterinfo/get failed: {Status} body={Body}",
                (int)response.StatusCode, json.Length > 500 ? json[..500] : json);
#pragma warning restore CA1848, CA1873
            response.EnsureSuccessStatusCode(); // throws
        }

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("sipregisterinfo/get response ({Length} chars): {Body}",
            json.Length, json[..Math.Min(500, json.Length)]);
#pragma warning restore CA1848, CA1873
        // Response format: [["sipToken",expiry],null,null,["authToken","cryptoKey"]]
        // Or possibly more complex — parse defensively
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Position 0: [timestamp, expiryMs] — NOT the SIP token
        var expiry = 600;
        if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array
            && root[0].GetArrayLength() > 1)
        {
            expiry = root[0][1].GetInt32();
        }

        // Position 3: ["sipIdentityToken", "cryptoKey/password"]
        var sipIdentity = "";
        var sipPassword = "";
        if (root.GetArrayLength() > 3 && root[3].ValueKind == JsonValueKind.Array)
        {
            sipIdentity = root[3][0].GetString() ?? "";
            if (root[3].GetArrayLength() > 1)
                sipPassword = root[3][1].GetString() ?? "";
        }

        // The SIP identity token IS the SIP username (used in URI + Digest auth)
        // It's base64-like and needs percent-encoding for SIP URIs (= -> %3D)
        var sipUsername = sipIdentity;

        LogFetched(_logger, expiry, null);

        return new SipCredentials(
            SipUsername: sipUsername,
            BearerToken: sipPassword, // The crypto key is the Digest auth password
            PhoneNumber: "+19196706660", // TODO: get from account/get
            ExpirySeconds: expiry);
    }
}
