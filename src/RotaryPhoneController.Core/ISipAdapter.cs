namespace RotaryPhoneController.Core;

public interface ISipAdapter
{
    event Action<bool>? OnHookChange;
    event Action<string>? OnDigitsReceived;
    event Action? OnIncomingCall;

    /// <summary>
    /// Gets whether the SIP server is currently listening for connections
    /// </summary>
    bool IsListening { get; }

    void SendInviteToHT801(string extensionToRing, string targetIP);

    /// <summary>
    /// Cancel a pending SIP INVITE (stop the rotary phone from ringing).
    /// Sends SIP CANCEL/BYE for an unanswered INVITE dialog.
    /// </summary>
    void CancelPendingInvite();
}
