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
    private readonly ICdpCookieExtractor _cdpExtractor;
    private readonly ILogger<GVBridgeController> _logger;

    public GVBridgeController(
        ICallAdapterRegistry registry,
        GVApiAdapter adapter,
        IGvCookieManager cookieManager,
        IOptions<GVBridgeConfig> config,
        ICdpCookieExtractor cdpExtractor,
        ILogger<GVBridgeController> logger)
    {
        _registry = registry;
        _adapter = adapter;
        _cookieManager = cookieManager;
        _config = config.Value;
        _cdpExtractor = cdpExtractor;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        // Typed DTO; the original four field names (available, activeMode, sipRegistered,
        // cookiesValid) are preserved exactly via [JsonPropertyName] for contract stability.
        return Ok(new GvBridgeStatusDto(
            Available: _adapter.IsAvailable,
            ActiveMode: _registry.ActiveMode.ToString(),
            SipRegistered: _adapter.IsSipRegistered,
            WsConnected: _adapter.IsWebSocketConnected,
            LastConnectedAt: _adapter.SipLastConnectedAt,
            CookiesValid: _adapter.AreCookiesValid,
            PsidtsAgeSeconds: _adapter.PsidtsAgeSeconds,
            Degraded: _adapter.Degraded,
            LastHealthyAt: _adapter.LastHealthyAt,
            ThrottledUntil: _adapter.ThrottledUntil,
            ThrottleReason: _adapter.ThrottleReason));
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
            var parsed = CdpCookieExtractor.ParseCookieHeader(request.RawCookieHeader);
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

        var extraction = await _cdpExtractor.ExtractAsync(cdpPort, targetUrl);
        if (!extraction.Success)
        {
            return extraction.Status switch
            {
                CdpExtractionStatus.NoMatchingTab => NotFound(new { error = extraction.Error }),
                CdpExtractionStatus.NoCookies or CdpExtractionStatus.MissingRequiredCookies
                    => BadRequest(new { error = extraction.Error }),
                _ => StatusCode(503, new { error = extraction.Error }),
            };
        }

        var cookieSet = extraction.Cookies!;
        var success = await _cookieManager.SetCookiesAsync(cookieSet);
        if (!success)
            return StatusCode(500, new { error = "Cookies extracted successfully but failed to save/activate. Check server logs." });

        var sapisidPrefix = cookieSet.Sapisid.Length > 8
            ? cookieSet.Sapisid[..8]
            : cookieSet.Sapisid;

        _logger.LogInformation("CDP cookie refresh: {Count} cookies extracted and activated", extraction.CookieCount);

        return Ok(new RefreshFromBrowserResponse(
            Refreshed: true,
            CookieCount: extraction.CookieCount,
            SapisidPrefix: sapisidPrefix));
    }

    public record SetModeRequest(string Mode);
}
