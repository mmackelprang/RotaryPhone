using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.Server.Auth;

/// <summary>
/// Validates the X-RotaryPhone-Auth shared secret (ADR §6.5). When the configured key is empty the gate
/// is DISABLED and everything is allowed — preserving today's LAN-only, no-auth behavior exactly (zero
/// behavior change when unset). When set, the supplied header must match exactly, compared in
/// CONSTANT TIME (CryptographicOperations.FixedTimeEquals) so the key cannot be recovered via a timing
/// oracle. Required before any non-LAN exposure (boundary doc Cookie Management Security note).
/// </summary>
public class InterServiceAuthValidator
{
    private readonly byte[] _keyBytes;

    public InterServiceAuthValidator(string configuredKey)
    {
        IsEnabled = !string.IsNullOrEmpty(configuredKey);
        _keyBytes = Encoding.UTF8.GetBytes(configuredKey ?? "");
    }

    /// <summary>True if a non-empty key is configured (the gate enforces).</summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gate decision. Disabled → always true. Enabled → constant-time exact match of the header bytes.
    /// </summary>
    public bool IsAuthorized(string? presentedKey)
    {
        if (!IsEnabled) return true;
        if (presentedKey is null) return false;
        var presented = Encoding.UTF8.GetBytes(presentedKey);
        return CryptographicOperations.FixedTimeEquals(presented, _keyBytes);
    }
}
