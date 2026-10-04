# PR2 Plan — `feat(gv): voicemail REST + audio proxy/cache`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Arc:** `docs/plans/gv-voicemail-sms-arc.md`
**ADR (source of truth):** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` (§3.2, §6.1, §6.2, §6.4, §10 row PR2, §11 step 3)
**Queue row:** `docs/BUILDER_QUEUE.md` → `GV-VM-SMS-2`
**Depends on:** PR1 (`GV-VM-SMS-1`) — uses `GvVoicemailClient`, `GvVoicemailNode`, the parser seam, and the `IGvAuthenticatedClientProvider` seam.
**Sensitivity:** Auto-merge eligible (read-only; cache is local disk; no GV writes; no non-LAN exposure added).

---

## Goal

Expose the **voicemail REST surface** (ADR §6.2) and the **audio proxy + disk cache** (ADR §6.4):

| Method | Route | Returns |
|---|---|---|
| GET | `/api/gvbridge/voicemail` | `VoicemailListDto` |
| GET | `/api/gvbridge/voicemail/{id}` | `VoicemailItemDto` |
| GET | `/api/gvbridge/voicemail/{id}/audio` | `audio/mpeg` byte stream (range-capable) |

All GV media flows **Google → RotaryPhone → RadioConsole, never Google → RadioConsole** (ADR §6.4 —
RadioConsole has no cookies; a redirect would 401). First fetch writes to a small on-disk cache;
later requests serve from cache with `Accept-Ranges: bytes` so the HTML5 `<audio>` scrubber works.

## Key decisions baked into this plan

1. **DTO mapping lives in the controller** (ADR §7): internal `GvVoicemailNode` (PR1) → public
   `VoicemailItemDto` (§6.1). Google's wire shape never reaches the public contract.
2. **`AudioUrl` is RotaryPhone-relative** (`/api/gvbridge/voicemail/{id}/audio`) — the controller
   constructs it; RadioConsole just uses it as an `<audio src>`.
3. **The recording fetch path is UNVERIFIED** (ADR §3.2, §11 step 3). Quarantine it behind a small
   `IGvRecordingFetcher` seam (same philosophy as PR1's parser seam) so the live correction of the
   media URL/`id` shape is a one-file change. The `GvVoicemailClient.GetRecordingStreamAsync` added
   here calls through that fetcher.
4. **Retention is configurable** (ADR §6.4, §12 q3): `VoicemailCacheRetentionDays` (default 7) and
   `VoicemailCacheMaxBytes` (default 200 MB). Surfaced in `GVBridgeConfig` + `appsettings.json` so the
   owner can adjust without code change. (Per project memory, GVBridge config is server-side; there is
   no Blazor UI in THIS repo — the active UI lives in the RTest repo — so no UI control is added here.)

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Clients/
  IGvRecordingFetcher.cs        -- seam isolating the UNVERIFIED media-fetch URL/id shape
  GvRecordingFetcher.cs         -- default impl (recording/get?id=… best-known, ADR §3.2/§11 step 3)
Services/
  GvVoicemailCache.cs           -- disk cache: get-or-fetch, range stream, age/size eviction
Api/
  GvVoicemailController.cs      -- /api/gvbridge/voicemail/*
  GvBridgeReadDtos.cs           -- VoicemailItemDto, VoicemailListDto (§6.1) [shared w/ PR3]
```

### New test files (in `src/RotaryPhoneController.GVBridge.Tests/`)

```
Clients/
  GvRecordingFetcherTests.cs
Services/
  GvVoicemailCacheTests.cs
Api/
  GvVoicemailControllerTests.cs
```

### Modified files

```
Clients/GvVoicemailClient.cs                              -- add GetRecordingStreamAsync via fetcher seam
Models/GVBridgeConfig.cs                                  -- add cache dir + retention config
Extensions/GVBridgeServiceExtensions.cs                  -- register fetcher, cache, voicemail client
src/RotaryPhoneController.Server/appsettings.json         -- voicemail cache config keys
```

---

