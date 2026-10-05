# PR1 Plan — `feat(gv): thread + voicemail read clients`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Arc:** `docs/plans/gv-voicemail-sms-arc.md`
**ADR (source of truth):** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` (§2, §3, §7, §8, §10 row PR1, §11 checklist)
**Queue row:** `docs/BUILDER_QUEUE.md` → `GV-VM-SMS-1`
**Sensitivity:** Auto-merge eligible (read-only, no new network exposure, no endpoints).

---

## Goal

Build the two GV-side **read clients** that list threads and parse voicemail nodes from
`api2thread/list`, behind an **isolated, swappable parser seam** plus captured **JSON fixtures**, so a
later live-capture correction of GV's exact field positions is a localized change to one parser class.

This PR delivers **no HTTP endpoints and no DI-registered services** beyond the parser/clients — it is
pure library code + unit tests. PR2 (REST + audio) and PR3 (poller + push) consume what this PR builds.

## Why the parser seam is the heart of this PR

The ADR is explicit (§3.1, §8, §11): the **exact GV positional-array field indices are UNVERIFIED**.
If we sprinkle `GvProtobuf.GetString(node, 7)` calls across the clients, a single live correction
becomes a scattered edit. Instead:

- A `IGvThreadParser` interface defines the contract (raw `JsonElement` thread/message node → typed
  `GvThreadNode` / `GvVoicemailNode` / `GvSmsNode` records).
- `PositionalGvThreadParser` is the **one** place field indices live. All index constants are named
  `const int` fields at the top of that class with an `// UNVERIFIED — ADR §11 step N` comment.
- Fixtures in `Tests/Fixtures/` drive the parser tests. When live capture lands, the Builder swaps the
  fixture bytes + the index constants in ONE file; clients and DTOs do not change.

## Architectural seam: how new clients reach the authenticated `HttpClient`

`GVApiAdapter` is a singleton that owns the cookie-rotating `_httpClient` and already exposes it
*internally* to `GvSipCredentialProvider` via a private `SingleHttpClientFactory(() => _httpClient!)`.
New read clients must ride that **same** live client so they inherit PSIDTS rotation + the
cookie-recovery ladder for free (ADR §1.3, §7 "Share the adapter's `HttpClient`").

This PR adds a **public seam** on the adapter — `IGvAuthenticatedClientProvider` — that resolves the
*current* `HttpClient` (a `Func`, not a captured instance, so post-rotation swaps propagate). The
clients themselves stay test-friendly: their constructors take a plain `HttpClient` (exactly like
`GvAccountClient`), so unit tests inject a `MockHandler`. The provider seam is only used by PR2/PR3
wiring — this PR just defines it and implements it on the adapter so later PRs have a stable hook.

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Clients/
  GvThreadModels.cs          -- GvThreadNode, GvVoicemailNode, GvSmsNode records (internal parse DTOs)
  IGvThreadParser.cs         -- parser seam interface
  PositionalGvThreadParser.cs-- the ONE place GV field indices live (UNVERIFIED, ADR §11)
  GvThreadClient.cs          -- ListThreadsAsync(folder, count, pageToken) -> parsed nodes
  GvVoicemailClient.cs       -- ListVoicemailsAsync(count, pageToken) -> parsed voicemail nodes
  GvThreadFolder.cs          -- folder enum (All / Sms / Voicemail) w/ UNVERIFIED wire values
Adapters/
  IGvAuthenticatedClientProvider.cs -- public seam exposing the live authenticated HttpClient
```

### New test files (in `src/RotaryPhoneController.GVBridge.Tests/`)

```
Clients/
  PositionalGvThreadParserTests.cs
  GvThreadClientTests.cs
  GvVoicemailClientTests.cs
Fixtures/
  api2thread-list-sms.json        -- representative SMS-folder list (hand-built to ADR §3 shape)
  api2thread-list-voicemail.json  -- representative voicemail-folder list (media id + transcript)
  README.md                       -- "these are SYNTHETIC until ADR §11 live capture; see header"
```

### Modified files

```
Adapters/GVApiAdapter.cs                                   -- implement IGvAuthenticatedClientProvider
src/RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj -- copy Fixtures/ to output
```

---

## Task 1: Folder enum + parse-DTO records

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvThreadFolder.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvThreadModels.cs`

- [ ] **Step 1: Create the folder enum**

