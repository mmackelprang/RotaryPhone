using System.Text;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

public class GvSmsClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvSmsClient> _logger;

    public GvSmsClient(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvSmsClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string toNumber, string body, CancellationToken ct = default)
    {
        try
        {
            var threadId = $"t.{toNumber}";
            var url = $"{_baseUrl}/api2thread/sendsms?alt=protojson&key={_apiKey}";
            var payload = GvProtobuf.BuildArray(null, null, null, null, body, threadId);
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("SMS sent to {Number}", toNumber);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SMS to {Number}", toNumber);
            return false;
        }
    }
}
