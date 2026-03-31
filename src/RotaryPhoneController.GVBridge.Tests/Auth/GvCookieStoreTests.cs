using System.Text.Json;
using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class GvCookieStoreTests : IDisposable
{
    private readonly string _tempFile;
    private readonly string _encryptionKey;

    public GvCookieStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"gv-cookies-test-{Guid.NewGuid():N}.enc");
        var keyBytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(keyBytes);
        _encryptionKey = Convert.ToBase64String(keyBytes);
    }

    public void Dispose() { if (File.Exists(_tempFile)) File.Delete(_tempFile); }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var store = new GvCookieStore(_tempFile, _encryptionKey);
        var cookies = new GvCookieSet
        {
            Sapisid = "SAP123", Sid = "SID456", Hsid = "HSID789",
            Ssid = "SSID012", Apisid = "API345",
            Secure1Psid = "SEC1_678", Secure3Psid = "SEC3_901",
        };

        await store.SaveAsync(cookies);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("SAP123", loaded!.Sapisid);
        Assert.Equal("SID456", loaded.Sid);
    }

    [Fact]
    public async Task Load_WhenFileDoesNotExist_ReturnsNull()
    {
        var store = new GvCookieStore(_tempFile, _encryptionKey);
        var result = await store.LoadAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task Load_WithWrongKey_ReturnsNull()
    {
        var store = new GvCookieStore(_tempFile, _encryptionKey);
        await store.SaveAsync(new GvCookieSet
        {
            Sapisid = "test", Sid = "s", Hsid = "h", Ssid = "ss", Apisid = "a"
        });

        var wrongKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(wrongKey);
        var wrongStore = new GvCookieStore(_tempFile, Convert.ToBase64String(wrongKey));
        var result = await wrongStore.LoadAsync();

        Assert.Null(result);
    }
}
