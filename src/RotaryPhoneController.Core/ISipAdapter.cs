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
    /// Resolves the address the HT801 can actually be reached at: a fresh learned registrar binding
    /// when one exists, otherwise <paramref name="configuredIP"/>.
    /// </summary>
    /// <remarks>
    /// Exposed on the interface so that callers needing the SAME address for a DIFFERENT purpose —
    /// specifically the RTP audio bridge — cannot drift from the address the bell was rung at.
    /// Before this existed, <see cref="SendInviteToHT801"/> resolved internally while the legacy
    /// Bluetooth/SipTrunk RTP bridge used the raw configured value, so a config/learned mismatch
    /// produced a call that rang at one address and streamed audio to another. That split-brain is
    /// the exact failure class this work exists to remove, so there is ONE resolver and every leg
    /// goes through it. Do not reimplement this precedence anywhere else.
    /// </remarks>
    /// <param name="extensionToRing">SIP extension whose registrar binding to look up (e.g. "1000").</param>
    /// <param name="configuredIP">Configured fallback address, used when no fresh binding exists.</param>
    /// <param name="logDiagnostics">
    /// Whether to journal the resolution decision at warning level. Pass <c>false</c> for repeat or
    /// background resolutions (a second pass over an already-resolved address, the 30-second
    /// reachability probe) so the journal carries exactly ONE line per real decision instead of one
    /// per caller. Quiet is not silent: the same information is still logged at Debug. The address
    /// returned is identical either way.
    /// </param>
    string ResolveHt801Address(string extensionToRing, string configuredIP, bool logDiagnostics = true);

    /// <summary>
    /// Cancel a pending SIP INVITE (stop the rotary phone from ringing).
    /// Sends SIP CANCEL/BYE for an unanswered INVITE dialog.
    /// </summary>
    void CancelPendingInvite();
}