## Task 1: Public read DTOs (§6.1)

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs`

> These are the EXACT §6.1 records. PR3 reuses the SMS records from this same file — for PR2 add ONLY
> the voicemail records; PR3 appends the SMS ones. (If PR3 lands first, it creates the file with SMS
> records and PR2 appends voicemail — the file is shared; add, don't overwrite.)

- [ ] **Step 1: Create the voicemail DTOs**

Create `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Api;

/// <summary>
/// Public cross-service voicemail item (ADR §6.1). Serialized camelCase; RadioConsole's Radio.Web
/// is case-insensitive. AudioUrl is RotaryPhone-relative — RadioConsole uses it as an &lt;audio src&gt;.
/// </summary>
public record VoicemailItemDto(
    string Id,
    string ThreadId,
    string FromNumber,
    string? FromName,
    DateTime ReceivedAt,
    int DurationSeconds,
    bool IsRead,
    string? Transcript,
    string AudioUrl);

public record VoicemailListDto(
    IReadOnlyList<VoicemailItemDto> Items,
    string? NextPageToken,
    DateTime FetchedAtUtc);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs
git commit -m "feat(gv): add public voicemail read DTOs (ADR §6.1)"
```

---

## Task 2: Recording-fetch seam + default fetcher — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Clients/IGvRecordingFetcher.cs`
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvRecordingFetcherTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvRecordingFetcher.cs`

- [ ] **Step 1: Define the fetcher seam**

Create `src/RotaryPhoneController.GVBridge/Clients/IGvRecordingFetcher.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>Result of fetching a recording's bytes from Google.</summary>
public record GvRecordingFetchResult(bool Success, byte[]? Bytes, string ContentType);

/// <summary>
/// Seam isolating the UNVERIFIED voicemail media-fetch shape (ADR §3.2, §11 step 3: the recording
/// may be recording/get?id=… OR an embedded media URL). Keeping it behind this interface means the
/// live correction is a one-file change in <see cref="GvRecordingFetcher"/>; the cache + controller
/// never learn Google's media URL form.
/// </summary>
public interface IGvRecordingFetcher
{
    /// <summary>Fetch the recording bytes for a media reference (id or URL from the list response).</summary>
    Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/GvRecordingFetcherTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvRecordingFetcherTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvRecordingFetcher NewFetcher(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new MockHandler(handler)), BaseUrl, ApiKey,
               NullLogger<GvRecordingFetcher>.Instance);

    [Fact]
    public async Task FetchAsync_RequestsRecordingGetWithMediaId()
    {
        string? capturedUrl = null;
        var fetcher = NewFetcher(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            return resp;
        });

        var result = await fetcher.FetchAsync("media-abc-123");

        Assert.True(result.Success);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.NotNull(capturedUrl);
        Assert.Contains("/recording/get", capturedUrl!);
        Assert.Contains("id=media-abc-123", capturedUrl!);
    }

    [Fact]
    public async Task FetchAsync_OnNon200_ReturnsFailure()
    {
        var fetcher = NewFetcher(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await fetcher.FetchAsync("media-abc-123");
        Assert.False(result.Success);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public async Task FetchAsync_DefaultsContentTypeToAudioMpeg()
    {
        var fetcher = NewFetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[] { 9 }) }); // no content-type header
        var result = await fetcher.FetchAsync("m1");
        Assert.True(result.Success);
        Assert.Equal("audio/mpeg", result.ContentType);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvRecordingFetcherTests" -v n`
Expected: FAIL — `GvRecordingFetcher` does not exist.

- [ ] **Step 4: Implement the default fetcher**

Create `src/RotaryPhoneController.GVBridge/Clients/GvRecordingFetcher.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Default recording fetcher. Best-known shape: GET recording/get?id=&lt;mediaId&gt;&amp;key=&lt;API_KEY&gt;
/// over the shared authenticated HttpClient (ADR §3.2). UNVERIFIED — ADR §11 step 3 may show the
/// media reference is an embedded URL instead; if so, the ONLY change is this class (detect an
/// absolute URL in mediaRef and GET it directly).
/// </summary>
public class GvRecordingFetcher : IGvRecordingFetcher
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly ILogger<GvRecordingFetcher> _logger;

    public GvRecordingFetcher(HttpClient http, string baseUrl, string apiKey,
        ILogger<GvRecordingFetcher> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
    {
        try
        {
            // UNVERIFIED — ADR §11 step 3. If mediaRef is already an absolute media URL, GET it
            // directly (auth rides the shared handler either way); otherwise resolve via recording/get.
            var url = Uri.TryCreate(mediaRef, UriKind.Absolute, out _)
                ? mediaRef
                : $"{_baseUrl}/recording/get?id={Uri.EscapeDataString(mediaRef)}&key={_apiKey}";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("recording fetch returned {Status} for {MediaRef}",
                    response.StatusCode, mediaRef);
                return new GvRecordingFetchResult(false, null, "audio/mpeg");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            return new GvRecordingFetchResult(true, bytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "recording fetch failed for {MediaRef}", mediaRef);
            return new GvRecordingFetchResult(false, null, "audio/mpeg");
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvRecordingFetcherTests" -v n`
Expected: 3 passed.

