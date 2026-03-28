using System.Net;
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
    private GvCookieJar? _jar;
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
    /// Start periodic rotation. Loads the cookie jar from disk.
    /// Returns the initial cookie jar for use by API clients.
    /// </summary>
    public async Task<GvCookieJar?> StartAsync(CancellationToken ct = default)
    {
        _jar = await _store.LoadAsync();
        if (_jar == null || !_jar.IsComplete)
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
        return _jar;
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _logger.LogInformation("Cookie rotation stopped");
    }

    /// <summary>Get the current (freshest) cookie jar.</summary>
    public GvCookieJar? CurrentJar => _jar;

    private async Task RotateAsync()
    {
        if (_jar == null) return;

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

            var cookieHeader = $"__Secure-1PSID={_jar.Secure1Psid}; " +
                               $"__Secure-3PSID={_jar.Secure3Psid}; " +
                               $"__Secure-1PSIDTS={_jar.Secure1Psidts}; " +
                               $"__Secure-3PSIDTS={_jar.Secure3Psidts}; " +
                               $"SID={_jar.Sid}; HSID={_jar.Hsid}; SSID={_jar.Ssid}; " +
                               $"SIDCC={_jar.Sidcc}";

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

            // Extract refreshed cookies from Set-Cookie headers
            var updated = false;
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    var (name, value) = ParseSetCookie(sc);
                    switch (name)
                    {
                        case "SIDCC":
                            _jar.Sidcc = value; updated = true; break;
                        case "__Secure-1PSIDTS":
                            _jar.Secure1Psidts = value; updated = true;
                            _logger.LogInformation("PSIDTS rotated");
                            break;
                        case "__Secure-3PSIDTS":
                            _jar.Secure3Psidts = value; updated = true; break;
                        case "__Secure-1PSIDCC":
                            // Track but don't store separately — SIDCC is enough
                            break;
                    }
                }
            }

            if (updated)
            {
                await _store.SaveAsync(_jar);
                _logger.LogDebug("Cookies rotated and saved to disk");
            }
            else
            {
                _logger.LogDebug("RotateCookies returned no new cookies to update");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cookie rotation failed");
        }
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
