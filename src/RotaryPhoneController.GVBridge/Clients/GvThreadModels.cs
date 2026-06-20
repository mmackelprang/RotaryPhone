namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Parsed representation of a single GV thread node from api2thread/list.
/// These are INTERNAL parse DTOs (Google's wire shape stays out of the public REST contract —
/// controllers map these to the §6.1 public records). Every field is nullable: GV positional
/// arrays shift and fields can be absent (ADR §7 "treat every field as possibly-null").
/// </summary>
public record GvThreadNode(
    string? ThreadId,
    string? CounterpartyNumber,   // E.164 as GV returns it (may need normalization upstream)
    string? CounterpartyName,
    long? LastMessageEpochMs,
    bool? HasUnread,
    string? LastMessagePreview);

/// <summary>
/// Parsed voicemail message node. MediaId is the reference PR2 resolves to fetchable audio.
/// Transcript may be null (pending/absent) — ADR §3.3.
/// </summary>
public record GvVoicemailNode(
    string? MessageId,
    string? ThreadId,
    string? FromNumber,
    string? FromName,
    long? ReceivedEpochMs,
    int? DurationSeconds,
    bool? IsRead,
    string? Transcript,
    string? MediaId);

/// <summary>
/// Parsed SMS message node (used by PR3 read path). Direction is GV-encoded; the parser maps it
/// to "Inbound"/"Outbound". Text may be null for non-text message subtypes.
/// </summary>
public record GvSmsNode(
    string? MessageId,
    string? ThreadId,
    string? Direction,            // "Inbound" | "Outbound"
    string? CounterpartyNumber,
    string? Text,
    long? SentEpochMs,
    bool? IsRead);