- [ ] **Step 6: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/IGvRecordingFetcher.cs \
        src/RotaryPhoneController.GVBridge/Clients/GvRecordingFetcher.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvRecordingFetcherTests.cs
git commit -m "feat(gv): add recording-fetch seam isolating UNVERIFIED media shape"
```

---

## Task 3: Wire GetRecordingStreamAsync into GvVoicemailClient

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs`

- [ ] **Step 1: Add the fetcher dependency and method**

In `src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs`, inject `IGvRecordingFetcher` and
add `GetRecordingAsync`. Update the constructor (add the parameter after `parser`):

```csharp
    private readonly IGvRecordingFetcher _recordingFetcher;

    public GvVoicemailClient(GvThreadClient threadClient, IGvThreadParser parser,
        IGvRecordingFetcher recordingFetcher, ILogger<GvVoicemailClient> logger)
    {
        _threadClient = threadClient;
        _parser = parser;
        _recordingFetcher = recordingFetcher;
        _logger = logger;
    }
```

Add the method:

```csharp
    /// <summary>
    /// Fetch the recording bytes for a voicemail media reference (the MediaId/MediaRef from a parsed
    /// voicemail node). Delegates to the fetcher seam so the UNVERIFIED media shape stays in one place.
    /// </summary>
    public Task<GvRecordingFetchResult> GetRecordingAsync(string mediaRef, CancellationToken ct = default)
        => _recordingFetcher.FetchAsync(mediaRef, ct);
```

> The existing `GvVoicemailClientTests` constructor calls from PR1 must be updated to pass a fetcher.
> Add a tiny inline stub in those tests, e.g.:
> `new StubFetcher()` implementing `IGvRecordingFetcher` returning a fixed `GvRecordingFetchResult`.
> Keep PR1's two list tests green; they don't exercise audio.

- [ ] **Step 2: Update PR1's GvVoicemailClientTests to satisfy the new constructor**

In `src/RotaryPhoneController.GVBridge.Tests/Clients/GvVoicemailClientTests.cs`, update `NewClient` to
pass a stub fetcher, and add the stub class:

```csharp
    private static GvVoicemailClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvVoicemailClient(threadClient, parser, new StubFetcher(),
            NullLogger<GvVoicemailClient>.Instance);
    }

    private sealed class StubFetcher : RotaryPhoneController.GVBridge.Clients.IGvRecordingFetcher
    {
        public Task<RotaryPhoneController.GVBridge.Clients.GvRecordingFetchResult> FetchAsync(
            string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new RotaryPhoneController.GVBridge.Clients.GvRecordingFetchResult(
                true, new byte[] { 1 }, "audio/mpeg"));
    }
```

- [ ] **Step 3: Run the voicemail client tests**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailClientTests" -v n`
Expected: 2 passed (still green with new constructor).

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvVoicemailClientTests.cs
git commit -m "feat(gv): add GetRecordingAsync to GvVoicemailClient via fetcher seam"
```

---

## Task 4: Cache config on GVBridgeConfig + appsettings

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`

- [ ] **Step 1: Add cache config properties**

In `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`, add after `CallLogDbPath`:

