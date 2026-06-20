using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Auth;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>Outcome category for a CDP cookie extraction attempt (maps to HTTP status in the controller).</summary>
public enum CdpExtractionStatus
{
    Success,
    ChromeUnreachable,
    NoTabs,
    NoMatchingTab,
    NoDebuggerUrl,
    ExtractionFailed,
    NoCookies,
    MissingRequiredCookies,
}

/// <summary>Result of a CDP cookie extraction. <see cref="Cookies"/> is non-null only on success.</summary>
public record CdpExtractionResult(CdpExtractionStatus Status, GvCookieSet? Cookies, int CookieCount, string? Error)
{
    public bool Success => Status == CdpExtractionStatus.Success;

    public static CdpExtractionResult Fail(CdpExtractionStatus status, string error) =>
        new(status, null, 0, error);
}

/// <summary>
/// Extracts Google cookies from a locally-running Chrome via the Chrome DevTools Protocol
/// (<c>/json</c> tab list + <c>Network.getCookies</c> over a debugger WebSocket). Shared by the
/// manual <c>POST /api/gvbridge/cookies/refresh-from-browser</c> endpoint and the adapter's
/// automatic auth-recovery ladder, so both use one implementation of the CDP protocol.
/// </summary>
public interface ICdpCookieExtractor
{
    /// <summary>
    /// Extract the Google cookie set from the Chrome tab whose URL contains <paramref name="targetUrl"/>.
    /// Returns a non-success status (with a human-readable error) rather than throwing on the common
    /// failure modes (Chrome down, no tab, no cookies). Does NOT persist anything — the caller decides
    /// how to save/activate the returned cookies.
    /// </summary>
    Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default);
}

public sealed class CdpCookieExtractor : ICdpCookieExtractor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CdpCookieExtractor> _logger;

    private static readonly JsonSerializerOptions CdpJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public CdpCookieExtractor(IHttpClientFactory httpClientFactory, ILogger<CdpCookieExtractor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
    {
        // Step 1: List browser tabs via the CDP HTTP endpoint.
        List<CdpTab>? tabs;
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"http://localhost:{cdpPort}/json", ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            tabs = JsonSerializer.Deserialize<List<CdpTab>>(json, CdpJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDP: Cannot reach Chrome on port {Port}", cdpPort);
            return CdpExtractionResult.Fail(CdpExtractionStatus.ChromeUnreachable,
                $"Chrome not reachable on CDP port {cdpPort}. Is Chrome running with --remote-debugging-port={cdpPort}?");
        }

        if (tabs is null || tabs.Count == 0)
            return CdpExtractionResult.Fail(CdpExtractionStatus.NoTabs, "CDP returned no browser tabs.");

        // Step 2: Find the target tab.
        var tab = tabs.FirstOrDefault(t =>
            t.Url?.Contains(targetUrl, StringComparison.OrdinalIgnoreCase) == true);

        if (tab is null)
            return CdpExtractionResult.Fail(CdpExtractionStatus.NoMatchingTab,
                $"No tab found with URL containing \"{targetUrl}\". Open voice.google.com in Chrome first.");

        if (string.IsNullOrEmpty(tab.WebSocketDebuggerUrl))
            return CdpExtractionResult.Fail(CdpExtractionStatus.NoDebuggerUrl,
                "Target tab has no WebSocket debugger URL. Ensure Chrome was started with --remote-debugging-port.");

        // Step 3: Connect via WebSocket and extract cookies.
        string rawCookieHeader;
        int cookieCount;
        try
        {
            (rawCookieHeader, cookieCount) = await ExtractCookiesViaCdpAsync(tab.WebSocketDebuggerUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDP: WebSocket cookie extraction failed");
            return CdpExtractionResult.Fail(CdpExtractionStatus.ExtractionFailed,
                $"WebSocket CDP connection failed: {ex.Message}");
        }

        if (cookieCount == 0 || string.IsNullOrEmpty(rawCookieHeader))
            return CdpExtractionResult.Fail(CdpExtractionStatus.NoCookies,
                "CDP returned no cookies for the target domains.");

        // Step 4: Parse and validate the minimum required fields.
        var parsed = ParseCookieHeader(rawCookieHeader);
        if (!parsed.ContainsKey("SAPISID") || !parsed.ContainsKey("SID"))
            return CdpExtractionResult.Fail(CdpExtractionStatus.MissingRequiredCookies,
                "Extracted cookies are missing required SAPISID and/or SID. Is the Chrome session logged into Google?");

        var cookieSet = new GvCookieSet
        {
            Sapisid = parsed.GetValueOrDefault("SAPISID") ?? "",
            Sid = parsed.GetValueOrDefault("SID") ?? "",
            Hsid = parsed.GetValueOrDefault("HSID") ?? "",
            Ssid = parsed.GetValueOrDefault("SSID") ?? "",
            Apisid = parsed.GetValueOrDefault("APISID") ?? "",
            Secure1Psid = parsed.GetValueOrDefault("__Secure-1PSID"),
            Secure3Psid = parsed.GetValueOrDefault("__Secure-3PSID"),
            RawCookieHeader = rawCookieHeader,
        };

        return new CdpExtractionResult(CdpExtractionStatus.Success, cookieSet, cookieCount, null);
    }

    /// <summary>
    /// Connects to a Chrome tab via WebSocket and calls <c>Network.getCookies</c> for the
    /// voice.google.com and clients6.google.com domains. Returns the raw cookie header and count.
    /// </summary>
    internal static async Task<(string RawCookieHeader, int CookieCount)> ExtractCookiesViaCdpAsync(
        string webSocketUrl, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await ws.ConnectAsync(new Uri(webSocketUrl), cts.Token);

        var command = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Network.getCookies",
            @params = new
            {
                urls = new[] { "https://voice.google.com", "https://clients6.google.com" },
            },
        });

        var sendBuffer = Encoding.UTF8.GetBytes(command);
        await ws.SendAsync(new ArraySegment<byte>(sendBuffer), WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        var receiveBuffer = new byte[64 * 1024];
        var responseBuilder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), cts.Token);
            responseBuilder.Append(Encoding.UTF8.GetString(receiveBuffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        var responseJson = responseBuilder.ToString();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var resultProp) ||
            !resultProp.TryGetProperty("cookies", out var cookiesArray))
        {
            return ("", 0);
        }

        var cookieParts = new List<string>();
        foreach (var cookie in cookiesArray.EnumerateArray())
        {
            var name = cookie.GetProperty("name").GetString();
            var value = cookie.GetProperty("value").GetString();
            if (!string.IsNullOrEmpty(name))
                cookieParts.Add($"{name}={value}");
        }

        var rawHeader = string.Join("; ", cookieParts);
        return (rawHeader, cookieParts.Count);
    }

    /// <summary>
    /// Parse a raw Cookie header ("name=value; name2=value2; ...") into a dictionary.
    /// Handles cookies whose values contain '='.
    /// </summary>
    public static Dictionary<string, string> ParseCookieHeader(string header)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx > 0)
            {
                var name = part[..eqIdx].Trim();
                var value = part[(eqIdx + 1)..].Trim();
                result[name] = value;
            }
        }
        return result;
    }

    /// <summary>Minimal DTO for deserializing CDP <c>/json</c> tab list entries (camelCase from Chrome).</summary>
    internal record CdpTab
    {
        public string? Url { get; init; }
        public string? WebSocketDebuggerUrl { get; init; }
    }
}
