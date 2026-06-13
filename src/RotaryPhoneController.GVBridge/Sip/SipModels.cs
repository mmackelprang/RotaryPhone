namespace RotaryPhoneController.GVBridge.Sip;

public sealed record SipCredentials(
    string SipUsername,
    string BearerToken,
    string PhoneNumber,
    int ExpirySeconds);

/// <summary>
/// Raised when the SIP-over-WebSocket channel's receive loop exits.
/// <see cref="WasIntentional"/> is true when WE closed the socket (via
/// <c>CloseAsync</c>/reconnect/disposal), false when Google closed it or a
/// receive error tore it down — the latter is what should drive auto-reconnect.
/// </summary>
public sealed class WebSocketClosedEventArgs(bool wasIntentional, string? description) : EventArgs
{
    public bool WasIntentional { get; } = wasIntentional;
    public string? Description { get; } = description;
}

/// <summary>
/// Raised when a REGISTER is genuinely rejected after the Digest re-send
/// (post-Digest 401/403) or when the credential fetch (<c>sipregisterinfo/get</c>)
/// returns an HTTP 401/403. Distinct from the normal 401 Digest challenge, which
/// is answered automatically and is NOT an auth failure. The adapter subscribes
/// to this to escalate to a cookie refresh (RotateCookies primary, CDP fallback).
/// </summary>
public sealed class AuthenticationFailedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Thrown by the credential fetch when <c>sipregisterinfo/get</c> returns HTTP 401/403 —
/// the real stale rotating-cookie failure (typically SESSION_COOKIE_INVALID). The transport
/// catches this in its register/reconnect path and raises <see cref="AuthenticationFailedEventArgs"/>
/// so the adapter can escalate to a cookie refresh. A 5xx / network error is NOT this type and
/// must not trigger cookie work.
/// </summary>
public sealed class GvAuthException : Exception
{
    public int StatusCode { get; }

    public GvAuthException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    public GvAuthException() { }
    public GvAuthException(string message) : base(message) { }
    public GvAuthException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed record TransportCallResult(
    string CallId,
    bool Success,
    string? ErrorMessage = null);

public sealed record TransportCallStatus(
    string CallId,
    CallStatusType Status);

public enum CallStatusType { Unknown, Ringing, Active, Completed, Failed }

public sealed class AudioDataEventArgs(string callId, ReadOnlyMemory<byte> pcmData, int sampleRate) : EventArgs
{
    public string CallId { get; } = callId;
    public ReadOnlyMemory<byte> PcmData { get; } = pcmData;
    public int SampleRate { get; } = sampleRate;
}

public sealed class IncomingCallEventArgs(IncomingCallInfo callInfo) : EventArgs
{
    public IncomingCallInfo CallInfo { get; } = callInfo;
}

public sealed record IncomingCallInfo(string CallId, string CallerNumber);

public sealed class CallStatusChangedEventArgs(string callId, CallStatusType oldStatus, CallStatusType newStatus) : EventArgs
{
    public string CallId { get; } = callId;
    public CallStatusType OldStatus { get; } = oldStatus;
    public CallStatusType NewStatus { get; } = newStatus;
}
