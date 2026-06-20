using System.Text.Json;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// The single source of truth for GV api2thread/list positional-array field indices.
/// EVERY index below is UNVERIFIED (ADR §3, §11). When a live capture lands, correct the
/// const map + the test fixtures together; clients, DTOs, and tests-structure stay unchanged.
///
/// Wire shape assumed (synthetic, ADR §3):
///   root                = { "threads": [ thread, ... ], "nextPageToken": string|null }
///   thread              = [ threadId, [number, name?], lastMsgEpochMs, hasUnread, [ message, ... ] ]
///   sms message         = [ msgId, threadId, directionInt, counterparty, text, sentEpochMs, isRead ]
///   voicemail message   = [ msgId, threadId, from, fromName, recvEpochMs, durSec, isRead, transcript?, mediaId ]
/// </summary>
public sealed class PositionalGvThreadParser : IGvThreadParser
{
    // --- root ---  (UNVERIFIED — ADR §11 step 1)
    private const string ThreadsProp = "threads";
    private const string NextPageTokenProp = "nextPageToken";

    // --- thread node indices ---  (UNVERIFIED — ADR §11 step 1)
    private const int ThreadIdIdx = 0;
    private const int ThreadParticipantIdx = 1;   // [number, name?]
    private const int ThreadLastMsgEpochIdx = 2;
    private const int ThreadHasUnreadIdx = 3;
    private const int ThreadMessagesIdx = 4;
    private const int ParticipantNumberIdx = 0;
    private const int ParticipantNameIdx = 1;

    // --- sms message node indices ---  (UNVERIFIED — ADR §11 step 1)
    private const int SmsIdIdx = 0;
    private const int SmsThreadIdIdx = 1;
    private const int SmsDirectionIdx = 2;        // 0 = inbound, 1 = outbound (UNVERIFIED)
    private const int SmsCounterpartyIdx = 3;
    private const int SmsTextIdx = 4;
    private const int SmsSentEpochIdx = 5;
    private const int SmsIsReadIdx = 6;

    // --- voicemail message node indices ---  (UNVERIFIED — ADR §11 step 2)
    private const int VmIdIdx = 0;
    private const int VmThreadIdIdx = 1;
    private const int VmFromIdx = 2;
    private const int VmFromNameIdx = 3;
    private const int VmRecvEpochIdx = 4;
    private const int VmDurationIdx = 5;
    private const int VmIsReadIdx = 6;
    private const int VmTranscriptIdx = 7;
    private const int VmMediaIdIdx = 8;

    public IReadOnlyList<GvThreadNode> ParseThreadList(JsonElement root)
    {
        var threads = ThreadsArray(root);
        if (threads is null) return Array.Empty<GvThreadNode>();

        var result = new List<GvThreadNode>(threads.Value.GetArrayLength());
        foreach (var thread in threads.Value.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Array) continue;
            var participant = GvProtobuf.GetArray(thread, ThreadParticipantIdx);
            result.Add(new GvThreadNode(
                ThreadId: GvProtobuf.GetString(thread, ThreadIdIdx),
                CounterpartyNumber: participant is { } p ? GvProtobuf.GetString(p, ParticipantNumberIdx) : null,
                CounterpartyName: participant is { } p2 ? GvProtobuf.GetString(p2, ParticipantNameIdx) : null,
                LastMessageEpochMs: GvProtobuf.GetLong(thread, ThreadLastMsgEpochIdx),
                HasUnread: GetBool(thread, ThreadHasUnreadIdx),
                LastMessagePreview: LastMessagePreview(thread)));
        }
        return result;
    }

    public IReadOnlyList<GvVoicemailNode> ParseVoicemailList(JsonElement root)
    {
        var result = new List<GvVoicemailNode>();
        foreach (var msg in EnumerateMessages(root))
        {
            result.Add(new GvVoicemailNode(
                MessageId: GvProtobuf.GetString(msg, VmIdIdx),
                ThreadId: GvProtobuf.GetString(msg, VmThreadIdIdx),
                FromNumber: GvProtobuf.GetString(msg, VmFromIdx),
                FromName: GvProtobuf.GetString(msg, VmFromNameIdx),
                ReceivedEpochMs: GvProtobuf.GetLong(msg, VmRecvEpochIdx),
                DurationSeconds: GvProtobuf.GetInt(msg, VmDurationIdx),
                IsRead: GetBool(msg, VmIsReadIdx),
                Transcript: GvProtobuf.GetString(msg, VmTranscriptIdx),
                MediaId: GvProtobuf.GetString(msg, VmMediaIdIdx)));
        }
        return result;
    }

    public IReadOnlyList<GvSmsNode> ParseSmsMessages(JsonElement root)
    {
        var result = new List<GvSmsNode>();
        foreach (var msg in EnumerateMessages(root))
        {
            result.Add(new GvSmsNode(
                MessageId: GvProtobuf.GetString(msg, SmsIdIdx),
                ThreadId: GvProtobuf.GetString(msg, SmsThreadIdIdx),
                Direction: GvProtobuf.GetInt(msg, SmsDirectionIdx) == 1 ? "Outbound" : "Inbound",
                CounterpartyNumber: GvProtobuf.GetString(msg, SmsCounterpartyIdx),
                Text: GvProtobuf.GetString(msg, SmsTextIdx),
                SentEpochMs: GvProtobuf.GetLong(msg, SmsSentEpochIdx),
                IsRead: GetBool(msg, SmsIsReadIdx)));
        }
        return result;
    }

    public string? ParseNextPageToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(NextPageTokenProp, out var tok)) return null;
        return tok.ValueKind == JsonValueKind.String ? tok.GetString() : null;
    }

    // ---- helpers ----

    private static JsonElement? ThreadsArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(ThreadsProp, out var threads)) return null;
        return threads.ValueKind == JsonValueKind.Array ? threads : null;
    }

    private static IEnumerable<JsonElement> EnumerateMessages(JsonElement root)
    {
        var threads = ThreadsArray(root);
        if (threads is null) yield break;
        foreach (var thread in threads.Value.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Array) continue;
            var messages = GvProtobuf.GetArray(thread, ThreadMessagesIdx);
            if (messages is null) continue;
            foreach (var msg in messages.Value.EnumerateArray())
                if (msg.ValueKind == JsonValueKind.Array)
                    yield return msg;
        }
    }

    private static string? LastMessagePreview(JsonElement thread)
    {
        var messages = GvProtobuf.GetArray(thread, ThreadMessagesIdx);
        if (messages is null || messages.Value.GetArrayLength() == 0) return null;
        var last = messages.Value[messages.Value.GetArrayLength() - 1];
        return last.ValueKind == JsonValueKind.Array ? GvProtobuf.GetString(last, SmsTextIdx) : null;
    }

    private static bool? GetBool(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength()) return null;
        var el = array[index];
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.GetInt32() != 0,
            _ => null
        };
    }
}
