namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Serializes the GV api2thread/updateread payload (ADR §7). The resource name, the payload POSITIONS
/// (which array slot carries the id vs the read bool), and the per-thread-vs-per-message GRAIN are all
/// UNVERIFIED — pending ADR §11 step 8 live capture. Isolating them here means the live-capture correction
/// is a ONE-FILE change, mirroring IGvThreadParser / GvThreadFolder.ToWireValue() / ISmsThreadIdResolver.
/// The contract boundary RadioConsole sees does NOT depend on anything in here.
/// </summary>
public interface IUpdateReadPayloadBuilder
{
    /// <summary>Build the updateread payload for a single voicemail node.</summary>
    string BuildVoicemail(string messageId, string threadId, bool isRead);

    /// <summary>
    /// Build the updateread payload(s) for a PER-THREAD SMS mark (ADR §4.2 Q4). Returns one payload per
    /// message id if GV's grain is per-message (the working assumption), or a single thread-level payload
    /// (also the fallback when messageIds is empty). The caller POSTs each returned payload.
    /// </summary>
    IReadOnlyList<string> BuildSmsThread(string threadId, IReadOnlyList<string> messageIds, bool isRead);
}
