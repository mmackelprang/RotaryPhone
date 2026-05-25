using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge")]
public class GVBridgeController : ControllerBase
{
    private readonly ICallAdapterRegistry _registry;
    private readonly GVApiAdapter _adapter;
    private readonly IGvCookieManager _cookieManager;
    private readonly GVBridgeConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GVBridgeController> _logger;

    public GVBridgeController(
        ICallAdapterRegistry registry,
        GVApiAdapter adapter,
        IGvCookieManager cookieManager,
        IOptions<GVBridgeConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<GVBridgeController> logger)
    {
        _registry = registry;
        _adapter = adapter;
        _cookieManager = cookieManager;
        _config = config.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            available = _adapter.IsAvailable,
            activeMode = _registry.ActiveMode.ToString(),
            sipRegistered = _adapter.IsSipRegistered,
            cookiesValid = _adapter.AreCookiesValid
        });
    }

    [HttpGet("adapter/mode")]
    public IActionResult GetMode()
    {
        var modes = _registry.AvailableModes.Select(m =>
        {
            return new { mode = m.ToString() };
        }).ToList();

        return Ok(new
        {
            activeMode = _registry.ActiveMode.ToString(),
            modes
        });
    }

    [HttpPut("adapter/mode")]
    public async Task<IActionResult> SetMode([FromBody] SetModeRequest request)
    {
        if (!Enum.TryParse<CallAdapterMode>(request.Mode, true, out var mode))
            return BadRequest(new { error = $"Invalid mode: {request.Mode}" });

        try
        {
            await _registry.SwitchModeAsync(mode);
            return Ok(new { activeMode = mode.ToString() });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("cookies")]
    public IActionResult GetCookies()
    {
        var status = _cookieManager.GetStatus();
        return Ok(new
        {
            cookiesPresent = status.CookiesPresent,
            cookiesValid = status.CookiesValid,
            lastValidatedAt = status.LastValidatedAt,
            loadedAt = status.LoadedAt,
            cookieCount = status.CookieCount,
            sapisidPrefix = status.SapisidPrefix
        });
    }

    [HttpPost("cookies")]
    [RequestSizeLimit(10_000)]
    public async Task<IActionResult> SetCookies([FromBody] SetCookiesRequest request)
    {
        // Parse raw cookie header if provided (preferred over individual fields)
        string? sapisid = request.Sapisid;
        string? sid = request.Sid;
        string? hsid = request.Hsid;
        string? ssid = request.Ssid;
        string? apisid = request.Apisid;
        string? secure1Psid = request.Secure1Psid;
        string? secure3Psid = request.Secure3Psid;

        if (!string.IsNullOrEmpty(request.RawCookieHeader))
        {
            var parsed = ParseCookieHeader(request.RawCookieHeader);
            sapisid ??= parsed.GetValueOrDefault("SAPISID");
            sid ??= parsed.GetValueOrDefault("SID");
            hsid ??= parsed.GetValueOrDefault("HSID");
            ssid ??= parsed.GetValueOrDefault("SSID");
            apisid ??= parsed.GetValueOrDefault("APISID");
            secure1Psid ??= parsed.GetValueOrDefault("__Secure-1PSID");
            secure3Psid ??= parsed.GetValueOrDefault("__Secure-3PSID");
        }

        // Validate minimum required fields
        if (string.IsNullOrEmpty(sapisid) || string.IsNullOrEmpty(sid))
            return BadRequest(new { error = "Sapisid and Sid are required (either as fields or in RawCookieHeader)" });

        var cookieSet = new GvCookieSet
        {
            Sapisid = sapisid,
            Sid = sid,
            Hsid = hsid ?? "",
            Ssid = ssid ?? "",
            Apisid = apisid ?? "",
            Secure1Psid = secure1Psid,
            Secure3Psid = secure3Psid,
            RawCookieHeader = request.RawCookieHeader
        };

        var success = await _cookieManager.SetCookiesAsync(cookieSet);
        return Ok(new { saved = success });
    }

    /// <summary>
    /// Extract cookies from a local Chrome instance via Chrome DevTools Protocol
    /// and feed them into the existing cookie management pipeline.
    /// Requires Chrome running with --remote-debugging-port on the same host.
    /// </summary>
    [HttpPost("cookies/refresh-from-browser")]
    public async Task<IActionResult> RefreshCookiesFromBrowser(
        [FromBody] RefreshFromBrowserRequest? request = null)
    {
        var cdpPort = request?.CdpPort ?? _config.ChromeCdpPort;
        var targetUrl = request?.TargetUrl ?? "voice.google.com";

        // Step 1: List browser tabs via CDP HTTP endpoint
        List<CdpTab>? tabs;
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"http://localhost:{cdpPort}/json");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            tabs = JsonSerializer.Deserialize<List<CdpTab>>(json, CdpJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDP: Cannot reach Chrome on port {Port}", cdpPort);
            return StatusCode(503, new { error = $"Chrome not reachable on CDP port {cdpPort}. Is Chrome running with --remote-debugging-port={cdpPort}?" });
        }

        if (tabs is null || tabs.Count == 0)
            return StatusCode(503, new { error = "CDP returned no browser tabs." });

        // Step 2: Find the voice.google.com tab
        var tab = tabs.FirstOrDefault(t =>
            t.Url?.Contains(targetUrl, StringComparison.OrdinalIgnoreCase) == true);

        if (tab is null)
            return NotFound(new { error = $"No tab found with URL containing \"{targetUrl}\". Open voice.google.com in Chrome first." });

        if (string.IsNullOrEmpty(tab.WebSocketDebuggerUrl))
            return StatusCode(503, new { error = "Target tab has no WebSocket debugger URL. Ensure Chrome was started with --remote-debugging-port." });

        // Step 3: Connect via WebSocket and extract cookies
        string rawCookieHeader;
        int cookieCount;
        try
        {
            (rawCookieHeader, cookieCount) = await ExtractCookiesViaCdpAsync(tab.WebSocketDebuggerUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CDP: WebSocket cookie extraction failed");
            return StatusCode(503, new { error = $"WebSocket CDP connection failed: {ex.Message}" });
        }

        if (cookieCount == 0 || string.IsNullOrEmpty(rawCookieHeader))
            return BadRequest(new { error = "CDP returned no cookies for the target domains." });

        // Step 4: Parse and validate minimum fields (SAPISID + SID required)
        var parsed = ParseCookieHeader(rawCookieHeader);
        if (!parsed.ContainsKey("SAPISID") || !parsed.ContainsKey("SID"))
            return BadRequest(new { error = "Extracted cookies are missing required SAPISID and/or SID. Is the Chrome session logged into Google?" });

        // Step 5: Save via existing cookie pipeline
        var cookieSet = new GvCookieSet
        {
            Sapisid = parsed.GetValueOrDefault("SAPISID") ?? "",
            Sid = parsed.GetValueOrDefault("SID") ?? "",
            Hsid = parsed.GetValueOrDefault("HSID") ?? "",
            Ssid = parsed.GetValueOrDefault("SSID") ?? "",
            Apisid = parsed.GetValueOrDefault("APISID") ?? "",
            Secure1Psid = parsed.GetValueOrDefault("__Secure-1PSID"),
            Secure3Psid = parsed.GetValueOrDefault("__Secure-3PSID"),
            RawCookieHeader = rawCookieHeader
        };

        var success = await _cookieManager.SetCookiesAsync(cookieSet);
        if (!success)
            return StatusCode(500, new { error = "Cookies extracted successfully but failed to save/activate. Check server logs." });

        var sapisidPrefix = cookieSet.Sapisid.Length > 8
            ? cookieSet.Sapisid[..8]
            : cookieSet.Sapisid;

        _logger.LogInformation("CDP cookie refresh: {Count} cookies extracted and activated", cookieCount);

        return Ok(new RefreshFromBrowserResponse(
            Refreshed: true,
            CookieCount: cookieCount,
            SapisidPrefix: sapisidPrefix));
    }

    /// <summary>
    /// Connects to a Chrome tab via WebSocket and calls Network.getCookies
    /// for voice.google.com and clients6.google.com domains.
    /// Returns the raw cookie header string and total cookie count.
    /// </summary>
    internal static async Task<(string RawCookieHeader, int CookieCount)> ExtractCookiesViaCdpAsync(
        string webSocketUrl, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await ws.ConnectAsync(new Uri(webSocketUrl), cts.Token);

        // Send Network.getCookies command
        var command = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Network.getCookies",
            @params = new
            {
                urls = new[] { "https://voice.google.com", "https://clients6.google.com" }
            }
        });

        var sendBuffer = Encoding.UTF8.GetBytes(command);
        await ws.SendAsync(
            new ArraySegment<byte>(sendBuffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cts.Token);

        // Receive response (may come in multiple frames)
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

        // Parse CDP response
        var responseJson = responseBuilder.ToString();
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var resultProp) ||
            !resultProp.TryGetProperty("cookies", out var cookiesArray))
        {
            return ("", 0);
        }

        // Build "name=value; name2=value2; ..." from cookies array
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
    /// Minimal DTO for deserializing CDP /json tab list entries.
    /// Chrome returns camelCase property names.
    /// </summary>
    internal record CdpTab
    {
        public string? Url { get; init; }
        public string? WebSocketDebuggerUrl { get; init; }
    }

    private static readonly JsonSerializerOptions CdpJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parse a raw Cookie header string ("name=value; name2=value2; ...")
    /// into a dictionary. Handles cookies with '=' in their values.
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

    public record SetModeRequest(string Mode);
}
