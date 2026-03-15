using Microsoft.AspNetCore.SignalR;

namespace RotaryPhoneController.GVBridge.Api;

public class GVBridgeHub : Hub
{
    // Server -> client push only. Events broadcast via IHubContext from the event bridge.
}
