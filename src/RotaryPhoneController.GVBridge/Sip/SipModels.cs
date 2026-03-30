namespace RotaryPhoneController.GVBridge.Sip;

public sealed record SipCredentials(
    string SipUsername,
    string BearerToken,
    string PhoneNumber,
    int ExpirySeconds);

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
