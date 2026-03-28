using System.Text.Json.Serialization;

namespace RotaryPhoneController.GVBridge.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectedMessage), "connected")]
[JsonDerivedType(typeof(IncomingCallMessage), "incomingCall")]
[JsonDerivedType(typeof(CallAnsweredMessage), "callAnswered")]
[JsonDerivedType(typeof(CallEndedMessage), "callEnded")]
[JsonDerivedType(typeof(SmsReceivedMessage), "smsReceived")]
[JsonDerivedType(typeof(MissedCallMessage), "missedCall")]
[JsonDerivedType(typeof(DtmfReceivedMessage), "dtmfReceived")]
[JsonDerivedType(typeof(AudioFrameMessage), "audioFrame")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(DialMessage), "dial")]
[JsonDerivedType(typeof(AnswerMessage), "answer")]
[JsonDerivedType(typeof(HangupMessage), "hangup")]
[JsonDerivedType(typeof(SendSmsMessage), "sendSms")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(MuteTabMessage), "muteTab")]
[JsonDerivedType(typeof(UnmuteTabMessage), "unmuteTab")]
public abstract class ExtensionMessage { }

// Extension -> Bridge
public class ConnectedMessage : ExtensionMessage { public string Version { get; set; } = "1.0.0"; }
public class IncomingCallMessage : ExtensionMessage { public string From { get; set; } = ""; public string CallId { get; set; } = ""; }
public class CallAnsweredMessage : ExtensionMessage { public string CallId { get; set; } = ""; }
public class CallEndedMessage : ExtensionMessage { public string CallId { get; set; } = ""; }
public class SmsReceivedMessage : ExtensionMessage { public string From { get; set; } = ""; public string Body { get; set; } = ""; public string ThreadId { get; set; } = ""; }
public class MissedCallMessage : ExtensionMessage { public string From { get; set; } = ""; }
public class DtmfReceivedMessage : ExtensionMessage { public string Digit { get; set; } = ""; }
public class AudioFrameMessage : ExtensionMessage { public string Pcm { get; set; } = ""; }
public class PongMessage : ExtensionMessage { }

// Bridge -> Extension
public class DialMessage : ExtensionMessage { public string Number { get; set; } = ""; }
public class AnswerMessage : ExtensionMessage { }
public class HangupMessage : ExtensionMessage { }
public class SendSmsMessage : ExtensionMessage { public string To { get; set; } = ""; public string Body { get; set; } = ""; }
public class PingMessage : ExtensionMessage { }
public class MuteTabMessage : ExtensionMessage { }
public class UnmuteTabMessage : ExtensionMessage { }
