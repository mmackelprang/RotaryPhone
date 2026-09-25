using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Tests.Adapters;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

/// <summary>
/// A FAILED <c>POST cookies/refresh-from-browser</c> must record why it failed in
/// <c>browserRefreshOutcome</c>, exactly as recovery rung 3 does.
/// </summary>
/// <remarks>
/// ⛔ MEASURED ON THE BOX, 2026-09-25 18:28–18:30 EDT, after PR #90 was deployed. The bridge Chrome was
/// deliberately signed out (its only page target was <c>https://workspace.google.com/products/voice/</c>).
/// The endpoint answered 404 "No tab found with URL containing voice.google.com" — and
/// <c>GET /status</c> kept reporting <c>browserRefreshOutcome: "Succeeded"</c> with the old validation
/// time. The outcome was only ever written by recovery rung 3 (which runs only when the phone's OWN
/// cookies fail) and by the adopt path (which runs only once cookies were extracted). So a browser-only
/// sign-out never surfaced: the auto-relogin actuator and the alarm's <c>browser_signed_out</c> both key
/// on this field, and the 20-minute cron that POSTs this endpoint could not move it.
/// <para>
/// Every test starts from <c>Succeeded</c>, which is the measured state and also means an assertion of
/// <c>Unreachable</c> cannot pass merely because nothing was written (the default is <c>NotAttempted</c>).
/// </para>
/// </remarks>
public class RefreshFromBrowserOutcomeTests
{
  private const int ConfiguredCdpPort = 9224;

  private sealed class FakeCdpExtractor(params CdpExtractionResult[] results) : ICdpCookieExtractor
  {
    private int _next;
    public int? LastPort { get; private set; }

    /// <summary>Returns the results in order, repeating the last one.</summary>
    public Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
    {
      LastPort = cdpPort;
      return Task.FromResult(results[Math.Min(_next++, results.Length - 1)]);
    }
  }

  private static readonly GvCookieSet Extracted = new()
  {
    Sapisid = "SAPISID-EXTRACTED", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
  };

  private static CdpExtractionResult Ok() => new(CdpExtractionStatus.Success, Extracted, 20, null);
  private static CdpExtractionResult Gone() =>
    CdpExtractionResult.Fail(CdpExtractionStatus.ChromeUnreachable, "Chrome not reachable");
  private static CdpExtractionResult NoSession() =>
    CdpExtractionResult.Fail(CdpExtractionStatus.MissingRequiredCookies, "no session");

  private sealed record Rig(
    GVBridgeController Controller,
    GVApiAdapter Adapter,
    Mock<IGvCookieManager> CookieManager,
    Mock<ICallAdapterRegistry> Registry);

