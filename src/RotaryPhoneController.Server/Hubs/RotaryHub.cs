using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.CallHistory;

namespace RotaryPhoneController.Server.Hubs;

public class RotaryHub : Hub
{
    private readonly PhoneManagerService _phoneManager;

    public RotaryHub(PhoneManagerService phoneManager)
    {
        _phoneManager = phoneManager;
    }

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

    public async Task SendSystemStatus(SystemStatus status)
    {
        await Clients.All.SendAsync("SystemStatusChanged", status);
    }

    /// <summary>
    /// Called by Radio.API to report a resolved caller name from PBAP contacts.
    /// Updates CallManager state and broadcasts to all connected UI clients.
    /// </summary>
    public async Task ReportCallerResolved(string phoneNumber, string displayName)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber) || string.IsNullOrWhiteSpace(displayName))
            return;

        foreach (var phone in _phoneManager.GetAllPhones())
        {
            phone.CallManager.SetResolvedCallerName(phoneNumber, displayName);
        }

        await Clients.All.SendAsync("CallerResolved", phoneNumber, displayName);
    }
}
