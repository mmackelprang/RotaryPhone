using Microsoft.AspNetCore.SignalR;

namespace RotaryPhoneController.GVTrunk.Api;

public class GVTrunkHub : Hub
{
    // Server -> client push only in Phase 1.
    // Events are broadcast via IHubContext<GVTrunkHub> from GVTrunkEventBridge.
}
