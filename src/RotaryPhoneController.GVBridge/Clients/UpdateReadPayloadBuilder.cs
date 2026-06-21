using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Default updateread payload builder. The array shape below is the WORKING ASSUMPTION
/// ([id, isRead]-positional via GvProtobuf.BuildArray) — **UNVERIFIED**, pending ADR §11 step 8 (send a
/// test updateread against a known voicemail + SMS thread, capture the exact resource name + payload
/// positions + per-thread vs per-message grain + whether the response echoes the node). If live capture
/// reveals different positions/grain, fix it HERE only — GvReadStateClient and the routes do not change.
/// </summary>
public class UpdateReadPayloadBuilder : IUpdateReadPayloadBuilder
{
    public string BuildVoicemail(string messageId, string threadId, bool isRead)
        // UNVERIFIED positions — ADR §11 step 8. GvProtobuf.BuildArray emits a JSON array; the bool arm
        // writes a real JSON bool (matches the §4.1 sendsms payload discipline).
        => GvProtobuf.BuildArray(messageId, threadId, isRead);

    public IReadOnlyList<string> BuildSmsThread(
        string threadId, IReadOnlyList<string> messageIds, bool isRead)
    {
        // Per-message is the working assumption (UNVERIFIED — §11 step 8): one updateread per message id.
        // Empty list → a single thread-level payload so an empty/preview-only thread can still be marked.
        if (messageIds.Count == 0)
            return new[] { GvProtobuf.BuildArray(threadId, isRead) };

        var payloads = new List<string>(messageIds.Count);
        foreach (var id in messageIds)
            payloads.Add(GvProtobuf.BuildArray(id, threadId, isRead));
        return payloads;
    }
}
