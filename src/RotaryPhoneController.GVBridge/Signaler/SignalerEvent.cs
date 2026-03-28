namespace RotaryPhoneController.GVBridge.Signaler;

public abstract record SignalerEvent;
public record IncomingCallEvent(string CallId, string CallerNumber) : SignalerEvent;
public record CallEndedEvent(string CallId) : SignalerEvent;
public record SmsReceivedEvent(string From, string Body, string ThreadId) : SignalerEvent;
public record UnknownSignalerEvent(string RawPayload) : SignalerEvent;
