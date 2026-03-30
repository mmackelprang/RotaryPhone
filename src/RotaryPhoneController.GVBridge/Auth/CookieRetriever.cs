using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Playwright;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// Retrieves Google Voice cookies via Chrome DevTools Protocol.
/// Launches Chrome with remote debugging if needed, extracts all cookies,
/// encrypts them to disk, and verifies with an API health check.
/// </summary>
public static class CookieRetriever
{
    private const int DebugPort = 9222;

    /// <summary>
    /// Extract cookies from Chrome and save encrypted to disk.
    /// If Chrome isn't running with debug port, launches it automatically.
    /// On first run, user must log in manually in the Chrome window.
    /// </summary>
    public static async Task<bool> RetrieveAndSaveAsync(
        string cookiePath, string keyPath, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };

        log("Starting cookie retrieval via Chrome CDP...");

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser? browser = null;
        var launched = false;

        // Try connecting to existing Chrome
        try
        {
            browser = await playwright.Chromium.ConnectOverCDPAsync(
                $"http://127.0.0.1:{DebugPort}").ConfigureAwait(false);
            log($"Connected to existing Chrome on port {DebugPort}.");
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            // Need to launch Chrome
        }

        if (browser is null)
        {
            log("Launching Chrome with remote debugging...");

            // Kill existing Chrome/Chromium to avoid port conflict
            foreach (var name in new[] { "chrome", "chromium", "chromium-browser" })
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try { proc.Kill(); }
#pragma warning disable CA1031
                    catch { /* best effort */ }
#pragma warning restore CA1031
                }
            }

            await Task.Delay(2000, ct).ConfigureAwait(false);

            var chromePath = FindChromePath();
            var debugProfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RotaryPhone", "chrome-debug-profile");
            Directory.CreateDirectory(debugProfilePath);

            var chromeProcess = Process.Start(new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = $"--remote-debugging-port={DebugPort} --user-data-dir=\"{debugProfilePath}\" --no-first-run --no-default-browser-check",
                UseShellExecute = false,
            });

            if (chromeProcess is null)
            {
                log("ERROR: Failed to start Chrome.");
                return false;
            }

            launched = true;

            // Wait for debug port
            for (int i = 0; i < 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                try
                {
                    browser = await playwright.Chromium.ConnectOverCDPAsync(
                        $"http://127.0.0.1:{DebugPort}").ConfigureAwait(false);
                    break;
                }
#pragma warning disable CA1031
                catch { /* not ready yet */ }
#pragma warning restore CA1031
            }

            if (browser is null)
            {
                log("ERROR: Could not connect to Chrome after 30 seconds.");
                return false;
            }

            log("Connected to Chrome.");
        }

        // Get browser context and page
        var contexts = browser.Contexts;
        IBrowserContext context;
        IPage page;

        if (contexts.Count > 0)
        {
            context = contexts[0];
            page = context.Pages.FirstOrDefault(p =>
                p.Url.Contains("voice.google.com", StringComparison.OrdinalIgnoreCase))
                ?? context.Pages[0];
        }
        else
        {
            context = await browser.NewContextAsync().ConfigureAwait(false);
            page = await context.NewPageAsync().ConfigureAwait(false);
        }

        // Navigate to voice.google.com if needed
        if (!page.Url.Contains("voice.google.com", StringComparison.OrdinalIgnoreCase))
        {
            log("Navigating to voice.google.com...");
            await page.GotoAsync("https://voice.google.com", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 60_000,
            }).ConfigureAwait(false);
        }

        // Check login
        if (!page.Url.Contains("voice.google.com", StringComparison.OrdinalIgnoreCase) ||
            page.Url.Contains("workspace.google.com", StringComparison.OrdinalIgnoreCase))
        {
            if (launched)
            {
                log("Please log in to Google Voice in the Chrome window, then re-run.");
            }
            else
            {
                log("Not logged in to Google Voice. Please log in via Chrome.");
            }
            return false;
        }

        log($"Logged in. URL: {page.Url}");

        // Extract cookies
        var allCookies = await context.CookiesAsync([
            "https://voice.google.com",
            "https://clients6.google.com",
            "https://www.google.com",
        ]).ConfigureAwait(false);

        log($"Extracted {allCookies.Count} cookies.");

        var cookieHeader = string.Join("; ", allCookies.Select(c => $"{c.Name}={c.Value}"));

        string GetCookie(string name) =>
            allCookies.FirstOrDefault(c => c.Name == name)?.Value ?? string.Empty;

        var sapisid = GetCookie("SAPISID");
        if (string.IsNullOrEmpty(sapisid))
        {
            log("ERROR: SAPISID cookie not found. Make sure you're logged in.");
            return false;
        }

        log($"SAPISID present ({sapisid.Length} chars), SID={(!string.IsNullOrEmpty(GetCookie("SID")) ? "yes" : "MISSING")}");

        // Encrypt and save
        var cookieSet = new GvCookieSet
        {
            Sapisid = sapisid,
            Sid = GetCookie("SID"),
            Hsid = GetCookie("HSID"),
            Ssid = GetCookie("SSID"),
            Apisid = GetCookie("APISID"),
            Secure1Psid = GetCookie("__Secure-1PSID"),
            Secure3Psid = GetCookie("__Secure-3PSID"),
            RawCookieHeader = cookieHeader,
        };

        var json = cookieSet.Serialize();
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var ciphertext = TokenEncryption.Encrypt(json, key);

        // Ensure parent directories exist
        var cookieDir = Path.GetDirectoryName(cookiePath);
        if (!string.IsNullOrEmpty(cookieDir))
            Directory.CreateDirectory(cookieDir);
        var keyDir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(keyDir))
            Directory.CreateDirectory(keyDir);

        await File.WriteAllBytesAsync(cookiePath, ciphertext, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(keyPath, key, ct).ConfigureAwait(false);

        log($"Saved cookies.enc ({ciphertext.Length} bytes) and key.bin.");

        // Verify with account/get
        var verified = await VerifyAsync(sapisid, cookieHeader, log).ConfigureAwait(false);
        return verified;
    }

    private static async Task<bool> VerifyAsync(string sapisid, string cookieHeader, Action<string> log)
    {
        using var http = new HttpClient();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var input = $"{ts} {sapisid} https://voice.google.com";
#pragma warning disable CA5350
        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(input)));
