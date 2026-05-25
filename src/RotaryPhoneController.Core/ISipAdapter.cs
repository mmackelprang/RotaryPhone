namespace RotaryPhoneController.Core;

public interface ISipAdapter
{
    event Action<bool>? OnHookChange;
    event Action<string>? OnDigitsReceived;
    event Action? OnIncomingCall;

    /// <summary>
    /// Fired when HT801 responds with 200 OK containing SDP.
    /// Parameters: (negotiated RTP port, negotiated IP address).
    /// Listeners can use this to configure audio bridges with the correct ports.
    /// </summary>
    event Action<int, string>? OnRtpDetailsNegotiated;

    /// <summary>
    /// Gets whether the SIP server is currently listening for connections
    /// </summary>
    bool IsListening { get; }

    void SendInviteToHT801(string extensionToRing, string targetIP, int localRtpPort = 49000);

    /// <summary>
    /// Cancel a pending SIP INVITE (stop the rotary phone from ringing).
    /// Sends SIP CANCEL/BYE for an unanswered INVITE dialog.
    /// </summary>
    void CancelPendingInvite();
}
