using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

/// <summary>
/// A Chrome that ANSWERS on CDP but is signed out of Google is not "unreachable".
/// </summary>
/// <remarks>
/// ⛔ MEASURED ON THE BOX, 2026-09-25. During an attended sign-in the bridge Chrome's only page sat on
/// <c>https://accounts.google.com/v3/signin/challenge/pwd?...</c>. That URL carries
/// <c>continue=https%3A%2F%2Fvoice.google.com…</c>, which CONTAINS "voice.google.com", so the extractor
/// matched it as the Voice tab, pulled its cookies, and returned <c>MissingRequiredCookies</c>. Every
/// non-success extraction status was then recorded as <c>Unreachable</c>, the ladder logged
/// "CHROME WAS UNREACHABLE … confirm Chrome is running", and the alarm posted <c>browser_unreachable</c>,
/// sending the owner to restart Chrome, which was running and fine, when the fix was to sign in.
/// The other known signed-out shape is a Chrome parked on the Workspace Voice landing page, which
/// matches no tab at all (<c>NoMatchingTab</c>).
/// </remarks>
public class GVApiAdapterSignedOutTests
{
    private sealed class FakeCdpExtractor(CdpExtractionResult result) : ICdpCookieExtractor
    {
        public Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private static GVApiAdapter AdapterWith(ICdpCookieExtractor extractor, ILogger<GVApiAdapter>? logger = null)
    {
        var adapter = GVApiAdapterRecoveryTests.CreateAdapter(
            rotator: new GVApiAdapterRecoveryTests.FakeCookieRotator(
                _ => Task.FromResult(CookieRotationResult.NotRotated)),   // rung 1 fails
            logger: logger);
        GVApiAdapterRecoveryTests.SetField(adapter, "_cookieSet", GVApiAdapterRecoveryTests.NewCookies());
        GVApiAdapterRecoveryTests.SetAvailable(adapter, true);
        // A store at a path that does not exist: rung 2 fails, and rung 3 is allowed to run.
        var missing = Path.Combine(
            Path.GetTempPath(), "gv-signedout-tests", Guid.NewGuid().ToString("n") + ".missing.enc");
        GVApiAdapterRecoveryTests.SetField(
            adapter, "_cookieStore", new GvCookieStore(missing, Convert.ToBase64String(new byte[32])));
        adapter.SetCookieExtractor(extractor);
        return adapter;
    }

    private static async Task<GVApiAdapter> RefreshWith(ICdpCookieExtractor extractor)
    {
        var adapter = AdapterWith(extractor);
        await (Task<bool>)GVApiAdapterRecoveryTests.Invoke(adapter, "TryCdpRefreshAsync")!;
        return adapter;
    }

    /// <summary>The real extractor, fed a CDP <c>/json</c> tab list and nothing else.</summary>
    private static CdpCookieExtractor ExtractorSeeingTabs(params string[] urls)
        => ExtractorSeeingJson(JsonSerializer.Serialize(urls.Select(u => new
        {
            url = u,
            webSocketDebuggerUrl = "ws://localhost:9224/devtools/page/abc",
        })));

