using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge")]
public class GVBridgeController : ControllerBase
{
    private readonly ICallAdapterRegistry _registry;
    private readonly GVApiAdapter _adapter;

    public GVBridgeController(ICallAdapterRegistry registry, GVApiAdapter adapter)
    {
        _registry = registry;
        _adapter = adapter;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            available = _adapter.IsAvailable,
            activeMode = _registry.ActiveMode.ToString()
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

    public record SetModeRequest(string Mode);
}
