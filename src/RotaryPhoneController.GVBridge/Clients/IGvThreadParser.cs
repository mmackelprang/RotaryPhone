using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Parser seam isolating GV's UNVERIFIED positional-array field positions (ADR §3, §8, §11).
/// The ONLY implementation that knows field indices is <see cref="PositionalGvThreadParser"/>;
/// when ADR §11 live capture pins the real positions, the fix is localized to that one class.
/// Clients depend on this interface, not on raw indices.
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
}
