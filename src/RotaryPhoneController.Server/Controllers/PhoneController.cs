using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Platform;
using RotaryPhoneController.Core.HT801;

namespace RotaryPhoneController.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PhoneController : ControllerBase
{
    private readonly PhoneManagerService _phoneManager;
    private readonly ILogger<PhoneController> _logger;
    private readonly IBluetoothHfpAdapter _bluetoothAdapter;
    private readonly ISipAdapter _sipAdapter;
    private readonly AppConfiguration _config;
    private readonly IHT801ConfigService _ht801Service;
    private readonly IBellFailureTracker _bellFailureTracker;

    public PhoneController(
        PhoneManagerService phoneManager,
        ILogger<PhoneController> logger,
        IBluetoothHfpAdapter bluetoothAdapter,
        ISipAdapter sipAdapter,
        AppConfiguration config,
        IHT801ConfigService ht801Service,
        IBellFailureTracker bellFailureTracker)
    {
        _phoneManager = phoneManager;
        _logger = logger;
        _bluetoothAdapter = bluetoothAdapter;
        _sipAdapter = sipAdapter;
        _config = config;
        _ht801Service = ht801Service;
        _bellFailureTracker = bellFailureTracker;
    }

    /// <summary>
    /// Current call state for a phone, plus the last known bell failure.
    /// </summary>
    /// <remarks>
    /// LastBellFailure is served here — rather than only pushed over SignalR — because the original
    /// bug was that nobody was looking at the screen during the only 60 seconds the failure was
    /// visible. It survives the ringing window and a browser reload until acknowledged.
    /// </remarks>
    [HttpGet("status")]
    public IActionResult GetStatus([FromQuery] string? phoneId = null)
    {
        if (string.IsNullOrEmpty(phoneId))
        {
            // Return default phone status as a single object matching PhoneCallStateDto shape
            var defaultPhone = _phoneManager.GetAllPhones().FirstOrDefault();
            // No phone registered: return the SAME shape with nulls so the client contract is stable.
            if (defaultPhone.CallManager == null)
                return Ok(new
                {
                    CallState = "Idle",
                    DialedNumber = (string?)null,
                    IncomingNumber = (string?)null,
                    CallId = (string?)null,
                    LastBellFailure = (object?)null
                });

            var m = defaultPhone.CallManager;
            return Ok(new
            {
                CallState = m.CurrentState.ToString(),
                DialedNumber = m.DialedNumber,
                IncomingNumber = m.IncomingPhoneNumber,
                CallId = m.CallId,
                LastBellFailure = BellFailureDto(defaultPhone.PhoneId)
            });
        }

        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound($"Phone {phoneId} not found");

        return Ok(new
        {
            CallState = manager.CurrentState.ToString(),
            DialedNumber = manager.DialedNumber,
            IncomingNumber = manager.IncomingPhoneNumber,
            CallId = manager.CallId,
            LastBellFailure = BellFailureDto(phoneId)
        });
    }

    /// <summary>Projects the tracked bell failure (if any) into the wire shape, or null.</summary>
    private object? BellFailureDto(string phoneId) =>
        _bellFailureTracker.Get(phoneId) is { } f
            ? new
            {
                occurredAtUtc = f.OccurredAtUtc,
                reason = f.Reason.ToString(),
                callerNumber = f.CallerNumber,
                callId = f.CallId,
                failureCount = f.FailureCount,
                acknowledged = f.Acknowledged
            }
            : null;

    /// <summary>Acknowledges (dismisses) the stored bell failure for a phone so it does not reappear after a reload.</summary>
    [HttpPost("bell-failure/ack")]
    public IActionResult AcknowledgeBellFailure([FromQuery] string phoneId = "default")
    {
        // Idempotent by design: nothing to acknowledge is a 200 with acknowledged=false, not a 404.
        var acknowledged = _bellFailureTracker.Acknowledge(phoneId);
        return Ok(new { acknowledged });
    }