  private static Rig Build(
    ICdpCookieExtractor extractor,
    GVApiAdapter.BrowserRefreshOutcome startingOutcome = GVApiAdapter.BrowserRefreshOutcome.Succeeded,
    int configuredCdpPort = ConfiguredCdpPort)
  {
    var registry = new Mock<ICallAdapterRegistry>();
    registry.Setup(r => r.ActiveMode).Returns(CallAdapterMode.GVApi);

    var config = Options.Create(new GVBridgeConfig
    {
      GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
      GvApiKey = "test",
      CookieFilePath = "test.enc",
      CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
      ChromeCdpPort = configuredCdpPort,
    });

    var adapter = new GVApiAdapter(config, NullLogger<GVApiAdapter>.Instance, NullLoggerFactory.Instance);
    GVApiAdapterRecoveryTests.SetField(adapter, "_lastBrowserRefreshOutcome", startingOutcome);
    GVApiAdapterRecoveryTests.SetAvailable(adapter, true);

    var cookieManager = new Mock<IGvCookieManager>();
    cookieManager.Setup(m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(SetCookiesOutcome.Adopted);

    var controller = new GVBridgeController(
      registry.Object, adapter, cookieManager.Object, config, extractor,
      NullLogger<GVBridgeController>.Instance);

    return new Rig(controller, adapter, cookieManager, registry);
  }

  /// <summary>The real extractor, fed a CDP <c>/json</c> tab list and nothing else.</summary>
  private static CdpCookieExtractor ExtractorSeeingTabs(params string[] urls)
  {
    var tabsJson = JsonSerializer.Serialize(urls.Select(u => new
    {
      type = "page",
      url = u,
      webSocketDebuggerUrl = "ws://localhost:9224/devtools/page/abc",
    }));
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

  private static void AssertNoRecoverySideEffects(Rig rig)
  {
    // A failed extraction handed nothing to the cookie pipeline and must not re-activate anything:
    // the cron hits this every 20 minutes, and a failure must stay a pure status write.
    rig.CookieManager.Verify(
      m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()), Times.Never);
    rig.Registry.Verify(
      r => r.SwitchModeAsync(It.IsAny<CallAdapterMode>(), It.IsAny<CancellationToken>()), Times.Never);
    // Regression guard only (availability was forced true on an unactivated adapter): it catches the
    // record path ever growing a SetAvailable(false)/MarkUnavailable.
    Assert.True(rig.Adapter.IsAvailable);
  }

  [Fact]
  public async Task SignedOutChrome_OnTheWorkspaceVoiceLanding_RecordsSignedOut_AndKeeps404()
  {
    // The exact box shape of 2026-09-25 18:28 EDT.
    var rig = Build(ExtractorSeeingTabs("https://workspace.google.com/products/voice/"));

    var result = await rig.Controller.RefreshCookiesFromBrowser(null);

    var notFound = Assert.IsType<NotFoundObjectResult>(result);          // response shape unchanged
    Assert.Contains("No tab found", JsonSerializer.Serialize(notFound.Value));
    Assert.Equal("SignedOut", rig.Adapter.BrowserRefreshOutcomeName);
    Assert.False(rig.Adapter.BrowserSessionStale);                      // Google never saw these cookies
    AssertNoRecoverySideEffects(rig);
  }

  [Theory]
  [InlineData(CdpExtractionStatus.MissingRequiredCookies)]
  [InlineData(CdpExtractionStatus.NoCookies)]
  public async Task ChromeAnsweredWithoutAGoogleSession_RecordsSignedOut_AndKeeps400(CdpExtractionStatus status)
  {
    var rig = Build(new FakeCdpExtractor(CdpExtractionResult.Fail(status, "no session")));

    var result = await rig.Controller.RefreshCookiesFromBrowser(null);

    Assert.IsType<BadRequestObjectResult>(result);
    Assert.Equal("SignedOut", rig.Adapter.BrowserRefreshOutcomeName);
    AssertNoRecoverySideEffects(rig);
  }

  [Fact]
  public async Task NoVoiceTab_AndNoGooglePage_IsUnreachable_NotSignedOut()
  {
    // Chrome on an unrelated page tells us nothing about the login: the historical classification, not
    // an unearned "signed out". Unreachable from this periodic caller needs two in a row (see below).
    var rig = Build(ExtractorSeeingTabs("https://www.google.com/search?q=hello"));

    var first = await rig.Controller.RefreshCookiesFromBrowser(null);
    Assert.IsType<NotFoundObjectResult>(first);
    Assert.Equal("Succeeded", rig.Adapter.BrowserRefreshOutcomeName);   // one is not enough

    var second = await rig.Controller.RefreshCookiesFromBrowser(null);
    Assert.IsType<NotFoundObjectResult>(second);
    Assert.Equal("Unreachable", rig.Adapter.BrowserRefreshOutcomeName);
    AssertNoRecoverySideEffects(rig);
  }

  [Fact]
  public async Task ChromeGone_RecordsUnreachable_OnTheSecondConsecutiveFailure_AndKeeps503()
  {
    var rig = Build(new FakeCdpExtractor(Gone()));

    var first = await rig.Controller.RefreshCookiesFromBrowser(null);
    Assert.Equal(503, Assert.IsType<ObjectResult>(first).StatusCode);
    Assert.Equal("Succeeded", rig.Adapter.BrowserRefreshOutcomeName);

    var second = await rig.Controller.RefreshCookiesFromBrowser(null);
    Assert.Equal(503, Assert.IsType<ObjectResult>(second).StatusCode);
    Assert.Equal("Unreachable", rig.Adapter.BrowserRefreshOutcomeName);
    AssertNoRecoverySideEffects(rig);
  }

  [Theory]
  // Pre-merge review 2026-09-25: from the cron, ONE 10 s CDP timeout used to be enough to overwrite a TRUE
  // Stale/SignedOut with Unreachable for 20 minutes — paging browser_unreachable and switching the
  // auto-relogin actuator (which acts on Stale/SignedOut only) off.
  [InlineData("Stale")]
  [InlineData("SignedOut")]
  [InlineData("Succeeded")]
  public async Task OneTransientUnreachable_OverwritesNothing(string before)
  {
    var rig = Build(new FakeCdpExtractor(Gone()),
      startingOutcome: Enum.Parse<GVApiAdapter.BrowserRefreshOutcome>(before));

    await rig.Controller.RefreshCookiesFromBrowser(null);

    Assert.Equal(before, rig.Adapter.BrowserRefreshOutcomeName);
  }

  [Fact]
  public async Task TheUnreachableStreak_IsBrokenByASuccessfulExtraction()
  {
    // gone, reached, gone: never two IN A ROW, so never recorded.
    var rig = Build(new FakeCdpExtractor(Gone(), Ok(), Gone()));

    for (var i = 0; i < 3; i++) await rig.Controller.RefreshCookiesFromBrowser(null);

    Assert.Equal("Succeeded", rig.Adapter.BrowserRefreshOutcomeName);
  }

  [Fact]
  public async Task TheUnreachableStreak_IsBrokenByASignedOutObservation()
  {
    // gone, signed-out, gone: the sign-out is recorded at once and is not then erased by one fault.
    var rig = Build(new FakeCdpExtractor(Gone(), NoSession(), Gone()));

    for (var i = 0; i < 3; i++) await rig.Controller.RefreshCookiesFromBrowser(null);

    Assert.Equal("SignedOut", rig.Adapter.BrowserRefreshOutcomeName);
  }

  [Fact]
  public async Task AnEmptyBody_ProbesTheConfiguredPort_AndRecords()
  {
    // The cron posts "{}". RefreshFromBrowserRequest used to default CdpPort to a literal 9224, which
    // ignored GVBridgeConfig.ChromeCdpPort — and, with the bridge-Chrome guard, would have made this fix
    // go silently quiet the day the configured port moved.
    var extractor = new FakeCdpExtractor(NoSession());
    var rig = Build(extractor, configuredCdpPort: 9555);

    await rig.Controller.RefreshCookiesFromBrowser(new RefreshFromBrowserRequest());

    Assert.Equal(9555, extractor.LastPort);
    Assert.Equal("SignedOut", rig.Adapter.BrowserRefreshOutcomeName);
  }

  [Fact]
  public void TheCronsLiteralBody_DeserializesToNoPort_SoConfigWins()
  {
    // What the box actually sends: `-d "{}"` (/opt/rotary-phone/refresh-gv-cookies.sh). The previous
    // test builds the record in C#; this proves the JSON binding agrees.
    var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    Assert.Null(JsonSerializer.Deserialize<RefreshFromBrowserRequest>("{}", web)!.CdpPort);
    Assert.Equal(9224, JsonSerializer.Deserialize<RefreshFromBrowserRequest>("""{"cdpPort":9224}""", web)!.CdpPort);
  }

  [Theory]
  // An operator probing another Chrome, or another tab, has learned nothing about the BRIDGE Chrome's
  // Voice session — which is the only thing browserRefreshOutcome describes.
  [InlineData(9333, null)]
  [InlineData(ConfiguredCdpPort, "mail.google.com")]
  public async Task AFailedProbeOfSomeOtherChromeOrTab_DoesNotOverwriteTheBridgeOutcome(int port, string? target)
  {
    var rig = Build(new FakeCdpExtractor(
      CdpExtractionResult.Fail(CdpExtractionStatus.MissingRequiredCookies, "no session")));

    var result = await rig.Controller.RefreshCookiesFromBrowser(new RefreshFromBrowserRequest(port, target));

    Assert.IsType<BadRequestObjectResult>(result);
    Assert.Equal("Succeeded", rig.Adapter.BrowserRefreshOutcomeName);
  }

  [Theory]
  // The explicit defaults ARE the bridge Chrome's Voice tab; spelling them out must not opt out.
  [InlineData(ConfiguredCdpPort, "voice.google.com")]
  [InlineData(ConfiguredCdpPort, "VOICE.google.com")]
  public async Task ExplicitDefaults_StillRecord(int port, string target)
  {
    var rig = Build(new FakeCdpExtractor(
      CdpExtractionResult.Fail(CdpExtractionStatus.MissingRequiredCookies, "no session")));

    await rig.Controller.RefreshCookiesFromBrowser(new RefreshFromBrowserRequest(port, target));

    Assert.Equal("SignedOut", rig.Adapter.BrowserRefreshOutcomeName);
  }

  [Fact]
  public async Task SuccessfulExtraction_LeavesTheOutcomeToTheAdoptPath()
  {
    // Success is unchanged: the controller writes nothing itself. Succeeded/Stale is decided by
    // GVApiAdapter.TryAdoptAndPersistCookiesAsync from a LIVE probe — here mocked out, so the value
    // must be exactly what it was.
    var rig = Build(new FakeCdpExtractor(Ok()), startingOutcome: GVApiAdapter.BrowserRefreshOutcome.NotAttempted);

    var result = await rig.Controller.RefreshCookiesFromBrowser(null);

    Assert.IsType<OkObjectResult>(result);
    Assert.Equal("NotAttempted", rig.Adapter.BrowserRefreshOutcomeName);
    rig.CookieManager.Verify(
      m => m.SetCookiesAsync(Extracted, It.IsAny<CancellationToken>()), Times.Once);
  }
}
