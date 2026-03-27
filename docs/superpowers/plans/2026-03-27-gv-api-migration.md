# GV API Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace CDP/extension call control with direct GV HTTP API calls, keeping the Chrome extension for audio only (Phase 1).

**Architecture:** New `GVApiAdapter : ICallAdapter` uses SAPISIDHASH cookie auth to call GV's undocumented HTTP API at `clients6.google.com/voice/v1/voiceclient/`. A `GvSignalerClient` long-poll channel replaces DOM polling for incoming call detection. The Chrome extension is stripped to audio relay only (tabCapture + answer/hangup button clicking via DOM). CDP is fully eliminated.

**Tech Stack:** .NET 10, SIPSorcery 8.0.23, System.Text.Json, Playwright (cookie extraction), xUnit + Moq

**Important Note:** GVResearch's API *documentation* (`docs/api-research/gv-api-reference.md`) is comprehensive, but the SDK code is mostly scaffolding. Components are built from the documented API spec, not absorbed from existing code. The AES-256 token encryption pattern is the main code we adapt.

**Spec:** `docs/superpowers/specs/2026-03-27-gv-api-migration-design.md`

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Auth/
  GvCookieJar.cs              -- Cookie model (7 required cookies + helpers)
  GvCookieStore.cs             -- AES-256 encrypted persistence to disk
  GvSapisidHash.cs             -- SAPISIDHASH computation (static helper)
  GvHttpClientHandler.cs       -- DelegatingHandler injecting auth headers on every request
Protocol/
  GvProtobuf.cs                -- Helpers for reading/writing GV's protobuf-JSON arrays
Clients/
  GvAccountClient.cs           -- GET account info, health check
  GvCallClient.cs              -- Initiate, status, hangup calls
  GvSmsClient.cs               -- Send SMS
Signaler/
  GvSignalerClient.cs          -- Long-poll channel for incoming call/SMS events
  SignalerEvent.cs              -- Event models (IncomingCallEvent, CallEndedEvent, etc.)
Adapters/
  GVApiAdapter.cs              -- New ICallAdapter replacing GVBrowserAdapter
Tools/
  GvLoginTool.cs               -- Playwright cookie extraction CLI command
```

### New test files (in `src/RotaryPhoneController.GVBridge.Tests/`)

```
Auth/
  GvSapisidHashTests.cs
  GvCookieStoreTests.cs
  GvHttpClientHandlerTests.cs
Protocol/
  GvProtobufTests.cs
Clients/
  GvAccountClientTests.cs
  GvCallClientTests.cs
Adapters/
  GVApiAdapterTests.cs
```

### Modified files

```
Models/GVBridgeConfig.cs                -- Add cookie file path, GV API key
Extensions/GVBridgeServiceExtensions.cs -- Register new services, add GVApi mode
CallAdapterMode.cs (Core)              -- Add GVApi enum value
ChromeExtension/content/gv-bridge.js   -- Strip call detection, keep audio + button click
ChromeExtension/background/service-worker.js -- Strip HTTP relay for call events
appsettings.json                        -- Add GVApi config section
```

### Deleted files (after GVApiAdapter is working)

```
Adapters/GVBrowserAdapter.cs           -- Replaced by GVApiAdapter
```

---

## Task 1: Add GVApi Mode and Config Properties

**Files:**
- Modify: `src/RotaryPhoneController.Core/CallAdapterMode.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`

- [ ] **Step 1: Add GVApi to CallAdapterMode enum**

In `src/RotaryPhoneController.Core/CallAdapterMode.cs`, add `GVApi` after `GVBrowser`:

```csharp
public enum CallAdapterMode
{
    BluetoothHfp,
    SipTrunk,
    GVBrowser,
    GVApi
}
```

- [ ] **Step 2: Add GV API config properties to GVBridgeConfig**

In `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`, add after existing properties:

```csharp
// GV API settings (used by GVApiAdapter)
public string GvApiBaseUrl { get; set; } = "https://clients6.google.com/voice/v1/voiceclient";
public string GvApiKey { get; set; } = "";  // Google API key from voice.google.com page source
public string CookieFilePath { get; set; } = "data/gv-cookies.enc";
public string CookieEncryptionKey { get; set; } = "";  // AES-256 key, base64-encoded
public int CookieHealthCheckIntervalMinutes { get; set; } = 30;
public string SignalerBaseUrl { get; set; } = "https://signaler-pa.clients6.google.com";
```

- [ ] **Step 3: Add config section to appsettings.json**

In `src/RotaryPhoneController.Server/appsettings.json`, add the new properties to the `GVBridge` section:

```json
"GvApiBaseUrl": "https://clients6.google.com/voice/v1/voiceclient",
"GvApiKey": "",
"CookieFilePath": "data/gv-cookies.enc",
"CookieEncryptionKey": "",
"CookieHealthCheckIntervalMinutes": 30,
"SignalerBaseUrl": "https://signaler-pa.clients6.google.com",
"DefaultMode": "GVApi"
```

- [ ] **Step 4: Build to verify no compile errors**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Core/CallAdapterMode.cs \
        src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs \
        src/RotaryPhoneController.Server/appsettings.json
git commit -m "feat(gvapi): add GVApi adapter mode and config properties"
```

---

