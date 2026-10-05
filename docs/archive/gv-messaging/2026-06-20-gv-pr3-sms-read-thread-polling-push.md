# PR3 Plan — `feat(gv): SMS read + thread polling push`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Arc:** `docs/plans/gv-voicemail-sms-arc.md`
**ADR (source of truth):** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` (§5, §6.1, §6.2, §6.3, §10 row PR3, §11 steps 1 & 5)
**Queue row:** `docs/BUILDER_QUEUE.md` → `GV-VM-SMS-3`
**Depends on:** PR1 (`GV-VM-SMS-1`) — `GvThreadClient`, parser seam, `GvSmsNode`, auth-client seam. Shares `GvBridgeReadDtos.cs` with PR2.
**Sensitivity:** Auto-merge eligible (read-only; push reuses the existing SignalR hub; the only polling is RotaryPhone→Google which we control).

---

## Goal

Deliver the **SMS read experience with push** (ADR §5.3, §6.3):

1. **SMS read endpoints** (ADR §6.2):
   | Method | Route | Returns |
   |---|---|---|
   | GET | `/api/gvbridge/sms/threads` | `SmsThreadListDto` |
   | GET | `/api/gvbridge/sms/threads/{threadId}` | `SmsThreadMessagesDto` |
2. **`GvThreadPoller`** (`IHostedService`): adaptive 15–60s poll of `api2thread/list`, diff against a
   per-thread high-water mark, raise `OnSmsReceived` / `OnVoicemailReceived` on each new inbound item.
3. **Push to RadioConsole over the existing SignalR hub** — new `SmsReceived` / `VoicemailReceived`
   events, mirroring exactly how `IncomingCall` already reaches RadioConsole (ADR §6.3).

## The stable seam (ADR §5.2, §9): poll-vs-signaler is invisible to RadioConsole

The whole risk-management story of the arc lives here. The poller raises **plain .NET events**
(`OnSmsReceived`/`OnVoicemailReceived`) on an `IGvMessageEventSource` seam. A thin **Server-side**
bridge subscribes to that seam and calls `_hubContext.Clients.All.SendAsync("SmsReceived", dto)` —
identical in shape to the existing `IncomingCall` broadcast. If the signaler is ever cracked (PR6), it
drops in behind the **same** `IGvMessageEventSource` with zero contract change for RadioConsole.

**Why the seam crosses the project boundary:** the poller lives in `GVBridge` (no `IHubContext`
dependency — keeps GVBridge UI-framework-free, matching how `SignalRNotifierService` already lives in
`RotaryPhoneController.Server`). So:

- `GVBridge` owns: the poller + the `IGvMessageEventSource` event seam + the `SmsMessageDto`/etc DTOs.
- `RotaryPhoneController.Server` owns: a small `GvMessagePushBridge : IHostedService` that subscribes
  to the seam and forwards to `RotaryHub` via `IHubContext<RotaryHub>` (mirroring `SignalRNotifierService`).

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Services/
  IGvMessageEventSource.cs   -- the stable seam: OnSmsReceived / OnVoicemailReceived events
  GvThreadPoller.cs          -- IHostedService: adaptive poll + high-water diff + raise events
  GvHighWaterMark.cs         -- per-thread last-seen tracker (in-memory)
Clients/
  GvSmsClient.cs             -- ListMessagesAsync (read only; SendAsync is PR4)
Api/
  GvSmsController.cs         -- /api/gvbridge/sms/threads, /threads/{threadId}
  GvBridgeReadDtos.cs        -- (extend) SmsMessageDto, SmsThreadDto, SmsThreadListDto, SmsThreadMessagesDto
```

### New files (in `src/RotaryPhoneController.Server/`)

```
Services/
  GvMessagePushBridge.cs     -- IHostedService bridging IGvMessageEventSource -> RotaryHub
```

### New test files

```
src/RotaryPhoneController.GVBridge.Tests/
  Clients/GvSmsClientTests.cs
  Services/GvHighWaterMarkTests.cs
  Services/GvThreadPollerTests.cs
  Api/GvSmsControllerTests.cs
```

### Modified files

```
Extensions/GVBridgeServiceExtensions.cs              -- register SMS client, poller, event source
src/RotaryPhoneController.Server/Program.cs (or Startup wiring) -- register GvMessagePushBridge hosted service
src/RotaryPhoneController.Server/appsettings.json     -- poller interval config (optional keys)
Models/GVBridgeConfig.cs                              -- poller interval/backoff config
```

