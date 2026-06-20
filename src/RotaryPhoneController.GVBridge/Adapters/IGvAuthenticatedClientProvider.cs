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
}
