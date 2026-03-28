using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

public class GvCallClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvCallClient> _logger;

    public GvCallClient(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvCallClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<JsonDocument?> InitiateAsync(string e164Number, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/call/create?alt=protojson&key={_apiKey}";
            var body = GvProtobuf.BuildArray(null, e164Number);
            var content = new StringContent(body, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Call initiated to {Number}", e164Number);
            return JsonDocument.Parse(responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate call to {Number}", e164Number);
            return null;
        }
    }

    public async Task<bool> HangupAsync(string callId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/call/cancel?alt=protojson&key={_apiKey}";
            var body = GvProtobuf.BuildArray(null, callId);
            var content = new StringContent(body, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Call {CallId} hung up", callId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hangup call {CallId}", callId);
            return false;
        }
    }
}
