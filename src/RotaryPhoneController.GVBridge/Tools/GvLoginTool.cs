using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;

namespace RotaryPhoneController.GVBridge.Tools;

public static class GvLoginTool
{
    private static readonly string[] RequiredCookieNames =
        ["SAPISID", "SID", "HSID", "SSID", "APISID", "__Secure-1PSID", "__Secure-3PSID"];

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
                new() { Timeout = 120_000 });
        }
        catch (TimeoutException)
        {
            logger.LogError("Login timed out after 2 minutes");
            return false;
        }

        await page.WaitForTimeoutAsync(3000);

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

        // Verify with health check
        var handler = new GvHttpClientHandler(() => Task.FromResult(jar), new HttpClientHandler());
        using var http = new HttpClient(handler);
        var account = new GvAccountClient(http, gvApiBaseUrl, gvApiKey,
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