    private static CdpCookieExtractor ExtractorSeeingJson(string tabsJson)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(tabsJson, Encoding.UTF8, "application/json"),
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));
        return new CdpCookieExtractor(factory.Object, NullLogger<CdpCookieExtractor>.Instance);
    }

    [Theory]
    // The 2026-09-25 shape: the sign-in page matched as the Voice tab, and its jar has no SID/SAPISID.
    [InlineData(CdpExtractionStatus.MissingRequiredCookies)]
    // Chrome answered and had no Google cookies at all for the Voice domains.
    [InlineData(CdpExtractionStatus.NoCookies)]
    public async Task ChromeAnsweredWithoutAGoogleSession_IsSignedOut_NotUnreachable(CdpExtractionStatus status)
    {
        var adapter = await RefreshWith(new FakeCdpExtractor(CdpExtractionResult.Fail(status, "no session")));

        Assert.Equal("SignedOut", adapter.BrowserRefreshOutcomeName);
        // ⛔ The boolean keeps its exact meaning: "Chrome handed us cookies and GOOGLE rejected them".
        // Google never saw these, so it stays false. Consumers read the string for this state.
        Assert.False(adapter.BrowserSessionStale);
    }

    [Theory]
    // The known signed-out landing: a Chrome parked here matches no voice.google.com tab at all.
    [InlineData("https://workspace.google.com/products/voice/")]
    // A sign-in page whose URL does NOT happen to carry voice.google.com in its continue parameter.
    [InlineData("https://accounts.google.com/v3/signin/identifier?flowName=GlifWebSignIn")]
    public async Task NoVoiceTab_ButChromeIsOnASignedOutPage_IsSignedOut(string url)
    {
        var adapter = await RefreshWith(ExtractorSeeingTabs(url));

        Assert.Equal("SignedOut", adapter.BrowserRefreshOutcomeName);
        Assert.False(adapter.BrowserSessionStale);
    }

    [Fact]
    public async Task NoVoiceTab_OnAnUnrelatedPage_IsNotClaimedSignedOut()
    {
        // Only the two known signed-out pages earn the label. A Chrome on some other page has told us
        // nothing about the Google login, and "signed out" would be an unearned claim.
        var adapter = await RefreshWith(ExtractorSeeingTabs("https://www.google.com/search?q=hello"));

        Assert.Equal("Unreachable", adapter.BrowserRefreshOutcomeName);   // exact: NotEqual would also pass if rung 3 never ran
    }

    [Theory]
    // Look-alike hosts must not match: the check is on the parsed host, not on a substring.
    [InlineData("https://accounts.google.com.evil.example/signin")]
    [InlineData("https://example.com/?next=https://workspace.google.com/products/voice/")]
    [InlineData("https://workspace.google.com/products/gmail/")]
    public async Task NoVoiceTab_LookAlikeUrls_AreNotClaimedSignedOut(string url)
    {
        var adapter = await RefreshWith(ExtractorSeeingTabs(url));

        Assert.Equal("Unreachable", adapter.BrowserRefreshOutcomeName);   // exact: NotEqual would also pass if rung 3 never ran
    }

    [Fact]
    public async Task NoVoiceTab_OnlyANonPageTargetOnTheSignInHost_IsNotClaimedSignedOut()
    {
        // /json lists iframes and service workers too. An accounts.google.com service worker says
        // nothing about what the visible page shows, so only PAGE targets may earn the label.
        var adapter = await RefreshWith(ExtractorSeeingJson("""
            [{"type":"page","url":"https://www.google.com/search?q=hello","webSocketDebuggerUrl":"ws://x/1"},
             {"type":"service_worker","url":"https://accounts.google.com/sw.js","webSocketDebuggerUrl":"ws://x/2"}]
            """));

        Assert.Equal("Unreachable", adapter.BrowserRefreshOutcomeName);
    }

    [Theory]
    // A CDP error reply, and a reply with no result.cookies, are protocol faults. Returning an empty
    // jar for them would read as NoCookies -> SignedOut: a fault reported as a sign-out.
    [InlineData("""{"id":1,"error":{"code":-32601,"message":"'Network.getCookies' wasn't found"}}""")]
    [InlineData("""{"id":1,"result":{}}""")]
    public void GetCookiesReply_ProtocolFault_Throws_NotAnEmptyJar(string reply)
        => Assert.Throws<InvalidOperationException>(() => CdpCookieExtractor.ParseGetCookiesReply(reply));

    [Fact]
    public void GetCookiesReply_GenuinelyEmptyJar_IsEmpty_NotAFault()
    {
        // The signed-out case proper: Chrome answered correctly and holds no cookies for Voice.
        var (header, count) = CdpCookieExtractor.ParseGetCookiesReply("""{"id":1,"result":{"cookies":[]}}""");
        Assert.Equal("", header);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ChromeTrulyUnreachable_IsStillUnreachable()
    {
        var adapter = await RefreshWith(new FakeCdpExtractor(
            CdpExtractionResult.Fail(CdpExtractionStatus.ChromeUnreachable, "connection refused")));

        Assert.Equal("Unreachable", adapter.BrowserRefreshOutcomeName);
    }

    [Fact]
    public async Task ExhaustedLadder_SignedOut_SendsTheOwnerToSignIn_NotToRestartChrome()
    {
        var log = new CapturingLogger<GVApiAdapter>();
        var adapter = AdapterWith(new FakeCdpExtractor(CdpExtractionResult.Fail(
            CdpExtractionStatus.MissingRequiredCookies,
            "Extracted cookies are missing required SAPISID and/or SID.")), log);

        Assert.False(await adapter.TryRecoverAuthAsync("test"));

        var errors = log.AtLevel(LogLevel.Error);
        Assert.Contains(errors, e => e.Message.Contains("the box's Chrome is SIGNED OUT"));
        Assert.Contains(errors, e => e.Message.Contains("ACTION: a human must sign in at voice.google.com"));
        Assert.Contains(errors, e => e.Message.Contains("restarting it will not help"));

        // ⛔ The 2026-09-25 regression: the wrong fix, stated with confidence.
        Assert.DoesNotContain(errors, e => e.Message.Contains("CHROME WAS UNREACHABLE"));
        Assert.DoesNotContain(errors, e => e.Message.Contains("confirm Chrome is running"));
        // …and not the Stale wording either: Google never saw these cookies.
        Assert.DoesNotContain(errors, e => e.Message.Contains("BROWSER SESSION IS STALE"));
    }
}