#pragma warning restore CA5350
        var auth = $"SAPISIDHASH {ts}_{hash} SAPISID1PHASH {ts}_{hash} SAPISID3PHASH {ts}_{hash}";

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://clients6.google.com/voice/v1/voiceclient/account/get?alt=protojson&key=AIzaSyDTYc1N4xiODyrQYK0Kl6g_y279LjYkrBg");
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        req.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        req.Headers.TryAddWithoutValidation("Origin", "https://voice.google.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://voice.google.com/");
        req.Content = new StringContent("[null,1]", Encoding.UTF8, "application/json+protobuf");

        var resp = await http.SendAsync(req).ConfigureAwait(false);
        log($"Verification: {(int)resp.StatusCode} {resp.StatusCode}");
        return resp.IsSuccessStatusCode;
    }

    private static string FindChromePath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new[]
        {
            // Chrome (Windows)
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            // Chromium (Windows)
            Path.Combine(programFiles, "Chromium", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Chromium", "Application", "chrome.exe"),
            // Chrome (Linux)
            "/usr/bin/google-chrome",
            "/usr/bin/google-chrome-stable",
            // Chromium (Linux)
            "/usr/bin/chromium-browser",
            "/usr/bin/chromium",
            "/snap/bin/chromium",
            // Chrome (macOS)
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            // Chromium (macOS)
            "/Applications/Chromium.app/Contents/MacOS/Chromium",
        };
        return candidates.FirstOrDefault(File.Exists) ?? "chrome";
    }
}
