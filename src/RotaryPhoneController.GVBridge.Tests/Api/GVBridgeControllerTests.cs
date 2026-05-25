using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Models;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GVBridgeControllerTests
{
  [Fact]
  public void GetStatus_ReturnsAllFourFields()
  {
    var registry = new Mock<ICallAdapterRegistry>();
    registry.Setup(r => r.ActiveMode).Returns(CallAdapterMode.GVApi);

    var adapter = CreateAdapter();
    var controller = new GVBridgeController(registry.Object, adapter);

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
    var registry = new Mock<ICallAdapterRegistry>();
    registry.Setup(r => r.ActiveMode).Returns(CallAdapterMode.GVApi);

    var adapter = CreateAdapter();
    var controller = new GVBridgeController(registry.Object, adapter);

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
