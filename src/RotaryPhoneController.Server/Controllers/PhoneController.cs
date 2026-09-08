using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Audio;
using RotaryPhoneController.Core.Bell;
using RotaryPhoneController.Core.Configuration;
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
    // Still needed by ValidateHT801, which legitimately acts on the CONFIGURED device record.
    // GetSystemStatus deliberately no longer touches it — see the remarks on that method.
    private readonly IHT801ConfigService _ht801Service;
    private readonly IBellFailureTracker _bellFailureTracker;
    private readonly IHt801ReachabilityCache _ht801Cache;

    public PhoneController(
        PhoneManagerService phoneManager,
        ILogger<PhoneController> logger,
        IBluetoothHfpAdapter bluetoothAdapter,
        ISipAdapter sipAdapter,
        AppConfiguration config,
        IHT801ConfigService ht801Service,
        IHt801ReachabilityCache ht801Cache,
        IBellFailureTracker bellFailureTracker)
    {
        _phoneManager = phoneManager;
        _logger = logger;
        _bluetoothAdapter = bluetoothAdapter;
        _sipAdapter = sipAdapter;
        _config = config;
        _ht801Service = ht801Service;
        _ht801Cache = ht801Cache;
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
                acknowledged = f.Acknowledged,
                // Diagnostics only, never user-facing — same rule the hub event's target/detail
                // carry. A client reloading mid-failure would otherwise lose the address the INVITE
                // actually went to, the single most useful fact when the bell does not ring.
                target = f.Target,
                detail = f.Detail
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
    /// <b>The three HT801 fields here are the SAME cached background probe the
    /// <c>SystemStatusChanged</c> hub event carries.</b> One probe, one meaning, whichever transport
    /// you read it over. It reports the <b>RESOLVED</b> registrar binding — the address an INVITE
    /// actually goes to — not the configured address.
    /// </para>
    /// <para>
    /// It used to report the configured address, pinged synchronously inside the request. That is
    /// why this remark once said the opposite: the configured address stayed CORRECT throughout the
    /// entire 2026-07 outage while every INVITE went to a stale one, which made this endpoint a
    /// confidently green signal during the exact failure it was being consulted about. It
    /// deliberately no longer reports it. For the raw registration table, see
    /// <c>GET /api/diagnostics/sip-registrations</c>.
    /// </para>
    /// <para>
    /// <b>The value is roughly 30 seconds stale by design</b> — the probe runs on that cadence in
    /// the background, and nothing is pinged in this request. The true bound is a little over 30 s:
    /// the interval is re-armed when a probe is KICKED OFF rather than when it finishes, and the
    /// monitor loop only tests for due-ness once a second, so the worst case is ~30 s + the probe's
    /// own duration (≤3 s) + up to 1 s of loop tick — call it ~34 s. Ht801LastCheckedUtc is a
    /// genuine probe age: it is when the probe RAN, not when you asked, so it is safe to build a
    /// "last checked" or stale-data affordance on it.
    /// </para>
    /// <para>
    /// <b>The cold-start window is one ping, not one interval.</b> The next-probe deadline starts at
    /// DateTime.MinValue and the monitor loop tests it on its FIRST iteration, so the first probe
    /// fires at start-up rather than 30 seconds into it. All three fields are null only for as long
    /// as that one ping takes: ~3 ms against a healthy device, and at most 3 s
    /// (SendPingAsync(ip, 3000)) against one that does not answer.
    /// </para>
    /// <para>
    /// <b>...or indefinitely, if no HT801 address can be resolved at all.</b> If <c>Phones</c> is
    /// empty, or nothing resolves and the configured address is blank or 0.0.0.0, the probe returns
    /// without writing to the cache and all three fields stay null for the process lifetime. That is
    /// a CONFIGURATION fault presenting as "Unknown", which is the honest rendering of it.
    /// </para>
    /// <para>
    /// Null means NOT YET PROBED, never "offline" — render it as "Unknown"
    /// (see <see cref="SystemStatus.Ht801Reachable"/>). Note that null is the cold-start and
    /// no-address contract specifically. Once a probe has succeeded, a LATER probe that cannot reach
    /// a conclusion does not necessarily null the fields: a probe that throws writes null, but the
    /// no-usable-address path leaves the previous snapshot in place rather than wiping it.
    /// </para>
    /// </remarks>
    [HttpGet("system-status")]
    public IActionResult GetSystemStatus()
    {
        // One read, into a local: three reads could straddle a probe and mix two of them together.
        var probe = _ht801Cache.Current;

        // Built through the SHARED factory, which is the same call SignalRNotifierService makes for
        // the SystemStatusChanged event. The two payloads are therefore identical by construction
        // rather than by two code paths that happen to agree.
        var status = SystemStatusFactory.Create(_config, _bluetoothAdapter, _sipAdapter, probe);

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
