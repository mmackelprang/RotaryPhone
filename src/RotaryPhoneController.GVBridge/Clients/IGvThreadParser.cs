using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Parser seam isolating GV's positional-array field positions. The ONLY implementation that knows
/// field indices is <see cref="PositionalGvThreadParser"/>, whose positions are VERIFIED against a
/// checked-in live capture. Clients depend on this interface, not on raw indices.
/// </summary>
public interface IGvThreadParser
{
    /// <summary>Parse the top-level api2thread/list response into thread nodes.</summary>
    IReadOnlyList<GvThreadNode> ParseThreadList(JsonElement root);

    /// <summary>Parse voicemail message nodes from a voicemail-folder list response.</summary>
    IReadOnlyList<GvVoicemailNode> ParseVoicemailList(JsonElement root);

    /// <summary>Parse SMS message nodes from a single thread's message list / SMS-folder list.</summary>
    IReadOnlyList<GvSmsNode> ParseSmsMessages(JsonElement root);

    /// <summary>Extract the next-page token from a list response, or null if none.</summary>
    string? ParseNextPageToken(JsonElement root);

    /// <summary>
    /// Count the raw thread nodes present in the payload, WITHOUT interpreting any field positions
    /// beyond the root envelope.
    /// <para>
    /// This exists so callers can tell "GV genuinely returned an empty folder" (0 raw threads) apart
    /// from "GV returned data our positional indices failed to interpret" (raw threads &gt; 0 but 0
    /// parsed items). Conflating those two is what let a completely broken parser look like a healthy
    /// empty inbox for weeks. Callers MUST treat the second case as a failure, not as empty.
    /// </para>
    /// </summary>
    int CountThreads(JsonElement root);
}
