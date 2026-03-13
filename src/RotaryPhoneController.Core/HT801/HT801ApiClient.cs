using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.Core.HT801;

/// <summary>
/// HTTP client for Grandstream HT801V2 REST API.
/// Auth uses Base64-encoded password in P2 field, cookie-based sessions,
/// and session_token in POST body (not as a cookie/header).
/// </summary>
public class HT801ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string? _sessionToken;

    // Key P-value identifiers for HT801V2 firmware
    public const string PSipServer = "P47";
    public const string PSipUserId = "P35";
    public const string PSipAuthId = "P36";
    public const string PSipAuthPassword = "P34";
    public const string PSipPort = "P40";
    public const string PSipTransport = "P2912";
    public const string PSipRegistrationEnable = "P72";
    public const string PPreferredCodec = "P58";
    public const string PFirmwareVersion = "P68";

    public HT801ApiClient(ILogger logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
    }

    public async Task<bool> LoginAsync(string ipAddress, string username, string password)
    {
        try
        {
            var base64Password = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("P2", base64Password)
            });

            var resp = await _http.PostAsync($"http://{ipAddress}/cgi-bin/dologin", content);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("response").GetString() != "success")
            {
                var body = doc.RootElement.GetProperty("body");
                var error = body.ValueKind == JsonValueKind.String
                    ? body.GetString()
                    : body.ToString();
                _logger.LogWarning("HT801 login failed: {Error}", error);
                return false;
            }

            _sessionToken = doc.RootElement
                .GetProperty("body")
                .GetProperty("session_token")
                .GetString();

            _logger.LogInformation("HT801 login successful");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HT801 login error");
            return false;
        }
    }

    public async Task<string?> GetValueAsync(string ipAddress, string pValue)
    {
        if (_sessionToken == null) return null;

        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("request", pValue),
                new KeyValuePair<string, string>("session_token", _sessionToken)
            });

            var resp = await _http.PostAsync($"http://{ipAddress}/cgi-bin/api.values.get", content);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("response").GetString() != "success")
                return null;

            var body = doc.RootElement.GetProperty("body");
            if (body.TryGetProperty("token", out var tokenElem))
                _sessionToken = tokenElem.GetString() ?? _sessionToken;

            return body.TryGetProperty(pValue, out var valElem)
                ? valElem.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HT801 GetValue error for {PValue}", pValue);
            return null;
        }
    }

    public async Task<bool> SetValuesAsync(string ipAddress, Dictionary<string, string> values)
    {
        if (_sessionToken == null) return false;

        try
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in values)
                pairs.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
            pairs.Add(new KeyValuePair<string, string>("update", "1"));
            pairs.Add(new KeyValuePair<string, string>("session_token", _sessionToken));

            var content = new FormUrlEncodedContent(pairs);
            var resp = await _http.PostAsync($"http://{ipAddress}/cgi-bin/api.values.post", content);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("response").GetString() != "success")
            {
                _logger.LogWarning("HT801 SetValues failed: {Response}", json);
                return false;
            }

            var body = doc.RootElement.GetProperty("body");
            if (body.TryGetProperty("token", out var tokenElem))
                _sessionToken = tokenElem.GetString() ?? _sessionToken;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HT801 SetValues error");
            return false;
        }
    }

    public async Task<string?> GetProductModelAsync(string ipAddress)
    {
        if (_sessionToken == null) return null;

        try
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("session_token", _sessionToken)
            });

            var resp = await _http.PostAsync($"http://{ipAddress}/cgi-bin/api-get_system_base_info", content);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.GetProperty("response").GetString() != "success")
                return null;

            return doc.RootElement
                .GetProperty("body")
                .GetProperty("product")
                .GetString();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
