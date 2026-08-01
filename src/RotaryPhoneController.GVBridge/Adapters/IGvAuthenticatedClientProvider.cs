namespace RotaryPhoneController.GVBridge.Adapters;

/// <summary>
/// Seam exposing the CURRENT authenticated GV HttpClient (cookie + SAPISIDHASH + PSIDTS-fresh).
/// Implemented by <see cref="GVApiAdapter"/>. New read clients/services resolve the live client
/// through this so they inherit cookie rotation + the recovery ladder (ADR §1.3, §7). Returns null
/// when the adapter is not activated / has no valid cookies.
/// </summary>
public interface IGvAuthenticatedClientProvider
{
    /// <summary>The current authenticated HttpClient, or null if the adapter is unavailable.</summary>
    HttpClient? GetAuthenticatedClient();

    /// <summary>The GV voiceclient base URL (e.g. .../voice/v1/voiceclient).</summary>
    string ApiBaseUrl { get; }

    /// <summary>The GV public web API key.</summary>
    string ApiKey { get; }

    /// <summary>
    /// Ask the adapter to refresh GV auth (the rotate → reload → CDP ladder) and report whether it
    /// worked. Concurrent callers share ONE recovery. READ paths await this and then retry once;
    /// WRITE paths (sendsms, updateread) may call it WITHOUT awaiting but MUST NOT replay the
    /// request — ADR §4.2 #4 forbids auto-retry on irreversible writes.
    /// </summary>
    Task<bool> TryRecoverAuthAsync(string reason, CancellationToken ct = default);
}
