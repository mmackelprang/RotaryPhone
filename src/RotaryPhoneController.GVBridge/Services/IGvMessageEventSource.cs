using RotaryPhoneController.GVBridge.Api;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// The stable seam through which new inbound GV messages reach RadioConsole (ADR §5.2, §6.3, §9).
/// The poller (or, later, a cracked signaler — PR6) raises these; a Server-side bridge forwards them
/// to RotaryHub as "SmsReceived"/"VoicemailReceived", mirroring "IncomingCall". Swapping the producer
/// behind this interface is invisible to RadioConsole — that is the whole point of the seam.
/// </summary>
public interface IGvMessageEventSource
{
    /// <summary>Raised once per newly-detected inbound SMS.</summary>
    event Action<SmsMessageDto>? OnSmsReceived;

    /// <summary>Raised once per newly-detected voicemail.</summary>
    event Action<VoicemailItemDto>? OnVoicemailReceived;

    /// <summary>
    /// Raised once per successfully-queued OUTBOUND SMS (the send the user just made). Distinct from
    /// OnSmsReceived so RadioConsole appends it WITHOUT an inbound toast (handoff). Forwarded to
    /// RotaryHub as "SmsSent".
    /// </summary>
    event Action<SmsMessageDto>? OnSmsSent;

    /// <summary>
    /// Raised when read-state changes from ANY source. Path (a): a mark route was called (THIS PR). Path
    /// (b): the poller detected an externally-originated read flip (FAST-FOLLOW, Task 9). Forwarded to
    /// RotaryHub as "ReadStateChanged". Broadcast unconditionally — RadioConsole de-dupes by (Id/ThreadId
    /// + IsRead).
    /// </summary>
    event Action<ReadStateChangedDto>? OnReadStateChanged;
}

/// <summary>
/// Narrow producer seam for the OUTBOUND channel (id-consistency, PR4 Task 6). GvSmsController calls
/// NotifySent after a successful send so the outbound echo reaches RadioConsole. Kept separate from the
/// consumer-only IGvMessageEventSource so the controller does not depend on the raise side. Implemented
/// by GvThreadPoller (which already owns the events); registered as both.
/// </summary>
public interface IGvOutboundSmsSink
{
    /// <summary>Surface a successfully-queued outbound SMS to the OnSmsSent channel.</summary>
    void NotifySent(SmsMessageDto dto);
}

/// <summary>
/// Narrow producer seam for the read-state-change channel (ADR §5 path a). The mark-read controllers call
/// NotifyReadStateChanged after a successful mark so the event reaches RadioConsole. Kept separate from the
/// consumer-only IGvMessageEventSource so the controllers do not depend on the raise side. Implemented by
/// GvThreadPoller (which owns the events); registered as both.
/// </summary>
public interface IGvReadStateSink
{
    /// <summary>Surface a read-state change to the OnReadStateChanged channel.</summary>
    void NotifyReadStateChanged(ReadStateChangedDto dto);
}
