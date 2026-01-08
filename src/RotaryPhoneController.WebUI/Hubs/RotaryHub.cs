using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.CallHistory;

namespace RotaryPhoneController.WebUI.Hubs;

public class RotaryHub : Hub
{
    public async Task SendCallState(string phoneId, CallState state)
    {
        await Clients.All.SendAsync("CallStateChanged", phoneId, state);
    }

    public async Task SendIncomingCall(string phoneId, string phoneNumber)
    {
        await Clients.All.SendAsync("IncomingCall", phoneId, phoneNumber);
    }
    
    public async Task SendCallHistoryUpdate(CallHistoryEntry entry)
    {
        await Clients.All.SendAsync("CallHistoryUpdated", entry);
    }
}