```csharp
    // Voicemail audio proxy cache (ADR §6.4). Retention is OWNER-ADJUSTABLE (ADR §12 q3):
    // proposed 7 days / 200 MB, whichever first. Cache holds small MP3/AMR recordings on disk so
    // RadioConsole never talks to Google for media.
    public string VoicemailCacheDir { get; set; } = "data/gv-voicemail-cache";
    public int VoicemailCacheRetentionDays { get; set; } = 7;
    public long VoicemailCacheMaxBytes { get; set; } = 200L * 1024 * 1024; // 200 MB
```

- [ ] **Step 2: Add config keys to appsettings.json**

In `src/RotaryPhoneController.Server/appsettings.json`, add to the `GVBridge` section:

```json
"VoicemailCacheDir": "data/gv-voicemail-cache",
"VoicemailCacheRetentionDays": 7,
"VoicemailCacheMaxBytes": 209715200
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs \
        src/RotaryPhoneController.Server/appsettings.json
git commit -m "feat(gv): add owner-adjustable voicemail cache retention config"
```

---

## Task 5: GvVoicemailCache (disk cache + range stream + eviction) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Services/GvVoicemailCacheTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Services/GvVoicemailCache.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Services/GvVoicemailCacheTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvVoicemailCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly GVBridgeConfig _config;

    public GvVoicemailCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vm-cache-{Guid.NewGuid():N}");
        _config = new GVBridgeConfig
        {
            VoicemailCacheDir = _dir,
            VoicemailCacheRetentionDays = 7,
            VoicemailCacheMaxBytes = 1024
        };
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class StubFetcher(byte[] bytes) : IGvRecordingFetcher
    {
        public int Calls { get; private set; }
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new GvRecordingFetchResult(true, bytes, "audio/mpeg"));
        }
    }

    private GvVoicemailCache NewCache(IGvRecordingFetcher fetcher)
        => new(fetcher, Options.Create(_config), NullLogger<GvVoicemailCache>.Instance);

    [Fact]
    public async Task GetOrFetch_FirstCall_FetchesAndWritesFile()
    {
        var fetcher = new StubFetcher(new byte[] { 1, 2, 3, 4 });
        var cache = NewCache(fetcher);

        var path = await cache.GetOrFetchAsync("vm.1", "media-1");

        Assert.NotNull(path);
        Assert.True(File.Exists(path!));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path!));
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task GetOrFetch_SecondCall_ServesFromCacheWithoutRefetch()
    {
        var fetcher = new StubFetcher(new byte[] { 1, 2, 3, 4 });
        var cache = NewCache(fetcher);

        await cache.GetOrFetchAsync("vm.1", "media-1");
        await cache.GetOrFetchAsync("vm.1", "media-1");

        Assert.Equal(1, fetcher.Calls); // second served from disk
    }

    [Fact]
    public async Task GetOrFetch_OnFetchFailure_ReturnsNull()
    {
        var failing = new FailingFetcher();
        var cache = NewCache(failing);
        var path = await cache.GetOrFetchAsync("vm.1", "media-1");
        Assert.Null(path);
    }

    [Fact]
    public async Task Evict_RemovesOldestWhenOverMaxBytes()
    {
        // MaxBytes=1024; write three 500-byte entries → total 1500 > 1024 → oldest evicted.
        var fetcher = new StubFetcher(new byte[500]);
        var cache = NewCache(fetcher);

        var p1 = await cache.GetOrFetchAsync("vm.1", "m1");
        await Task.Delay(10);
        await cache.GetOrFetchAsync("vm.2", "m2");
        await Task.Delay(10);
        await cache.GetOrFetchAsync("vm.3", "m3");

        cache.Evict();

        Assert.False(File.Exists(p1!)); // oldest gone
    }

    private sealed class FailingFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(false, null, "audio/mpeg"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailCacheTests" -v n`
Expected: FAIL — `GvVoicemailCache` does not exist.

- [ ] **Step 3: Implement GvVoicemailCache**

Create `src/RotaryPhoneController.GVBridge/Services/GvVoicemailCache.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// On-disk cache for voicemail recordings (ADR §6.4). First request fetches from Google via the
/// recording fetcher and writes data/gv-voicemail-cache/{id}.bin; later requests serve the file so
/// RadioConsole never re-hits Google. Eviction by age (RetentionDays) and total size (MaxBytes).
/// The controller streams the file with range support so the &lt;audio&gt; scrubber works.
/// </summary>
public class GvVoicemailCache
{
    private readonly IGvRecordingFetcher _fetcher;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GvVoicemailCache> _logger;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    public GvVoicemailCache(IGvRecordingFetcher fetcher, IOptions<GVBridgeConfig> config,
        ILogger<GvVoicemailCache> logger)
    {
        _fetcher = fetcher;
        _config = config.Value;
        _logger = logger;
    }

    private string PathFor(string voicemailId)
    {
        // Sanitize id → safe filename (GV ids are alnum + . ; keep it defensive).
        var safe = string.Concat(voicemailId.Select(c =>
            char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        return Path.Combine(_config.VoicemailCacheDir, $"{safe}.bin");
    }

    /// <summary>
    /// Return the cache file path for a voicemail, fetching+writing on a miss. Null on fetch failure.
    /// </summary>
    public async Task<string?> GetOrFetchAsync(string voicemailId, string mediaRef, CancellationToken ct = default)
    {
        var path = PathFor(voicemailId);
        if (File.Exists(path))
            return path;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return path; // double-checked after lock

            var result = await _fetcher.FetchAsync(mediaRef, ct);
            if (!result.Success || result.Bytes is null)
                return null;

            Directory.CreateDirectory(_config.VoicemailCacheDir);
            await File.WriteAllBytesAsync(path, result.Bytes, ct);
            _logger.LogDebug("Cached voicemail {Id} ({Bytes} bytes)", voicemailId, result.Bytes.Length);
            Evict();
            return path;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>Evict cache files older than RetentionDays, then oldest-first until under MaxBytes.</summary>
    public void Evict()
    {
        try
        {
            if (!Directory.Exists(_config.VoicemailCacheDir)) return;

            var files = new DirectoryInfo(_config.VoicemailCacheDir)
                .GetFiles("*.bin")
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-_config.VoicemailCacheRetentionDays);
            foreach (var f in files.Where(f => f.LastWriteTimeUtc < cutoff).ToList())
            {
                TryDelete(f);
                files.Remove(f);
            }

            var total = files.Sum(f => f.Length);
            foreach (var f in files) // already oldest-first
            {
                if (total <= _config.VoicemailCacheMaxBytes) break;
                total -= f.Length;
                TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voicemail cache eviction error");
        }
    }

    private void TryDelete(FileInfo f)
    {
        try { f.Delete(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete cache file {File}", f.Name); }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailCacheTests" -v n`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/GvVoicemailCache.cs \
        src/RotaryPhoneController.GVBridge.Tests/Services/GvVoicemailCacheTests.cs
git commit -m "feat(gv): add GvVoicemailCache with age/size eviction"
```

---

## Task 6: GvVoicemailController (list, item, audio) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Api/GvVoicemailControllerTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs`

- [ ] **Step 1: Write failing controller tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Api/GvVoicemailControllerTests.cs`. These test the
DTO mapping + AudioUrl construction + 404/range behavior with a stubbed `GvVoicemailClient` path. To
keep the controller unit-testable, the controller depends on small interfaces the tests can fake; the
simplest faithful approach is to construct a real `GvVoicemailClient` over a `MockHandler` and a real
`GvVoicemailCache` over a temp dir.

```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GvVoicemailControllerTests : IDisposable
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vmctl-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private GvVoicemailController NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler, byte[] audioBytes)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var fetcher = new StubFetcher(audioBytes);
        var vmClient = new GvVoicemailClient(threadClient, parser, fetcher,
            NullLogger<GvVoicemailClient>.Instance);
        var config = Options.Create(new GVBridgeConfig { VoicemailCacheDir = _dir });
        var cache = new GvVoicemailCache(fetcher, config, NullLogger<GvVoicemailCache>.Instance);
        var controller = new GvVoicemailController(vmClient, cache, NullLogger<GvVoicemailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    private static HttpResponseMessage VmListResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
              [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,false,"call me","media-1"]]]],
             "nextPageToken":null}
            """)
        };

    [Fact]
    public async Task GetList_MapsNodesToDtosWithRelativeAudioUrl()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 1, 2 });

        var result = await controller.GetList(count: 20, pageToken: null, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailListDto>(ok.Value);
        Assert.Single(dto.Items);
        Assert.Equal("vm.1", dto.Items[0].Id);
        Assert.Equal("/api/gvbridge/voicemail/vm.1/audio", dto.Items[0].AudioUrl);
        Assert.Equal("call me", dto.Items[0].Transcript);
        Assert.Equal(23, dto.Items[0].DurationSeconds);
    }

    [Fact]
    public async Task GetItem_NotFound_Returns404()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 1 });
        var result = await controller.GetItem("does-not-exist", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetAudio_ServesBytesAsAudioMpeg()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 7, 7, 7 });
        var result = await controller.GetAudio("vm.1", default);
        var file = Assert.IsAssignableFrom<PhysicalFileResult>(result);
        Assert.Equal("audio/mpeg", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }

    [Fact]
    public async Task GetAudio_UnknownId_Returns404()
    {
        var controller = NewController(_ => VmListResponse(), new byte[] { 1 });
        var result = await controller.GetAudio("nope", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    private sealed class StubFetcher(byte[] bytes) : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, bytes, "audio/mpeg"));
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailControllerTests" -v n`
Expected: FAIL — `GvVoicemailController` does not exist.

- [ ] **Step 3: Implement GvVoicemailController**

Create `src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs`. The controller maps PR1's
internal `GvVoicemailNode` to the public `VoicemailItemDto`, builds the relative `AudioUrl`, and
streams cached audio with range support via `PhysicalFileResult` (`EnableRangeProcessing = true`).

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Services;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge/voicemail")]
public class GvVoicemailController : ControllerBase
{
    private readonly GvVoicemailClient _voicemailClient;
    private readonly GvVoicemailCache _cache;
    private readonly ILogger<GvVoicemailController> _logger;

    public GvVoicemailController(GvVoicemailClient voicemailClient, GvVoicemailCache cache,
        ILogger<GvVoicemailController> logger)
    {
        _voicemailClient = voicemailClient;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] int count = 20, [FromQuery] string? pageToken = null, CancellationToken ct = default)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count, pageToken, ct);
        var items = result.Items.Select(ToDto).ToList();
        return Ok(new VoicemailListDto(items, result.NextPageToken, DateTime.UtcNow));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetItem(string id, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(id, ct);
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });
        return Ok(ToDto(node));
    }

    [HttpGet("{id}/audio")]
    public async Task<IActionResult> GetAudio(string id, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(id, ct);
        if (node?.MediaId is null)
            return NotFound(new { error = $"Voicemail {id} has no recording" });

        var path = await _cache.GetOrFetchAsync(id, node.MediaId, ct);
        if (path is null)
            return StatusCode(502, new { error = "Failed to fetch recording from Google" });

        // PhysicalFileResult + EnableRangeProcessing gives Accept-Ranges so the HTML5 <audio>
        // scrubber works (ADR §6.4). All bytes flow Google→RotaryPhone→RadioConsole; never a redirect.
        return new PhysicalFileResult(path, "audio/mpeg") { EnableRangeProcessing = true };
    }

    // Voicemail is a thread/message subtype — there is no per-id GET on GV; we list and filter.
    // Lists are small (tens of items); a future optimization could cache the last list.
    private async Task<GvVoicemailNode?> FindNodeAsync(string id, CancellationToken ct)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count: 100, pageToken: null, ct);
        return result.Items.FirstOrDefault(v => v.MessageId == id);
    }

    private static VoicemailItemDto ToDto(GvVoicemailNode n) => new(
        Id: n.MessageId ?? "",
        ThreadId: n.ThreadId ?? "",
        FromNumber: n.FromNumber ?? "",
        FromName: n.FromName,
        ReceivedAt: n.ReceivedEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UnixEpoch,
        DurationSeconds: n.DurationSeconds ?? 0,
        IsRead: n.IsRead ?? false,
        Transcript: n.Transcript,
        AudioUrl: $"/api/gvbridge/voicemail/{n.MessageId}/audio");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailControllerTests" -v n`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs \
        src/RotaryPhoneController.GVBridge.Tests/Api/GvVoicemailControllerTests.cs
git commit -m "feat(gv): add GvVoicemailController (list/item/audio proxy)"
```

---

## Task 7: DI registration

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`

- [ ] **Step 1: Register fetcher, voicemail client, cache, and a wired-up GvThreadClient**

The new read clients need the **live** authenticated `HttpClient` from the adapter (via the PR1 seam
`IGvAuthenticatedClientProvider`). Register them with factory lambdas that resolve the client at use
time. In `AddGVBridge`, after the existing registrations, add:

```csharp
        // Read-side clients ride the adapter's authenticated HttpClient (PR1 seam) so they inherit
        // cookie rotation + the recovery ladder. Resolve the client lazily via the provider.
        services.AddSingleton<IGvThreadParser, PositionalGvThreadParser>();

        services.AddSingleton<IGvRecordingFetcher>(sp =>
        {
            var provider = sp.GetRequiredService<IGvAuthenticatedClientProvider>();
            var http = provider.GetAuthenticatedClient()
                       ?? throw new InvalidOperationException(
                           "GV authenticated client unavailable — adapter not activated.");
            return new GvRecordingFetcher(http, provider.ApiBaseUrl, provider.ApiKey,
                sp.GetRequiredService<ILogger<GvRecordingFetcher>>());
        });

        services.AddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<IGvAuthenticatedClientProvider>();
            var http = provider.GetAuthenticatedClient()
                       ?? throw new InvalidOperationException(
                           "GV authenticated client unavailable — adapter not activated.");
            return new GvThreadClient(http, provider.ApiBaseUrl, provider.ApiKey,
                sp.GetRequiredService<IGvThreadParser>(),
                sp.GetRequiredService<ILogger<GvThreadClient>>());
        });

        services.AddSingleton(sp => new GvVoicemailClient(
            sp.GetRequiredService<GvThreadClient>(),
            sp.GetRequiredService<IGvThreadParser>(),
            sp.GetRequiredService<IGvRecordingFetcher>(),
            sp.GetRequiredService<ILogger<GvVoicemailClient>>()));

        services.AddSingleton<GvVoicemailCache>();
```

> **Activation-order caveat to flag for the Builder/UAT:** resolving `GetAuthenticatedClient()` at DI
> build time will throw if the adapter hasn't activated yet. The clients are only *used* inside
> request handlers (and PR3's poller loop), which run after startup, so the **cleaner** pattern is to
> resolve the live `HttpClient` per call rather than capture it once. If the Builder finds the
> capture-at-construction throws during startup, switch these factories to a tiny wrapper that holds
> the `IGvAuthenticatedClientProvider` and fetches `GetAuthenticatedClient()` on each method call.
> Add a unit test for the "adapter unavailable" path returning a 503-style empty result rather than
> throwing. (This is the one integration seam the synthetic unit tests cannot fully prove — verify in
> the live UAT step below.)

Add the necessary `using` lines at the top if not already present:

```csharp
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Services;
```

- [ ] **Step 2: Build the Server project**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs
git commit -m "feat(gv): register voicemail read clients + audio cache"
```

---

## Task 8: Full suite + completion gate

- [ ] **Step 1: Full GVBridge test suite**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests -v n`
Expected: all green.

- [ ] **Step 2: Server build (deploy-all-DLLs hygiene)**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Live UAT (Tester / owner, on the `radio` box)**

These cannot be proven by hermetic unit tests — they need live cookies (ADR §11 step 3):
- `GET /api/gvbridge/voicemail` returns real items once cookies are live.
- `GET /api/gvbridge/voicemail/{id}/audio` plays in a browser `<audio>` element AND the scrubber
  seeks (proves range support).
- Confirm the recording fetch shape matches `GvRecordingFetcher`; if §11 step 3 shows an embedded
  media URL instead of `recording/get?id=…`, correct ONLY `GvRecordingFetcher` (the seam contains it).
- Confirm the cache directory fills on first listen and the second listen does not re-hit Google.

---

## Out of scope for PR2 (do NOT do here)

- No SMS read/threads endpoints, no poller, no SignalR events (PR3).
- No SMS send (PR4).
- No inter-service auth gate (PR5) — these endpoints stay LAN-only as today.
- No voicemail delete / mark-read (ADR §3.4, out of scope v1).
- No Blazor UI control (the active UI is in the RTest repo; retention is config-only here).
