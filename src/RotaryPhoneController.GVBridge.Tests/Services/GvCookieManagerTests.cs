using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvCookieManagerTests : IDisposable
{
  private readonly string _tempDir;
  private readonly string _cookieFile;
  private readonly string _keyFile;

  public GvCookieManagerTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"gvcm-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
    _cookieFile = Path.Combine(_tempDir, "gv-cookies.enc");
    _keyFile = Path.Combine(_tempDir, "gv-key.bin");
  }

  public void Dispose()
  {
    if (Directory.Exists(_tempDir))
      Directory.Delete(_tempDir, recursive: true);
  }

  [Fact]
  public void GetStatus_NoCookies_ReturnsCookiesPresentFalse()
  {
    var (manager, _, _) = CreateManager();
    var status = manager.GetStatus();

    Assert.False(status.CookiesPresent);
    Assert.False(status.CookiesValid);
    Assert.Null(status.LastValidatedAt);
    Assert.Null(status.LoadedAt);
    Assert.Null(status.CookieCount);
    Assert.Null(status.SapisidPrefix);
  }

  [Fact]
  public async Task SetCookies_ValidPayload_SavesAndReactivates()
  {
    var (manager, _, registry) = CreateManager();
    registry
      .Setup(r => r.SwitchModeAsync(CallAdapterMode.GVApi, It.IsAny<CancellationToken>()))
      .Returns(Task.CompletedTask);

    var cookies = new GvCookieSet
    {
      Sapisid = "ABCDEF1234567890ABCDEF",
      Sid = "test-sid",
      Hsid = "test-hsid",
      Ssid = "test-ssid",
      Apisid = "test-apisid",
    };

    var result = await manager.SetCookiesAsync(cookies);

    Assert.True(result);
    Assert.True(File.Exists(_cookieFile), "Cookie file should be created");
    Assert.True(File.Exists(_keyFile), "Key file should be auto-generated");
    registry.Verify(
      r => r.SwitchModeAsync(CallAdapterMode.GVApi, It.IsAny<CancellationToken>()),
      Times.Once);
  }

  [Fact]
  public async Task SetCookies_GeneratesKeyFile_WhenMissing()
  {
    var (manager, _, registry) = CreateManager();
    registry
      .Setup(r => r.SwitchModeAsync(CallAdapterMode.GVApi, It.IsAny<CancellationToken>()))
      .Returns(Task.CompletedTask);

    Assert.False(File.Exists(_keyFile));

    await manager.SetCookiesAsync(new GvCookieSet
    {
      Sapisid = "test", Sid = "s", Hsid = "h", Ssid = "ss", Apisid = "a"
    });

    Assert.True(File.Exists(_keyFile));
    var keyBytes = await File.ReadAllBytesAsync(_keyFile);
    Assert.Equal(32, keyBytes.Length);
  }

  [Fact]
  public async Task SetCookies_ReusesExistingKeyFile()
  {
    // Pre-create a key file
    var existingKey = new byte[32];
    RandomNumberGenerator.Fill(existingKey);
    await File.WriteAllBytesAsync(_keyFile, existingKey);

    var (manager, _, registry) = CreateManager();
    registry
      .Setup(r => r.SwitchModeAsync(CallAdapterMode.GVApi, It.IsAny<CancellationToken>()))
      .Returns(Task.CompletedTask);

    await manager.SetCookiesAsync(new GvCookieSet
    {
      Sapisid = "test", Sid = "s", Hsid = "h", Ssid = "ss", Apisid = "a"
    });

    // Verify saved cookies can be loaded with the original key
    var store = new GvCookieStore(_cookieFile, Convert.ToBase64String(existingKey));
    var loaded = await store.LoadAsync();
    Assert.NotNull(loaded);
    Assert.Equal("test", loaded!.Sapisid);
  }

  [Fact]
  public async Task GetStatus_AfterSet_ReturnsCookiePrefix()
  {
    // Pre-create a key file so we can construct a store to manually load
    var keyBytes = new byte[32];
    RandomNumberGenerator.Fill(keyBytes);
    await File.WriteAllBytesAsync(_keyFile, keyBytes);

    // Save a cookie file directly using the store so the file exists
    var store = new GvCookieStore(_cookieFile, Convert.ToBase64String(keyBytes));
    await store.SaveAsync(new GvCookieSet
    {
      Sapisid = "ABCDEFGHIJKLMNOP",
      Sid = "sid-value",
      Hsid = "hsid-value",
      Ssid = "ssid-value",
      Apisid = "apisid-value",
    });

    // The manager's GetStatus checks File.Exists for the cookie file
    // and reads CurrentCookieSet from the adapter. Since the adapter isn't
    // activated in tests, CookieCount and SapisidPrefix will be null, but
    // CookiesPresent should be true based on the file check.
    var (manager, _, _) = CreateManager();
    var status = manager.GetStatus();

    Assert.True(status.CookiesPresent);
    // CookiesValid is false because the adapter hasn't been activated
    Assert.False(status.CookiesValid);
  }

  [Fact]
  public async Task SetCookies_ReturnsFalse_WhenAdapterActivationFails()
  {
    var (manager, _, registry) = CreateManager();
    registry
      .Setup(r => r.SwitchModeAsync(CallAdapterMode.GVApi, It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("Activation failed"));

    var result = await manager.SetCookiesAsync(new GvCookieSet
    {
      Sapisid = "test", Sid = "s", Hsid = "h", Ssid = "ss", Apisid = "a"
    });

    Assert.False(result);
    // But the cookie file should still have been saved
    Assert.True(File.Exists(_cookieFile));
  }

  private (GvCookieManager manager, GVApiAdapter adapter, Mock<ICallAdapterRegistry> registry) CreateManager()
  {
    var config = Options.Create(new GVBridgeConfig
    {
      CookieFilePath = _cookieFile,
      CookieKeyFilePath = _keyFile,
      CookieEncryptionKey = "",
      GvApiBaseUrl = "https://clients6.google.com/voice/v1/voiceclient",
      GvApiKey = "test",
    });

    var adapter = new GVApiAdapter(
      config,
      NullLogger<GVApiAdapter>.Instance,
      NullLoggerFactory.Instance);

    var registry = new Mock<ICallAdapterRegistry>();

    var manager = new GvCookieManager(
      config,
      adapter,
      registry.Object,
      NullLogger<GvCookieManager>.Instance);

    return (manager, adapter, registry);
  }
}