Create `src/RotaryPhoneController.GVBridge/Clients/GvThreadFolder.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// GV thread "folder"/filter selector for api2thread/list.
/// The integer WIRE VALUES are UNVERIFIED — ADR §11 step 1 must confirm the folder enum for
/// SMS/inbox vs voicemail. These are best-known placeholders; keep the mapping in ONE place
/// (<see cref="GvThreadFolderExtensions.ToWireValue"/>) so a live correction is a one-line change.
/// </summary>
public enum GvThreadFolder
{
    All,
    Sms,
    Voicemail
}

public static class GvThreadFolderExtensions
{
    /// <summary>
    /// Map a folder to its api2thread/list request wire value.
    /// UNVERIFIED — ADR §11 step 1. The web client loads each tab (All / Voicemail / Recorded /
    /// Missed) with a folder enum; the exact integers are pinned during live capture.
    /// </summary>
    public static int ToWireValue(this GvThreadFolder folder) => folder switch
    {
        GvThreadFolder.All => 1,
        GvThreadFolder.Sms => 2,
        GvThreadFolder.Voicemail => 3,
        _ => 1
    };
}
```

- [ ] **Step 2: Create the parse-DTO records**

Create `src/RotaryPhoneController.GVBridge/Clients/GvThreadModels.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Parsed representation of a single GV thread node from api2thread/list.
/// These are INTERNAL parse DTOs (Google's wire shape stays out of the public REST contract —
/// controllers map these to the §6.1 public records). Every field is nullable: GV positional
/// arrays shift and fields can be absent (ADR §7 "treat every field as possibly-null").
/// </summary>
public record GvThreadNode(
    string? ThreadId,
    string? CounterpartyNumber,   // E.164 as GV returns it (may need normalization upstream)
    string? CounterpartyName,
    long? LastMessageEpochMs,
    bool? HasUnread,
    string? LastMessagePreview);

/// <summary>
/// Parsed voicemail message node. MediaId is the reference PR2 resolves to fetchable audio.
/// Transcript may be null (pending/absent) — ADR §3.3.
/// </summary>
public record GvVoicemailNode(
    string? MessageId,
    string? ThreadId,
    string? FromNumber,
    string? FromName,
    long? ReceivedEpochMs,
    int? DurationSeconds,
    bool? IsRead,
    string? Transcript,
    string? MediaId);

/// <summary>
/// Parsed SMS message node (used by PR3 read path). Direction is GV-encoded; the parser maps it
/// to "Inbound"/"Outbound". Text may be null for non-text message subtypes.
/// </summary>
public record GvSmsNode(
    string? MessageId,
    string? ThreadId,
    string? Direction,            // "Inbound" | "Outbound"
    string? CounterpartyNumber,
    string? Text,
    long? SentEpochMs,
    bool? IsRead);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvThreadFolder.cs \
        src/RotaryPhoneController.GVBridge/Clients/GvThreadModels.cs
git commit -m "feat(gv): add thread folder enum and parse-DTO records"
```

---

## Task 2: Parser seam interface + synthetic fixtures

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Clients/IGvThreadParser.cs`
- Create: `src/RotaryPhoneController.GVBridge.Tests/Fixtures/api2thread-list-sms.json`
- Create: `src/RotaryPhoneController.GVBridge.Tests/Fixtures/api2thread-list-voicemail.json`
- Create: `src/RotaryPhoneController.GVBridge.Tests/Fixtures/README.md`
- Modify: `src/RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj`

- [ ] **Step 1: Create the parser interface**

Create `src/RotaryPhoneController.GVBridge/Clients/IGvThreadParser.cs`:

```csharp
using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Parser seam isolating GV's UNVERIFIED positional-array field positions (ADR §3, §8, §11).
/// The ONLY implementation that knows field indices is <see cref="PositionalGvThreadParser"/>;
/// when ADR §11 live capture pins the real positions, the fix is localized to that one class.
/// Clients depend on this interface, not on raw indices.
/// </summary>
public interface IGvThreadParser
{
    /// <summary>Parse the top-level api2thread/list response into thread nodes.</summary>
    IReadOnlyList<GvThreadNode> ParseThreadList(JsonElement root);

    /// <summary>Parse voicemail message nodes from a voicemail-folder list response.</summary>
    IReadOnlyList<GvVoicemailNode> ParseVoicemailList(JsonElement root);

    /// <summary>Parse SMS message nodes from a single thread's message list / SMS-folder list.</summary>
    IReadOnlyList<GvSmsNode> ParseSmsMessages(JsonElement root);

