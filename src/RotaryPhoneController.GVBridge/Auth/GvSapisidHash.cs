using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.GVBridge.Auth;

public static class GvSapisidHash
{
    private const string GvOrigin = "https://voice.google.com";

    public static string Compute(string sapisid, string origin, long timestampSeconds)
    {
        var input = $"{timestampSeconds} {sapisid} {origin}";
#pragma warning disable CA5350
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
#pragma warning restore CA5350
        return $"{timestampSeconds}_{Convert.ToHexStringLower(hash)}";
    }

    public static string ComputeCurrent(string sapisid, string origin = GvOrigin) =>
        Compute(sapisid, origin, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
