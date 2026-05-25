namespace RotaryPhoneController.Core;

/// <summary>
/// Universal adapter for a remote call path (BT phone, SIP trunk, GV browser).
/// Note: This does NOT replace ISipAdapter. ISipAdapter handles the local HT801
/// phone interface. ICallAdapter handles the remote side of the call.
/// </summary>
public interface ICallAdapter
{
    CallAdapterMode Mode { get; }
    bool IsAvailable { get; }
    event Action<bool>? OnAvailabilityChanged;
    event Action<string>? OnIncomingCall;
    event Action? OnCallAnswered;
    event Action? OnCallEnded;
    event Action<string>? OnDtmfReceived;
    Task ActivateAsync(CancellationToken ct = default);
    Task DeactivateAsync(CancellationToken ct = default);
    Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default);
    Task AnswerCallAsync(CancellationToken ct = default);
    Task HangUpAsync(CancellationToken ct = default);

    /// <summary>
    /// Called by CallManager after HT801 answers (200 OK with SDP) to provide negotiated
    /// RTP port details. Adapters that manage their own audio bridge should store these
    /// and use them in OnCallAnsweredOnRotaryPhoneAsync. Default is no-op.
    /// </summary>
    /// <param name="ht801RtpPort">HT801's RTP port from SDP (-1 if parsing failed).</param>
    /// <param name="ht801RtpIp">HT801's IP from SDP.</param>
    /// <param name="inviteRtpPort">The local RTP port we advertised in the INVITE SDP.</param>
    void SetNegotiatedRtpDetails(int? ht801RtpPort, string? ht801RtpIp, int? inviteRtpPort) { }

    /// <summary>
    /// Called by CallManager when the call is answered on the rotary phone (SIP 200 OK).
    /// Adapters that manage their own audio bridge (e.g., GVBrowser) should start it here.
    /// Default implementation is a no-op for adapters that don't need it.
    /// </summary>
    Task OnCallAnsweredOnRotaryPhoneAsync() => Task.CompletedTask;

    /// <summary>
    /// Called by CallManager on hangup. Adapters should stop any audio bridge here.
    /// </summary>
    Task OnCallHungUpAsync() => Task.CompletedTask;
}