    /// <summary>Extract the next-page token from a list response, or null if none.</summary>
    string? ParseNextPageToken(JsonElement root);
}
```

- [ ] **Step 2: Create the synthetic SMS-folder fixture**

Create `src/RotaryPhoneController.GVBridge.Tests/Fixtures/api2thread-list-sms.json`. This is a
**synthetic** approximation of the ADR §3 shape (a thread array, each thread holding a message
array). It exists to lock the parser's behavior; the index map gets corrected against a real capture
in ADR §11 step 1 without changing the test structure.

```json
{
  "_comment": "SYNTHETIC fixture — NOT a live GV capture. Shape approximates ADR §3. Field positions are placeholders pinned in PositionalGvThreadParser and corrected via ADR §11 step 1.",
  "threads": [
    [
      "t.+19195551234",
      ["+19195551234", "Alice Example"],
      1718841600000,
      true,
      [
        ["m.111", "t.+19195551234", 0, "+19195551234", "hey are you around?", 1718841600000, false]
      ]
    ],
    [
      "t.+14045559876",
      ["+14045559876", null],
      1718838000000,
      false,
      [
        ["m.222", "t.+14045559876", 1, "+14045559876", "thanks!", 1718838000000, true]
      ]
    ]
  ],
  "nextPageToken": "PAGE2"
}
```

- [ ] **Step 3: Create the synthetic voicemail-folder fixture**

Create `src/RotaryPhoneController.GVBridge.Tests/Fixtures/api2thread-list-voicemail.json`:

```json
{
  "_comment": "SYNTHETIC fixture — NOT a live GV capture. Approximates ADR §3.2/§3.3: each voicemail message node carries a media id and an optional transcript (null = pending). Corrected via ADR §11 step 2.",
  "threads": [
    [
      "t.+19195551234",
      ["+19195551234", "Alice Example"],
      1718841600000,
      true,
      [
        ["vm.111", "t.+19195551234", "+19195551234", "Alice Example", 1718841600000, 23, false, "Hey it's Alice, call me back.", "media-abc-123"]
      ]
    ],
    [
      "t.+18005550000",
      ["+18005550000", null],
      1718830000000,
      false,
      [
        ["vm.222", "t.+18005550000", "+18005550000", null, 1718830000000, 8, true, null, "media-def-456"]
      ]
    ]
  ],
  "nextPageToken": null
}
```

- [ ] **Step 4: Create the fixtures README**

Create `src/RotaryPhoneController.GVBridge.Tests/Fixtures/README.md`:

```markdown
# GV API fixtures

These JSON files are **SYNTHETIC** — hand-built to approximate the shapes described in
`docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` §3. They are **not** live
Google Voice captures.

They exist so the parser (`PositionalGvThreadParser`) has deterministic input for unit tests and so a
later live-capture correction is a localized, test-covered change.

## Updating after live verification (ADR §11)

When real responses are captured on the `radio` box:
1. Replace these files with the real (redacted) response bodies.
2. Correct the `const int` index map at the top of `PositionalGvThreadParser`.
3. Re-run the parser tests — they should pass against the real shape with no client/DTO changes.

Until then, these fixtures encode the **best-known** shape; treat parser behavior as provisional.
```

- [ ] **Step 5: Make the test project copy fixtures to the output directory**

In `src/RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj`, add a new
`ItemGroup` (the project currently has none for content) so tests can read fixture files at runtime:

```xml
  <ItemGroup>
    <None Include="Fixtures\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/IGvThreadParser.cs \
        src/RotaryPhoneController.GVBridge.Tests/Fixtures/ \
        src/RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj
git commit -m "feat(gv): add IGvThreadParser seam and synthetic fixtures"
```

---

## Task 3: PositionalGvThreadParser (the one place indices live) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/PositionalGvThreadParserTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/PositionalGvThreadParser.cs`