## Task 2: SAPISIDHASH Authentication

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Auth/GvCookieJar.cs`
- Create: `src/RotaryPhoneController.GVBridge/Auth/GvSapisidHash.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Auth/GvSapisidHashTests.cs`

- [ ] **Step 1: Write the failing test for SAPISIDHASH computation**

Create `src/RotaryPhoneController.GVBridge.Tests/Auth/GvSapisidHashTests.cs`:

```csharp
using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class GvSapisidHashTests
{
    [Fact]
    public void Compute_ReturnsTimestampUnderscoreHexSha1()
    {
        // Known inputs → deterministic SHA1
        var result = GvSapisidHash.Compute(
            sapisid: "ABCDEF1234567890",
            origin: "https://voice.google.com",
            timestampSeconds: 1711500000);

        // SHA1("1711500000 ABCDEF1234567890 https://voice.google.com")
        Assert.StartsWith("1711500000_", result);
        Assert.Matches(@"^\d+_[0-9a-f]{40}$", result);
    }

    [Fact]
    public void Compute_DifferentTimestamps_ProduceDifferentHashes()
    {
        var a = GvSapisidHash.Compute("SAPISID", "https://voice.google.com", 1000);
        var b = GvSapisidHash.Compute("SAPISID", "https://voice.google.com", 2000);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeCurrent_UsesCurrentTimestamp()
    {
        var result = GvSapisidHash.ComputeCurrent("SAPISID", "https://voice.google.com");

        Assert.Matches(@"^\d+_[0-9a-f]{40}$", result);
        // Timestamp should be within last few seconds
        var ts = long.Parse(result.Split('_')[0]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(ts, now - 5, now + 5);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvSapisidHashTests" -v n`
Expected: FAIL — `GvSapisidHash` type does not exist

- [ ] **Step 3: Create GvCookieJar model**

Create `src/RotaryPhoneController.GVBridge/Auth/GvCookieJar.cs`:

```csharp
using System.Text.Json.Serialization;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// The 7 Google cookies required for GV API authentication.
/// </summary>
public class GvCookieJar
{
    [JsonPropertyName("SAPISID")]
    public string Sapisid { get; set; } = "";

    [JsonPropertyName("SID")]
    public string Sid { get; set; } = "";

    [JsonPropertyName("HSID")]
    public string Hsid { get; set; } = "";

    [JsonPropertyName("SSID")]
    public string Ssid { get; set; } = "";

    [JsonPropertyName("APISID")]
    public string Apisid { get; set; } = "";

    [JsonPropertyName("__Secure-1PSID")]
    public string Secure1Psid { get; set; } = "";

    [JsonPropertyName("__Secure-3PSID")]
    public string Secure3Psid { get; set; } = "";

    public bool IsComplete =>
        !string.IsNullOrEmpty(Sapisid) &&
        !string.IsNullOrEmpty(Sid) &&
        !string.IsNullOrEmpty(Hsid) &&
        !string.IsNullOrEmpty(Ssid) &&
        !string.IsNullOrEmpty(Apisid) &&
        !string.IsNullOrEmpty(Secure1Psid) &&
        !string.IsNullOrEmpty(Secure3Psid);

    /// <summary>
    /// Format cookies as a Cookie header value.
    /// </summary>
    public string ToCookieHeader() =>
        $"SID={Sid}; HSID={Hsid}; SSID={Ssid}; APISID={Apisid}; SAPISID={Sapisid}; __Secure-1PSID={Secure1Psid}; __Secure-3PSID={Secure3Psid}";
}
```

- [ ] **Step 4: Implement GvSapisidHash**

Create `src/RotaryPhoneController.GVBridge/Auth/GvSapisidHash.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.GVBridge.Auth;

public static class GvSapisidHash
{
    private const string GvOrigin = "https://voice.google.com";

    /// <summary>
    /// Compute SAPISIDHASH for a given timestamp (testable).
    /// Format: {timestamp}_{SHA1(timestamp + " " + sapisid + " " + origin)} as lowercase hex.
    /// </summary>
    public static string Compute(string sapisid, string origin, long timestampSeconds)
    {
        var input = $"{timestampSeconds} {sapisid} {origin}";
#pragma warning disable CA5350 // Google requires SHA1 for SAPISIDHASH
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
#pragma warning restore CA5350
        return $"{timestampSeconds}_{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>
    /// Compute SAPISIDHASH using the current UTC timestamp.
    /// </summary>
    public static string ComputeCurrent(string sapisid, string origin = GvOrigin) =>
        Compute(sapisid, origin, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvSapisidHashTests" -v n`
Expected: 3 passed

- [ ] **Step 6: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Auth/GvCookieJar.cs \
        src/RotaryPhoneController.GVBridge/Auth/GvSapisidHash.cs \
        src/RotaryPhoneController.GVBridge.Tests/Auth/GvSapisidHashTests.cs
git commit -m "feat(gvapi): implement SAPISIDHASH auth and cookie model"
```

---

## Task 3: Encrypted Cookie Store

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Auth/GvCookieStore.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Auth/GvCookieStoreTests.cs`

- [ ] **Step 1: Write failing tests for cookie store**

Create `src/RotaryPhoneController.GVBridge.Tests/Auth/GvCookieStoreTests.cs`:

```csharp
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
        // Generate a valid AES-256 key (32 bytes, base64-encoded)
        var keyBytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(keyBytes);
        _encryptionKey = Convert.ToBase64String(keyBytes);
    }

    public void Dispose() { if (File.Exists(_tempFile)) File.Delete(_tempFile); }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var store = new GvCookieStore(_tempFile, _encryptionKey);
        var cookies = new GvCookieJar
        {
            Sapisid = "SAP123", Sid = "SID456", Hsid = "HSID789",
            Ssid = "SSID012", Apisid = "API345",
            Secure1Psid = "SEC1_678", Secure3Psid = "SEC3_901"
        };

        await store.SaveAsync(cookies);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("SAP123", loaded!.Sapisid);
        Assert.Equal("SID456", loaded.Sid);
        Assert.True(loaded.IsComplete);
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
        await store.SaveAsync(new GvCookieJar { Sapisid = "test" });

        var wrongKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(wrongKey);
        var wrongStore = new GvCookieStore(_tempFile, Convert.ToBase64String(wrongKey));
        var result = await wrongStore.LoadAsync();

        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvCookieStoreTests" -v n`
Expected: FAIL — `GvCookieStore` does not exist

- [ ] **Step 3: Implement GvCookieStore**

Create `src/RotaryPhoneController.GVBridge/Auth/GvCookieStore.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// Persists GvCookieJar to disk with AES-256-CBC encryption.
/// IV is prepended to the ciphertext.
/// </summary>
public class GvCookieStore
{
    private readonly string _filePath;
    private readonly byte[] _key;

    public GvCookieStore(string filePath, string base64Key)
    {
        _filePath = filePath;
        _key = Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (AES-256).");
    }

    public async Task SaveAsync(GvCookieJar cookies)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(cookies);
        var encrypted = Encrypt(json);

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(_filePath, encrypted);
    }

    public async Task<GvCookieJar?> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(_filePath);
            var json = Decrypt(encrypted);
            return JsonSerializer.Deserialize<GvCookieJar>(json);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(result, 0);
        ciphertext.CopyTo(result, aes.IV.Length);
        return result;
    }

    private byte[] Decrypt(byte[] encrypted)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        var iv = encrypted[..16];
        var ciphertext = encrypted[16..];
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvCookieStoreTests" -v n`
Expected: 3 passed

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Auth/GvCookieStore.cs \
        src/RotaryPhoneController.GVBridge.Tests/Auth/GvCookieStoreTests.cs
git commit -m "feat(gvapi): add encrypted cookie store with AES-256"
```

---

## Task 4: GvHttpClientHandler (Auth-Injecting DelegatingHandler)

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Auth/GvHttpClientHandler.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Auth/GvHttpClientHandlerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Auth/GvHttpClientHandlerTests.cs`:

```csharp
using System.Net;
using RotaryPhoneController.GVBridge.Auth;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class GvHttpClientHandlerTests
{
    private static GvCookieJar TestCookies => new()
    {
        Sapisid = "SAP_TEST", Sid = "SID_TEST", Hsid = "HSID_TEST",
        Ssid = "SSID_TEST", Apisid = "API_TEST",
        Secure1Psid = "SEC1_TEST", Secure3Psid = "SEC3_TEST"
    };

    [Fact]
    public async Task SendAsync_InjectsAuthorizationHeader()
    {
        HttpRequestMessage? captured = null;
        var inner = new MockHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var handler = new GvHttpClientHandler(() => Task.FromResult(TestCookies), inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://clients6.google.com/voice/v1/voiceclient/account/get");

        Assert.NotNull(captured);
        Assert.StartsWith("SAPISIDHASH", captured!.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task SendAsync_InjectsCookieHeader()
    {
        HttpRequestMessage? captured = null;
        var inner = new MockHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var handler = new GvHttpClientHandler(() => Task.FromResult(TestCookies), inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://clients6.google.com/test");

        Assert.Contains("SAPISID=SAP_TEST", captured!.Headers.GetValues("Cookie").First());
    }

    [Fact]
    public async Task SendAsync_InjectsOriginAndReferer()
    {
        HttpRequestMessage? captured = null;
        var inner = new MockHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.OK); });
        var handler = new GvHttpClientHandler(() => Task.FromResult(TestCookies), inner);
        var client = new HttpClient(handler);

        await client.GetAsync("https://clients6.google.com/test");

        Assert.Equal("https://voice.google.com", captured!.Headers.GetValues("Origin").First());
        Assert.Equal("https://voice.google.com/", captured.Headers.GetValues("Referer").First());
        Assert.Equal("0", captured.Headers.GetValues("X-Goog-AuthUser").First());
    }

    /// <summary>Simple test double for HttpMessageHandler.</summary>
    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(handler(request));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvHttpClientHandlerTests" -v n`
Expected: FAIL — `GvHttpClientHandler` does not exist

- [ ] **Step 3: Implement GvHttpClientHandler**

Create `src/RotaryPhoneController.GVBridge/Auth/GvHttpClientHandler.cs`:

```csharp
using System.Net.Http.Headers;

namespace RotaryPhoneController.GVBridge.Auth;

/// <summary>
/// DelegatingHandler that injects SAPISIDHASH authorization, cookies,
/// and required headers on every outgoing request to GV APIs.
/// </summary>
public class GvHttpClientHandler : DelegatingHandler
{
    private readonly Func<Task<GvCookieJar>> _getCookies;

    public GvHttpClientHandler(Func<Task<GvCookieJar>> getCookies, HttpMessageHandler inner)
        : base(inner)
    {
        _getCookies = getCookies;
    }

    public GvHttpClientHandler(Func<Task<GvCookieJar>> getCookies)
    {
        _getCookies = getCookies;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cookies = await _getCookies();
        var hash = GvSapisidHash.ComputeCurrent(cookies.Sapisid);

        request.Headers.Authorization = new AuthenticationHeaderValue("SAPISIDHASH", hash);
        request.Headers.TryAddWithoutValidation("Cookie", cookies.ToCookieHeader());
        request.Headers.TryAddWithoutValidation("Origin", "https://voice.google.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://voice.google.com/");
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");

        return await base.SendAsync(request, cancellationToken);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvHttpClientHandlerTests" -v n`
Expected: 3 passed

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Auth/GvHttpClientHandler.cs \
        src/RotaryPhoneController.GVBridge.Tests/Auth/GvHttpClientHandlerTests.cs
git commit -m "feat(gvapi): add GvHttpClientHandler for auto auth injection"
```

---

## Task 5: Protobuf-JSON Protocol Helpers

GV uses a positional array format (not standard protobuf). Responses look like `[[[[1,null,69]]]]`. We need helpers to safely navigate these nested arrays.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Protocol/GvProtobuf.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Protocol/GvProtobufTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Protocol/GvProtobufTests.cs`:

```csharp
using System.Text.Json;
using RotaryPhoneController.GVBridge.Protocol;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Protocol;

public class GvProtobufTests
{
    [Fact]
    public void GetString_ReturnsValueAtPath()
    {
        // Simulate api2thread/sendsms format: [null,null,null,null,"Hello","t.+1919..."]
        var json = JsonDocument.Parse("""[null,null,null,null,"Hello","t.+19193718044"]""");
        var result = GvProtobuf.GetString(json.RootElement, 4);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void GetString_ReturnsNullForMissingIndex()
    {
        var json = JsonDocument.Parse("""["a","b"]""");
        Assert.Null(GvProtobuf.GetString(json.RootElement, 5));
    }

    [Fact]
    public void GetInt_ReturnsValueFromNestedArray()
    {
        // threadinginfo/get: [[[[1,null,69]]]]
        var json = JsonDocument.Parse("""[[[[1,null,69]]]]""");
        var inner = GvProtobuf.GetArray(json.RootElement, 0, 0, 0);
        Assert.NotNull(inner);
        Assert.Equal(69, GvProtobuf.GetInt(inner!.Value, 2));
    }

    [Fact]
    public void BuildRequest_CreatesPositionalArray()
    {
        // Build: [null,null,null,null,"Hello","t.+1919"]
        var json = GvProtobuf.BuildArray(null, null, null, null, "Hello", "t.+1919");
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Hello", doc.RootElement[4].GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement[0].ValueKind);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvProtobufTests" -v n`
Expected: FAIL — `GvProtobuf` does not exist

- [ ] **Step 3: Implement GvProtobuf helpers**

Create `src/RotaryPhoneController.GVBridge/Protocol/GvProtobuf.cs`:

```csharp
using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Protocol;

/// <summary>
/// Helpers for GV's protobuf-JSON wire format — nested positional arrays
/// where field positions are fixed by the proto schema.
/// </summary>
public static class GvProtobuf
{
    /// <summary>Get a string at a given array index, or null if out of bounds/null.</summary>
    public static string? GetString(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    /// <summary>Get an int at a given array index, or null if out of bounds/null.</summary>
    public static int? GetInt(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    }

    /// <summary>Get a long at a given array index, or null if out of bounds/null.</summary>
    public static long? GetLong(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetInt64() : null;
    }

    /// <summary>Navigate nested arrays by successive indices. Returns null if any hop fails.</summary>
    public static JsonElement? GetArray(JsonElement root, params int[] path)
    {
        var current = root;
        foreach (var index in path)
        {
            if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                return null;
            current = current[index];
        }
        return current.ValueKind == JsonValueKind.Array ? current : null;
    }

    /// <summary>Build a positional JSON array from values. Nulls become JSON null.</summary>
    public static string BuildArray(params object?[] values)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartArray();
        foreach (var val in values)
        {
            switch (val)
            {
                case null: writer.WriteNullValue(); break;
                case string s: writer.WriteStringValue(s); break;
                case int i: writer.WriteNumberValue(i); break;
                case long l: writer.WriteNumberValue(l); break;
                case JsonElement el: el.WriteTo(writer); break;
                default: writer.WriteStringValue(val.ToString()); break;
            }
        }
        writer.WriteEndArray();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvProtobufTests" -v n`
Expected: 4 passed

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Protocol/GvProtobuf.cs \
        src/RotaryPhoneController.GVBridge.Tests/Protocol/GvProtobufTests.cs
git commit -m "feat(gvapi): add protobuf-JSON protocol helpers"
```

---

## Task 6: GvAccountClient (Health Check + Account Info)

This client calls `threadinginfo/get` (health check) and `account/get` (account info). It's the first real GV API call and validates that auth works.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvAccountClient.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvAccountClientTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/GvAccountClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using RotaryPhoneController.GVBridge.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvAccountClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task IsHealthyAsync_ReturnsTrueOn200()
    {
        // threadinginfo/get returns unread counts
        var handler = new MockHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[[[[1,null,0]]]]", Encoding.UTF8, "application/json")
            });
        var client = new GvAccountClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvAccountClient>.Instance);

        Assert.True(await client.IsHealthyAsync());
    }

    [Fact]
    public async Task IsHealthyAsync_ReturnsFalseOn401()
    {
        var handler = new MockHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new GvAccountClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvAccountClient>.Instance);

        Assert.False(await client.IsHealthyAsync());
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvAccountClientTests" -v n`
Expected: FAIL — `GvAccountClient` does not exist

- [ ] **Step 3: Implement GvAccountClient**

Create `src/RotaryPhoneController.GVBridge/Clients/GvAccountClient.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

public class GvAccountClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvAccountClient> _logger;

    public GvAccountClient(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvAccountClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Lightweight health check — calls threadinginfo/get.
    /// Returns true if cookies are valid, false if expired/invalid.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/threadinginfo/get?alt=protojson&key={_apiKey}";
            var content = new StringContent("[]", Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GV health check passed");
                return true;
            }

            _logger.LogWarning("GV health check failed: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GV health check error");
            return false;
        }
    }

    /// <summary>
    /// Get account info (phone numbers, settings).
    /// Returns the raw JsonDocument for the caller to extract what they need.
    /// </summary>
    public async Task<JsonDocument?> GetAccountAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/account/get?alt=protojson&key={_apiKey}";
            var content = new StringContent("[]", Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get GV account info");
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvAccountClientTests" -v n`
Expected: 2 passed

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvAccountClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvAccountClientTests.cs
git commit -m "feat(gvapi): add GvAccountClient with health check"
```

---

## Task 7: GvCallClient (Initiate, Hangup)

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvCallClient.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvCallClientTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/GvCallClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using RotaryPhoneController.GVBridge.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvCallClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    [Fact]
    public async Task InitiateAsync_PostsToCorrectEndpoint()
    {
        string? capturedUrl = null;
        var handler = new MockHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var client = new GvCallClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvCallClient>.Instance);

        await client.InitiateAsync("+15551234567");

        Assert.NotNull(capturedUrl);
        Assert.Contains("/call/create", capturedUrl!);
    }

    [Fact]
    public async Task HangupAsync_PostsToCorrectEndpoint()
    {
        string? capturedUrl = null;
        var handler = new MockHandler(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var client = new GvCallClient(new HttpClient(handler), BaseUrl, ApiKey,
            NullLogger<GvCallClient>.Instance);

        await client.HangupAsync("call-123");

        Assert.NotNull(capturedUrl);
        Assert.Contains("/call/cancel", capturedUrl!);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvCallClientTests" -v n`
Expected: FAIL — `GvCallClient` does not exist

- [ ] **Step 3: Implement GvCallClient**

Create `src/RotaryPhoneController.GVBridge/Clients/GvCallClient.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

public class GvCallClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvCallClient> _logger;

    public GvCallClient(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvCallClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Initiate an outbound call to the given phone number.
    /// Uses GV's call/create endpoint which triggers a callback to the linked phone.
    /// </summary>
    public async Task<JsonDocument?> InitiateAsync(string e164Number, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/call/create?alt=protojson&key={_apiKey}";
            var body = GvProtobuf.BuildArray(null, e164Number);
            var content = new StringContent(body, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("Call initiated to {Number}", e164Number);
            return JsonDocument.Parse(responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate call to {Number}", e164Number);
            return null;
        }
    }

    /// <summary>
    /// Hang up / cancel an active or ringing call.
    /// </summary>
    public async Task<bool> HangupAsync(string callId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/call/cancel?alt=protojson&key={_apiKey}";
            var body = GvProtobuf.BuildArray(null, callId);
            var content = new StringContent(body, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Call {CallId} hung up", callId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to hangup call {CallId}", callId);
            return false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvCallClientTests" -v n`
Expected: 2 passed

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvCallClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvCallClientTests.cs
git commit -m "feat(gvapi): add GvCallClient for call initiate/hangup"
```

---

## Task 8: GvSmsClient

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`

- [ ] **Step 1: Implement GvSmsClient**

Create `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

public class GvSmsClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvSmsClient> _logger;

    public GvSmsClient(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvSmsClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    /// <summary>
    /// Send an SMS via GV API. Thread ID format: "t.+{e164number}".
    /// </summary>
    public async Task<bool> SendAsync(string toNumber, string body, CancellationToken ct = default)
    {
        try
        {
            var threadId = $"t.{toNumber}";
            var url = $"{_baseUrl}/api2thread/sendsms?alt=protojson&key={_apiKey}";
            var payload = GvProtobuf.BuildArray(null, null, null, null, body, threadId);
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("SMS sent to {Number}", toNumber);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send SMS to {Number}", toNumber);
            return false;
        }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs
git commit -m "feat(gvapi): add GvSmsClient for sending SMS"
```

---

## Task 9: GvSignalerClient (Incoming Call Detection)

This is the most complex new component. It implements Google's long-poll channel protocol.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Signaler/SignalerEvent.cs`
- Create: `src/RotaryPhoneController.GVBridge/Signaler/GvSignalerClient.cs`

- [ ] **Step 1: Create event models**

Create `src/RotaryPhoneController.GVBridge/Signaler/SignalerEvent.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Signaler;

public abstract record SignalerEvent;
public record IncomingCallEvent(string CallId, string CallerNumber) : SignalerEvent;
public record CallEndedEvent(string CallId) : SignalerEvent;
public record SmsReceivedEvent(string From, string Body, string ThreadId) : SignalerEvent;
public record UnknownSignalerEvent(string RawPayload) : SignalerEvent;
```

- [ ] **Step 2: Implement GvSignalerClient**

Create `src/RotaryPhoneController.GVBridge/Signaler/GvSignalerClient.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Signaler;

/// <summary>
/// Long-poll client for Google's signaler channel.
/// Protocol: VER=8, CVER=22 at signaler-pa.clients6.google.com/punctual/multi-watch/channel
///
/// Lifecycle:
///   ConnectAsync() → chooseServer → create channel (get SID/gsessionid) → start PollLoopAsync
///   DisconnectAsync() → cancel poll loop
/// </summary>
public class GvSignalerClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<GvSignalerClient> _logger;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private string? _sid;
    private string? _gsessionId;
    private int _lastAid;

    public event Action<IncomingCallEvent>? OnIncomingCall;
    public event Action<CallEndedEvent>? OnCallEnded;
    public event Action<SmsReceivedEvent>? OnSmsReceived;
    public bool IsConnected { get; private set; }

    public GvSignalerClient(HttpClient http, string baseUrl, ILogger<GvSignalerClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Choose server
            var serverUrl = await ChooseServerAsync(ct);

            // Step 2: Create channel (get SID + gsessionid)
            await CreateChannelAsync(serverUrl, ct);

            // Step 3: Start poll loop
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pollTask = PollLoopAsync(serverUrl, _pollCts.Token);
            IsConnected = true;
            _logger.LogInformation("Signaler connected (SID={Sid})", _sid?[..8]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect signaler");
            IsConnected = false;
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_pollCts != null)
        {
            await _pollCts.CancelAsync();
            if (_pollTask != null)
            {
                try { await _pollTask; } catch (OperationCanceledException) { }
            }
            _pollCts.Dispose();
            _pollCts = null;
        }
        IsConnected = false;
        _logger.LogInformation("Signaler disconnected");
    }

    private async Task<string> ChooseServerAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/punctual/v1/chooseServer";
        var response = await _http.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        // Response contains server hostname; if empty, use default base URL
        return string.IsNullOrWhiteSpace(body) ? _baseUrl : body.Trim();
    }

    private async Task CreateChannelAsync(string serverUrl, CancellationToken ct)
    {
        var url = $"{serverUrl}/punctual/multi-watch/channel?VER=8&CVER=22&RID=rpc&t=1";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        // Parse SID and gsessionid from response
        // Response format varies; typically contains JSON array with session params
        try
        {
            var doc = JsonDocument.Parse(body);
            // Navigate the response to find SID — exact structure depends on Google's response
            // This will need refinement during integration testing with live API
            _sid = ExtractSessionParam(doc, "SID");
            _gsessionId = ExtractSessionParam(doc, "gsessionid");
            _lastAid = 0;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse channel creation response, using raw parsing");
            // Fallback: extract from URL params or headers in response
            _sid = "unknown";
            _gsessionId = "unknown";
        }
    }

    private async Task PollLoopAsync(string serverUrl, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        var maxRetryDelay = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"{serverUrl}/punctual/multi-watch/channel" +
                    $"?VER=8&CVER=22&RID=rpc&SID={_sid}&gsessionid={_gsessionId}" +
                    $"&AID={_lastAid}&TYPE=xmlhttp&CI=0";

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Signaler poll returned {Status}", response.StatusCode);
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                        response.StatusCode == System.Net.HttpStatusCode.Gone)
                    {
                        // Session expired; reconnect
                        _logger.LogInformation("Signaler session expired, reconnecting");
                        await CreateChannelAsync(serverUrl, ct);
                    }
                    await Task.Delay(retryDelay, ct);
                    retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay.TotalSeconds));
                    continue;
                }

                retryDelay = TimeSpan.FromSeconds(1); // reset on success
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!string.IsNullOrWhiteSpace(body))
                {
                    var events = ParseSignalerResponse(body);
                    foreach (var evt in events)
                    {
                        switch (evt)
                        {
                            case IncomingCallEvent ic:
                                _logger.LogInformation("Signaler: incoming call from {Number}", ic.CallerNumber);
                                OnIncomingCall?.Invoke(ic);
                                break;
                            case CallEndedEvent ce:
                                _logger.LogInformation("Signaler: call {CallId} ended", ce.CallId);
                                OnCallEnded?.Invoke(ce);
                                break;
                            case SmsReceivedEvent sms:
                                _logger.LogInformation("Signaler: SMS from {From}", sms.From);
                                OnSmsReceived?.Invoke(sms);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Signaler poll error, retrying in {Delay}", retryDelay);
                await Task.Delay(retryDelay, ct);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay.TotalSeconds));
            }
        }
    }

    private List<SignalerEvent> ParseSignalerResponse(string body)
    {
        var events = new List<SignalerEvent>();

        try
        {
            // Google's signaler responses use a chunked format.
            // Each chunk: line with byte count, then JSON array payload.
            // The payload contains arrays where position indicates event type.
            // Exact parsing will need refinement during integration testing.
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (!line.StartsWith('['))
                    continue; // skip byte count lines

                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array)
                    continue;

                for (int i = 0; i < root.GetArrayLength(); i++)
                {
                    var item = root[i];
                    var evt = ClassifyEvent(item);
                    if (evt != null)
                    {
                        events.Add(evt);
                        // Update AID from the event array
                        if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0 &&
                            item[0].ValueKind == JsonValueKind.Number)
                        {
                            _lastAid = item[0].GetInt32();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Signaler response parse error (raw: {Body})",
                body.Length > 500 ? body[..500] : body);
        }

        return events;
    }

    private SignalerEvent? ClassifyEvent(JsonElement item)
    {
        // Signaler events are positional arrays. The exact structure needs
        // to be determined via live capture. Common patterns from GVResearch docs:
        // - Incoming call: contains SDP offer with "o=xavier" and caller info
        // - Call ended: contains hangup signal
        // - SMS: contains message text and sender
        //
        // For now, inspect string content for known patterns.
        var text = item.ToString();

        if (text.Contains("o=xavier") || text.Contains("incoming_call", StringComparison.OrdinalIgnoreCase))
        {
            // Extract caller number — will need refinement
            return new IncomingCallEvent(
                CallId: $"gv-{Guid.NewGuid():N}",
                CallerNumber: ExtractCallerNumber(item) ?? "Unknown");
        }

        if (text.Contains("call_ended", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hangup", StringComparison.OrdinalIgnoreCase))
        {
            return new CallEndedEvent(CallId: "");
        }

        if (text.Contains("sms", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("message", StringComparison.OrdinalIgnoreCase))
        {
            return new SmsReceivedEvent(From: "", Body: "", ThreadId: "");
        }

        return null;
    }

    private static string? ExtractCallerNumber(JsonElement element)
    {
        // Walk the element looking for a string that looks like a phone number
        var text = element.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\+\d{10,15}");
        return match.Success ? match.Value : null;
    }

    private static string? ExtractSessionParam(JsonDocument doc, string paramName)
    {
        // Navigate the channel creation response for session parameters
        // Structure varies; this is a best-effort parser
        var text = doc.RootElement.ToString();
        var key = $"\"{paramName}\"";
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // Find the value after the key (skip key, colon/comma, quotes)
        var valueStart = text.IndexOf('"', idx + key.Length);
        if (valueStart < 0) return null;
        var valueEnd = text.IndexOf('"', valueStart + 1);
        if (valueEnd < 0) return null;
        return text[(valueStart + 1)..valueEnd];
    }
}
```

**Note to implementer:** The signaler parsing (`ParseSignalerResponse`, `ClassifyEvent`) is intentionally rough. The exact wire format requires live capture against Google's API to finalize. This scaffolding handles the protocol lifecycle (connect, poll, reconnect) while the event classification will be refined during integration testing. Log the raw responses at Debug level to capture the actual format.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Signaler/SignalerEvent.cs \
        src/RotaryPhoneController.GVBridge/Signaler/GvSignalerClient.cs
git commit -m "feat(gvapi): add GvSignalerClient long-poll channel for incoming calls"
```

---

## Task 10: GVApiAdapter (New ICallAdapter)

The main adapter wiring everything together.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`
- Test: `src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterTests.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.Core;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Adapters;

public class GVApiAdapterTests
{
    [Fact]
    public void Mode_IsGVApi()
    {
        var adapter = CreateAdapter();
        Assert.Equal(CallAdapterMode.GVApi, adapter.Mode);
    }

    [Fact]
    public void IsAvailable_DefaultsFalse()
    {
        var adapter = CreateAdapter();
        Assert.False(adapter.IsAvailable);
    }

    [Fact]
    public async Task PlaceCallAsync_ThrowsWhenNotAvailable()
    {
        var adapter = CreateAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.PlaceCallAsync("+15551234567"));
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
            NullLogger<GVApiAdapter>.Instance);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GVApiAdapterTests" -v n`
Expected: FAIL — `GVApiAdapter` does not exist

- [ ] **Step 3: Implement GVApiAdapter**

Create `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.Core;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.GVBridge.Signaler;

namespace RotaryPhoneController.GVBridge.Adapters;

public class GVApiAdapter : ICallAdapter
{
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GVApiAdapter> _logger;

    private GvCookieStore? _cookieStore;
    private GvCookieJar? _cookies;
    private HttpClient? _gvHttp;
    private GvAccountClient? _accountClient;
    private GvCallClient? _callClient;
    private GvSmsClient? _smsClient;
    private GvSignalerClient? _signalerClient;
    private GVAudioBridgeService? _audioBridge;
    private GVBridgeService? _bridgeService;

    private string? _activeCallId;
    private Timer? _healthCheckTimer;

    public CallAdapterMode Mode => CallAdapterMode.GVApi;
    public bool IsAvailable { get; private set; }

    public event Action<bool>? OnAvailabilityChanged;
    public event Action<string>? OnIncomingCall;
    public event Action? OnCallAnswered;
    public event Action? OnCallEnded;
    public event Action<string>? OnDtmfReceived;

    public GVApiAdapter(IOptions<GVBridgeConfig> config, ILogger<GVApiAdapter> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Inject optional dependencies that are created by the DI container.
    /// Called after construction to avoid circular dependencies.
    /// </summary>
    public void SetServices(
        GVBridgeService bridgeService,
        GVAudioBridgeService audioBridge)
    {
        _bridgeService = bridgeService;
        _audioBridge = audioBridge;
    }

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GVApiAdapter activating");

        // 1. Load cookies
        _cookieStore = new GvCookieStore(_config.CookieFilePath, _config.CookieEncryptionKey);
        _cookies = await _cookieStore.LoadAsync();
        if (_cookies == null || !_cookies.IsComplete)
        {
            _logger.LogError("No valid GV cookies found. Run 'gv-login' to authenticate.");
            SetAvailability(false);
            return;
        }

        // 2. Create authenticated HttpClient
        var handler = new GvHttpClientHandler(() => Task.FromResult(_cookies!), new HttpClientHandler());
        _gvHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // 3. Create API clients
        _accountClient = new GvAccountClient(_gvHttp, _config.GvApiBaseUrl, _config.GvApiKey,
            NullLogger<GvAccountClient>.Instance);
        _callClient = new GvCallClient(_gvHttp, _config.GvApiBaseUrl, _config.GvApiKey,
            NullLogger<GvCallClient>.Instance);
        _smsClient = new GvSmsClient(_gvHttp, _config.GvApiBaseUrl, _config.GvApiKey,
            NullLogger<GvSmsClient>.Instance);

        // 4. Health check
        var healthy = await _accountClient.IsHealthyAsync(ct);
        if (!healthy)
        {
            _logger.LogError("GV API health check failed — cookies may be expired. Run 'gv-login'.");
            SetAvailability(false);
            return;
        }

        // 5. Connect signaler for incoming calls
        _signalerClient = new GvSignalerClient(_gvHttp, _config.SignalerBaseUrl,
            NullLogger<GvSignalerClient>.Instance);
        _signalerClient.OnIncomingCall += HandleSignalerIncomingCall;
        _signalerClient.OnCallEnded += HandleSignalerCallEnded;

        try
        {
            await _signalerClient.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signaler connection failed — incoming calls won't be detected. " +
                "Outbound calls and SMS still work.");
        }

        // 6. Start periodic health check
        _healthCheckTimer = new Timer(
            async _ => await PeriodicHealthCheckAsync(),
            null,
            TimeSpan.FromMinutes(_config.CookieHealthCheckIntervalMinutes),
            TimeSpan.FromMinutes(_config.CookieHealthCheckIntervalMinutes));

        SetAvailability(true);
        _logger.LogInformation("GVApiAdapter activated (signaler={Signaler})",
            _signalerClient.IsConnected ? "connected" : "disconnected");
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;

        if (_signalerClient != null)
        {
            _signalerClient.OnIncomingCall -= HandleSignalerIncomingCall;
            _signalerClient.OnCallEnded -= HandleSignalerCallEnded;
            await _signalerClient.DisconnectAsync();
        }

        _gvHttp?.Dispose();
        SetAvailability(false);
        _logger.LogInformation("GVApiAdapter deactivated");
    }

    public async Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("GVApiAdapter is not available");

        var callId = $"gv-{Guid.NewGuid():N}";
        _activeCallId = callId;

        var result = await _callClient!.InitiateAsync(e164Number, ct);
        _logger.LogInformation("Placed call {CallId} to {Number}", callId, e164Number);
        return callId;
    }

    public Task AnswerCallAsync(CancellationToken ct = default)
    {
        // In GVApi mode, answering is handled by OnCallAnsweredOnRotaryPhoneAsync
        // when the SIP 200 OK comes from the HT801
        _logger.LogDebug("AnswerCallAsync called (no-op, SIP-driven)");
        return Task.CompletedTask;
    }

    public async Task HangUpAsync(CancellationToken ct = default)
    {
        if (_activeCallId != null)
        {
            await _callClient!.HangupAsync(_activeCallId, ct);
            _activeCallId = null;
        }
    }

    public async Task OnCallAnsweredOnRotaryPhoneAsync()
    {
        _logger.LogInformation("GVApi: rotary phone answered — sending answer command to extension and starting audio bridge");

        // Send answer + mute commands to extension (Phase 1: extension handles WebRTC)
        if (_bridgeService != null)
        {
            await _bridgeService.SendMessageAsync(new AnswerMessage());
            // Give the browser a moment to click, then mute
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await _bridgeService.SendMessageAsync(new MuteTabMessage());
            });
        }

        // Start audio bridge (RTP to HT801)
        if (_audioBridge != null)
            await _audioBridge.StartAsync();
    }

    public async Task OnCallHungUpAsync()
    {
        _logger.LogInformation("GVApi: stopping audio bridge and ending call");

        if (_audioBridge != null)
            await _audioBridge.StopAsync();

        // Send hangup + unmute to extension
        if (_bridgeService != null)
        {
            await _bridgeService.SendMessageAsync(new HangupMessage());
            await _bridgeService.SendMessageAsync(new UnmuteTabMessage());
        }

        if (_activeCallId != null)
        {
            await _callClient!.HangupAsync(_activeCallId);
            _activeCallId = null;
        }
    }

    private void HandleSignalerIncomingCall(IncomingCallEvent evt)
    {
        _activeCallId = evt.CallId;
        _logger.LogInformation("GVApi: incoming call from {Number}", evt.CallerNumber);
        OnIncomingCall?.Invoke(evt.CallerNumber);
    }

    private void HandleSignalerCallEnded(CallEndedEvent evt)
    {
        _activeCallId = null;
        _logger.LogInformation("GVApi: call ended via signaler");
        OnCallEnded?.Invoke();
    }

    private async Task PeriodicHealthCheckAsync()
    {
        if (_accountClient == null) return;
        var healthy = await _accountClient.IsHealthyAsync();
        if (!healthy && IsAvailable)
        {
            _logger.LogWarning("GV API health check failed — cookies may have expired");
            SetAvailability(false);
        }
        else if (healthy && !IsAvailable)
        {
            _logger.LogInformation("GV API health check recovered");
            SetAvailability(true);
        }
    }

    private void SetAvailability(bool available)
    {
        if (IsAvailable != available)
        {
            IsAvailable = available;
            OnAvailabilityChanged?.Invoke(available);
        }
    }
}
```

**Note:** `MuteTabMessage` and `UnmuteTabMessage` are new extension message types that need to be added to `ExtensionMessage.cs` — handled in Task 12 (extension stripping).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GVApiAdapterTests" -v n`
Expected: 3 passed

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs \
        src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterTests.cs
git commit -m "feat(gvapi): add GVApiAdapter implementing ICallAdapter"
```

---

## Task 11: Extension Message Types for Mute/Unmute

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Models/ExtensionMessage.cs`

- [ ] **Step 1: Add MuteTabMessage and UnmuteTabMessage**

Add to the `[JsonDerivedType]` attributes on `ExtensionMessage`:

```csharp
[JsonDerivedType(typeof(MuteTabMessage), "muteTab")]
[JsonDerivedType(typeof(UnmuteTabMessage), "unmuteTab")]
```

Add the message classes at the bottom:

```csharp
public class MuteTabMessage : ExtensionMessage { }
public class UnmuteTabMessage : ExtensionMessage { }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Models/ExtensionMessage.cs
git commit -m "feat(gvapi): add MuteTab/UnmuteTab extension messages"
```

---

## Task 12: Strip Chrome Extension to Audio + Button Click + Mute

Remove call detection polling, SMS interception, and HTTP relay from the extension. Keep: tabCapture audio, answer/hangup button clicking via DOM, and tab audio muting via DOM.

**Files:**
- Modify: `ChromeExtension/content/gv-bridge.js`
- Modify: `ChromeExtension/background/service-worker.js`

- [ ] **Step 1: Strip gv-bridge.js**

In `ChromeExtension/content/gv-bridge.js`, make these changes:

**Remove** the `startCallPolling()` function and its call (lines ~353-451). This is the 500ms DOM polling loop for call state detection — replaced by `GvSignalerClient`.

**Remove** the `fetch()` interceptor (lines ~307-333). SMS detection is replaced by `GvSmsClient` + signaler.

**Add** a `muteTab()` handler in the WebSocket message handler:

```javascript
// In the WebSocket onmessage handler, add cases for muteTab/unmuteTab:
case 'muteTab':
    document.querySelectorAll('audio,video').forEach(e => e.muted = true);
    try {
        const contexts = window.__audioContexts || [];
        contexts.forEach(ctx => { if (ctx.state === 'running') ctx.suspend(); });
    } catch(e) {}
    console.log('[GV Bridge] Tab muted');
    break;

case 'unmuteTab':
    document.querySelectorAll('audio,video').forEach(e => e.muted = false);
    console.log('[GV Bridge] Tab unmuted');
    break;
```

**Keep**: `connect()`, `sendToServer()`, `startAudioCapture()`, `stopAudioCapture()`, `answer()`, `hangup()`, `dial()`, WebSocket connection and message handler for commands.

- [ ] **Step 2: Strip service-worker.js**

In `ChromeExtension/background/service-worker.js`:

**Remove** the `checkPendingCommand` handler (lines ~93-102) — server no longer sends commands via HTTP polling.

**Remove** the `postCallEvent` handler (lines ~104-118) — call events now come from signaler, not extension.

**Keep**: `requestTabCapture`, `createOffscreen`, `audioFrame` relay, tab tracking.

- [ ] **Step 3: Test extension loads without errors**

Load the extension in Chrome, open `voice.google.com`, check the extension console for errors.
Expected: No errors. Audio capture should still work. Call polling should not be running.

- [ ] **Step 4: Commit**

```bash
git add ChromeExtension/content/gv-bridge.js \
        ChromeExtension/background/service-worker.js
git commit -m "refactor(extension): strip call detection and SMS intercept, keep audio + button click"
```

---

## Task 13: DI Registration and Wiring

Wire `GVApiAdapter` into the DI container alongside `GVBrowserAdapter`. Mode selection determines which is active.

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`

- [ ] **Step 1: Register GVApiAdapter in DI**

In `GVBridgeServiceExtensions.AddGVBridge()`, after the existing `GVBrowserAdapter` registration, add:

```csharp
// GV API adapter (direct HTTP API, no CDP)
services.AddSingleton<GVApiAdapter>();
services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVApiAdapter>());
```

Also add a post-build setup to inject services into GVApiAdapter:

```csharp
// Wire up GVApiAdapter's optional service dependencies
services.AddSingleton<IHostedService>(sp =>
{
    var apiAdapter = sp.GetRequiredService<GVApiAdapter>();
    var bridgeService = sp.GetRequiredService<GVBridgeService>();
    var audioBridge = sp.GetRequiredService<GVAudioBridgeService>();
    apiAdapter.SetServices(bridgeService, audioBridge);
    // Return a no-op hosted service — the setup is the side effect
    return new SetupHostedService(() => { });
});
```

Add the helper class at the bottom of the file:

```csharp
internal class SetupHostedService(Action setup) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { setup(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 2: Update default mode handling**

In the server's startup code where `DefaultMode` is read, ensure `"GVApi"` maps to `CallAdapterMode.GVApi`. Find the mode parsing logic (likely in `Program.cs` or the adapter registry setup) and add the case.

- [ ] **Step 3: Build and run existing tests**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj && dotnet test src/RotaryPhoneController.GVBridge.Tests -v n`
Expected: Build succeeded, all tests pass

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs
git commit -m "feat(gvapi): wire GVApiAdapter into DI and adapter registry"
```

---

## Task 14: Cookie Extraction Tool (Playwright)

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Tools/GvLoginTool.cs`
- Modify: `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj` (add Playwright NuGet)

- [ ] **Step 1: Add Playwright NuGet package**

Add to `src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`:

```xml
<PackageReference Include="Microsoft.Playwright" Version="1.*" />
```

Run: `dotnet restore src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`

- [ ] **Step 2: Implement GvLoginTool**

Create `src/RotaryPhoneController.GVBridge/Tools/GvLoginTool.cs`:

```csharp
using Microsoft.Playwright;
using RotaryPhoneController.GVBridge.Auth;
using RotaryPhoneController.GVBridge.Clients;
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Tools;

/// <summary>
/// Opens a Playwright browser for one-time Google Voice login.
/// Extracts the 7 required cookies and saves them encrypted.
/// </summary>
public class GvLoginTool
{
    private static readonly string[] RequiredCookieNames =
        ["SAPISID", "SID", "HSID", "SSID", "APISID", "__Secure-1PSID", "__Secure-3PSID"];

    /// <summary>
    /// Launch Playwright Chromium, navigate to voice.google.com,
    /// wait for user to log in, extract cookies.
    /// </summary>
    public static async Task<GvCookieJar?> LoginAsync(ILogger logger, CancellationToken ct = default)
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

        // Wait for the user to complete login and reach the GV inbox
        // The inbox URL contains "/u/" or shows the messaging UI
        logger.LogInformation("Waiting for login to complete (looking for GV inbox)...");
        try
        {
            await page.WaitForURLAsync("**/voice.google.com/u/**",
                new() { Timeout = 120_000 }); // 2 minute timeout
        }
        catch (TimeoutException)
        {
            logger.LogError("Login timed out after 2 minutes");
            return null;
        }

        // Give the page a moment to settle and set all cookies
        await page.WaitForTimeoutAsync(3000);

        // Extract cookies
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
            var missing = RequiredCookieNames.Where(name => name switch
            {
                "SAPISID" => string.IsNullOrEmpty(jar.Sapisid),
                "SID" => string.IsNullOrEmpty(jar.Sid),
                "HSID" => string.IsNullOrEmpty(jar.Hsid),
                "SSID" => string.IsNullOrEmpty(jar.Ssid),
                "APISID" => string.IsNullOrEmpty(jar.Apisid),
                "__Secure-1PSID" => string.IsNullOrEmpty(jar.Secure1Psid),
                "__Secure-3PSID" => string.IsNullOrEmpty(jar.Secure3Psid),
                _ => false
            });
            logger.LogError("Missing cookies: {Missing}", string.Join(", ", missing));
            return null;
        }

        logger.LogInformation("All 7 cookies extracted successfully");
        return jar;
    }

    /// <summary>
    /// Full login + save + verify flow.
    /// </summary>
    public static async Task<bool> LoginAndSaveAsync(
        string cookieFilePath, string encryptionKey,
        string gvApiBaseUrl, string gvApiKey,
        ILogger logger, CancellationToken ct = default)
    {
        var jar = await LoginAsync(logger, ct);
        if (jar == null) return false;

        // Save encrypted
        var store = new GvCookieStore(cookieFilePath, encryptionKey);
        await store.SaveAsync(jar);
        logger.LogInformation("Cookies saved to {Path}", cookieFilePath);

        // Verify with health check
        var handler = new GvHttpClientHandler(() => Task.FromResult(jar), new HttpClientHandler());
        using var http = new HttpClient(handler);
        var account = new GvAccountClient(http, gvApiBaseUrl, gvApiKey,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GvAccountClient>.Instance);

        var healthy = await account.IsHealthyAsync(ct);
        if (healthy)
        {
            logger.LogInformation("Health check passed — cookies are valid");
        }
        else
        {
            logger.LogWarning("Health check failed — cookies may not work. " +
                "Try logging in again or check the API key.");
        }

        return healthy;
    }
}
```

- [ ] **Step 3: Add DevTools paste fallback**

Add a static method to `GvLoginTool.cs` for manual cookie import:

```csharp
/// <summary>
/// Import cookies from a JSON string (pasted from Chrome DevTools Application tab).
/// Expected format: [{"name":"SID","value":"...","domain":".google.com",...},...]
/// </summary>
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

        // Verify
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
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Tools/GvLoginTool.cs \
        src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj
git commit -m "feat(gvapi): add Playwright cookie extraction tool with DevTools fallback"
```

---

## Task 15: Delete GVBrowserAdapter

Now that `GVApiAdapter` is in place, remove the old adapter.

**Files:**
- Delete: `src/RotaryPhoneController.GVBridge/Adapters/GVBrowserAdapter.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`
- Modify: `src/RotaryPhoneController.GVBridge.Tests/GVBrowserAdapterTests.cs`

- [ ] **Step 1: Remove GVBrowserAdapter registration from DI**

In `GVBridgeServiceExtensions.AddGVBridge()`, remove:

```csharp
services.AddSingleton<GVBrowserAdapter>();
services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVBrowserAdapter>());
```

- [ ] **Step 2: Delete GVBrowserAdapter.cs**

Delete: `src/RotaryPhoneController.GVBridge/Adapters/GVBrowserAdapter.cs`

- [ ] **Step 3: Update or remove GVBrowserAdapterTests**

Either delete `GVBrowserAdapterTests.cs` or rename it to test `GVApiAdapter` equivalents. The `GVApiAdapterTests` from Task 10 cover the same ground, so deleting is cleaner.

Delete: `src/RotaryPhoneController.GVBridge.Tests/GVBrowserAdapterTests.cs`

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj && dotnet test -v n`
Expected: Build succeeded, all tests pass (no references to GVBrowserAdapter remain)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(gvapi): remove GVBrowserAdapter, replaced by GVApiAdapter"
```

---

## Task 16: Integration Smoke Test

Verify the full stack wires up and starts without errors.

- [ ] **Step 1: Run all tests**

Run: `dotnet test -v n`
Expected: All tests pass

- [ ] **Step 2: Start the server**

Run: `dotnet run --project src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Server starts. Logs should show:
- "GVApiAdapter activating"
- Either "No valid GV cookies found" (expected without cookies) or health check result
- No unhandled exceptions

- [ ] **Step 3: Verify no CDP references remain**

Search for CDP artifacts:

Run: `grep -r "9224\|ClickGv\|MuteGvTab\|remote-debugging" src/ --include="*.cs"`
Expected: No matches (all CDP code removed)

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "chore(gvapi): integration smoke test, verify CDP fully removed"
```

---

## Summary

| Task | Component | Complexity |
|------|-----------|-----------|
| 1 | Config + enum | Low |
| 2 | SAPISIDHASH auth | Low |
| 3 | Encrypted cookie store | Low |
| 4 | GvHttpClientHandler | Low |
| 5 | Protobuf-JSON helpers | Low |
| 6 | GvAccountClient | Low |
| 7 | GvCallClient | Low |
| 8 | GvSmsClient | Low |
| 9 | GvSignalerClient | High (needs live refinement) |
| 10 | GVApiAdapter | Medium |
| 11 | Extension message types | Low |
| 12 | Strip extension | Medium |
| 13 | DI wiring | Low |
| 14 | Playwright login tool | Medium |
| 15 | Delete GVBrowserAdapter | Low |
| 16 | Integration smoke test | Low |

**Critical path:** Tasks 1-4 (auth foundation) → 5-8 (API clients) → 9 (signaler) → 10 (adapter) → 13 (wiring) → 15-16 (cleanup).

**Parallelizable:** Tasks 6, 7, 8 (API clients) can be built in parallel after Task 5. Task 14 (Playwright) can be built in parallel with Tasks 9-13. Task 12 (extension strip) can be built in parallel with Tasks 10-11.

## Deferred (not needed for Phase 1 MVP)

- **GvThreadClient** — listed in spec for thread/voicemail listing. Not in any call flow. Add when call history or voicemail features are needed.
- **Phase 2: WebRtcAudioBridge** — pending GVResearch WebRTC implementation. Tracked separately.
- **Cookie auto-refresh** — `RotateCookies` endpoint is unverified. Manual re-login via Playwright is sufficient for now.
