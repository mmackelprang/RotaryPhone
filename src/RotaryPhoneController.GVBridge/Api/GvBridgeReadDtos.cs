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

/// <summary>
/// Cross-service mark-read request (ADR §4, contract §2). Body for BOTH mark routes
/// (POST /api/gvbridge/voicemail/{id}/read and POST /api/gvbridge/sms/threads/{threadId}/read).
/// IsRead: true = mark read (the v1 contract). false = best-effort mark-UNREAD — honored only when the
/// server's AllowMarkUnread flag is on (default off); otherwise the route returns 400 unread_unsupported
/// (ADR §6.1). RadioConsole sends only IsRead:true until we confirm unread support.
/// </summary>
public record MarkReadRequest(bool IsRead);

/// <summary>
/// Unified read-state change event (ADR §5, reply §4) — broadcast over RotaryHub as "ReadStateChanged",
/// camelCase on the wire. Fired when read-state changes from ANY source:
///   • path (a) — a mark route was called (THIS PR), and
///   • path (b) — the poller detected an externally-originated read flip (FAST-FOLLOW, Task 9).
/// RadioConsole de-dupes by (Id/ThreadId + IsRead); a client's own mark and the echoed event are
/// idempotent on its side, so we broadcast UNCONDITIONALLY (do not suppress the originator).
///
/// • Kind: "Voicemail" | "Sms" (treat unknown defensively on the consumer).
/// • Id: voicemail id when Kind=Voicemail; null/empty for an Sms thread-level change.
/// • ThreadId: thread id when Kind=Sms (required); the voicemail's threadId when Kind=Voicemail.
/// • IsRead: the new read-state; for an Sms thread-level change this is "thread fully read" (!hasUnread).
/// • ChangedAtUtc: ISO-8601 UTC, when the change was observed/applied.
/// </summary>
public record ReadStateChangedDto(
    string Kind,
    string? Id,
    string? ThreadId,
    bool IsRead,
    DateTime ChangedAtUtc);
