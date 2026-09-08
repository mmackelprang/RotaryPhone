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
            ThrottleReason: _adapter.ThrottleReason,
            AuthBlackout: _adapter.AuthBlackout,
            LastApiSuccessAt: _adapter.LastApiSuccessAt,
            LastApiAuthFailureAt: _adapter.LastApiAuthFailureAt,
            PsidtsMintedAtUtc: _adapter.PsidtsMintedAtUtc,
            BrowserSessionValidatedAt: _adapter.BrowserSessionValidatedAt,
            BrowserSessionAgeSeconds: _adapter.BrowserSessionAgeSeconds,
            BrowserSessionStale: _adapter.BrowserSessionStale));
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

        var outcome = await _cookieManager.SetCookiesAsync(cookieSet);

        // `saved` keeps its meaning exactly — "the cookies actually WORK" — so the existing consumer
        // contract does not move; `outcome` is additive and says WHICH of the several very different
        // things happened.
        return Ok(new
        {
            saved = outcome is SetCookiesOutcome.Adopted or SetCookiesOutcome.AdoptedButActivationFailed,
            outcome = outcome.ToString()
        });
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
        var outcome = await _cookieManager.SetCookiesAsync(cookieSet);

        // ⚠ ONE 502 FOR EVERY CAUSE IS A LIE, and it was this endpoint's. It told the operator that
        // "the browser session is stale" and that "nothing was overwritten" for three unrelated causes:
        // a genuinely stale session, an unrelated earlier data-plane 401 that makes AreCookiesValid
        // false, and a missing key / registry throw / IO error. On the cold path the file HAD been
        // overwritten, and in two of the three branches nothing had tested the Google login at all.
        // Same rule as the exhausted-ladder message (Task 7): state only what was actually tested.
        switch (outcome)
        {
            case SetCookiesOutcome.RejectedByGoogle:
                return StatusCode(502, new
                {
                    error = "Cookies were extracted from Chrome and Google refused them on a live probe "
                          + "— the browser session is stale. This is TESTED, not inferred. The existing "
                          + "credentials were kept and the cookie file was NOT overwritten. "
                          + "ACTION: re-login at voice.google.com."
                });

            case SetCookiesOutcome.ColdSeedUnvalidated:
                return StatusCode(202, new
                {
                    error = "Cookies were extracted from Chrome and WRITTEN to disk — there was no "
                          + "validated set to protect, so the previous file WAS overwritten. Nothing "
                          + "here proved them against Google: the adapter does not report valid cookies "
                          + "afterwards, which may be a dead set OR an unrelated auth failure still in "
                          + "effect. ACTION: check GET /api/gvbridge/status before re-logging in."
                });

            case SetCookiesOutcome.ActivationFailed:
                return StatusCode(500, new
                {
                    error = "Cookies were extracted from Chrome, but the write or the re-activation "
                          + "threw. Nothing here tested the Google login — do NOT assume it is dead. "
                          + "ACTION: check the service log, which says which step failed."
                });

            case SetCookiesOutcome.AdoptedButNotPersisted:
                return StatusCode(500, new
                {
                    error = "Cookies were extracted from Chrome and Google ACCEPTED them on a live "
                          + "probe — they are in use now — but they could not be written to disk, so a "
                          + "restart will revert to the older set. The Google login is fine; the disk "
                          + "is not. ACTION: check disk space and permissions."
                });

            case SetCookiesOutcome.AdoptedButActivationFailed:
                // The refresh itself SUCCEEDED: the cookies passed a live probe and are on disk, which
                // is what this endpoint was asked to do. The call path may still be down, which is
                // separately visible as sipRegistered:false / degraded:true on /status.
                _logger.LogError(
                    "CDP cookie refresh validated and persisted {Count} cookies, but re-activating the "
                    + "adapter failed — SMS and voicemail should work, CALLS MAY NOT. "
                    + "ACTION: GET /api/gvbridge/status.", extraction.CookieCount);
                break;

            case SetCookiesOutcome.Adopted:
                break;
        }

        var sapisidPrefix = cookieSet.Sapisid.Length > 8
            ? cookieSet.Sapisid[..8]
            : cookieSet.Sapisid;

        // "extracted and activated" used to be logged for cookies Google had already rejected — the exact
        // INF line that ran every 20 minutes for two days while the bridge was dead. It now means what it
        // says: this set passed a live probe before it was persisted.
        _logger.LogInformation(
            "CDP cookie refresh: {Count} cookies extracted, validated against Google, and activated",
            extraction.CookieCount);

        return Ok(new RefreshFromBrowserResponse(
            Refreshed: true,
            CookieCount: extraction.CookieCount,
            SapisidPrefix: sapisidPrefix));
    }

    public record SetModeRequest(string Mode);
}
