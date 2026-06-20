namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Default thread-id resolver. NEW-conversation form is "t.+<E164>" per ADR §4.1 — **UNVERIFIED**,
/// pending ADR §11 step 4 (send a test text, capture the id GV actually assigns, confirm reply uses
/// that id vs t.+<E164>). If live capture reveals a different new-thread format, fix it HERE only.
/// </summary>
public class SmsThreadIdResolver : ISmsThreadIdResolver
{
    public string Resolve(string normalizedE164, string? explicitThreadId)
        => string.IsNullOrWhiteSpace(explicitThreadId)
            ? $"t.{normalizedE164}"          // UNVERIFIED — ADR §11 step 4
            : explicitThreadId;              // reply: Google's real id, verbatim (ADR §4.2 #1)
}