---

## Task 1: SMS read DTOs (§6.1) — extend the shared file

**Files:**
- Modify (or create if PR2 hasn't landed): `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs`

- [ ] **Step 1: Append the SMS read DTOs**

Add to `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs` (exact §6.1 records). If the file
does not yet exist (PR2 landed after this), create it with these records:

```csharp
namespace RotaryPhoneController.GVBridge.Api;

/// <summary>Public cross-service SMS message (ADR §6.1). Direction is "Inbound" | "Outbound".</summary>
public record SmsMessageDto(
    string Id,
    string ThreadId,
    string Direction,
    string CounterpartyNumber,
    string? Text,
    DateTime SentAt,
    bool IsRead);

public record SmsThreadDto(
    string ThreadId,
    string CounterpartyNumber,
    string? CounterpartyName,
    DateTime LastMessageAt,
    bool HasUnread,
    string? LastMessagePreview);

public record SmsThreadListDto(
    IReadOnlyList<SmsThreadDto> Threads,
    DateTime FetchedAtUtc);

public record SmsThreadMessagesDto(
    string ThreadId,
    IReadOnlyList<SmsMessageDto> Messages,
    DateTime FetchedAtUtc);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs
git commit -m "feat(gv): add public SMS read DTOs (ADR §6.1)"
```

---

## Task 2: GvSmsClient (read only) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvSmsClientTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`

> Read path ONLY. `SendAsync` is PR4 (held for owner). Do NOT add it here.

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/GvSmsClientTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvSmsClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvSmsClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
    }

    private static HttpResponseMessage SmsListResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
              [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false],
               ["m.2","t.+19195551234",1,"+19195551234","hello back",1718841700000,true]]]],
             "nextPageToken":null}
            """)
        };

    [Fact]
    public async Task ListThreadsAsync_ReturnsThreadNodes()
    {
        var client = NewClient(_ => SmsListResponse());
        var result = await client.ListThreadsAsync(count: 20);
        Assert.True(result.Succeeded);
        Assert.Single(result.Threads);
        Assert.Equal("t.+19195551234", result.Threads[0].ThreadId);
    }

    [Fact]
    public async Task ListMessagesAsync_ReturnsMessagesForThread()
    {
        var client = NewClient(_ => SmsListResponse());
        var msgs = await client.ListMessagesAsync("t.+19195551234", count: 50);
        Assert.Equal(2, msgs.Count);
        Assert.Equal("Inbound", msgs[0].Direction);
        Assert.Equal("Outbound", msgs[1].Direction);
    }

    [Fact]
    public async Task ListMessagesAsync_FiltersByThreadId()
    {
        var client = NewClient(_ => SmsListResponse());
        var msgs = await client.ListMessagesAsync("t.+nonexistent", count: 50);
        Assert.Empty(msgs);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvSmsClientTests" -v n`
Expected: FAIL — `GvSmsClient` does not exist.

- [ ] **Step 3: Implement GvSmsClient (read only)**

Create `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`. Composes `GvThreadClient`; no
direct HTTP. (GV has no per-thread message endpoint in the read path we use — we list the SMS folder
and filter by thread id; lists are small. ADR §11 step 1 may later reveal a per-thread list call; if
so, that's a localized change to this client only.)

```csharp
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// SMS read client (ADR §5.3, §6.2). Composes GvThreadClient — voicemail/SMS/threads all ride the
/// same api2thread/list call + parser seam. SendAsync (the account write) is PR4, intentionally absent.
/// </summary>
public class GvSmsClient
{
    private readonly GvThreadClient _threadClient;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvSmsClient> _logger;

    public GvSmsClient(GvThreadClient threadClient, IGvThreadParser parser, ILogger<GvSmsClient> logger)
    {
        _threadClient = threadClient;
        _parser = parser;
        _logger = logger;
    }

    /// <summary>List SMS threads (folder = Sms).</summary>
    public Task<GvThreadListResult> ListThreadsAsync(
        int count = 20, string? pageToken = null, CancellationToken ct = default)
        => _threadClient.ListThreadsAsync(GvThreadFolder.Sms, count, pageToken, ct);

    /// <summary>List messages for a single thread by filtering the SMS-folder list.</summary>
    public async Task<IReadOnlyList<GvSmsNode>> ListMessagesAsync(
        string threadId, int count = 50, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Sms, count, pageToken: null, ct);
        if (doc is null) return Array.Empty<GvSmsNode>();

        var all = _parser.ParseSmsMessages(doc.RootElement);
        return all.Where(m => m.ThreadId == threadId).ToList();
    }

    /// <summary>List ALL recent SMS messages across threads (used by the poller's diff).</summary>
    public async Task<IReadOnlyList<GvSmsNode>> ListRecentMessagesAsync(
        int count = 50, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Sms, count, pageToken: null, ct);
        return doc is null ? Array.Empty<GvSmsNode>() : _parser.ParseSmsMessages(doc.RootElement);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvSmsClientTests" -v n`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvSmsClientTests.cs
git commit -m "feat(gv): add GvSmsClient read path (threads + messages)"
```

---

## Task 3: GvHighWaterMark (per-thread last-seen diff) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Services/GvHighWaterMarkTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Services/GvHighWaterMark.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Services/GvHighWaterMarkTests.cs`:

```csharp
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvHighWaterMarkTests
{
    [Fact]
    public void FirstObservation_IsNotNew_ToAvoidStartupFlood()
    {
        var hwm = new GvHighWaterMark();
        // On the very first poll we seed the marks and DO NOT raise events for history.
        var firstSeed = hwm.IsNewMessage("t.1", "m.1", epochMs: 1000);
        Assert.False(firstSeed); // seeding, not "new"
    }

    [Fact]
    public void NewerMessage_AfterSeed_IsNew()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 1000L) });

        Assert.True(hwm.IsNewMessage("t.1", "m.2", 2000));
    }

    [Fact]
    public void OlderOrEqualMessage_AfterSeed_IsNotNew()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 2000L) });

        Assert.False(hwm.IsNewMessage("t.1", "m.0", 1000));
        Assert.False(hwm.IsNewMessage("t.1", "m.1", 2000)); // same id+ts
    }

    [Fact]
    public void SameMessageTwice_IsNewOnlyOnce()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 1000L) });

        Assert.True(hwm.IsNewMessage("t.1", "m.2", 2000));
        Assert.False(hwm.IsNewMessage("t.1", "m.2", 2000)); // mark advanced, no double-fire
    }

    [Fact]
    public void UnknownThread_AfterSeed_TreatsNewMessageAsNew()
    {
        var hwm = new GvHighWaterMark();
        hwm.Seed(new[] { ("t.1", "m.1", 1000L) });

        Assert.True(hwm.IsNewMessage("t.2", "m.9", 500)); // brand-new thread → its first inbound is new
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvHighWaterMarkTests" -v n`
Expected: FAIL — `GvHighWaterMark` does not exist.

- [ ] **Step 3: Implement GvHighWaterMark**

Create `src/RotaryPhoneController.GVBridge/Services/GvHighWaterMark.cs`:

```csharp
using System.Collections.Concurrent;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Per-thread high-water mark for the poller's diff (ADR §5.3). Tracks the max message timestamp seen
/// per thread; a message strictly newer than the mark is "new" and advances it. The FIRST poll seeds
/// the marks WITHOUT raising events (so we don't flood RadioConsole with history on startup). In-memory
/// for v1 (ADR §5.3 notes optional SQLite durability — out of scope here; a restart re-seeds silently).
/// </summary>
public class GvHighWaterMark
{
    private readonly ConcurrentDictionary<string, long> _maxEpochByThread = new();
    private bool _seeded;

    /// <summary>Seed marks from the first poll's messages without treating any as new.</summary>
    public void Seed(IEnumerable<(string ThreadId, string MessageId, long EpochMs)> messages)
    {
        foreach (var (threadId, _, epoch) in messages)
            Advance(threadId, epoch);
        _seeded = true;
    }

    /// <summary>
    /// True if this message is newer than the thread's mark (and advances the mark). Before the first
    /// Seed, returns false and seeds the mark — so the very first poll never raises events.
    /// </summary>
    public bool IsNewMessage(string threadId, string messageId, long epochMs)
    {
        if (!_seeded)
        {
            Advance(threadId, epochMs);
            return false;
        }

        var current = _maxEpochByThread.GetValueOrDefault(threadId, long.MinValue);
        if (epochMs > current)
        {
            Advance(threadId, epochMs);
            return true;
        }
        return false;
    }

    private void Advance(string threadId, long epochMs)
        => _maxEpochByThread.AddOrUpdate(threadId, epochMs, (_, prev) => Math.Max(prev, epochMs));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvHighWaterMarkTests" -v n`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/GvHighWaterMark.cs \
        src/RotaryPhoneController.GVBridge.Tests/Services/GvHighWaterMarkTests.cs
git commit -m "feat(gv): add GvHighWaterMark per-thread message diff"
```

---

## Task 4: The event-source seam + poller config

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`

- [ ] **Step 1: Define the stable seam**

Create `src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs`. The events carry the
public §6.1 DTOs directly (the bridge forwards them verbatim to SignalR).

```csharp
using RotaryPhoneController.GVBridge.Api;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// The stable seam through which new inbound GV messages reach RadioConsole (ADR §5.2, §6.3, §9).
/// The poller (or, later, a cracked signaler — PR6) raises these; a Server-side bridge forwards them
/// to RotaryHub as "SmsReceived"/"VoicemailReceived", mirroring "IncomingCall". Swapping the producer
/// behind this interface is invisible to RadioConsole — that is the whole point of the seam.
/// </summary>
public interface IGvMessageEventSource
{
    /// <summary>Raised once per newly-detected inbound SMS.</summary>
    event Action<SmsMessageDto>? OnSmsReceived;

    /// <summary>Raised once per newly-detected voicemail.</summary>
    event Action<VoicemailItemDto>? OnVoicemailReceived;
}
```

- [ ] **Step 2: Add poller config**

In `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`, add after the voicemail-cache config
(from PR2):

```csharp
    // SMS/voicemail thread poller (ADR §5.3). Adaptive interval: active vs idle, with backoff on
    // repeated failure. Owner-tunable; defaults match the ADR.
    public bool EnableThreadPoller { get; set; } = true;
    public int ThreadPollActiveSeconds { get; set; } = 15;
    public int ThreadPollIdleSeconds { get; set; } = 60;
    public int ThreadPollBackoffSeconds { get; set; } = 120;
    public int ThreadPollActiveWindowMinutes { get; set; } = 5; // "active" if a poll found new msgs within this window
```

- [ ] **Step 3: Add config keys to appsettings.json**

In `src/RotaryPhoneController.Server/appsettings.json`, add to the `GVBridge` section:

```json
"EnableThreadPoller": true,
"ThreadPollActiveSeconds": 15,
"ThreadPollIdleSeconds": 60,
"ThreadPollBackoffSeconds": 120,
"ThreadPollActiveWindowMinutes": 5
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs \
        src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs \
        src/RotaryPhoneController.Server/appsettings.json
git commit -m "feat(gv): add message-event seam and poller config"
```

---

## Task 5: GvThreadPoller (adaptive poll + diff + raise) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Services/GvThreadPollerTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Services/GvThreadPoller.cs`

> The poller is an `IHostedService` AND implements `IGvMessageEventSource`. Tests drive a single poll
> cycle via an internal `PollOnceAsync` method (not the timer loop) so they are deterministic and
> hermetic. Active-only gating: the poller no-ops while `IGvAuthenticatedClientProvider` reports no
> client (adapter not activated / not the active mode), mirroring how the adapter gates its own work.

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Services/GvThreadPollerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Services;

public class GvThreadPollerTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static (GvThreadPoller poller, List<SmsMessageDto> sms) NewPoller(
        Queue<HttpResponseMessage> responses)
    {
        var http = new HttpClient(new QueueHandler(responses));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var vmClient = new GvVoicemailClient(threadClient, parser, new StubFetcher(),
            NullLogger<GvVoicemailClient>.Instance);
        var config = Options.Create(new GVBridgeConfig());
        var poller = new GvThreadPoller(smsClient, vmClient, config, NullLogger<GvThreadPoller>.Instance);

        var received = new List<SmsMessageDto>();
        poller.OnSmsReceived += dto => received.Add(dto);
        return (poller, received);
    }

    private static HttpResponseMessage SmsResponse(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string OneInbound = """
        {"threads":[["t.1",["+19195551234","Alice"],1000,true,
          [["m.1","t.1",0,"+19195551234","first",1000,false]]]],"nextPageToken":null}
        """;
    private const string TwoInbound = """
        {"threads":[["t.1",["+19195551234","Alice"],2000,true,
          [["m.1","t.1",0,"+19195551234","first",1000,false],
           ["m.2","t.1",0,"+19195551234","second",2000,false]]]],"nextPageToken":null}
        """;

    [Fact]
    public async Task FirstPoll_SeedsWithoutRaising()
    {
        // SMS folder poll + voicemail folder poll per cycle → enqueue both.
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        { SmsResponse(OneInbound), SmsResponse("""{"threads":[],"nextPageToken":null}""") }));

        await poller.PollOnceAsync(default);

        Assert.Empty(received); // history not pushed on first poll
    }

    [Fact]
    public async Task SecondPoll_RaisesOnlyNewInbound()
    {
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound), SmsResponse("""{"threads":[],"nextPageToken":null}"""), // seed
            SmsResponse(TwoInbound), SmsResponse("""{"threads":[],"nextPageToken":null}""")  // new m.2
        }));

        await poller.PollOnceAsync(default); // seed
        await poller.PollOnceAsync(default); // diff

        Assert.Single(received);
        Assert.Equal("m.2", received[0].Id);
        Assert.Equal("Inbound", received[0].Direction);
        Assert.Equal("second", received[0].Text);
    }

    [Fact]
    public async Task OutboundMessage_DoesNotRaise()
    {
        const string outbound = """
            {"threads":[["t.1",["+19195551234","Alice"],3000,false,
              [["m.1","t.1",0,"+19195551234","first",1000,false],
               ["m.3","t.1",1,"+19195551234","me replying",3000,true]]]],"nextPageToken":null}
            """;
        var (poller, received) = NewPoller(new Queue<HttpResponseMessage>(new[]
        {
            SmsResponse(OneInbound), SmsResponse("""{"threads":[],"nextPageToken":null}"""),
            SmsResponse(outbound), SmsResponse("""{"threads":[],"nextPageToken":null}""")
        }));

        await poller.PollOnceAsync(default);
        await poller.PollOnceAsync(default);

        Assert.Empty(received); // outbound (direction=1) is not an inbound "received" event
    }

    private sealed class StubFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, new byte[] { 1 }, "audio/mpeg"));
    }

    private sealed class QueueHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responses.Count > 0
                ? responses.Dequeue()
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                  { Content = new StringContent("""{"threads":[],"nextPageToken":null}""") });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvThreadPollerTests" -v n`
Expected: FAIL — `GvThreadPoller` does not exist.

- [ ] **Step 3: Implement GvThreadPoller**

Create `src/RotaryPhoneController.GVBridge/Services/GvThreadPoller.cs`. Note the dependency on
`GvSmsClient` + `GvVoicemailClient` (which already ride the auth seam via DI from PR2), so the poller
itself does not touch `IGvAuthenticatedClientProvider` directly — when those clients' calls fail
(adapter down), `Succeeded=false`/empty results flow through and the poller simply raises nothing and
backs off.

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;

namespace RotaryPhoneController.GVBridge.Services;

/// <summary>
/// Background poller (ADR §5.3) — the shipped SMS-read transport. Polls api2thread/list (SMS +
/// voicemail folders), diffs against a per-thread high-water mark, and raises OnSmsReceived /
/// OnVoicemailReceived for each NEW inbound item. A Server-side bridge forwards those to RotaryHub as
/// "SmsReceived"/"VoicemailReceived" — mirroring IncomingCall. The poll-vs-signaler choice lives
/// entirely behind IGvMessageEventSource (ADR §5.2, §9).
/// </summary>
public class GvThreadPoller : BackgroundService, IGvMessageEventSource
{
    private readonly GvSmsClient _smsClient;
    private readonly GvVoicemailClient _voicemailClient;
    private readonly GVBridgeConfig _config;
    private readonly ILogger<GvThreadPoller> _logger;

    private readonly GvHighWaterMark _smsHwm = new();
    private readonly GvHighWaterMark _vmHwm = new();
    private DateTime _lastActivityUtc = DateTime.MinValue;
    private int _consecutiveFailures;

    public event Action<SmsMessageDto>? OnSmsReceived;
    public event Action<VoicemailItemDto>? OnVoicemailReceived;

    public GvThreadPoller(GvSmsClient smsClient, GvVoicemailClient voicemailClient,
        IOptions<GVBridgeConfig> config, ILogger<GvThreadPoller> logger)
    {
        _smsClient = smsClient;
        _voicemailClient = voicemailClient;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.EnableThreadPoller)
        {
            _logger.LogInformation("GvThreadPoller disabled by config");
            return;
        }

        _logger.LogInformation("GvThreadPoller started");
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            try { await Task.Delay(NextDelay(), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One poll cycle (SMS folder + voicemail folder). Public for deterministic testing.</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            await PollSmsAsync(ct);
            await PollVoicemailAsync(ct);
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex, "GvThreadPoller poll cycle failed (#{Count})", _consecutiveFailures);
        }
    }

    private async Task PollSmsAsync(CancellationToken ct)
    {
        var messages = await _smsClient.ListRecentMessagesAsync(count: 50, ct);
        foreach (var m in messages)
        {
            if (m.MessageId is null || m.ThreadId is null || m.SentEpochMs is not { } epoch) continue;
            var isNew = _smsHwm.IsNewMessage(m.ThreadId, m.MessageId, epoch);
            if (isNew && m.Direction == "Inbound")
            {
                _lastActivityUtc = DateTime.UtcNow;
                OnSmsReceived?.Invoke(ToSmsDto(m));
                _logger.LogInformation("Poller: new inbound SMS {Id} on {Thread}", m.MessageId, m.ThreadId);
            }
        }
    }

    private async Task PollVoicemailAsync(CancellationToken ct)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count: 50, pageToken: null, ct);
        if (!result.Succeeded) return;
        foreach (var v in result.Items)
        {
            if (v.MessageId is null || v.ThreadId is null || v.ReceivedEpochMs is not { } epoch) continue;
            if (_vmHwm.IsNewMessage(v.ThreadId, v.MessageId, epoch))
            {
                _lastActivityUtc = DateTime.UtcNow;
                OnVoicemailReceived?.Invoke(ToVoicemailDto(v));
                _logger.LogInformation("Poller: new voicemail {Id}", v.MessageId);
            }
        }
    }

    private TimeSpan NextDelay()
    {
        if (_consecutiveFailures > 0)
            return TimeSpan.FromSeconds(_config.ThreadPollBackoffSeconds);

        var active = (DateTime.UtcNow - _lastActivityUtc).TotalMinutes
                     < _config.ThreadPollActiveWindowMinutes;
        return TimeSpan.FromSeconds(active ? _config.ThreadPollActiveSeconds : _config.ThreadPollIdleSeconds);
    }

    private static SmsMessageDto ToSmsDto(GvSmsNode m) => new(
        Id: m.MessageId ?? "",
        ThreadId: m.ThreadId ?? "",
        Direction: m.Direction ?? "Inbound",
        CounterpartyNumber: m.CounterpartyNumber ?? "",
        Text: m.Text,
        SentAt: m.SentEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
        IsRead: m.IsRead ?? false);

    private static VoicemailItemDto ToVoicemailDto(GvVoicemailNode v) => new(
        Id: v.MessageId ?? "",
        ThreadId: v.ThreadId ?? "",
        FromNumber: v.FromNumber ?? "",
        FromName: v.FromName,
        ReceivedAt: v.ReceivedEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
        DurationSeconds: v.DurationSeconds ?? 0,
        IsRead: v.IsRead ?? false,
        Transcript: v.Transcript,
        AudioUrl: $"/api/gvbridge/voicemail/{v.MessageId}/audio");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvThreadPollerTests" -v n`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/GvThreadPoller.cs \
        src/RotaryPhoneController.GVBridge.Tests/Services/GvThreadPollerTests.cs
git commit -m "feat(gv): add GvThreadPoller adaptive poll + diff raising message events"
```

---

## Task 6: GvSmsController (read endpoints) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GvSmsControllerTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvSmsController NewController(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        return new GvSmsController(smsClient, NullLogger<GvSmsController>.Instance);
    }

    private static HttpResponseMessage Response() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
              [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false]]]],
             "nextPageToken":null}
            """)
        };

    [Fact]
    public async Task GetThreads_MapsToThreadDtos()
    {
        var controller = NewController(_ => Response());
        var result = await controller.GetThreads(count: 20, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SmsThreadListDto>(ok.Value);
        Assert.Single(dto.Threads);
        Assert.Equal("t.+19195551234", dto.Threads[0].ThreadId);
        Assert.Equal("Alice", dto.Threads[0].CounterpartyName);
        Assert.True(dto.Threads[0].HasUnread);
    }

    [Fact]
    public async Task GetThreadMessages_MapsToMessageDtos()
    {
        var controller = NewController(_ => Response());
        var result = await controller.GetThreadMessages("t.+19195551234", count: 50, default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SmsThreadMessagesDto>(ok.Value);
        Assert.Single(dto.Messages);
        Assert.Equal("m.1", dto.Messages[0].Id);
        Assert.Equal("Inbound", dto.Messages[0].Direction);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvSmsControllerTests" -v n`
Expected: FAIL — `GvSmsController` does not exist.

- [ ] **Step 3: Implement GvSmsController**

Create `src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Clients;

namespace RotaryPhoneController.GVBridge.Api;

[ApiController]
[Route("api/gvbridge/sms")]
public class GvSmsController : ControllerBase
{
    private readonly GvSmsClient _smsClient;
    private readonly ILogger<GvSmsController> _logger;

    public GvSmsController(GvSmsClient smsClient, ILogger<GvSmsController> logger)
    {
        _smsClient = smsClient;
        _logger = logger;
    }

    [HttpGet("threads")]
    public async Task<IActionResult> GetThreads([FromQuery] int count = 20, CancellationToken ct = default)
    {
        var result = await _smsClient.ListThreadsAsync(count, pageToken: null, ct);
        var threads = result.Threads.Select(t => new SmsThreadDto(
            ThreadId: t.ThreadId ?? "",
            CounterpartyNumber: t.CounterpartyNumber ?? "",
            CounterpartyName: t.CounterpartyName,
            LastMessageAt: t.LastMessageEpochMs is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
            HasUnread: t.HasUnread ?? false,
            LastMessagePreview: t.LastMessagePreview)).ToList();
        return Ok(new SmsThreadListDto(threads, DateTime.UtcNow));
    }

    [HttpGet("threads/{threadId}")]
    public async Task<IActionResult> GetThreadMessages(
        string threadId, [FromQuery] int count = 50, CancellationToken ct = default)
    {
        var nodes = await _smsClient.ListMessagesAsync(threadId, count, ct);
        var messages = nodes.Select(m => new SmsMessageDto(
            Id: m.MessageId ?? "",
            ThreadId: m.ThreadId ?? "",
            Direction: m.Direction ?? "Inbound",
            CounterpartyNumber: m.CounterpartyNumber ?? "",
            Text: m.Text,
            SentAt: m.SentEpochMs is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
            IsRead: m.IsRead ?? false)).ToList();
        return Ok(new SmsThreadMessagesDto(threadId, messages, DateTime.UtcNow));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvSmsControllerTests" -v n`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs \
        src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerTests.cs
git commit -m "feat(gv): add GvSmsController read endpoints"
```

---

## Task 7: Server-side push bridge (seam → RotaryHub)

**Files:**
- Create: `src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs`
- Modify: the Server DI wiring (e.g. `Program.cs`) to register it + the poller.

- [ ] **Step 1: Implement the bridge**

Create `src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs`. It mirrors
`SignalRNotifierService`: an `IHostedService` that subscribes to the `GVBridge` seam and broadcasts to
`RotaryHub`. New SignalR events: `SmsReceived` (payload `SmsMessageDto`) and `VoicemailReceived`
(payload `VoicemailItemDto`) — exactly the §6.3 contract.

```csharp
using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.GVBridge.Services;
using RotaryPhoneController.Server.Hubs;

namespace RotaryPhoneController.Server.Services;

/// <summary>
/// Bridges the GVBridge message-event seam (IGvMessageEventSource — fed by GvThreadPoller, or later a
/// cracked signaler) to RotaryHub, broadcasting "SmsReceived"/"VoicemailReceived" to all connected
/// clients. Mirrors SignalRNotifierService's IncomingCall pattern (ADR §6.3). RadioConsole already
/// holds the hub connection; this is the only new wiring needed for SMS/voicemail push.
/// </summary>
public class GvMessagePushBridge : IHostedService
{
    private readonly IGvMessageEventSource _eventSource;
    private readonly IHubContext<RotaryHub> _hubContext;
    private readonly ILogger<GvMessagePushBridge> _logger;

    public GvMessagePushBridge(
        IGvMessageEventSource eventSource,
        IHubContext<RotaryHub> hubContext,
        ILogger<GvMessagePushBridge> logger)
    {
        _eventSource = eventSource;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eventSource.OnSmsReceived += BroadcastSms;
        _eventSource.OnVoicemailReceived += BroadcastVoicemail;
        _logger.LogInformation("GvMessagePushBridge subscribed to GV message events");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _eventSource.OnSmsReceived -= BroadcastSms;
        _eventSource.OnVoicemailReceived -= BroadcastVoicemail;
        return Task.CompletedTask;
    }

    private void BroadcastSms(GVBridge.Api.SmsMessageDto dto)
    {
        _logger.LogInformation("Broadcasting SmsReceived {Id} from {Number}", dto.Id, dto.CounterpartyNumber);
        _ = _hubContext.Clients.All.SendAsync("SmsReceived", dto);
    }

    private void BroadcastVoicemail(GVBridge.Api.VoicemailItemDto dto)
    {
        _logger.LogInformation("Broadcasting VoicemailReceived {Id} from {Number}", dto.Id, dto.FromNumber);
        _ = _hubContext.Clients.All.SendAsync("VoicemailReceived", dto);
    }
}
```

- [ ] **Step 2: Register the poller + event source + bridge in DI**

In `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`, register the poller as
a singleton, alias it to the seam, and start it as a hosted service:

```csharp
        services.AddSingleton<GvSmsClient>(sp => new GvSmsClient(
            sp.GetRequiredService<GvThreadClient>(),
            sp.GetRequiredService<IGvThreadParser>(),
            sp.GetRequiredService<ILogger<GvSmsClient>>()));

        services.AddSingleton<GvThreadPoller>();
        services.AddSingleton<IGvMessageEventSource>(sp => sp.GetRequiredService<GvThreadPoller>());
        services.AddHostedService(sp => sp.GetRequiredService<GvThreadPoller>());
```

In the **Server** DI wiring (`Program.cs`, where `SignalRNotifierService` / `AddGVBridge` are wired),
register the bridge as a hosted service (it lives in the Server project because it needs
`IHubContext<RotaryHub>`):

```csharp
builder.Services.AddHostedService<RotaryPhoneController.Server.Services.GvMessagePushBridge>();
```

> Find the existing `AddGVBridge(...)` call + the `SignalRNotifierService` hosted-service registration
> in `Program.cs` and add the `GvMessagePushBridge` registration alongside them. The `Explore` of
> Program.cs at implementation time will confirm the exact location; the bridge only needs
> `IGvMessageEventSource` (from `AddGVBridge`) and `IHubContext<RotaryHub>` (already registered by the
> SignalR setup) to be in the container.

- [ ] **Step 3: Build the Server project**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs \
        src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs \
        src/RotaryPhoneController.Server/Program.cs
git commit -m "feat(gv): bridge poller message events to RotaryHub (SmsReceived/VoicemailReceived)"
```

---

## Task 8: Full suite + completion gate

- [ ] **Step 1: Full GVBridge test suite**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests -v n`
Expected: all green.

- [ ] **Step 2: Server build**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Live UAT (Tester / owner, on the `radio` box) — ADR §11 step 5**

- Send a text TO the GV number from a phone; confirm the next poll surfaces it and `SmsReceived` fires
  **exactly once** (the high-water diff). Confirm an outbound (sent from GV) does NOT fire `SmsReceived`.
- Confirm `GET /api/gvbridge/sms/threads` and `/threads/{threadId}` return real data once cookies live.
- Confirm RadioConsole receives the `SmsReceived` push over its existing SignalR connection (no
  RadioConsole-side polling needed).
- Confirm a new voicemail fires `VoicemailReceived` once.

---

## Out of scope for PR3 (do NOT do here)

- No SMS send / `GvSmsClient.SendAsync` / `POST /api/gvbridge/sms/send` (PR4 — held for owner).
- No signaler work (PR6 — held/experimental). The seam (`IGvMessageEventSource`) is the drop-in point
  if it's ever cracked; do not build it here.
- No inter-service auth gate (PR5 — held for owner); endpoints stay LAN-only as today.
- No SQLite durability of the high-water mark (ADR §5.3 notes it as optional; in-memory for v1).
- No group-MMS threads (ADR §9, out of scope v1).
