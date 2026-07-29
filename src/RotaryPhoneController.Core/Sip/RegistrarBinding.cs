namespace RotaryPhoneController.Core.Sip;

/// <summary>
/// A learned SIP registrar binding: where a registered endpoint (the HT801) can actually be reached.
/// Learned from the source address of its REGISTER, which the device repeats roughly every 50
/// minutes — so a DHCP move self-heals within one registration interval.
/// </summary>
/// <param name="AddressOfRecord">URI user part the device registered as (e.g. "rotaryphone", "1000").</param>
/// <param name="Address">Address to send INVITEs to — the IP the REGISTER actually arrived from.</param>
/// <param name="Port">Source SIP port of the REGISTER (normally 5060).</param>
/// <param name="ContactHost">Host advertised in the device's Contact header. Diagnostics only — see plan D3.</param>
/// <param name="LearnedAtUtc">When this binding was last refreshed.</param>
/// <param name="ExpiresSeconds">Expiry the device requested in its REGISTER.</param>
public sealed record RegistrarBinding(
    string AddressOfRecord,
    string Address,
    int Port,
    string? ContactHost,
    DateTime LearnedAtUtc,
    int ExpiresSeconds)
{
    /// <summary>
    /// Grace added to the requested expiry before a binding is considered stale. The HT801
    /// re-registers at ~50% of expiry, so a single missed refresh must not invalidate the binding.
    /// </summary>
    public static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(5);

    public bool IsFresh(DateTime utcNow) =>
        utcNow - LearnedAtUtc <= TimeSpan.FromSeconds(ExpiresSeconds) + StaleGrace;
}
