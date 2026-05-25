using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.Core.Configuration;
using RotaryPhoneController.Core.Diagnostics;
using RotaryPhoneController.Core.HT801;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.Server.Controllers;

namespace RotaryPhoneController.Tests;

public class DiagnosticsControllerTests
{
  private readonly SipDiagnosticService _diagnostics;
  private readonly Mock<IHT801ConfigService> _ht801Service;
  private readonly Mock<ISipAdapter> _sipAdapter;
  private readonly GVApiAdapter _gvAdapter;
  private readonly GVAudioBridgeService _gvAudioBridge;
  private readonly AppConfiguration _config;

  public DiagnosticsControllerTests()
  {
    _diagnostics = new SipDiagnosticService(Mock.Of<ILogger<SipDiagnosticService>>());
    _ht801Service = new Mock<IHT801ConfigService>();
    _sipAdapter = new Mock<ISipAdapter>();
    _gvAdapter = CreateGvAdapter();
    _gvAudioBridge = new GVAudioBridgeService(
      Options.Create(new GVBridgeConfig()),
      NullLogger<GVAudioBridgeService>.Instance);
    _config = new AppConfiguration();
  }

  // --- Audio Bridge endpoint tests ---

  [Fact]
  public void GetAudioBridge_ReturnsAllSixFields()
  {
    var controller = CreateController();
    var result = controller.GetAudioBridge();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var dto = Assert.IsType<AudioBridgeSnapshotDto>(okResult.Value);

    Assert.False(dto.IsActive);
    Assert.Equal(0, dto.InboundFramesSent);
    Assert.Equal(0, dto.OutboundFramesReceived);
    Assert.Equal(0, dto.InboundErrors);
    Assert.Equal(0, dto.OutboundErrors);
    Assert.False(dto.BidirectionalAudio);
  }

  [Fact]
  public void GetAudioBridge_BidirectionalAudio_FalseWhenInactive()
  {
    // Bridge is not active, so even if stats had values, bidirectional should be false.
    // (Stats are all zero here anyway because bridge was never started.)
    var controller = CreateController();
    var result = controller.GetAudioBridge();

    var dto = Assert.IsType<AudioBridgeSnapshotDto>(
      Assert.IsType<OkObjectResult>(result).Value);

    Assert.False(dto.BidirectionalAudio);
  }

  // --- HT801 endpoint tests ---

  [Fact]
  public async Task GetHt801_NoSipHistory_ReturnsNotRegistered()
  {
    _ht801Service.Setup(s => s.GetConfig(It.IsAny<string>()))
      .Returns(new HT801Config { IpAddress = "192.168.86.22", Extension = "1000" });
    _ht801Service.Setup(s => s.TestConnectionAsync(It.IsAny<string>()))
      .ReturnsAsync(new HT801ConnectionTestResult { Success = true });

    var controller = CreateController();
    var result = await controller.GetHt801();

    var dto = Assert.IsType<Ht801StatusDto>(
      Assert.IsType<OkObjectResult>(result).Value);

    Assert.False(dto.SipRegistered);
    Assert.Null(dto.LastRegisterReceived);
    Assert.True(dto.IpReachable);
    Assert.Equal("192.168.86.22", dto.IpAddress);
    Assert.Equal("1000", dto.Extension);
  }

  [Fact]
  public async Task GetHt801_WithSipHistory_ReturnsRegistered()
  {
    // Simulate a REGISTER message so diagnostics sees the HT801 as registered
    _diagnostics.HandleSipMessage(new SipMessageEntry(
      DateTime.UtcNow, SipDirection.Received, "REGISTER",
      "192.168.86.22:5060", "0.0.0.0:5060", null, null, null, null));

    _ht801Service.Setup(s => s.GetConfig(It.IsAny<string>()))
      .Returns(new HT801Config { IpAddress = "192.168.86.22", Extension = "1000" });
    _ht801Service.Setup(s => s.TestConnectionAsync(It.IsAny<string>()))
      .ReturnsAsync(new HT801ConnectionTestResult { Success = true });

    var controller = CreateController();
    var result = await controller.GetHt801();

    var dto = Assert.IsType<Ht801StatusDto>(
      Assert.IsType<OkObjectResult>(result).Value);

    Assert.True(dto.SipRegistered);
    Assert.NotNull(dto.LastRegisterReceived);
  }

  [Fact]
  public async Task GetHt801_NoIp_ReturnsNullReachable()
  {
    _ht801Service.Setup(s => s.GetConfig(It.IsAny<string>()))
      .Returns(new HT801Config { IpAddress = "", Extension = "1000" });

    var controller = CreateController();
    var result = await controller.GetHt801();

    var dto = Assert.IsType<Ht801StatusDto>(
      Assert.IsType<OkObjectResult>(result).Value);

    // When IP is empty, the probe is not attempted — IpReachable should be null
    Assert.Null(dto.IpReachable);
  }

  // --- Helpers ---

  private DiagnosticsController CreateController()
  {
    return new DiagnosticsController(
      _diagnostics,
      _ht801Service.Object,
      _sipAdapter.Object,
      _gvAdapter,
      _gvAudioBridge,
      _config,
      NullLogger<DiagnosticsController>.Instance);
  }

  private static GVApiAdapter CreateGvAdapter()
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
