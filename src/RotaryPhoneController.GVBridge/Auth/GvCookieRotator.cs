using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// Best-effort browser-less rotating-cookie refresh via
/// <c>POST https://accounts.google.com/RotateCookies</c>, the same mechanism the always-on
/// Gemini/Bard web-API libraries use to keep <c>__Secure-1PSIDTS</c> fresh from the
/// long-lived <c>__Secure-1PSID</c>.
///
/// TODO (fast-follow — request shape UNCONFIRMED for the voice.google.com origin):
///   The exact RotateCookies body and required Referer/Origin/key for voice.google.com are
///   NOT yet confirmed from a packet capture (the public references are from the Bard/Gemini
///   origin). This implementation sends the documented best-effort body (<c>[000,"-0000000000000000000"]</c>
///   style poke) with the stored Cookie header, then parses fresh PSIDTS out of Set-Cookie.
///   If Google changes/ rejects the shape, RotateAsync returns NotRotated (never throws) so the
///   caller falls back to the CDP refresh-from-browser flow. Confirm the request against the
///   live account and tighten this once a capture is available. See
///   docs/research/gv-protocol-notes.md §3.2 / §5.1.
/// </summary>
public sealed class GvCookieRotator : ICookieRotator
{
    private const string RotateCookiesUrl = "https://accounts.google.com/RotateCookies";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GvCookieRotator> _logger;

    public GvCookieRotator(HttpClient httpClient, ILogger<GvCookieRotator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CookieRotationResult> RotateAsync(GvCookieSet current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RotateCookiesUrl);

            // Best-effort body: the public references use a small JSON array "poke". The exact
            // value is not load-bearing for the refresh; Google rotates based on the cookies.
            request.Content = new StringContent("[000,\"-0000000000000000000\"]", Encoding.UTF8, "application/json");

            // The long-lived + current rotating cookies must ride along so Google can rotate.
            request.Headers.TryAddWithoutValidation("Cookie", current.ToCookieHeader());
            request.Headers.TryAddWithoutValidation("Origin", "https://voice.google.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://voice.google.com/");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogWarning("RotateCookies returned {Status} — falling back", (int)response.StatusCode);
#pragma warning restore CA1848, CA1873
                return CookieRotationResult.NotRotated;
            }

            // Parse fresh PSIDTS values out of the Set-Cookie response headers.
            string? psidts1 = null;
            string? psidts3 = null;
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    psidts1 ??= ExtractCookieValue(sc, "__Secure-1PSIDTS");
                    psidts3 ??= ExtractCookieValue(sc, "__Secure-3PSIDTS");
                }
            }

            if (psidts1 is null && psidts3 is null)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogWarning("RotateCookies succeeded but returned no PSIDTS Set-Cookie — falling back");
#pragma warning restore CA1848, CA1873
                return CookieRotationResult.NotRotated;
            }

#pragma warning disable CA1848, CA1873
            _logger.LogInformation("RotateCookies refreshed PSIDTS (1P={Has1}, 3P={Has3})",
                psidts1 is not null, psidts3 is not null);
#pragma warning restore CA1848, CA1873

            return new CookieRotationResult(true, psidts1, psidts3);
        }
#pragma warning disable CA1031 // Best-effort: any failure -> fall back, never throw out.
        catch (Exception ex)
#pragma warning restore CA1031
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "RotateCookies failed — falling back to CDP/operator refresh");
#pragma warning restore CA1848, CA1873
            return CookieRotationResult.NotRotated;
        }
    }

    /// <summary>Extract "name=value" from a single Set-Cookie header line.</summary>
    internal static string? ExtractCookieValue(string setCookieHeader, string name)
    {
        // name=value;... at the start of the Set-Cookie value.
        var match = Regex.Match(setCookieHeader, $@"(^|;\s*){Regex.Escape(name)}=([^;]*)");
        return match.Success ? match.Groups[2].Value : null;
    }
}