- [ ] **Step 1: Write the failing parser tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/PositionalGvThreadParserTests.cs`:

```csharp
using System.Text.Json;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class PositionalGvThreadParserTests
{
    private static JsonElement LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var json = File.ReadAllText(path);
        return JsonDocument.Parse(json).RootElement;
    }

    private readonly PositionalGvThreadParser _parser = new();

    [Fact]
    public void ParseThreadList_ReturnsAllThreads()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        var threads = _parser.ParseThreadList(root);

        Assert.Equal(2, threads.Count);
        Assert.Equal("t.+19195551234", threads[0].ThreadId);
        Assert.Equal("+19195551234", threads[0].CounterpartyNumber);
        Assert.Equal("Alice Example", threads[0].CounterpartyName);
        Assert.True(threads[0].HasUnread);
        Assert.Equal("hey are you around?", threads[0].LastMessagePreview);
    }

    [Fact]
    public void ParseThreadList_ToleratesMissingName()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        var threads = _parser.ParseThreadList(root);
        Assert.Null(threads[1].CounterpartyName);
    }

    [Fact]
    public void ParseThreadList_OnNonArrayRoot_ReturnsEmpty()
    {
        var root = JsonDocument.Parse("\"not-an-array\"").RootElement;
        Assert.Empty(_parser.ParseThreadList(root));
    }

    [Fact]
    public void ParseVoicemailList_ParsesMediaIdAndTranscript()
    {
        var root = LoadFixture("api2thread-list-voicemail.json");
        var vms = _parser.ParseVoicemailList(root);

        Assert.Equal(2, vms.Count);
        Assert.Equal("vm.111", vms[0].MessageId);
        Assert.Equal("media-abc-123", vms[0].MediaId);
        Assert.Equal("Hey it's Alice, call me back.", vms[0].Transcript);
        Assert.Equal(23, vms[0].DurationSeconds);
        Assert.False(vms[0].IsRead);
    }

    [Fact]
    public void ParseVoicemailList_NullTranscriptIsPending()
    {
        var root = LoadFixture("api2thread-list-voicemail.json");
        var vms = _parser.ParseVoicemailList(root);
        Assert.Null(vms[1].Transcript);          // pending/absent — ADR §3.3
        Assert.Equal("media-def-456", vms[1].MediaId);
    }

    [Fact]
    public void ParseSmsMessages_MapsDirection()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        var msgs = _parser.ParseSmsMessages(root);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("m.111", msgs[0].MessageId);
        Assert.Equal("Inbound", msgs[0].Direction);   // wire 0 -> Inbound
        Assert.Equal("Outbound", msgs[1].Direction);  // wire 1 -> Outbound
        Assert.Equal("hey are you around?", msgs[0].Text);
    }

    [Fact]
    public void ParseNextPageToken_ReturnsToken()
    {
        var root = LoadFixture("api2thread-list-sms.json");
        Assert.Equal("PAGE2", _parser.ParseNextPageToken(root));
    }

    [Fact]
    public void ParseNextPageToken_NullWhenAbsent()
    {
        var root = LoadFixture("api2thread-list-voicemail.json");
        Assert.Null(_parser.ParseNextPageToken(root));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~PositionalGvThreadParserTests" -v n`
Expected: FAIL — `PositionalGvThreadParser` does not exist.

- [ ] **Step 3: Implement PositionalGvThreadParser**

Create `src/RotaryPhoneController.GVBridge/Clients/PositionalGvThreadParser.cs`. **All field indices
live here as named constants** with UNVERIFIED markers. The fixtures above are built to match these
constants; both get corrected together against a real capture (ADR §11).

```csharp
using System.Text.Json;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// The single source of truth for GV api2thread/list positional-array field indices.
/// EVERY index below is UNVERIFIED (ADR §3, §11). When a live capture lands, correct the
/// const map + the test fixtures together; clients, DTOs, and tests-structure stay unchanged.
///
/// Wire shape assumed (synthetic, ADR §3):
///   root                = { "threads": [ thread, ... ], "nextPageToken": string|null }
///   thread              = [ threadId, [number, name?], lastMsgEpochMs, hasUnread, [ message, ... ] ]
///   sms message         = [ msgId, threadId, directionInt, counterparty, text, sentEpochMs, isRead ]
///   voicemail message   = [ msgId, threadId, from, fromName, recvEpochMs, durSec, isRead, transcript?, mediaId ]
/// </summary>
public sealed class PositionalGvThreadParser : IGvThreadParser
{
    // --- root ---  (UNVERIFIED — ADR §11 step 1)
    private const string ThreadsProp = "threads";
    private const string NextPageTokenProp = "nextPageToken";

    // --- thread node indices ---  (UNVERIFIED — ADR §11 step 1)
    private const int ThreadIdIdx = 0;
    private const int ThreadParticipantIdx = 1;   // [number, name?]
    private const int ThreadLastMsgEpochIdx = 2;
    private const int ThreadHasUnreadIdx = 3;
    private const int ThreadMessagesIdx = 4;
    private const int ParticipantNumberIdx = 0;
    private const int ParticipantNameIdx = 1;

    // --- sms message node indices ---  (UNVERIFIED — ADR §11 step 1)
    private const int SmsIdIdx = 0;
    private const int SmsThreadIdIdx = 1;
    private const int SmsDirectionIdx = 2;        // 0 = inbound, 1 = outbound (UNVERIFIED)
    private const int SmsCounterpartyIdx = 3;
    private const int SmsTextIdx = 4;
    private const int SmsSentEpochIdx = 5;
    private const int SmsIsReadIdx = 6;

    // --- voicemail message node indices ---  (UNVERIFIED — ADR §11 step 2)
    private const int VmIdIdx = 0;
    private const int VmThreadIdIdx = 1;
    private const int VmFromIdx = 2;
    private const int VmFromNameIdx = 3;
    private const int VmRecvEpochIdx = 4;
    private const int VmDurationIdx = 5;
    private const int VmIsReadIdx = 6;
    private const int VmTranscriptIdx = 7;
    private const int VmMediaIdIdx = 8;

    public IReadOnlyList<GvThreadNode> ParseThreadList(JsonElement root)
    {
        var threads = ThreadsArray(root);
        if (threads is null) return Array.Empty<GvThreadNode>();

        var result = new List<GvThreadNode>(threads.Value.GetArrayLength());
        foreach (var thread in threads.Value.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Array) continue;
            var participant = GvProtobuf.GetArray(thread, ThreadParticipantIdx);
            result.Add(new GvThreadNode(
                ThreadId: GvProtobuf.GetString(thread, ThreadIdIdx),
                CounterpartyNumber: participant is { } p ? GvProtobuf.GetString(p, ParticipantNumberIdx) : null,
                CounterpartyName: participant is { } p2 ? GvProtobuf.GetString(p2, ParticipantNameIdx) : null,
                LastMessageEpochMs: GvProtobuf.GetLong(thread, ThreadLastMsgEpochIdx),
                HasUnread: GetBool(thread, ThreadHasUnreadIdx),
                LastMessagePreview: LastMessagePreview(thread)));
        }
        return result;
    }

    public IReadOnlyList<GvVoicemailNode> ParseVoicemailList(JsonElement root)
    {
        var result = new List<GvVoicemailNode>();
        foreach (var msg in EnumerateMessages(root))
        {
            result.Add(new GvVoicemailNode(
                MessageId: GvProtobuf.GetString(msg, VmIdIdx),
                ThreadId: GvProtobuf.GetString(msg, VmThreadIdIdx),
                FromNumber: GvProtobuf.GetString(msg, VmFromIdx),
                FromName: GvProtobuf.GetString(msg, VmFromNameIdx),
                ReceivedEpochMs: GvProtobuf.GetLong(msg, VmRecvEpochIdx),
                DurationSeconds: GvProtobuf.GetInt(msg, VmDurationIdx),
                IsRead: GetBool(msg, VmIsReadIdx),
                Transcript: GvProtobuf.GetString(msg, VmTranscriptIdx),
                MediaId: GvProtobuf.GetString(msg, VmMediaIdIdx)));
        }
        return result;
    }

    public IReadOnlyList<GvSmsNode> ParseSmsMessages(JsonElement root)
    {
        var result = new List<GvSmsNode>();
        foreach (var msg in EnumerateMessages(root))
        {
            result.Add(new GvSmsNode(
                MessageId: GvProtobuf.GetString(msg, SmsIdIdx),
                ThreadId: GvProtobuf.GetString(msg, SmsThreadIdIdx),
                Direction: GvProtobuf.GetInt(msg, SmsDirectionIdx) == 1 ? "Outbound" : "Inbound",
                CounterpartyNumber: GvProtobuf.GetString(msg, SmsCounterpartyIdx),
                Text: GvProtobuf.GetString(msg, SmsTextIdx),
                SentEpochMs: GvProtobuf.GetLong(msg, SmsSentEpochIdx),
                IsRead: GetBool(msg, SmsIsReadIdx)));
        }
        return result;
    }

    public string? ParseNextPageToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(NextPageTokenProp, out var tok)) return null;
        return tok.ValueKind == JsonValueKind.String ? tok.GetString() : null;
    }

    // ---- helpers ----

    private static JsonElement? ThreadsArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(ThreadsProp, out var threads)) return null;
        return threads.ValueKind == JsonValueKind.Array ? threads : null;
    }

    private static IEnumerable<JsonElement> EnumerateMessages(JsonElement root)
    {
        var threads = ThreadsArray(root);
        if (threads is null) yield break;
        foreach (var thread in threads.Value.EnumerateArray())
        {
            if (thread.ValueKind != JsonValueKind.Array) continue;
            var messages = GvProtobuf.GetArray(thread, ThreadMessagesIdx);
            if (messages is null) continue;
            foreach (var msg in messages.Value.EnumerateArray())
                if (msg.ValueKind == JsonValueKind.Array)
                    yield return msg;
        }
    }

    private static string? LastMessagePreview(JsonElement thread)
    {
        var messages = GvProtobuf.GetArray(thread, ThreadMessagesIdx);
        if (messages is null || messages.Value.GetArrayLength() == 0) return null;
        var last = messages.Value[messages.Value.GetArrayLength() - 1];
        return last.ValueKind == JsonValueKind.Array ? GvProtobuf.GetString(last, SmsTextIdx) : null;
    }

    private static bool? GetBool(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength()) return null;
        var el = array[index];
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.GetInt32() != 0,
            _ => null
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~PositionalGvThreadParserTests" -v n`
Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/PositionalGvThreadParser.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/PositionalGvThreadParserTests.cs
git commit -m "feat(gv): add PositionalGvThreadParser isolating UNVERIFIED field positions"
```

---

## Task 4: GvThreadClient — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvThreadClientTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvThreadClient.cs`

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/GvThreadClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvThreadClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvThreadClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new MockHandler(handler)), BaseUrl, ApiKey,
               new PositionalGvThreadParser(), NullLogger<GvThreadClient>.Instance);

    [Fact]
    public async Task ListThreadsAsync_PostsToApi2ThreadList()
    {
        string? capturedUrl = null;
        var client = NewClient(req =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"threads":[],"nextPageToken":null}""") };
        });

        await client.ListThreadsAsync(GvThreadFolder.Sms, count: 20);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/api2thread/list", capturedUrl!);
        Assert.Contains($"key={ApiKey}", capturedUrl!);
    }

    [Fact]
    public async Task ListThreadsAsync_ParsesThreadsViaParser()
    {
        var body = """
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["m.1","t.+19195551234",0,"+19195551234","hi",1718841600000,false]]]],
         "nextPageToken":"P2"}
        """;
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body) });

        var result = await client.ListThreadsAsync(GvThreadFolder.Sms, count: 20);

        Assert.Single(result.Threads);
        Assert.Equal("t.+19195551234", result.Threads[0].ThreadId);
        Assert.Equal("P2", result.NextPageToken);
    }

    [Fact]
    public async Task ListThreadsAsync_OnNon200_ReturnsEmptyResult()
    {
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.ListThreadsAsync(GvThreadFolder.Sms, count: 20);

        Assert.Empty(result.Threads);
        Assert.False(result.Succeeded);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvThreadClientTests" -v n`
Expected: FAIL — `GvThreadClient` does not exist.

- [ ] **Step 3: Implement GvThreadClient**

Create `src/RotaryPhoneController.GVBridge/Clients/GvThreadClient.cs`. Mirrors `GvAccountClient`
exactly (shared `HttpClient`, base URL, api key, logger) and delegates ALL field extraction to the
injected `IGvThreadParser`.

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>Result of a thread list call. Succeeded=false means a non-200/parse failure (caller
/// should not treat empty as "no threads" — the poller distinguishes them, ADR §5.3).</summary>
public record GvThreadListResult(IReadOnlyList<GvThreadNode> Threads, string? NextPageToken, bool Succeeded)
{
    public static GvThreadListResult Empty(bool succeeded) =>
        new(Array.Empty<GvThreadNode>(), null, succeeded);
}

/// <summary>
/// Lists GV threads via api2thread/list. Thin wrapper over the shared authenticated HttpClient
/// (ADR §1.3, §7) — gets auth/cookies/PSIDTS freshness for free. All field parsing is delegated to
/// <see cref="IGvThreadParser"/> so UNVERIFIED positions live in exactly one place.
/// </summary>
public class GvThreadClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvThreadClient> _logger;

    public GvThreadClient(HttpClient http, string baseUrl, string apiKey,
        IGvThreadParser parser, ILogger<GvThreadClient> logger)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _parser = parser;
        _logger = logger;
    }

    public async Task<GvThreadListResult> ListThreadsAsync(
        GvThreadFolder folder, int count = 20, string? pageToken = null, CancellationToken ct = default)
    {
        var root = await ListRawAsync(folder, count, pageToken, ct);
        if (root is null) return GvThreadListResult.Empty(succeeded: false);

        var threads = _parser.ParseThreadList(root.Value.RootElement);
        var token = _parser.ParseNextPageToken(root.Value.RootElement);
        return new GvThreadListResult(threads, token, Succeeded: true);
    }

    /// <summary>
    /// Raw list call shared by thread/voicemail/SMS read paths — returns the parsed JsonDocument or
    /// null on failure. Request body positions (folder, pageToken, count) are UNVERIFIED — ADR §11
    /// step 1. Built via GvProtobuf.BuildArray so the positional shape is in one obvious place.
    /// </summary>
    public async Task<JsonDocument?> ListRawAsync(
        GvThreadFolder folder, int count, string? pageToken, CancellationToken ct = default)
    {
        try
        {
            var url = $"{_baseUrl}/api2thread/list?alt=protojson&key={_apiKey}";
            // UNVERIFIED positional body — ADR §11 step 1 (candidate: [folder, pageToken?, count?]).
            var payload = GvProtobuf.BuildArray(folder.ToWireValue(), pageToken, count);
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await _http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("api2thread/list returned {Status} for folder {Folder}",
                    response.StatusCode, folder);
                return null;
            }
            var raw = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "api2thread/list failed for folder {Folder}", folder);
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvThreadClientTests" -v n`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvThreadClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvThreadClientTests.cs
git commit -m "feat(gv): add GvThreadClient over shared authenticated HttpClient"
```

---

## Task 5: GvVoicemailClient (list only) — TDD

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvVoicemailClientTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs`

> NOTE: This PR ships **list only**. `GetRecordingStreamAsync` (audio fetch) is PR2 — do NOT add it
> here. Keeping the media fetch out of PR1 keeps PR1 strictly read-list and avoids the UNVERIFIED
> media-endpoint risk (ADR §3.2, §11 step 3) bleeding into the auto-merge-eligible read-clients PR.

- [ ] **Step 1: Write failing tests**

Create `src/RotaryPhoneController.GVBridge.Tests/Clients/GvVoicemailClientTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvVoicemailClientTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvVoicemailClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvVoicemailClient(threadClient, parser, NullLogger<GvVoicemailClient>.Instance);
    }

    [Fact]
    public async Task ListVoicemailsAsync_ParsesMediaIdAndTranscript()
    {
        var body = """
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,false,"call me","media-1"]]]],
         "nextPageToken":null}
        """;
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body) });

        var result = await client.ListVoicemailsAsync(count: 20);

        Assert.Single(result.Items);
        Assert.Equal("vm.1", result.Items[0].MessageId);
        Assert.Equal("media-1", result.Items[0].MediaId);
        Assert.Equal("call me", result.Items[0].Transcript);
    }

    [Fact]
    public async Task ListVoicemailsAsync_OnFailure_ReturnsEmptyNotSucceeded()
    {
        var client = NewClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var result = await client.ListVoicemailsAsync(count: 20);
        Assert.Empty(result.Items);
        Assert.False(result.Succeeded);
    }

    private class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailClientTests" -v n`
Expected: FAIL — `GvVoicemailClient` does not exist.

- [ ] **Step 3: Implement GvVoicemailClient (list only)**

Create `src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs`. It composes `GvThreadClient`
(voicemail is a thread/message subtype, ADR §3) — it does NOT duplicate the HTTP call.

```csharp
using Microsoft.Extensions.Logging;

namespace RotaryPhoneController.GVBridge.Clients;

public record GvVoicemailListResult(IReadOnlyList<GvVoicemailNode> Items, string? NextPageToken, bool Succeeded)
{
    public static GvVoicemailListResult Empty(bool succeeded) =>
        new(Array.Empty<GvVoicemailNode>(), null, succeeded);
}

/// <summary>
/// Lists voicemails by reading the voicemail folder of api2thread/list (ADR §3.1: voicemail is a
/// thread/message subtype, not a separate product). Composes <see cref="GvThreadClient"/> so the
/// authenticated HTTP call + parser seam are shared. Audio fetch is PR2 — intentionally absent here.
/// </summary>
public class GvVoicemailClient
{
    private readonly GvThreadClient _threadClient;
    private readonly IGvThreadParser _parser;
    private readonly ILogger<GvVoicemailClient> _logger;

    public GvVoicemailClient(GvThreadClient threadClient, IGvThreadParser parser,
        ILogger<GvVoicemailClient> logger)
    {
        _threadClient = threadClient;
        _parser = parser;
        _logger = logger;
    }

    public async Task<GvVoicemailListResult> ListVoicemailsAsync(
        int count = 20, string? pageToken = null, CancellationToken ct = default)
    {
        using var doc = await _threadClient.ListRawAsync(GvThreadFolder.Voicemail, count, pageToken, ct);
        if (doc is null) return GvVoicemailListResult.Empty(succeeded: false);

        var items = _parser.ParseVoicemailList(doc.RootElement);
        var token = _parser.ParseNextPageToken(doc.RootElement);
        _logger.LogDebug("Listed {Count} voicemails", items.Count);
        return new GvVoicemailListResult(items, token, Succeeded: true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~GvVoicemailClientTests" -v n`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvVoicemailClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvVoicemailClientTests.cs
git commit -m "feat(gv): add GvVoicemailClient (list only) over thread read path"
```

---

## Task 6: Expose the authenticated-client seam on GVApiAdapter

This is the hook PR2/PR3 use to give the controllers + poller the **live** cookie-rotating client.
This PR only defines and implements it (no consumers yet) so the later PRs have a stable seam.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge/Adapters/IGvAuthenticatedClientProvider.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`

- [ ] **Step 1: Define the provider interface**

Create `src/RotaryPhoneController.GVBridge/Adapters/IGvAuthenticatedClientProvider.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Adapters;

/// <summary>
/// Seam exposing the CURRENT authenticated GV HttpClient (cookie + SAPISIDHASH + PSIDTS-fresh).
/// Implemented by <see cref="GVApiAdapter"/>. New read clients/services resolve the live client
/// through this so they inherit cookie rotation + the recovery ladder (ADR §1.3, §7). Returns null
/// when the adapter is not activated / has no valid cookies.
/// </summary>
public interface IGvAuthenticatedClientProvider
{
    /// <summary>The current authenticated HttpClient, or null if the adapter is unavailable.</summary>
    HttpClient? GetAuthenticatedClient();

    /// <summary>The GV voiceclient base URL (e.g. .../voice/v1/voiceclient).</summary>
    string ApiBaseUrl { get; }

    /// <summary>The GV public web API key.</summary>
    string ApiKey { get; }
}
```

- [ ] **Step 2: Implement it on GVApiAdapter**

In `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`, add `IGvAuthenticatedClientProvider`
to the class declaration and implement the members. The adapter already holds `_httpClient` (swapped
on rotation/reload) and `_config`; expose them through the seam. Returning the field directly is
correct because every reload/rotate re-assigns `_httpClient`, so callers that fetch on each use get
the fresh client (same rationale as the existing `SingleHttpClientFactory`).

Change the class declaration:

```csharp
public class GVApiAdapter : ICallAdapter, IGvAuthenticatedClientProvider, IDisposable
```

Add these members (e.g. just after the `CurrentCookieSet`/`CookieStore` internal accessors):

```csharp
    // --- IGvAuthenticatedClientProvider (seam for PR2/PR3 read clients) ---

    /// <summary>
    /// The current authenticated HttpClient or null if unavailable. Fetched live (not cached by
    /// callers) so cookie rotation/reload that swaps _httpClient propagates — same contract as the
    /// internal SingleHttpClientFactory used by the SIP credential provider.
    /// </summary>
    public HttpClient? GetAuthenticatedClient() => IsAvailable ? _httpClient : null;

    /// <inheritdoc />
    public string ApiBaseUrl => _config.GvApiBaseUrl;

    /// <inheritdoc />
    public string ApiKey => _config.GvApiKey;
```

- [ ] **Step 3: Register the seam in DI (alias to the existing singleton)**

In `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`, add after the
existing `ICallAdapter` alias registration so the seam resolves to the same `GVApiAdapter` singleton:

```csharp
        services.AddSingleton<IGvAuthenticatedClientProvider>(
            sp => sp.GetRequiredService<GVApiAdapter>());
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Adapters/IGvAuthenticatedClientProvider.cs \
        src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs \
        src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs
git commit -m "feat(gv): expose authenticated-client seam on GVApiAdapter for read clients"
```

---

## Task 7: Full suite + plan-completion gate

- [ ] **Step 1: Run the full GVBridge test suite**

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests -v n`
Expected: all green (existing + the ~14 new tests).

- [ ] **Step 2: Build the whole solution (deploy-all-DLLs hygiene)**

Run: `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`
Expected: Build succeeded — confirms the new seam doesn't break Server wiring.

- [ ] **Step 3: Verify no live-network test was added**

Confirm by inspection that every new test uses an in-memory `MockHandler` or a local fixture file —
**no test hits Google**. This PR must stay hermetic.

---

## ADR §11 live-verification gate (BEFORE field parsing is considered final)

This PR ships against the **best-known synthetic** field positions. Field parsing is **provisional**
until the ADR §11 checklist is run on the `radio` box with live cookies:

- **§11 step 1 (thread list shape)** pins folder enum + thread/message indices → corrects
  `PositionalGvThreadParser` const map + `api2thread-list-sms.json`.
- **§11 step 2 (voicemail node shape)** pins media-id + transcript positions → corrects the voicemail
  const map + `api2thread-list-voicemail.json`.

**Do not mark the voicemail/SMS read experience "verified" until those two steps pass.** The seam +
fixtures are designed so each correction is a one-file edit with the tests re-run. This gate is owned
by the Tester/owner during live verification, NOT by the Builder at merge time — PR1 is auto-merge
eligible as **hermetic provisional code** precisely because the risk is quarantined behind the seam.

---

## Out of scope for PR1 (do NOT do here)

- No REST endpoints (PR2).
- No audio fetch / `GetRecordingStreamAsync` (PR2).
- No poller, no SignalR events (PR3).
- No SMS send (PR4).
- No DTO mapping to the §6.1 public records (that mapping lives in the controllers, PR2/PR3).
- No `GvSmsClient` (PR3 read path / PR4 send path).
