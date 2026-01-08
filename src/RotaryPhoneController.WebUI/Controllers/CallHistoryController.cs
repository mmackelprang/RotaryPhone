using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core.CallHistory;

namespace RotaryPhoneController.WebUI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CallHistoryController : ControllerBase
{
    private readonly ICallHistoryService _historyService;
    private readonly ILogger<CallHistoryController> _logger;

    public CallHistoryController(ICallHistoryService historyService, ILogger<CallHistoryController> logger)
    {
        _historyService = historyService;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Get([FromQuery] string? phoneId = null)
    {
        IEnumerable<CallHistoryEntry> history;
        if (!string.IsNullOrEmpty(phoneId))
        {
            history = _historyService.GetCallHistoryForPhone(phoneId);
        }
        else
        {
            history = _historyService.GetCallHistory();
        }
        
        return Ok(history.OrderByDescending(h => h.StartTime));
    }

    [HttpDelete]
    public IActionResult Clear([FromQuery] string? phoneId = null)
    {
        // Interface currently only supports clearing all history
        _historyService.ClearHistory();
        return NoContent();
    }
}
