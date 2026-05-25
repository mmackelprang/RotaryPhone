using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge")]
public class GVBridgeController : ControllerBase
{
    private readonly ICallAdapterRegistry _registry;
    private readonly GVApiAdapter _adapter;
    private readonly IGvCookieManager _cookieManager;

    public GVBridgeController(
        ICallAdapterRegistry registry,
        GVApiAdapter adapter,
        IGvCookieManager cookieManager)
    {
        _registry = registry;
        _adapter = adapter;
        _cookieManager = cookieManager;
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
