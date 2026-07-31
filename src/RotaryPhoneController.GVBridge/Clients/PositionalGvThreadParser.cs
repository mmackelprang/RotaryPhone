using System.Text.Json;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// The single source of truth for GV api2thread/list positional-array field indices.
///
/// VERIFIED against a live authenticated capture (2026-07-31, CDP against voice.google.com).
/// The redacted capture is checked in at Fixtures/capture/*.response.json and every index below is
/// asserted directly against it by <c>CapturedWireShapeTests</c> — if Google moves a field, those
/// tests fail. Do NOT hand-write a fixture to match a change here; re-capture instead.
///
/// Wire shape (VERIFIED):
///   root      = [ threads[], numericString, versionCursor ]      // an ARRAY of 3, not an object
///   thread    = [ threadId, isRead, messages[], _, participants[], [1, folder], ... ]   // len 11-12
///   message   = [ msgId, epochMs, accountOwnerE164, counterparty[], type, isRead,
///                 transcript?, _, durationSec?, smsText, _, mediaUrl?, ..., counterpartyE164, ... ]
///                                                                                      // len >= 19
///
/// Two shape facts that are deliberately NOT hard-coded as lengths: threads are 11 (voicemail/calls)
/// or 12 (SMS) elements, and messages are 19 or 22. Every accessor is bounds-checked, so a longer
/// array is read fine and a shorter one yields nulls rather than throwing.
/// </summary>
public sealed class PositionalGvThreadParser : IGvThreadParser
{
    // --- root ---  (VERIFIED: array of 3)
    private const int RootThreadsIdx = 0;
    private const int RootVersionCursorIdx = 2;

    // --- thread node indices ---  (VERIFIED)
    private const int ThreadIdIdx = 0;
    private const int ThreadIsReadIdx = 1;         // 1 = READ, 0 = UNREAD (see IsReadFlag)
    private const int ThreadMessagesIdx = 2;
    private const int ThreadParticipantsIdx = 4;   // [[e164, e164, null, null, null, null, 0]]
    private const int ThreadFolderPairIdx = 5;     // [1, folderWireValue]
    private const int ParticipantNumberIdx = 0;

    // --- message node indices ---  (VERIFIED; shared by voicemail / SMS / calls)
    private const int MsgIdIdx = 0;
    private const int MsgEpochMsIdx = 1;
    private const int MsgAccountOwnerIdx = 2;      // OUR number, never the counterparty
    private const int MsgCounterpartyArrIdx = 3;   // [e164, e164, ...] — len 2 or 7
    private const int MsgTypeIdx = 4;              // 2=voicemail, 1/14/0=call, 10/11=SMS
    private const int MsgIsReadIdx = 5;            // 1 = READ, 0 = UNREAD
    private const int MsgTranscriptIdx = 6;        // [confidence, [[word, _, _, conf], ...]] | null
    private const int MsgDurationSecIdx = 8;       // int for voicemail/calls, null for SMS
    private const int MsgTextIdx = 9;              // SMS body; "" for voicemail/calls
    private const int MsgMediaUrlIdx = 11;         // ABSOLUTE https://www.google.com/voice/media/svm/...
    private const int MsgCounterpartyIdx = 15;     // e164, equals MsgCounterpartyArrIdx[0]

    /// <summary>SMS message type values (VERIFIED): 10 = inbound, 11 = outbound.</summary>
    private const int SmsTypeInbound = 10;
    private const int SmsTypeOutbound = 11;

