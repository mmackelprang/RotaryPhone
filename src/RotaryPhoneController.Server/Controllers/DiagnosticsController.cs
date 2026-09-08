using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Diagnostics;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.Core.Sip;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly SipDiagnosticService _diagnostics;
    private readonly IHT801ConfigService _ht801Service;
    private readonly ISipAdapter _sipAdapter;
    private readonly GVApiAdapter _gvAdapter;
    private readonly GVAudioBridgeService _gvAudioBridge;
    private readonly AppConfiguration _config;
    private readonly IRegistrarBindingStore _bindingStore;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        SipDiagnosticService diagnostics,
        IHT801ConfigService ht801Service,
        ISipAdapter sipAdapter,
        GVApiAdapter gvAdapter,
        GVAudioBridgeService gvAudioBridge,
        AppConfiguration config,
        IRegistrarBindingStore bindingStore,
        ILogger<DiagnosticsController> logger)
    {
        _diagnostics = diagnostics;
        _ht801Service = ht801Service;
        _sipAdapter = sipAdapter;
        _gvAdapter = gvAdapter;
        _gvAudioBridge = gvAudioBridge;
        _config = config;
        _bindingStore = bindingStore;
        _logger = logger;
    }

    /// <summary>
    /// Learned registrar bindings — i.e. where INVITEs will ACTUALLY be sent.
    ///
    /// <para>
    /// This used to say "use this, NOT /api/phone/system-status", because system-status reported the
    /// CONFIGURED address — a different value that could disagree, and that stayed green throughout
    /// the 2026-07 outage while every INVITE went elsewhere. That is why the endpoint was changed:
    /// as of 2026-09-08 system-status reports the RESOLVED binding the reachability probe was aimed
    /// at, so the two now AGREE on which address a ring lands on.
    /// </para>
    ///
    /// <para>
    /// This endpoint remains the place to see the full registration table — every AOR, its port,
    /// <c>learnedAtUtc</c> and freshness — where system-status answers only the narrower question
    /// "is the one resolved address reachable, and when was that last checked". Reach for this one
    /// when you need to see the bindings themselves, or when there may be more than one.
    /// </para>
    /// </summary>
    [HttpGet("sip-registrations")]
    public IActionResult GetSipRegistrations()
    {
        var now = DateTime.UtcNow;

        return Ok(_bindingStore.All().Select(b => new
        {
            addressOfRecord = b.AddressOfRecord,
            address = b.Address,
            port = b.Port,
            contactHost = b.ContactHost,
            learnedAtUtc = b.LearnedAtUtc,
            expiresSeconds = b.ExpiresSeconds,
            isFresh = b.IsFresh(now)
        }));
    }

    /// <summary>
    /// Full diagnostic snapshot: SIP status, HT801 health, GV bridge state, recent events.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var ht801Health = _diagnostics.GetHt801Health();
        var recentMessages = _diagnostics.GetRecentMessages(10);
        var timeline = _diagnostics.GetTimeline(10);

        return Ok(new
        {
            Sip = new
            {
                IsListening = _sipAdapter.IsListening,
                ListenAddress = _config.SipListenAddress,
                Port = _config.SipPort
            },
            Ht801 = ht801Health,
            GVBridge = new
            {
                IsAvailable = _gvAdapter.IsAvailable,
            },
            GVAudioBridge = new
            {
                IsActive = _gvAudioBridge.IsActive,
                Stats = _gvAudioBridge.Stats
            },
            RecentSipMessages = recentMessages,
            RecentTimeline = timeline
        });
    }

    /// <summary>
    /// Recent SIP messages with optional filtering.
    /// Query params: count (default 50), method (e.g. INVITE, REGISTER).
    /// </summary>
    [HttpGet("sip-log")]
    public IActionResult GetSipLog([FromQuery] int count = 50, [FromQuery] string? method = null)
    {
        var messages = _diagnostics.GetRecentMessages(count, method);
        return Ok(messages);
    }

    /// <summary>
    /// Call state timeline (INVITE_SENT, RINGING, CALL_ANSWERED, etc.).
    /// </summary>
    [HttpGet("timeline")]
    public IActionResult GetTimeline([FromQuery] int count = 50)
    {
        var timeline = _diagnostics.GetTimeline(count);
        return Ok(timeline);
    }

    /// <summary>
    /// Lightweight audio-bridge snapshot for cheap polling (no SIP messages / timeline).
    /// </summary>
    [HttpGet("audio-bridge")]
    public IActionResult GetAudioBridge()
    {
        var stats = _gvAudioBridge.Stats;
        return Ok(new AudioBridgeSnapshotDto(
            _gvAudioBridge.IsActive,
            stats.InboundFramesSent,
            stats.OutboundFramesReceived,
            stats.InboundErrors,
            stats.OutboundErrors,
            _gvAudioBridge.IsActive
                && stats.InboundFramesSent > 0
                && stats.OutboundFramesReceived > 0));
    }

    /// <summary>
    /// Consolidated HT801 status: network reachability, SIP registration, and freshness.
    /// </summary>
    [HttpGet("ht801")]
    public async Task<IActionResult> GetHt801([FromQuery] string? phoneId = null)
    {
        phoneId ??= _config.Phones.FirstOrDefault()?.Id ?? "default";
        var ht801Config = _ht801Service.GetConfig(phoneId);
        var sipHealth = _diagnostics.GetHt801Health();

        bool? ipReachable = null;
        if (!string.IsNullOrEmpty(ht801Config.IpAddress)
            && ht801Config.IpAddress != "0.0.0.0")
        {
            var probe = await _ht801Service.TestConnectionAsync(ht801Config.IpAddress);
            ipReachable = probe.Success;
        }

        return Ok(new Ht801StatusDto(
            ht801Config.IpAddress,
            ht801Config.Extension,
            ipReachable,
            sipHealth.IsRegistered,
            sipHealth.LastRegisterReceived,
            sipHealth.RegistrationExpiresIn));
    }

    /// <summary>
    /// Send a test INVITE to the HT801 to verify SIP connectivity.
    ///
    /// The log line and the response body report where the INVITE ACTUALLY went, not what is
    /// configured. <see cref="IHT801ConfigService.GetConfig"/> is a last-wins projection over
    /// data/ht801-config.json and is explicitly NOT a valid verification signal for addressing
    /// (docs/HT801-ADDRESS.md), so it is used only for the extension and as the resolver's fallback.
    /// </summary>
    [HttpPost("test-ring")]
    public IActionResult TestRing([FromQuery] string? phoneId = null)
    {
        phoneId ??= _config.Phones.FirstOrDefault()?.Id ?? "default";
        var ht801Config = _ht801Service.GetConfig(phoneId);

        var target = _sipAdapter.ResolveHt801Address(ht801Config.Extension, ht801Config.IpAddress);

        _logger.LogInformation("Sending test INVITE to HT801 at {Ip} for extension {Ext}",
            target, ht801Config.Extension);

        _sipAdapter.SendInviteToHT801(ht801Config.Extension, target);

        return Ok(new
        {
            Message = $"Test INVITE sent to {ht801Config.Extension}@{target}",
            PhoneId = phoneId
        });
    }

    /// <summary>
    /// Placeholder for RTP test tone. Will send a short audio burst to HT801 once implemented.
    /// </summary>
    [HttpPost("test-audio")]
    public IActionResult TestAudio()
    {
        // TODO: Implement RTP test tone generation
        return Ok(new
        {
            Message = "RTP test tone not yet implemented",
            Status = "placeholder"
        });
    }

    /// <summary>
    /// Compare HT801 configuration parameters (expected vs actual from device).
    /// </summary>
    [HttpGet("ht801/config")]
    public async Task<IActionResult> GetHt801Config([FromQuery] string? phoneId = null)
    {
        phoneId ??= _config.Phones.FirstOrDefault()?.Id ?? "default";

        if (_ht801Service is HT801ConfigService configService)
        {
            var parameters = await configService.CompareConfigAsync(phoneId);
            return Ok(parameters);
        }

        // Fallback: return validation result items as config parameters
        var result = await _ht801Service.ValidateDeviceAsync(phoneId, autoFix: false);
        var fallback = result.Items.Select(item => new ConfigParameter(
            item.Setting, item.PValue, item.Expected, item.Actual, item.Match
        )).ToList();
        return Ok(fallback);
    }

    /// <summary>
    /// Validate HT801 configuration with optional auto-fix.
    /// </summary>
    [HttpPost("ht801/validate")]
    public async Task<IActionResult> ValidateHt801(
        [FromQuery] string? phoneId = null,
        [FromQuery] bool autoFix = false)
    {
        phoneId ??= _config.Phones.FirstOrDefault()?.Id ?? "default";

        _logger.LogInformation("Validating HT801 config for {PhoneId}, autoFix={AutoFix}", phoneId, autoFix);
        var result = await _ht801Service.ValidateDeviceAsync(phoneId, autoFix);

        return Ok(result);
    }
}

/// <summary>
/// Lightweight snapshot of the GV audio bridge state for dashboard polling.
/// </summary>
public record AudioBridgeSnapshotDto(
    bool IsActive,
    long InboundFramesSent,
    long OutboundFramesReceived,
    long InboundErrors,
    long OutboundErrors,
    bool BidirectionalAudio);

/// <summary>
/// Consolidated HT801 status combining network probe, SIP registration, and freshness.
/// </summary>
public record Ht801StatusDto(
    string? IpAddress,
    string Extension,
    bool? IpReachable,
    bool SipRegistered,
    DateTime? LastRegisterReceived,
    int? RegistrationExpiresInSeconds);
