using System.Text;
using System.Text.Json;

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

    public string Serialize() => JsonSerializer.Serialize(this);

    public static GvCookieSet Deserialize(string json) =>
        JsonSerializer.Deserialize<GvCookieSet>(json)
        ?? throw new InvalidOperationException("Failed to deserialize cookie set.");
}