    public IReadOnlyList<GvThreadNode> ParseThreadList(JsonElement root)
    {
        var threads = ThreadsArray(root);
        if (threads is null) return Array.Empty<GvThreadNode>();

        var result = new List<GvThreadNode>(threads.Value.GetArrayLength());
        foreach (var thread in threads.Value.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Array) continue;
            result.Add(new GvThreadNode(
                ThreadId: GvProtobuf.GetString(thread, ThreadIdIdx),
                CounterpartyNumber: ThreadCounterparty(thread),
                // No display name exists anywhere in this payload — GV returns E.164 only. Contact
                // resolution is the consumer's job (RadioConsole has ContactResolutionService).
                CounterpartyName: null,
                LastMessageEpochMs: LastMessageEpochMs(thread),
                HasUnread: Negate(IsReadFlag(thread, ThreadIsReadIdx)),
                LastMessagePreview: LastMessagePreview(thread)));
        }
        return result;
    }

    public IReadOnlyList<GvVoicemailNode> ParseVoicemailList(JsonElement root)
    {
        var result = new List<GvVoicemailNode>();
        foreach (var (threadId, msg) in EnumerateMessages(root))
        {
            result.Add(new GvVoicemailNode(
                MessageId: GvProtobuf.GetString(msg, MsgIdIdx),
                // The message node carries NO thread id — it lives on the parent thread. The poller's
                // high-water mark and the per-thread filters key on this, so it must be propagated.
                ThreadId: threadId,
                FromNumber: Counterparty(msg),
                // Never populated: the payload has no display name at any position.
                FromName: null,
                ReceivedEpochMs: GvProtobuf.GetLong(msg, MsgEpochMsIdx),
                DurationSeconds: GvProtobuf.GetInt(msg, MsgDurationSecIdx),
                IsRead: IsReadFlag(msg, MsgIsReadIdx),
                Transcript: Transcript(msg),
                // Absolute media URL on www.google.com (a DIFFERENT host from the API base).
                // GvRecordingFetcher detects the absolute form and GETs it directly.
                MediaId: GvProtobuf.GetString(msg, MsgMediaUrlIdx)));
        }
        return result;
    }

    public IReadOnlyList<GvSmsNode> ParseSmsMessages(JsonElement root)
    {
        var result = new List<GvSmsNode>();
        foreach (var (threadId, msg) in EnumerateMessages(root))
        {
            result.Add(new GvSmsNode(
                MessageId: GvProtobuf.GetString(msg, MsgIdIdx),
                ThreadId: threadId,
                // Direction comes from the message TYPE (index 4), not a separate field. Anything
                // outside the known SMS values stays null rather than being guessed as Inbound.
                Direction: GvProtobuf.GetInt(msg, MsgTypeIdx) switch
                {
                    SmsTypeOutbound => "Outbound",
                    SmsTypeInbound => "Inbound",
                    _ => null
                },
                CounterpartyNumber: Counterparty(msg),
                Text: GvProtobuf.GetString(msg, MsgTextIdx),
                SentEpochMs: GvProtobuf.GetLong(msg, MsgEpochMsIdx),
                IsRead: IsReadFlag(msg, MsgIsReadIdx)));
        }
        return result;
    }

    /// <inheritdoc />
    public int CountThreads(JsonElement root)
    {
        var threads = ThreadsArray(root);
        return threads?.GetArrayLength() ?? 0;
    }

    /// <summary>
    /// The account owner's own E.164 at message index 2. This is OUR number on every message in
    /// every folder — it is NOT the sender. Exposed so tests can pin the trap that made the original
    /// synthetic contract read index 2 as "from".
    /// </summary>
    public static string? ParseAccountOwner(JsonElement msg) =>
        GvProtobuf.GetString(msg, MsgAccountOwnerIdx);

    /// <summary>
    /// root[2] is a version cursor (e.g. "v1-1-1785332793946724"), NOT a page token. Its paging
    /// semantics are UNVERIFIED — the capture was a single un-paged request, so we have no evidence
    /// it can be fed back to advance a page. Returning it as a "next page token" would make callers
    /// loop forever on the same page, so this deliberately returns null until paging is captured.
    /// <see cref="ParseVersionCursor"/> exposes the raw value for change-detection use.
    /// </summary>
    public string? ParseNextPageToken(JsonElement root) => null;

    /// <summary>
    /// The raw root[2] version cursor. Useful as an opaque "did anything change" marker; do NOT
    /// treat it as a page token without a live paging capture.
    /// </summary>
    public static string? ParseVersionCursor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() <= RootVersionCursorIdx)
            return null;
        var el = root[RootVersionCursorIdx];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>
    /// The folder wire value echoed back at thread[5] = [1, folder]. Exposed so tests can assert the
    /// response we parsed actually corresponds to the folder we asked for.
    /// </summary>
    public static int? ParseThreadFolder(JsonElement thread)
    {
        var pair = GvProtobuf.GetArray(thread, ThreadFolderPairIdx);
        return pair is null ? null : GvProtobuf.GetInt(pair.Value, 1);
    }

    // ---- helpers ----

    private static JsonElement? ThreadsArray(JsonElement root)
    {
        // VERIFIED: root is an ARRAY of 3 — [threads, numericString, versionCursor]. The old code
        // required an object with a "threads" property, which no real response has ever had.
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() <= RootThreadsIdx)
            return null;
        var threads = root[RootThreadsIdx];
        return threads.ValueKind == JsonValueKind.Array ? threads : null;
    }

    /// <summary>Yields (threadId, message) so each message carries its parent thread's id.</summary>
    private static IEnumerable<(string? ThreadId, JsonElement Message)> EnumerateMessages(JsonElement root)
    {
        var threads = ThreadsArray(root);
        if (threads is null) yield break;
        foreach (var thread in threads.Value.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Array) continue;
            var threadId = GvProtobuf.GetString(thread, ThreadIdIdx);
            var messages = GvProtobuf.GetArray(thread, ThreadMessagesIdx);
            if (messages is null) continue;
            foreach (var msg in messages.Value.EnumerateArray())
                if (msg.ValueKind == JsonValueKind.Array)
                    yield return (threadId, msg);
        }
    }

    /// <summary>
    /// Read-state. VERIFIED polarity: wire 1 = READ, 0 = UNREAD. Determined empirically — the SMS
    /// folder returned {0:2, 1:18} against a UI reporting exactly "Messages: 2 unread".
    /// </summary>
    private static bool? IsReadFlag(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength()) return null;
        var el = array[index];
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt32() != 0,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool? Negate(bool? value) => value is null ? null : !value.Value;

    /// <summary>Counterparty E.164 — index 15, falling back to counterparty-array[0].</summary>
    private static string? Counterparty(JsonElement msg)
    {
        var direct = GvProtobuf.GetString(msg, MsgCounterpartyIdx);
        if (direct is not null) return direct;
        var arr = GvProtobuf.GetArray(msg, MsgCounterpartyArrIdx);
        return arr is null ? null : GvProtobuf.GetString(arr.Value, 0);
    }

    private static string? ThreadCounterparty(JsonElement thread)
    {
        // participants = [[e164, e164, null, null, null, null, 0]]
        var participants = GvProtobuf.GetArray(thread, ThreadParticipantsIdx);
        if (participants is not null && participants.Value.GetArrayLength() > 0)
        {
            var first = participants.Value[0];
            if (first.ValueKind == JsonValueKind.Array)
            {
                var num = GvProtobuf.GetString(first, ParticipantNumberIdx);
                if (num is not null) return num;
            }
        }
        // Fall back to the newest message's counterparty.
        var last = NewestMessage(thread);
        return last is null ? null : Counterparty(last.Value);
    }

    /// <summary>
    /// Transcript flattened to text. Wire shape: [confidence, [[word, null, null, conf], ...]].
    /// Null when transcription is pending/absent (VERIFIED: 2 of 20 captured voicemails).
    /// </summary>
    private static string? Transcript(JsonElement msg)
    {
        var node = GvProtobuf.GetArray(msg, MsgTranscriptIdx);
        if (node is null) return null;
        var words = GvProtobuf.GetArray(node.Value, 1);
        if (words is null) return null;

        var parts = new List<string>();
        foreach (var w in words.Value.EnumerateArray())
        {
            var text = w.ValueKind == JsonValueKind.Array
                ? GvProtobuf.GetString(w, 0)
                : (w.ValueKind == JsonValueKind.String ? w.GetString() : null);
            if (!string.IsNullOrEmpty(text)) parts.Add(text);
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Messages within a thread are newest-first in the capture, but rather than rely on that we
    /// pick by max timestamp so ordering drift cannot silently change the preview.
    /// </summary>
    private static JsonElement? NewestMessage(JsonElement thread)
    {
        var messages = GvProtobuf.GetArray(thread, ThreadMessagesIdx);
        if (messages is null || messages.Value.GetArrayLength() == 0) return null;

        JsonElement? best = null;
        long bestEpoch = long.MinValue;
        foreach (var msg in messages.Value.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Array) continue;
            var epoch = GvProtobuf.GetLong(msg, MsgEpochMsIdx) ?? long.MinValue;
            if (best is null || epoch > bestEpoch)
            {
                best = msg;
                bestEpoch = epoch;
            }
        }
        return best;
    }

    private static long? LastMessageEpochMs(JsonElement thread)
    {
        var newest = NewestMessage(thread);
        return newest is null ? null : GvProtobuf.GetLong(newest.Value, MsgEpochMsIdx);
    }

    private static string? LastMessagePreview(JsonElement thread)
    {
        var newest = NewestMessage(thread);
        if (newest is null) return null;
        // SMS body when present; otherwise fall back to a voicemail transcript so voicemail threads
        // are not previewed as an empty string (msg[9] is "" for voicemail/calls).
        var text = GvProtobuf.GetString(newest.Value, MsgTextIdx);
        return string.IsNullOrEmpty(text) ? Transcript(newest.Value) : text;
    }
}
