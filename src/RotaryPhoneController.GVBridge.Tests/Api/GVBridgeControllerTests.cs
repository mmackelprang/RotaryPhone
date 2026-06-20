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
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GVBridgeControllerTests
{
  [Fact]
  public void GetStatus_ReturnsAllFourFields()
  {
    var controller = CreateController();

    var result = controller.GetStatus();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.True(root.TryGetProperty("available", out _));
    Assert.True(root.TryGetProperty("activeMode", out _));
    Assert.True(root.TryGetProperty("sipRegistered", out _));
    Assert.True(root.TryGetProperty("cookiesValid", out _));
  }

  [Fact]
  public void GetStatus_DefaultValues_ShowUnavailable()
  {
    var controller = CreateController();

    var result = controller.GetStatus();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.False(root.GetProperty("available").GetBoolean());
    Assert.False(root.GetProperty("sipRegistered").GetBoolean());
    Assert.False(root.GetProperty("cookiesValid").GetBoolean());
    Assert.Equal("GVApi", root.GetProperty("activeMode").GetString());
  }

  [Fact]
  public void GetStatus_IncludesWsConnectedAndLastConnectedAt()
  {
    var controller = CreateController();

    var result = controller.GetStatus();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    // New honest-status fields exist (camelCase JSON).
    Assert.True(root.TryGetProperty("wsConnected", out var wsConnected));
    Assert.True(root.TryGetProperty("lastConnectedAt", out var lastConnectedAt));

    // Defaults on an inactive adapter: not connected, no last-connected timestamp.
    Assert.False(wsConnected.GetBoolean());
    Assert.Equal(JsonValueKind.Null, lastConnectedAt.ValueKind);

    // The original four fields are still present (contract preserved).
    Assert.True(root.TryGetProperty("available", out _));
    Assert.True(root.TryGetProperty("activeMode", out _));
    Assert.True(root.TryGetProperty("sipRegistered", out _));
    Assert.True(root.TryGetProperty("cookiesValid", out _));
  }

  [Fact]
  public void GetCookies_ReturnsAllSixFields()
  {
    var controller = CreateController();

    var result = controller.GetCookies();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.True(root.TryGetProperty("cookiesPresent", out _));
    Assert.True(root.TryGetProperty("cookiesValid", out _));
    Assert.True(root.TryGetProperty("lastValidatedAt", out _));
    Assert.True(root.TryGetProperty("loadedAt", out _));
    Assert.True(root.TryGetProperty("cookieCount", out _));
    Assert.True(root.TryGetProperty("sapisidPrefix", out _));
  }

  [Fact]
  public void GetCookies_NoCookiesLoaded_ReturnsFalseAndNulls()
  {
    var controller = CreateController();

    var result = controller.GetCookies();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.False(root.GetProperty("cookiesPresent").GetBoolean());
    Assert.False(root.GetProperty("cookiesValid").GetBoolean());
    Assert.Equal(JsonValueKind.Null, root.GetProperty("cookieCount").ValueKind);
    Assert.Equal(JsonValueKind.Null, root.GetProperty("sapisidPrefix").ValueKind);
  }

  [Fact]
  public async Task SetCookies_MissingSapisidAndSid_ReturnsBadRequest()
  {
    var controller = CreateController();
    var request = new SetCookiesRequest(null, null, null, null, null, null, null, null);

    var result = await controller.SetCookies(request);

    var badRequest = Assert.IsType<BadRequestObjectResult>(result);
    var json = JsonSerializer.Serialize(badRequest.Value);
    Assert.Contains("Sapisid", json);
  }

  [Fact]
  public async Task SetCookies_WithRawHeader_ExtractsFieldsAndSaves()
  {
    var cookieManager = new Mock<IGvCookieManager>();
    cookieManager
      .Setup(m => m.SetCookiesAsync(It.IsAny<RotaryPhoneController.GVBridge.Auth.GvCookieSet>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(true);
    var controller = CreateController(cookieManager: cookieManager);

    var rawHeader = "SAPISID=abc123def456; SID=mysid; HSID=myhsid; SSID=myssid; APISID=myapisid; __Secure-1PSID=sec1; other=ignored";
    var request = new SetCookiesRequest(null, null, null, null, null, null, null, rawHeader);

    var result = await controller.SetCookies(request);

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    Assert.Contains("saved", json);

    cookieManager.Verify(m => m.SetCookiesAsync(
      It.Is<RotaryPhoneController.GVBridge.Auth.GvCookieSet>(c =>
        c.Sapisid == "abc123def456" &&
        c.Sid == "mysid" &&
        c.RawCookieHeader == rawHeader),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task SetCookies_WithIndividualFields_Saves()
  {
    var cookieManager = new Mock<IGvCookieManager>();
    cookieManager
      .Setup(m => m.SetCookiesAsync(It.IsAny<RotaryPhoneController.GVBridge.Auth.GvCookieSet>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(true);
    var controller = CreateController(cookieManager: cookieManager);

    var request = new SetCookiesRequest(
      "my-sapisid", "my-sid", "my-hsid", "my-ssid", "my-apisid",
      "sec1", "sec3", null);

    var result = await controller.SetCookies(request);

    Assert.IsType<OkObjectResult>(result);
    cookieManager.Verify(m => m.SetCookiesAsync(
      It.Is<RotaryPhoneController.GVBridge.Auth.GvCookieSet>(c =>
        c.Sapisid == "my-sapisid" && c.Sid == "my-sid"),
      It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public void ParseCookieHeader_ParsesCorrectly()
  {
    var header = "SAPISID=abc123; SID=mysid; __Secure-1PSID=s1; HSID=h; complex=a=b=c";
    var parsed = CdpCookieExtractor.ParseCookieHeader(header);

    Assert.Equal("abc123", parsed["SAPISID"]);
    Assert.Equal("mysid", parsed["SID"]);
    Assert.Equal("s1", parsed["__Secure-1PSID"]);
    Assert.Equal("h", parsed["HSID"]);
    Assert.Equal("a=b=c", parsed["complex"]); // Handles '=' in values
  }

  [Fact]
  public void ParseCookieHeader_CaseInsensitiveLookup()
  {
    var header = "SAPISID=abc123; sid=mysid";
    var parsed = CdpCookieExtractor.ParseCookieHeader(header);

    Assert.Equal("abc123", parsed["sapisid"]);
    Assert.Equal("mysid", parsed["SID"]);
  }

  private static GVBridgeController CreateController(Mock<IGvCookieManager>? cookieManager = null)
  {
    var registry = new Mock<ICallAdapterRegistry>();
    registry.Setup(r => r.ActiveMode).Returns(CallAdapterMode.GVApi);

    var config = Options.Create(new GVBridgeConfig
    {
      GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
      GvApiKey = "test",
      CookieFilePath = "test.enc",
      CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
    });

    var adapter = CreateAdapter();
    var cm = cookieManager ?? CreateDefaultCookieManager();

    // Minimal IHttpClientFactory mock (not exercised by these tests)
    var handler = new Mock<HttpMessageHandler>();
    handler.Protected()
      .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
      .ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent("[]") });
    var httpFactory = new Mock<IHttpClientFactory>();
    httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
      .Returns(new HttpClient(handler.Object));

    var cdpExtractor = new CdpCookieExtractor(httpFactory.Object, NullLogger<CdpCookieExtractor>.Instance);

    return new GVBridgeController(
      registry.Object,
      adapter,
      cm.Object,
      config,
      cdpExtractor,
      NullLogger<GVBridgeController>.Instance);
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
    return mock;
  }

  private static GVApiAdapter CreateAdapter()
  {
    var config = Options.Create(new GVBridgeConfig
    {
      GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
      GvApiKey = "test",
      CookieFilePath = "test.enc",
      CookieEncryptionKey = Convert.ToBase64String(new byte[32]),
    });

    return new GVApiAdapter(
      config,
      NullLogger<GVApiAdapter>.Instance,
      NullLoggerFactory.Instance);
  }
}
