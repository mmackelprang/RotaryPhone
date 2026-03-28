using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

public class GvAccountClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvAccountClient> _logger;

    public GvAccountClient(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvAccountClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/threadinginfo/get?alt=protojson&key={_apiKey}";
            var content = new StringContent("[]", Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GV health check passed");
                return true;
            }
            _logger.LogWarning("GV health check failed: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GV health check error");
            return false;
        }
    }

    /// <summary>
    /// Get account info (phone numbers, settings).
    /// Returns the raw JsonDocument for the caller to extract what they need.
    /// Caller is responsible for disposing the returned JsonDocument.
    /// </summary>
    public async Task<JsonDocument?> GetAccountAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/account/get?alt=protojson&key={_apiKey}";
            var content = new StringContent("[]", Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get GV account info");
            return null;
        }
    }
}
