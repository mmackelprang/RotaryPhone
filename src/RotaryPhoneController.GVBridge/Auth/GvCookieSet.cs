using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RotaryPhoneController.GVBridge.Auth;

public sealed class GvCookieSet
{
    public required string Sapisid { get; init; }
    public required string Sid { get; init; }
    public required string Hsid { get; init; }
    public required string Ssid { get; init; }
    public required string Apisid { get; init; }
    public string? Secure1Psid { get; init; }
    public string? Secure3Psid { get; init; }

    /// <summary>
    /// Full raw cookie header captured from the browser. When present,
    /// ToCookieHeader() returns this verbatim instead of building from
    /// individual fields. Google requires many cookies beyond the core 7
    /// (SIDCC, __Secure-1PSIDCC, NID, etc.) for auth to succeed.
    /// </summary>
    public string? RawCookieHeader { get; init; }

    /// <summary>
    /// UTC time the PSIDTS values carried by this set were genuinely MINTED — the moment
    /// <c>RotateCookies</c> returned them. <c>null</c> means UNKNOWN: a set written before this field
    /// existed, one pasted in by hand, one produced by <c>scripts/gv-extract-cookies.py</c>, or one
    /// extracted from the browser (Chrome's jar carries no issue time we can read).
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately NOT "when we loaded the file". The adapter used to stamp <c>DateTime.UtcNow</c> on
    /// every load, which reported a credential minted 2026-09-06 as 208 seconds old and concealed a
    /// two-day session death. It is PERSISTED because a process that cannot know the age of the
    /// credential it inherited cannot schedule around it — that is the 2026-09-08 outage.
    /// </remarks>
    public DateTime? PsidtsMintedAtUtc { get; init; }

    /// <summary>
    /// UTC time a cookie set extracted from the box's Chrome last PASSED a live health probe against
    /// Google. <c>null</c> means no browser-sourced set in this lineage has ever been validated.
    /// Persisted so the age of the BROWSER session survives a restart — the missing signal that let a
    /// dead Chrome login go unnoticed from 2026-09-06 to 2026-09-08.
    /// </summary>
    public DateTime? BrowserSessionValidatedAtUtc { get; init; }

    public string ToCookieHeader()
    {
        if (!string.IsNullOrEmpty(RawCookieHeader))
            return RawCookieHeader;

        var sb = new StringBuilder();
        sb.Append("SAPISID=").Append(Sapisid)
          .Append("; SID=").Append(Sid)
          .Append("; HSID=").Append(Hsid)
          .Append("; SSID=").Append(Ssid)
          .Append("; APISID=").Append(Apisid);
        if (Secure1Psid is not null)
            sb.Append("; __Secure-1PSID=").Append(Secure1Psid);
        if (Secure3Psid is not null)
            sb.Append("; __Secure-3PSID=").Append(Secure3Psid);
        return sb.ToString();
    }

    /// <summary>
    /// Returns a new cookie set with the rotating freshness cookies
    /// (__Secure-1PSIDTS / __Secure-3PSIDTS) spliced into the raw cookie header. Google
    /// rotates these server-side; we capture the raw header once and replay it verbatim
    /// forever (the rotating cookies live only inside RawCookieHeader and are never updated),
    /// which is the root cause of the periodic 401 SESSION_COOKIE_INVALID. After a
    /// browser-less RotateCookies refresh, call this to overlay the fresh values so
    /// ToCookieHeader() stops sending the stale ones. A null argument leaves that partner
    /// unchanged. If no raw header is present, one is built from the typed fields first.
    /// </summary>
    /// <param name="mintedAtUtc">
    /// Test seam. Defaults to now; pass an explicit value to build a set of a known age.
    /// </param>
    public GvCookieSet WithRefreshedPsidts(string? psidts1, string? psidts3, DateTime? mintedAtUtc = null)
    {
        if (psidts1 is null && psidts3 is null)
            return this;

        // Ensure we have a raw header to overlay onto (build from fields if absent).
        var raw = string.IsNullOrEmpty(RawCookieHeader) ? ToCookieHeader() : RawCookieHeader;

        if (psidts1 is not null)
            raw = SpliceCookie(raw, "__Secure-1PSIDTS", psidts1);
        if (psidts3 is not null)
            raw = SpliceCookie(raw, "__Secure-3PSIDTS", psidts3);

        // A successful RotateCookies IS the mint, so the timestamp is stamped here rather than at the
        // call site — that is what makes it travel into Serialize() and onto disk.
        return CopyWith(raw, mintedAtUtc ?? DateTime.UtcNow, BrowserSessionValidatedAtUtc);
    }

    /// <summary>
    /// Record that this set came from the browser and has just passed a live health probe. Only ever
    /// called after a successful probe — an unvalidated browser set must never carry this stamp.
    /// </summary>
    public GvCookieSet WithBrowserSessionValidatedAt(DateTime validatedAtUtc)
        => CopyWith(RawCookieHeader, PsidtsMintedAtUtc, validatedAtUtc);

