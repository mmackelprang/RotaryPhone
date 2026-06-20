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
