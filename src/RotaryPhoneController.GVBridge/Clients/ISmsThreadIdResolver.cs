namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Resolves the GV thread id to send to (ADR §4.2 #1). The reply-vs-new rule and the synthesized
/// new-thread format are UNVERIFIED (ADR §11 step 4) — isolating them here means the live-capture
/// correction is a ONE-FILE change, mirroring IGvThreadParser / GvThreadFolder.ToWireValue().
/// </summary>
public interface ISmsThreadIdResolver
{
    /// <summary>
    /// Given a normalized E.164 recipient and an optional existing thread id, return the id to send to.
    /// Non-empty explicitThreadId → reply (verbatim). Null/empty → new conversation (synthesized form).
    /// </summary>
    string Resolve(string normalizedE164, string? explicitThreadId);
}
