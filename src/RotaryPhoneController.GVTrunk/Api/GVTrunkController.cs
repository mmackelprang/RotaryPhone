using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.GVTrunk.Interfaces;
using RotaryPhoneController.GVTrunk.Models;

namespace RotaryPhoneController.GVTrunk.Api;

[ApiController]
[Route("api/gvtrunk")]
public class GVTrunkController : ControllerBase
{
    private readonly ITrunkAdapter _trunk;
    private readonly ICallLogService _callLog;
    private readonly RotaryPhoneController.Core.PhoneManagerService _phoneManager;

    public GVTrunkController(
        ITrunkAdapter trunk,
        ICallLogService callLog,
        RotaryPhoneController.Core.PhoneManagerService phoneManager)
    {
        _trunk = trunk;
        _callLog = callLog;
        _phoneManager = phoneManager;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var callState = Core.CallState.Idle;
        int durationSeconds = 0;
        var phone = _phoneManager.GetAllPhones().FirstOrDefault();
        if (phone.CallManager is { } callManager)
        {
            callState = callManager.CurrentState;
            if (callState == Core.CallState.InCall && callManager.CallStartedAtUtc != null)
                durationSeconds = (int)(DateTime.UtcNow - callManager.CallStartedAtUtc.Value).TotalSeconds;
        }
        return Ok(new
        {
            isRegistered = _trunk.IsRegistered,
            callState = callState.ToString(),
            activeCallDurationSeconds = durationSeconds
        });
    }

    [HttpGet("calls")]
    public async Task<IActionResult> GetCalls()
    {
        var calls = await _callLog.GetRecentAsync(50);
        return Ok(calls);
    }

    [HttpGet("sms")]
    public IActionResult GetSms()
    {
        return Ok(GVTrunkSmsCache.GetRecent(20));
    }

    [HttpPost("dial")]
    public async Task<IActionResult> Dial([FromBody] DialRequest request)
    {
        if (!_trunk.IsRegistered)
            return Conflict(new { error = "Trunk not registered" });
        if (string.IsNullOrWhiteSpace(request.Number))
            return BadRequest(new { error = "Number required" });

        var sessionId = await _trunk.PlaceOutboundCallAsync(request.Number);
        return Ok(new { sessionId });
    }

    [HttpPost("reregister")]
    public async Task<IActionResult> Reregister()
    {
        await _trunk.RegisterAsync();
        return Ok(new { status = "Registration initiated" });
    }

    public record DialRequest(string Number);
}

/// <summary>
/// Thread-safe in-memory cache for recent SMS notifications.
/// </summary>
public static class GVTrunkSmsCache
{
    private static readonly List<SmsNotification> _notifications = new();
    private static readonly object _lock = new();

    public static void Add(SmsNotification notification)
    {
        lock (_lock)
        {
            _notifications.Add(notification);
            if (_notifications.Count > 100)
                _notifications.RemoveRange(0, _notifications.Count - 100);
        }
    }

    public static List<SmsNotification> GetRecent(int count)
    {
        lock (_lock)
        {
            return _notifications.TakeLast(count).ToList();
        }
    }
}
