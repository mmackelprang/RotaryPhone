using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;

namespace RotaryPhoneController.GVBridge.Tools;

public static class GvLoginTool
{
    public static async Task<bool> LoginAndSaveAsync(
        string cookieFilePath, string encryptionKey,
        string gvApiBaseUrl, string gvApiKey,
        ILogger logger, CancellationToken ct = default)
    {
        logger.LogInformation("Installing Playwright browsers if needed...");
        Microsoft.Playwright.Program.Main(["install", "chromium"]);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = false,
            Args = ["--disable-blink-features=AutomationControlled"]
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        logger.LogInformation("Opening voice.google.com — please log in...");
        await page.GotoAsync("https://voice.google.com/");

        logger.LogInformation("Waiting for login to complete (up to 5 minutes)...");
        try
        {
            await page.WaitForURLAsync("**/voice.google.com/u/**",
                new() { Timeout = 300_000 }); // 5 minutes for login + 2FA
        }
        catch (TimeoutException)
        {
            logger.LogError("Login timed out after 5 minutes");
            return false;
        }

        await page.WaitForTimeoutAsync(3000);

        // Extract the GV API key from the page source (embedded in JS)
        string? apiKey = null;
        try
        {
            apiKey = await page.EvaluateAsync<string?>(
                "(() => { const m = document.documentElement.innerHTML.match(/\"AIzaSy[A-Za-z0-9_-]{33}\"/); return m ? m[0].replace(/\"/g, '') : null; })()");
            if (apiKey != null)
                logger.LogInformation("Extracted GV API key: {Key}", apiKey[..12] + "...");
            else
                logger.LogWarning("Could not extract GV API key from page — you may need to set it manually in appsettings.json");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract API key from page");
        }

        // Extract all cookies as a raw header (captures SIDCC, NID, and all others Google needs)
        var allCookies = await context.CookiesAsync([
            "https://voice.google.com",
            "https://clients6.google.com",
            "https://www.google.com",
        ]);

        string GetCookie(string name) =>
            allCookies.FirstOrDefault(c => c.Name == name)?.Value ?? string.Empty;

        var sapisid = GetCookie("SAPISID");
        if (string.IsNullOrEmpty(sapisid))
        {
            logger.LogError("Missing SAPISID cookie after login");
            return false;
        }

        logger.LogInformation("Extracted {Count} cookies — SAPISID present", allCookies.Count);

        var cookieSet = new GvCookieSet
        {
            Sapisid = sapisid,
            Sid = GetCookie("SID"),
            Hsid = GetCookie("HSID"),
            Ssid = GetCookie("SSID"),
            Apisid = GetCookie("APISID"),
            Secure1Psid = GetCookie("__Secure-1PSID"),
            Secure3Psid = GetCookie("__Secure-3PSID"),
            RawCookieHeader = string.Join("; ", allCookies.Select(c => $"{c.Name}={c.Value}")),
        };

        var store = new GvCookieStore(cookieFilePath, encryptionKey);
        await store.SaveAsync(cookieSet);
        logger.LogInformation("Cookies saved to {Path}", cookieFilePath);

        // Save API key if extracted
        if (apiKey != null)
        {
            var keyPath = Path.Combine(Path.GetDirectoryName(cookieFilePath) ?? "data", "gv-api-key.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            await File.WriteAllTextAsync(keyPath, apiKey, ct);
            logger.LogInformation("API key saved to {Path}", keyPath);
        }

        // Verify with health check (use extracted key if available)
        var effectiveKey = apiKey ?? gvApiKey;
        if (string.IsNullOrEmpty(effectiveKey))
        {
            logger.LogWarning("No API key available — skipping health check. Set GvApiKey in appsettings.json.");
            return true; // cookies saved successfully even without health check
        }

        var handler = new GvHttpClientHandler(() => Task.FromResult(cookieSet), new HttpClientHandler());
        using var http = new HttpClient(handler);
        var account = new GvAccountClient(http, gvApiBaseUrl, effectiveKey,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GvAccountClient>.Instance);
        var healthy = await account.IsHealthyAsync(ct);
        logger.LogInformation(healthy ? "Health check passed — cookies are valid" : "Health check failed");
        return healthy;
    }

    public static async Task<bool> ImportFromJsonAsync(
        string json, string cookieFilePath, string encryptionKey,
        string gvApiBaseUrl, string gvApiKey,
        ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var rawCookies = JsonSerializer.Deserialize<JsonElement[]>(json);
            if (rawCookies == null) { logger.LogError("Invalid JSON"); return false; }

            string GetValue(string name) =>
                rawCookies
                    .Where(c => c.TryGetProperty("name", out var n) && n.GetString() == name)
                    .Select(c => c.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "")
                    .FirstOrDefault() ?? "";

            var sapisid = GetValue("SAPISID");
            if (string.IsNullOrEmpty(sapisid))
            {
                logger.LogError("JSON is missing SAPISID cookie");
                return false;
            }

            // Build raw header from all cookies in the JSON
            var rawHeader = string.Join("; ", rawCookies
                .Where(c => c.TryGetProperty("name", out _) && c.TryGetProperty("value", out _))
                .Select(c => $"{c.GetProperty("name").GetString()}={c.GetProperty("value").GetString()}"));

            var cookieSet = new GvCookieSet
            {
                Sapisid = sapisid,
                Sid = GetValue("SID"),
                Hsid = GetValue("HSID"),
                Ssid = GetValue("SSID"),
                Apisid = GetValue("APISID"),
                Secure1Psid = GetValue("__Secure-1PSID"),
                Secure3Psid = GetValue("__Secure-3PSID"),
                RawCookieHeader = rawHeader,
            };

            var store = new GvCookieStore(cookieFilePath, encryptionKey);
            await store.SaveAsync(cookieSet);
            logger.LogInformation("Cookies imported and saved to {Path}", cookieFilePath);

            var handler = new GvHttpClientHandler(() => Task.FromResult(cookieSet), new HttpClientHandler());
            using var http = new HttpClient(handler);
            var account = new GvAccountClient(http, gvApiBaseUrl, gvApiKey,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GvAccountClient>.Instance);
            var healthy = await account.IsHealthyAsync(ct);
            logger.LogInformation(healthy ? "Health check passed" : "Health check failed");
            return healthy;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to import cookies");
            return false;
        }
    }
}
