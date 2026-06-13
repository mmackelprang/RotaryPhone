namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// Result of a browser-less rotating-cookie refresh attempt. <see cref="Rotated"/> is false
/// (with null PSIDTS values) whenever the refresh could not be completed for ANY reason —
/// the caller treats that as "fall back to CDP / operator re-login" rather than an exception.
/// </summary>
public readonly record struct CookieRotationResult(bool Rotated, string? Psidts1, string? Psidts3)
{
    public static CookieRotationResult NotRotated => new(false, null, null);
}

/// <summary>
/// Browser-less refresh of Google's rotating freshness cookies
/// (<c>__Secure-1PSIDTS</c> / <c>__Secure-3PSIDTS</c>) from the stored long-lived
/// <c>__Secure-1PSID</c>. This is the PRIMARY 401-recovery for the headless box (no Chrome
/// required); the CDP <c>cookies/refresh-from-browser</c> flow remains the fallback for a
/// genuinely dead login. Implementations must be best-effort and non-throwing — surface
/// failure via <see cref="CookieRotationResult.Rotated"/> = false so the recovery ladder
/// can fall back cleanly.
/// </summary>
public interface ICookieRotator
{
    Task<CookieRotationResult> RotateAsync(GvCookieSet current, CancellationToken ct = default);
}
