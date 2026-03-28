using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;

namespace RotaryPhoneController.GVBridge.Tools;

public static class GvLoginTool
{
    private static readonly string[] RequiredCookieNames =
        ["SAPISID", "SID", "HSID", "SSID", "APISID", "__Secure-1PSID", "__Secure-3PSID",
         "__Secure-1PSIDTS", "__Secure-3PSIDTS", "__Secure-1PAPISID", "__Secure-3PAPISID", "SIDCC"];

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

        logger.LogInformation("Waiting for login to complete (up to 2 minutes)...");
        try
        {
            await page.WaitForURLAsync("**/voice.google.com/u/**",
                new() { Timeout = 300_000 }); // 5 minutes for login + 2FA
        }
        catch (TimeoutException)
        {
            logger.LogError("Login timed out after 2 minutes");
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

        var allCookies = await context.CookiesAsync(["https://voice.google.com", "https://google.com"]);
        var jar = new GvCookieJar();

        foreach (var cookie in allCookies)
        {
            switch (cookie.Name)
            {
                case "SAPISID": jar.Sapisid = cookie.Value; break;
                case "SID": jar.Sid = cookie.Value; break;
                case "HSID": jar.Hsid = cookie.Value; break;
                case "SSID": jar.Ssid = cookie.Value; break;
                case "APISID": jar.Apisid = cookie.Value; break;
                case "__Secure-1PSID": jar.Secure1Psid = cookie.Value; break;
                case "__Secure-3PSID": jar.Secure3Psid = cookie.Value; break;
                case "__Secure-1PSIDTS": jar.Secure1Psidts = cookie.Value; break;
                case "__Secure-3PSIDTS": jar.Secure3Psidts = cookie.Value; break;
                case "__Secure-1PAPISID": jar.Secure1Papisid = cookie.Value; break;
                case "__Secure-3PAPISID": jar.Secure3Papisid = cookie.Value; break;
                case "SIDCC": jar.Sidcc = cookie.Value; break;
            }
        }

        if (!jar.IsComplete)
        {
            logger.LogError("Missing required cookies after login");
            return false;
        }

        logger.LogInformation("All 7 cookies extracted successfully");

        var store = new GvCookieStore(cookieFilePath, encryptionKey);
        await store.SaveAsync(jar);
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

        var handler = new GvHttpClientHandler(() => Task.FromResult(jar), new HttpClientHandler());
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

            var jar = new GvCookieJar();
            foreach (var c in rawCookies)
            {
                var name = c.GetProperty("name").GetString() ?? "";
                var value = c.GetProperty("value").GetString() ?? "";
                switch (name)
                {
                    case "SAPISID": jar.Sapisid = value; break;
                    case "SID": jar.Sid = value; break;
                    case "HSID": jar.Hsid = value; break;
                    case "SSID": jar.Ssid = value; break;
                    case "APISID": jar.Apisid = value; break;
                    case "__Secure-1PSID": jar.Secure1Psid = value; break;
                    case "__Secure-3PSID": jar.Secure3Psid = value; break;
                }
            }

            if (!jar.IsComplete)
            {
                logger.LogError("JSON is missing required cookies");
                return false;
            }

            var store = new GvCookieStore(cookieFilePath, encryptionKey);
            await store.SaveAsync(jar);
            logger.LogInformation("Cookies imported and saved to {Path}", cookieFilePath);

            var handler = new GvHttpClientHandler(() => Task.FromResult(jar), new HttpClientHandler());
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