    [HttpPost("simulate/incoming")]
    public IActionResult SimulateIncoming([FromQuery] string phoneId = "default")
    {
        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound();

        manager.SimulateIncomingCall();
        return Ok("Incoming call simulated");
    }

    [HttpPost("simulate/hook")]
    public IActionResult SimulateHook([FromQuery] string phoneId = "default", [FromQuery] bool offHook = true)
    {
        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound();

        manager.HandleHookChange(offHook);
        return Ok($"Hook state set to {(offHook ? "OFF-HOOK" : "ON-HOOK")}");
    }
    
    [HttpPost("simulate/dial")]
    public IActionResult SimulateDial([FromQuery] string phoneId = "default", [FromQuery] string digits = "")
    {
        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound();

        manager.HandleDigitsReceived(digits);
        return Ok($"Digits '{digits}' received");
    }

    /// <summary>
    /// Gets the current system status including platform, Bluetooth, and SIP information.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This endpoint reports the CONFIGURED HT801 address, not the INVITE target.</b> The two are
    /// different values and can disagree: the configured address is a projection of
    /// RotaryPhone:Phones[].HT801IpAddress, while INVITEs go to the address learned from the
    /// device's own REGISTER when one is fresh.
    /// </para>
    /// <para>
    /// It reported the CORRECT address throughout the entire 2026-07 outage while every INVITE went
    /// to a stale one, so it is NOT a valid verification signal for addressing. Use
    /// <c>GET /api/diagnostics/sip-registrations</c> instead.
    /// </para>
    /// <para>
    /// Ht801Reachable is null when the probe did not run or could not determine an answer — render
    /// that as "Unknown", never "Offline". Ht801LastCheckedUtc is set only when a probe actually ran.
    /// </para>
    /// </remarks>
    [HttpGet("system-status")]
    public async Task<IActionResult> GetSystemStatus()
    {
        var status = new SystemStatus
        {
            Platform = PlatformDetector.CurrentPlatform.ToString(),
            IsRaspberryPi = PlatformDetector.IsRaspberryPi,
            BluetoothEnabled = _config.UseActualBluetoothHfp,
            BluetoothConnected = _bluetoothAdapter.IsConnected,
            BluetoothDeviceAddress = _bluetoothAdapter.ConnectedDeviceAddress,
            SipListening = _sipAdapter.IsListening,
            SipListenAddress = _config.SipListenAddress,
            SipPort = _config.SipPort
        };

        // Check HT801 status
        // We'll use the default phone's config for now
        var defaultPhoneId = _config.Phones.FirstOrDefault()?.Id ?? "default";
        var ht801Config = _ht801Service.GetConfig(defaultPhoneId);
        
        status.Ht801IpAddress = ht801Config.IpAddress;
        
        // Only check reachability if we have a valid IP. When we don't probe, BOTH Ht801Reachable
        // and Ht801LastCheckedUtc stay null — that pair means "genuinely unknown", not "offline".
        if (!string.IsNullOrEmpty(ht801Config.IpAddress) && ht801Config.IpAddress != "0.0.0.0")
        {
            var result = await _ht801Service.TestConnectionAsync(ht801Config.IpAddress);
            status.Ht801Reachable = result.Success;
            status.Ht801LastCheckedUtc = DateTime.UtcNow;
        }

        _logger.LogDebug("System status requested: Platform={Platform}, Bluetooth={BluetoothConnected}, SIP={SipListening}, HT801={Ht801Reachable}",
            status.Platform, status.BluetoothConnected, status.SipListening, status.Ht801Reachable);

        return Ok(status);
    }

    [HttpGet("ht801/validate")]
    public async Task<IActionResult> ValidateHT801([FromQuery] string? phoneId, [FromQuery] bool autoFix = false)
    {
        phoneId ??= _config.Phones.FirstOrDefault()?.Id ?? "default";
        var result = await _ht801Service.ValidateDeviceAsync(phoneId, autoFix);
        return Ok(result);
    }
}
