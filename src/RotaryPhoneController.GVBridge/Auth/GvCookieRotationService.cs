using System.Text;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// Periodically calls accounts.google.com/RotateCookies to refresh
/// short-lived session cookies (SIDCC, PSIDCC, and PSIDTS when stale).
/// Without rotation, PSIDTS expires in ~10-30 minutes and all API calls fail.
/// </summary>
public class GvCookieRotationService : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly GvCookieStore _store;
    private readonly ILogger<GvCookieRotationService> _logger;
    private readonly TimeSpan _interval;

    private Timer? _timer;
    private GvCookieSet? _cookieSet;
    private DateTime _lastRotation = DateTime.MinValue;

    public GvCookieRotationService(
        GvCookieStore store,
        ILogger<GvCookieRotationService> logger,
        TimeSpan? interval = null)
    {
        _store = store;
        _logger = logger;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>
    /// Start periodic rotation. Loads the cookie set from disk.
    /// Returns the initial cookie set for use by API clients.
    /// </summary>
    public async Task<GvCookieSet?> StartAsync(CancellationToken ct = default)
    {
        _cookieSet = await _store.LoadAsync();
        if (_cookieSet == null)
        {
            _logger.LogError("Cannot start rotation — no valid cookies on disk");
            return null;
        }

        // Do an immediate rotation to get fresh short-lived cookies
        await RotateAsync();

        _timer = new Timer(
            async _ => await RotateAsync(),
            null,
            _interval,
            _interval);

        _logger.LogInformation("Cookie rotation started (every {Interval})", _interval);
        return _cookieSet;
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _logger.LogInformation("Cookie rotation stopped");
    }

    /// <summary>Get the current (freshest) cookie set.</summary>
    public GvCookieSet? CurrentCookieSet => _cookieSet;

    private async Task RotateAsync()
    {
        if (_cookieSet == null) return;

        // Rate limit: no more than once per 60 seconds
        var elapsed = DateTime.UtcNow - _lastRotation;
        if (elapsed < MinInterval)
        {
            _logger.LogDebug("Skipping rotation — last was {Ago}s ago", elapsed.TotalSeconds);
            return;
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                UseCookies = false // We manage cookies manually
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            // Use the raw header if available; otherwise fall back to individual fields
            var cookieHeader = _cookieSet.ToCookieHeader();

            var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.google.com/RotateCookies")
            {
                Content = new StringContent("[000,\"-0000000000000000000\"]", Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation("Origin", "https://accounts.google.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://accounts.google.com/RotateCookiesPage");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");

            var response = await http.SendAsync(request);
            _lastRotation = DateTime.UtcNow;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("RotateCookies returned {Status}", response.StatusCode);
                return;
            }

            // Extract refreshed cookies from Set-Cookie headers and patch the raw header
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                var updatedHeader = _cookieSet.RawCookieHeader ?? cookieHeader;
                var updated = false;

                foreach (var sc in setCookies)
                {
                    var (name, value) = ParseSetCookie(sc);
                    if (string.IsNullOrEmpty(name)) continue;

                    switch (name)
                    {
                        case "SIDCC":
                        case "__Secure-1PSIDTS":
                        case "__Secure-3PSIDTS":
                        case "__Secure-1PSIDCC":
                            updatedHeader = UpdateCookieInHeader(updatedHeader, name, value);
                            updated = true;
                            if (name == "__Secure-1PSIDTS")
                                _logger.LogInformation("PSIDTS rotated");
                            break;
                    }
                }

                if (updated)
                {
                    // Rebuild the GvCookieSet with the updated raw header (init-only properties)
                    _cookieSet = new GvCookieSet
                    {
                        Sapisid = _cookieSet.Sapisid,
                        Sid = _cookieSet.Sid,
                        Hsid = _cookieSet.Hsid,
                        Ssid = _cookieSet.Ssid,
                        Apisid = _cookieSet.Apisid,
                        Secure1Psid = _cookieSet.Secure1Psid,
                        Secure3Psid = _cookieSet.Secure3Psid,
                        RawCookieHeader = updatedHeader,
                    };

                    await _store.SaveAsync(_cookieSet);
                    _logger.LogDebug("Cookies rotated and saved to disk");
                }
                else
                {
                    _logger.LogDebug("RotateCookies returned no new cookies to update");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cookie rotation failed");
        }
    }

    /// <summary>
    /// Replace an existing cookie value in a raw Cookie header string.
    /// If the cookie is not present, appends it.
    /// </summary>
    private static string UpdateCookieInHeader(string header, string name, string value)
    {
        // Try to find "name=..." in the header (handles start, middle, end positions)
        var prefix = name + "=";
        var idx = header.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Not found — append
            return header.Length > 0 ? $"{header}; {name}={value}" : $"{name}={value}";
        }

        var valueStart = idx + prefix.Length;
        var valueEnd = header.IndexOf(';', valueStart);
        var oldValue = valueEnd < 0 ? header[valueStart..] : header[valueStart..valueEnd];

        return header.Replace($"{name}={oldValue}", $"{name}={value}", StringComparison.Ordinal);
    }

    private static (string name, string value) ParseSetCookie(string header)
    {
        // Format: "NAME=VALUE; path=/; domain=.google.com; ..."
        var nameEnd = header.IndexOf('=');
        if (nameEnd < 0) return ("", "");
        var name = header[..nameEnd];
        var valueEnd = header.IndexOf(';', nameEnd);
        var value = valueEnd > 0 ? header[(nameEnd + 1)..valueEnd] : header[(nameEnd + 1)..];
        return (name, value);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
