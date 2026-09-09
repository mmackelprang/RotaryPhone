using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Routing;

/// <summary>
/// Pins the two-fallback arrangement at the bottom of Program.cs: an unmatched <c>/api/*</c> path
/// must 404 as JSON, and a SPA deep link must still be served the SPA shell.
///
/// Background: a bare <c>MapFallbackToFile("index.html")</c> answered every unmatched <c>/api/*</c>
/// request with HTTP 200 and text/html. A JSON caller probing a wrong path got a success code and
/// concluded the route existed — a day of cross-repo debugging, twice, on two different sessions.
/// See docs/prompts/2026-09-09-radioconsole-ui11-was-never-ours.md.
///
/// ⚠ SCOPE — read before trusting this file. It builds its OWN minimal WebApplication and registers
/// a COPY of the two fallback patterns from Program.cs. It does NOT execute Program.cs, which is
/// ~460 lines that wire Bluetooth/SIP/Chrome-GV/SQLite and fail fast on config validation; booting
/// it in-process was considered and deliberately declined. So this pins THE PATTERN PAIR, not
/// Program.cs's registration of it. An edit to Program.cs that changes or drops a pattern can drift
/// from this copy with these tests still green. The guard against that is <see cref="MapFallbacks"/>
/// being the single place the copy lives, plus the pointer comment in Program.cs.
///
/// What it does genuinely prove: that these two patterns, registered in this order on a real
/// endpoint-routing pipeline, resolve the way we claim in both directions — which is the part that
/// was assumed rather than checked last time.
/// </summary>
public sealed class ApiFallbackRoutingTests : IAsyncLifetime
{
    /// <summary>Marker baked into the stand-in index.html, so "was the SPA shell served?" is checkable.</summary>
    private const string SpaShellMarker = "SPA-SHELL-SENTINEL";

    private string _contentRoot = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// The behaviour under test, copied verbatim from Program.cs (the "Fallback to React SPA" block).
    /// Keep these two registrations, and their order, in sync with Program.cs.
    /// </summary>
    private static void MapFallbacks(WebApplication app)
    {
        app.MapFallback("/api/{**rest}", (HttpContext ctx) => Results.Json(
            new { error = $"No API route matches {ctx.Request.Method} {ctx.Request.Path}" },
            statusCode: StatusCodes.Status404NotFound));

        app.MapFallbackToFile("index.html");
    }

    public async Task InitializeAsync()
    {
        // A throwaway content root holding just wwwroot/index.html, so MapFallbackToFile has a real
        // file to serve and the SPA direction is a genuine 200-with-content, not a stubbed assertion.
        _contentRoot = Directory.CreateTempSubdirectory("rp-api-fallback-").FullName;
        var webRoot = Directory.CreateDirectory(Path.Combine(_contentRoot, "wwwroot")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(webRoot, "index.html"),
            $"<!doctype html><html><head><title>{SpaShellMarker}</title></head><body></body></html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _contentRoot,
            WebRootPath = "wwwroot",
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.UseStaticFiles();

        // Stand-in for a real mapped API endpoint (Program.cs has these via MapControllers), so the
        // "the fallback does not shadow real routes" direction is testable.
        app.MapGet("/api/phone/status", () => Results.Json(new { ok = true }));

        MapFallbacks(app);

        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { /* temp dir; best effort */ }
    }

    // ---------------------------------------------------------------- direction 1: /api/* → JSON 404

    [Fact]
    public async Task UnmatchedApiPath_Is404_WithJsonContentTypeAndNonEmptyBody()
    {
        var res = await _client.GetAsync("/api/definitely-not-a-route");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);

        // The missing body is the part that actually cost a probe: an empty 404 tells the caller
        // nothing, so "non-empty" is a real requirement here, not incidental.
        Assert.False(string.IsNullOrWhiteSpace(body));
        Assert.DoesNotContain(SpaShellMarker, body);

        // Matches the { error = ... } shape used across the GVBridge/GVTrunk controllers.
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
    }

    [Theory]
    [InlineData("/api")]                                  // the bare prefix, no trailing segment
    [InlineData("/api/")]
    [InlineData("/api/definitely-not-a-route")]
    [InlineData("/api/gvbridge/sms/threads/nope/nope")]    // the deep shape of the original probe
    [InlineData("/api/foo.json")]                          // extension: SPA's {*path:nonfile} would miss it anyway
    public async Task UnmatchedApiPaths_Never_ServeTheSpaShell(string path)
    {
        var res = await _client.GetAsync(path);
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(SpaShellMarker, body);
    }

    // ------------------------------------------------------------- direction 2: SPA deep links live

    [Theory]
    [InlineData("/")]
    [InlineData("/settings/audio")]   // the deep link named in the work item
    [InlineData("/contacts")]
    [InlineData("/apifoo")]           // NOT under /api/: guards against a prefix (StartsWith) rewrite
    public async Task SpaDeepLink_StillServesTheShell(string path)
    {
        var res = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(SpaShellMarker, await res.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------- direction 3: real routes are not shadowed

    [Fact]
    public async Task MappedApiRoute_IsNotShadowedByTheApiFallback()
    {
        var res = await _client.GetAsync("/api/phone/status");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"ok\":true", await res.Content.ReadAsStringAsync());
    }
}
