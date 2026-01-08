using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;

namespace RotaryPhoneController.WebUI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PhoneController : ControllerBase
{
    private readonly PhoneManagerService _phoneManager;
    private readonly ILogger<PhoneController> _logger;

    public PhoneController(PhoneManagerService phoneManager, ILogger<PhoneController> logger)
    {
        _phoneManager = phoneManager;
        _logger = logger;
    }

    [HttpGet("status")]
    public IActionResult GetStatus([FromQuery] string? phoneId = null)
    {
        if (string.IsNullOrEmpty(phoneId))
        {
            // Default to first phone or return all?
            // For now, let's return a list of all phones status
            var statuses = _phoneManager.GetAllPhones().Select(p => new
            {
                Id = p.PhoneId,
                State = p.CallManager.CurrentState.ToString(),
                DialedNumber = p.CallManager.DialedNumber
            });
            return Ok(statuses);
        }

        var manager = _phoneManager.GetPhone(phoneId);
        if (manager == null) return NotFound($"Phone {phoneId} not found");

        return Ok(new 
        { 
            Id = phoneId, 
            State = manager.CurrentState.ToString(),
            DialedNumber = manager.DialedNumber
        });
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
}
