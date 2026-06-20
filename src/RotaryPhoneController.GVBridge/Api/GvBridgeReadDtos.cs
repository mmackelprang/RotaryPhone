namespace RotaryPhoneController.GVBridge.Api;

/// <summary>
/// Public cross-service voicemail item (ADR §6.1). Serialized camelCase; RadioConsole's Radio.Web
/// is case-insensitive. AudioUrl is RotaryPhone-relative — RadioConsole uses it as an &lt;audio src&gt;.
/// </summary>
public record VoicemailItemDto(
    string Id,
    string ThreadId,
    string FromNumber,
    string? FromName,
    DateTime ReceivedAt,
    int DurationSeconds,
    bool IsRead,
    string? Transcript,
    string AudioUrl);

public record VoicemailListDto(
    IReadOnlyList<VoicemailItemDto> Items,
    string? NextPageToken,
    DateTime FetchedAtUtc);

/// <summary>Public cross-service SMS message (ADR §6.1). Direction is "Inbound" | "Outbound".</summary>
public record SmsMessageDto(
    string Id,
    string ThreadId,
    string Direction,
    string CounterpartyNumber,
    string? Text,
    DateTime SentAt,
    bool IsRead);

public record SmsThreadDto(
    string ThreadId,
    string CounterpartyNumber,
    string? CounterpartyName,
    DateTime LastMessageAt,
    bool HasUnread,
    string? LastMessagePreview);

public record SmsThreadListDto(
    IReadOnlyList<SmsThreadDto> Threads,
    DateTime FetchedAtUtc);

public record SmsThreadMessagesDto(
    string ThreadId,
    IReadOnlyList<SmsMessageDto> Messages,
    DateTime FetchedAtUtc);
