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

    /// <summary>
    /// Rings the HT801 by sending it a SIP INVITE.
    /// </summary>
    /// <param name="extensionToRing">SIP extension to ring (e.g. "1000").</param>
    /// <param name="targetIP">
    /// Cold-start fallback address. Implementations that learn a registrar binding from the device's
    /// own REGISTER prefer the learned address and use this only until one exists (or when it is stale).
    /// </param>
    /// <param name="localRtpPort">RTP port to advertise in the INVITE's SDP.</param>
    /// <returns>
    /// True when the INVITE reached the wire. False on a socket-level failure or when no SIP
    /// transport is available — meaning the bell definitely did NOT ring. NOTE: a UDP send to a
    /// dead-but-routable address still succeeds, so `true` is not proof the bell rang; the
    /// INVITE-response path (SipDiagnosticService) supplies that.
    /// </returns>
    bool SendInviteToHT801(string extensionToRing, string targetIP, int localRtpPort = 49000);

    /// <summary>
    /// Cancel a pending SIP INVITE (stop the rotary phone from ringing).
    /// Sends SIP CANCEL/BYE for an unanswered INVITE dialog.
    /// </summary>
    void CancelPendingInvite();
}
