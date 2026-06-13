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
    public GvCookieSet WithRefreshedPsidts(string? psidts1, string? psidts3)
    {
        if (psidts1 is null && psidts3 is null)
            return this;

        // Ensure we have a raw header to overlay onto (build from fields if absent).
        var raw = string.IsNullOrEmpty(RawCookieHeader) ? ToCookieHeader() : RawCookieHeader;

        if (psidts1 is not null)
            raw = SpliceCookie(raw, "__Secure-1PSIDTS", psidts1);
        if (psidts3 is not null)
            raw = SpliceCookie(raw, "__Secure-3PSIDTS", psidts3);

        return new GvCookieSet
        {
            Sapisid = Sapisid,
            Sid = Sid,
            Hsid = Hsid,
            Ssid = Ssid,
            Apisid = Apisid,
            Secure1Psid = Secure1Psid,
            Secure3Psid = Secure3Psid,
            RawCookieHeader = raw,
        };
    }

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
