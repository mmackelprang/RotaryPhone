using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge")]
public class GVBridgeController : ControllerBase
{
    private readonly GVBridgeService _bridge;
    private readonly GVSmsService _sms;
    private readonly ICallAdapterRegistry _registry;

    public GVBridgeController(GVBridgeService bridge, GVSmsService sms, ICallAdapterRegistry registry)
    {
        _bridge = bridge;
        _sms = sms;
        _registry = registry;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            extensionConnected = _bridge.IsExtensionConnected,
            extensionVersion = _bridge.ExtensionVersion,
            activeMode = _registry.ActiveMode.ToString()
        });
    }

    [HttpGet("sms")]
    public IActionResult GetSms()
    {
        return Ok(_sms.GetRecent(20));
    }

    [HttpPost("sms/send")]
    public async Task<IActionResult> SendSms([FromBody] SendSmsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.To) || string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new { error = "To and body required" });
        await _sms.SendSmsAsync(request.To, request.Body);
        return Ok(new { status = "sent" });
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

    public record SendSmsRequest(string To, string Body);
    public record SetModeRequest(string Mode);
}
