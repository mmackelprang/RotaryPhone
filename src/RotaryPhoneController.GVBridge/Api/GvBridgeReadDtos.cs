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

/// <summary>
/// Cross-service SMS send request (ADR §6.1, §4). ToNumber is whatever the user typed; RotaryPhone
/// normalizes it to E.164 before building the GV thread id. ThreadId is OPTIONAL: present = reply to
/// an existing thread (use Google's real id); null/empty = start a new conversation (ADR §4.2 #1).
/// ClientCorrelationId is OPTIONAL: the UI may pass the id of its optimistic bubble so the echo it gets
/// back carries the SAME correlation id (id-consistency rule, Task 1 Step 1b). If null, the server
/// synthesizes one.
/// </summary>
public record SendSmsRequest(string ToNumber, string Text, string? ThreadId, string? ClientCorrelationId = null);

/// <summary>
/// Cross-service SMS send result (ADR §6.1, §4.2 #3) — reconciled to the RadioConsole handoff
/// (§"Outbound write-path bubble states", §"Send-failure copy matrix").
///
/// • Queued=true means GOOGLE ACCEPTED the send (HTTP 200) — NOT confirmed delivery. sendsms returns a
///   transaction ack, not the echoed message (ADR §4.2 #3). Honest status: never report delivery.
/// • Message is the created OUTBOUND message as a full <see cref="SmsMessageDto"/> — the SAME shape the
///   UI already consumes for reads/pushes — with a STABLE Id (see the id-consistency rule). The UI shows
///   it as the optimistic "sent (queued)" bubble and later de-dupes it against the re-surfaced copy by Id.
/// • Code is a machine-readable outcome the UI maps to copy WITHOUT parsing Error prose:
///   "queued" | "invalid_number" | "rate_limited" | "auth_unavailable" | "upstream_error" |
///   "timeout" | "send_disabled" | "error". (See the §"Error taxonomy" table.)
/// • Error is human-readable (logged + safe to surface); on any failure Queued=false and the caller must
///   NOT auto-retry (ADR §4.2 #4) — retry is the user's decision (handoff: "preserve the typed text").
/// </summary>
public record SendSmsResponse(
    bool Queued,
    string Code,
    string? ThreadId,
    string? Error,
    SmsMessageDto? Message);
