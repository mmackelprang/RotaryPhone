namespace RotaryPhoneController.Core.Diagnostics;

public enum SipDirection { Sent, Received }

public record SipMessageEntry(
    DateTime Timestamp,
    SipDirection Direction,
    string Method,
    string FromAddress,
    string ToAddress,
    int? StatusCode,
    string? StatusText,
    string? DiagnosticNote,
    string? CallId
);

public record CallTimelineEntry(
    DateTime Timestamp,
    string EventType,
    string Description,
    Dictionary<string, string>? Metadata
);

public record Ht801HealthStatus(
    bool IsReachable,
    double? PingMs,
    bool IsRegistered,
    int? RegistrationExpiresIn,
    DateTime? LastRegisterReceived,
    string? HookState,
    string? FirmwareVersion
);

public record ConfigParameter(
    string Name,
    string PCode,
    string Expected,
    string? Actual,
    bool IsMatch
);