    /// <summary>
    /// Return this set carrying <paramref name="mintedAtUtc"/> as its PSIDTS mint time.
    /// </summary>
    /// <remarks>
    /// ⚠ Only legitimate for CARRYING FORWARD a mint time already known for THESE PSIDTS values — see
    /// <see cref="CarriesTheSamePsidtsAs"/>. A mint is stamped by <see cref="WithRefreshedPsidts"/> and
    /// nowhere else, because a successful <c>RotateCookies</c> IS the mint; inventing one here for
    /// values we did not mint would recreate the dishonesty this whole lineage exists to remove.
    /// </remarks>
    internal GvCookieSet WithPsidtsMintedAt(DateTime? mintedAtUtc)
        => CopyWith(RawCookieHeader, mintedAtUtc, BrowserSessionValidatedAtUtc);

    /// <summary>
    /// The rotating freshness cookie values this set would actually put on the wire.
    /// </summary>
    /// <remarks>
    /// Read from the RENDERED header, not from the typed fields: the PSIDTS values live only inside
    /// <see cref="RawCookieHeader"/> (<see cref="Secure1Psid"/> is the long-lived PSID, a different
    /// cookie). A set with no raw header has no PSIDTS at all, and both values are null.
    /// </remarks>
    internal (string? Psidts1, string? Psidts3) RotatingPsidts()
    {
        var header = ToCookieHeader();
        return (ReadCookie(header, "__Secure-1PSIDTS"), ReadCookie(header, "__Secure-3PSIDTS"));
    }

    /// <summary>
    /// Whether <paramref name="other"/> carries exactly the same rotating PSIDTS as this set — i.e. it
    /// is the SAME credential, however it reached us, and a mint time known for one is true of the other.
    /// </summary>
    /// <remarks>
    /// ⚠ A set carrying NO PSIDTS never matches, even against another that carries none. There is then
    /// no rotating credential for a mint time to describe, and "unknown" is the honest answer — which
    /// <see cref="GVApiAdapter.ComputeFirstRefreshDelayMs"/> deliberately treats as "refresh at the
    /// floor" rather than as "brand new".
    /// </remarks>
    internal bool CarriesTheSamePsidtsAs(GvCookieSet other)
    {
        var (mine1, mine3) = RotatingPsidts();
        if (mine1 is null && mine3 is null) return false;

        var (theirs1, theirs3) = other.RotatingPsidts();
        return string.Equals(mine1, theirs1, StringComparison.Ordinal)
            && string.Equals(mine3, theirs3, StringComparison.Ordinal);
    }

    /// <summary>
    /// Read the value of <paramref name="name"/> out of a "name=value; name2=value2" cookie header, or
    /// null if absent. Token-boundary matched, the same way <see cref="SpliceCookie"/> writes, so
    /// "__Secure-1PSIDTS" does not collide with "__Secure-1PSID".
    /// </summary>
    internal static string? ReadCookie(string header, string name)
    {
        if (string.IsNullOrEmpty(header)) return null;

        var match = Regex.Match(header, $@"(?:^|;\s*){Regex.Escape(name)}=([^;]*)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The ONLY place this type is copied. Every property must appear here exactly once.
    /// </summary>
    /// <remarks>
    /// ⚠ A property added to <see cref="GvCookieSet"/> and forgotten here is silently reset on every
    /// rotation. No test of a rotation's OUTPUT would catch it, because the output looks correct — the
    /// loss is of a field the test did not think to check. <c>CopyWith_CoversEveryProperty</c> is the
    /// tripwire; if you add a property, it will fail until you add it here too.
    /// </remarks>
    private GvCookieSet CopyWith(
        string? rawCookieHeader, DateTime? psidtsMintedAtUtc, DateTime? browserSessionValidatedAtUtc)
        => new()
        {
            Sapisid = Sapisid,
            Sid = Sid,
            Hsid = Hsid,
            Ssid = Ssid,
            Apisid = Apisid,
            Secure1Psid = Secure1Psid,
            Secure3Psid = Secure3Psid,
            RawCookieHeader = rawCookieHeader,
            PsidtsMintedAtUtc = psidtsMintedAtUtc,
            BrowserSessionValidatedAtUtc = browserSessionValidatedAtUtc,
        };

    /// <summary>
    /// Replace the value of <paramref name="name"/> in a "name=value; name2=value2" cookie
    /// header, or append it if absent. The name is matched at a token boundary (start or after
    /// "; ") so "__Secure-1PSIDTS" does not collide with "__Secure-1PSID".
    /// </summary>
    internal static string SpliceCookie(string header, string name, string value)
    {
        // (^|;\s*)NAME=  up to the next ';' or end of string.
        var pattern = $@"(^|;\s*){Regex.Escape(name)}=[^;]*";
        var replacement = $"$1{name}={value}";
        if (Regex.IsMatch(header, pattern))
            return Regex.Replace(header, pattern, replacement);

        // Not present — append.
        return string.IsNullOrEmpty(header) ? $"{name}={value}" : $"{header}; {name}={value}";
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static GvCookieSet Deserialize(string json) =>
        JsonSerializer.Deserialize<GvCookieSet>(json)
        ?? throw new InvalidOperationException("Failed to deserialize cookie set.");
}
