using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class CdpCookieRefreshTests
{
  [Fact]
  public async Task RefreshFromBrowser_CdpNotReachable_Returns503()
  {
    // Arrange: HttpClient that throws on any request (simulating Chrome not running)
    var handler = new Mock<HttpMessageHandler>();
    handler.Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ThrowsAsync(new HttpRequestException("Connection refused"));

    var factory = CreateHttpClientFactory(handler.Object);
    var controller = CreateController(httpClientFactory: factory);

    // Act
    var result = await controller.RefreshCookiesFromBrowser(null);

    // Assert
    var statusResult = Assert.IsType<ObjectResult>(result);
    Assert.Equal(503, statusResult.StatusCode);
    var json = JsonSerializer.Serialize(statusResult.Value);
    Assert.Contains("Chrome not reachable", json);
  }

  [Fact]
  public async Task RefreshFromBrowser_NoMatchingTab_Returns404()
  {
    // Arrange: CDP returns tabs but none match voice.google.com
    var tabs = new[]
    {
      new { url = "https://www.google.com/search?q=hello", webSocketDebuggerUrl = "ws://localhost:9224/devtools/page/abc" }
    };
    var tabsJson = JsonSerializer.Serialize(tabs);

    var handler = CreateMockHandler(tabsJson, HttpStatusCode.OK);
    var factory = CreateHttpClientFactory(handler);
    var controller = CreateController(httpClientFactory: factory);

    // Act
    var result = await controller.RefreshCookiesFromBrowser(null);

    // Assert
    var notFound = Assert.IsType<NotFoundObjectResult>(result);
    var json = JsonSerializer.Serialize(notFound.Value);
    Assert.Contains("No tab found", json);
  }

  [Fact]
  public void ExtractCookiesViaCdp_BuildsCookieHeaderCorrectly()
  {
    // This test validates the static cookie header building logic indirectly
    // by testing ParseCookieHeader which is used after extraction.
    var rawHeader = "SID=abc123; SAPISID=def456; HSID=ghi789; SSID=jkl012; APISID=mno345; __Secure-1PSID=pqr678; NID=extra";
    var parsed = CdpCookieExtractor.ParseCookieHeader(rawHeader);

    Assert.Equal("abc123", parsed["SID"]);
    Assert.Equal("def456", parsed["SAPISID"]);
    Assert.Equal("ghi789", parsed["HSID"]);
    Assert.Equal("jkl012", parsed["SSID"]);
    Assert.Equal("mno345", parsed["APISID"]);
    Assert.Equal("pqr678", parsed["__Secure-1PSID"]);
    Assert.Equal("extra", parsed["NID"]);
    Assert.Equal(7, parsed.Count);
  }

  [Fact]
  public void CdpTabJson_DeserializesCorrectly()
  {
    // Validates that Chrome's /json response can be deserialized with the same
    // JSON options the controller uses (case-insensitive property names)
    var json = """[{"description":"","devtoolsFrontendUrl":"...","id":"ABC","title":"Google Voice","type":"page","url":"https://voice.google.com/u/0/calls","webSocketDebuggerUrl":"ws://127.0.0.1:9224/devtools/page/ABC"}]""";

    using var doc = JsonDocument.Parse(json);
    var tabs = doc.RootElement.EnumerateArray().ToList();

    Assert.Single(tabs);
    Assert.Contains("voice.google.com", tabs[0].GetProperty("url").GetString());
    Assert.Contains("ws://", tabs[0].GetProperty("webSocketDebuggerUrl").GetString());
  }

  [Fact]
  public async Task RefreshFromBrowser_EmptyDebuggerUrl_Returns503()
  {
    // Arrange: Tab found but webSocketDebuggerUrl is empty (edge case)
    var tabs = new[]
    {
      new { url = "https://voice.google.com/u/0/calls", webSocketDebuggerUrl = "" }
    };
    var tabsJson = JsonSerializer.Serialize(tabs);

    var handler = CreateMockHandler(tabsJson, HttpStatusCode.OK);
    var factory = CreateHttpClientFactory(handler);
    var controller = CreateController(httpClientFactory: factory);

    // Act
    var result = await controller.RefreshCookiesFromBrowser(null);

    // Assert — should return 503 because webSocketDebuggerUrl is empty
    var statusResult = Assert.IsType<ObjectResult>(result);
    Assert.Equal(503, statusResult.StatusCode);
  }

  [Fact]
  public async Task RefreshFromBrowser_RespectsCustomCdpPort()
  {
    // Arrange: Request with custom port; handler inspects the URL
    string? requestedUrl = null;
    var handler = new Mock<HttpMessageHandler>();
    handler.Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
      {
        requestedUrl = req.RequestUri?.ToString();
        throw new HttpRequestException("Connection refused");
      });

    var factory = CreateHttpClientFactory(handler.Object);
    var controller = CreateController(httpClientFactory: factory);

    var request = new RefreshFromBrowserRequest(CdpPort: 9333);

    // Act
    await controller.RefreshCookiesFromBrowser(request);

    // Assert — should have hit port 9333
    Assert.NotNull(requestedUrl);
    Assert.Contains("9333", requestedUrl);
  }

  [Fact]
  public void GVBridgeConfig_ChromeCdpPort_DefaultsTo9224()
  {
    var config = new GVBridgeConfig();
    Assert.Equal(9224, config.ChromeCdpPort);
  }

  // --- the 502 used to claim "the browser session is stale" and "nothing was overwritten" for
  // --- causes where NEITHER was true. One status code and one message for every cause is the same
  // --- sin Task 7 removed from the exhausted-ladder message: state only what was actually tested.

  private sealed class FakeCdpExtractor(CdpExtractionResult result) : ICdpCookieExtractor
  {
    public Task<CdpExtractionResult> ExtractAsync(int cdpPort, string targetUrl, CancellationToken ct = default)
      => Task.FromResult(result);
  }

  private static GVBridgeController ControllerWithOutcome(SetCookiesOutcome outcome)
  {
    var cookieManager = CreateDefaultCookieManager();
    cookieManager.Setup(m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(outcome);

    var extracted = new GvCookieSet
    {
      Sapisid = "SAPISID-EXTRACTED", Sid = "sid", Hsid = "hsid", Ssid = "ssid", Apisid = "apisid",
    };

    return CreateController(
      cookieManager: cookieManager,
      extractor: new FakeCdpExtractor(
        new CdpExtractionResult(CdpExtractionStatus.Success, extracted, 20, null)));
  }

  [Fact]
  public async Task RefreshFromBrowser_RejectedByGoogle_Returns502_AndTheStaleSessionClaimIsEarned()
  {
    // The ONE branch where "the browser session is stale" and "nothing was overwritten" are both true.
    var result = await ControllerWithOutcome(SetCookiesOutcome.RejectedByGoogle)
      .RefreshCookiesFromBrowser(null);

    var status = Assert.IsType<ObjectResult>(result);
    Assert.Equal(502, status.StatusCode);
    var json = JsonSerializer.Serialize(status.Value);
    Assert.Contains("TESTED, not inferred", json);
    Assert.Contains("NOT overwritten", json);
    Assert.Contains("re-login at voice.google.com", json);
  }

  [Fact]
  public async Task RefreshFromBrowser_ColdSeedUnvalidated_DoesNotClaimGoogleRejectedThem()
  {
    // On the cold path the file WAS overwritten and nothing tested the Google login — yet this branch
    // used to return the identical 502 saying Google refused the cookies and nothing was overwritten.
    var result = await ControllerWithOutcome(SetCookiesOutcome.ColdSeedUnvalidated)
      .RefreshCookiesFromBrowser(null);

    var status = Assert.IsType<ObjectResult>(result);
    Assert.Equal(202, status.StatusCode);
    var json = JsonSerializer.Serialize(status.Value);
    Assert.Contains("WAS overwritten", json);              // the truth this used to invert
    Assert.DoesNotContain("nothing was overwritten", json);
    Assert.DoesNotContain("session is stale", json);
  }

  [Fact]
  public async Task RefreshFromBrowser_ActivationFailed_DoesNotAccuseTheGoogleLogin()
  {
    // A missing encryption key, a throwing registry or an IO error tests nothing about Google. Sending
    // the operator to re-login for one of those is the unearned assertion Task 7 exists to prevent.
    var result = await ControllerWithOutcome(SetCookiesOutcome.ActivationFailed)
      .RefreshCookiesFromBrowser(null);

    var status = Assert.IsType<ObjectResult>(result);
    Assert.Equal(500, status.StatusCode);
    var json = JsonSerializer.Serialize(status.Value);
    Assert.Contains("Nothing here tested the Google login", json);
    Assert.DoesNotContain("session is stale", json);
    Assert.DoesNotContain("nothing was overwritten", json);
  }

  [Fact]
  public async Task RefreshFromBrowser_AdoptedButNotPersisted_BlamesTheDisk_NotTheGoogleLogin()
  {
    // Google ACCEPTED these cookies on a live probe; only the write failed. Reporting that as a stale
    // browser session would send the operator to re-login while the disk stays full.
    var result = await ControllerWithOutcome(SetCookiesOutcome.AdoptedButNotPersisted)
      .RefreshCookiesFromBrowser(null);

    var status = Assert.IsType<ObjectResult>(result);
    Assert.Equal(500, status.StatusCode);
    var json = JsonSerializer.Serialize(status.Value);
    Assert.Contains("Google login is fine", json);
    Assert.Contains("disk space and permissions", json);
    Assert.DoesNotContain("session is stale", json);
  }

  [Theory]
  [InlineData(SetCookiesOutcome.Adopted)]
  [InlineData(SetCookiesOutcome.AdoptedButActivationFailed)]
  public async Task RefreshFromBrowser_CookiesWereAdopted_Returns200(SetCookiesOutcome outcome)
  {
    // AdoptedButActivationFailed is a SUCCESS for this endpoint: the cookies passed a live probe and
    // are on disk, which is what it was asked to do. A failed re-activation is separately visible as
    // sipRegistered:false on /status, and must not be reported as a refusal by Google.
    var result = await ControllerWithOutcome(outcome).RefreshCookiesFromBrowser(null);

    var ok = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(ok.Value);
    Assert.Contains("efreshed", json);
  }

  // --- Helpers ---

  private static HttpMessageHandler CreateMockHandler(string responseBody, HttpStatusCode statusCode)
  {
    var handler = new Mock<HttpMessageHandler>();
    handler.Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(new HttpResponseMessage
      {
        StatusCode = statusCode,
        Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
      });
    return handler.Object;
  }

  private static IHttpClientFactory CreateHttpClientFactory(HttpMessageHandler handler)
  {
    var factory = new Mock<IHttpClientFactory>();
    factory.Setup(f => f.CreateClient(It.IsAny<string>()))
      .Returns(new HttpClient(handler));
    return factory.Object;
  }

  private static GVBridgeController CreateController(
    IHttpClientFactory? httpClientFactory = null,
    Mock<IGvCookieManager>? cookieManager = null,
    ICdpCookieExtractor? extractor = null)
  {
    var registry = new Mock<ICallAdapterRegistry>();
    registry.Setup(r => r.ActiveMode).Returns(CallAdapterMode.GVApi);

    var config = Options.Create(new GVBridgeConfig
    {
      GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
      GvApiKey = "test",
      CookieFilePath = "test.enc",
      CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
      ChromeCdpPort = 9224
    });

    var adapter = new GVApiAdapter(
      config,
      NullLogger<GVApiAdapter>.Instance,
      NullLoggerFactory.Instance);

    var cm = cookieManager ?? CreateDefaultCookieManager();
    var factory = httpClientFactory ?? CreateHttpClientFactory(
      CreateMockHandler("[]", HttpStatusCode.OK));
    var logger = NullLogger<GVBridgeController>.Instance;

    var cdpExtractor = extractor
      ?? new CdpCookieExtractor(factory, NullLogger<CdpCookieExtractor>.Instance);

    return new GVBridgeController(
      registry.Object,
      adapter,
      cm.Object,
      config,
      cdpExtractor,
      logger);
  }

  private static Mock<IGvCookieManager> CreateDefaultCookieManager()
  {
    var mock = new Mock<IGvCookieManager>();
    mock.Setup(m => m.GetStatus()).Returns(new GvCookieStatusDto(
      CookiesPresent: false,
      CookiesValid: false,
      LastValidatedAt: null,
      LoadedAt: null,
      CookieCount: null,
      SapisidPrefix: null));
    mock.Setup(m => m.SetCookiesAsync(It.IsAny<GvCookieSet>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(SetCookiesOutcome.Adopted);
    return mock;
  }
}
