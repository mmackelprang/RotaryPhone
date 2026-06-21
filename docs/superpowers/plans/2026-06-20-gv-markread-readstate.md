# Plan — `feat(gv): mark-read / durable read-state`

> # 🔒 OWNER-HOLD — DO NOT BUILD OR MERGE WITHOUT EXPLICIT OWNER APPROVAL
> **Reason: this adds a real Google Voice ACCOUNT WRITE (`api2thread/updateread`).** Like SMS send
> (PR4), it mutates the owner's live GV account. The contract is **ratified** (RadioConsole is wiring
> against it now), but the **build is HELD by the owner** per the ADR. This document is **QUEUED &
> build-ready** so a future Builder cycle has a frozen, literal target — it does **NOT** authorize the
> build. When unheld, it ships **DARK** behind `EnableMarkRead` (default FALSE), fixture-verified only;
> the first **real** `updateread` is gated on the ADR §11 step 8 on-box live capture.
>
> **Sequencing — split into two PRs (this plan covers PATH A only):**
> - **PATH A (this plan / one PR):** the two mark routes + a serialization seam for the UNVERIFIED
>   `updateread` payload + the `EnableMarkRead` dark flag + on-mark `ReadStateChanged` broadcast.
> - **PATH B (separate fast-follow PR — Task 9, scoped here but NOT built in path A):** poller-detected
>   externally-originated read-flip → `ReadStateChanged` ("hear on phone → kiosk badge clears live").
>   Heavier: needs new per-item read-flag diff state in `GvThreadPoller`. Deferrable; routes ship without it.

> **For agentic workers (once unheld):** REQUIRED SUB-SKILL — use `superpowers:subagent-driven-development`
> (recommended) or `superpowers:executing-plans` to implement task-by-task. Steps use checkbox (`- [ ]`)
> syntax for tracking. Follow the project branch/PR + auto-merge policy in `CLAUDE.md` — but the OWNER-HOLD
> above overrides auto-merge: **do not merge path A without explicit owner approval** (GV account write).

**Arc:** `docs/plans/gv-voicemail-sms-arc.md` (phase-log row "FF. GV mark-read")
**ADR / contract (source of truth — implement FAITHFULLY, do NOT redesign):**
`docs/architecture/decisions/2026-06-20-gv-markread-readstate-contract.md`
— §3 (write-through, no local store), §4 (routes/shapes/status), §5 (`ReadStateChanged`, path-a/path-b
split), §6 (mark-unread best-effort, auth auto-covered), §7 (UNVERIFIED `updateread` seam + §11 step 8),
§8 (dark-flag safety posture).
**Reply already sent to RadioConsole (the shapes we PROMISED — match VERBATIM):**
`docs/handoffs/radioconsole-gv-markread-reply.md`.
**Parent ADR:** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` (§3.4 updateread,
§6.1 frozen DTOs, §6.3 push bridge, §6.5 auth gate, §11 live-capture checklist).
**Original request (context):** `docs/prompts/radioconsole-gv-markread-readstate-request.md`.
**TEMPLATE (copy its discipline — flag, seam, taxonomy, honest status, fixtures):** merged PR4 plan
`docs/superpowers/plans/2026-06-20-gv-pr4-sms-send.md` + its shipped code (PR #60).
**Depends on:** PR3 (read path: `GvSmsClient`, `GvThreadClient`, `GvVoicemailClient`, `IGvThreadParser`,
`GvThreadPoller`, `IGvMessageEventSource`, `GvMessagePushBridge`, `RotaryHub`) + PR4 (`EnableSmsSend`
flag pattern, `ISmsThreadIdResolver` seam pattern, `IGvAuthenticatedClientProvider` write seam,
`IGvOutboundSmsSink` push-producer pattern) + PR5 (`GvBridgeAuthMiddleware` prefix gate) — **all merged**
(#54/#56/#57/#60/#61).
**Sensitivity:** 🔒 **OWNER-HELD** — GV account write. Ships DARK (`EnableMarkRead`=false). First real
`updateread` pending ADR §11 step 8. **Fixture-verified only at merge; no live mark from the dev env.**

---

## Goal (PATH A)

Add the write half the read-side arc deliberately deferred (parent ADR §3.4): **durable mark-read** via
GV write-through, exactly as ratified. Concretely:

1. **`GvReadStateClient.MarkReadAsync(...)`** — a new client that `POST`s GV **`api2thread/updateread`**
   over the **shared authenticated `HttpClient`** (same `IGvAuthenticatedClientProvider` seam every read
   client + `GvSmsClient.SendAsync` rides), mirroring `GvSmsClient.SendAsync` exactly: resolve live
   client → POST → classify outcome into an honest taxonomy → **never throw**. The `updateread` payload
   is built behind a **serialization seam** `IUpdateReadPayloadBuilder`, tagged **UNVERIFIED — pending
   ADR §11 step 8 `updateread` capture** (same discipline as PR4's `ISmsThreadIdResolver`).
2. **Two routes**, each returning the **updated frozen DTO** (NOT 204), behind the `EnableMarkRead` flag:
   - `POST /api/gvbridge/voicemail/{id}/read`, body `{ "isRead": bool }` → updated `VoicemailItemDto`.
   - `POST /api/gvbridge/sms/threads/{threadId}/read`, body `{ "isRead": bool }` → updated `SmsThreadDto`
     with `hasUnread=false`.
3. **Honest status table** (§"Status taxonomy"): **200** applied-or-already (idempotent no-op re-mark →
   200, never 409; may satisfy locally without an upstream call when already in the target state, but the
   returned DTO must reflect TRUE state), **404** unknown id/thread, **502** upstream GV failure, **409
   `markread_disabled`** when the flag is off (checked FIRST, no GV call), **400 `unread_unsupported`**
   for `isRead:false` (v1 mark-unread is best-effort; ADR §6.1).
4. **`ReadStateChanged` event** on `RotaryHub` (camelCase payload `{ kind, id, threadId, isRead,
   changedAtUtc }`), fired **on a mark route call** (path a) through the **existing** `IGvMessageEventSource`
   → `GvMessagePushBridge` → `RotaryHub` pattern. (Path b — poller-detected external flip — is Task 9,
   NOT built here.)
5. **`EnableMarkRead` config flag on `GVBridgeConfig`, DEFAULT FALSE** — mirrors `EnableSmsSend`. With the
   flag off, both routes return `409 markread_disabled` and perform **NO GV call** (checked first). Makes
   the account-write code safe to merge dark.

## The honesty constraint (read first — this project has a dishonest-status incident)

Memory `project_gv_registration_603_incident.md`: a prior "rings but no audio" bug traced to **status
that over-claimed success**. Same discipline applies (this is the same posture PR4 took for `sendsms`):

- A 200 from `updateread` means **GV accepted the mark**, not a guess. The returned DTO's `isRead` /
  `hasUnread` reflects the **applied** state. Never report a mark we did not observe GV accept.
- The returned DTO is built by the **same list+filter re-read the read routes already do**
  (`GvVoicemailController.FindNodeAsync`, `GvSmsController.GetThreads`) — so it reflects GV's truth, not
  an optimistic local guess. (If a future §11 capture shows `updateread` echoes the node, switch to that;
  until then, re-read — same cheap call the routes make anyway. ADR §4.4.)
- On any non-200 / ambiguous upstream failure: **502**, never a false 200. RadioConsole keeps its
  optimistic flip and reconciles on the next list/poll (ADR §3.2, §4.3).
- **No auto-retry** on ambiguous upstream failure (ADR §8) — return 502, let RadioConsole reconcile.

## Status taxonomy — distinguishable, deterministic outcomes (match the reply VERBATIM)

The reply (`radioconsole-gv-markread-reply.md` §2) promised this exact table. Both routes map each
outcome to a distinct **HTTP status** so RadioConsole switches without parsing prose. The error body is
`{ "error": "..." }` (same shape the existing read routes return on 404/502).

| Outcome | HTTP | Body | When |
|---|---|---|---|
| **Applied / already in state** (idempotent) | **200** | updated `VoicemailItemDto` / `SmsThreadDto` | GV accepted the mark, OR item already in the target state (no-op) |
| **Mark-read disabled (dark)** | **409** | `{ "error": "markread_disabled" }` | server `EnableMarkRead`=false — **no GV call made** |
| **Unknown id/thread** | **404** | `{ "error": "..." }` | `{id}` / `{threadId}` not found in the GV list |
| **Mark-unread unsupported (v1)** | **400** | `{ "error": "unread_unsupported" }` | body `isRead:false` AND `AllowMarkUnread`=false (ADR §6.1) — **no GV call** |
| **Upstream GV failure** | **502** | `{ "error": "..." }` | `updateread` non-200 / auth blip / timeout / no authenticated client |

**Mapping rules (deterministic + testable):**
- **`markread_disabled` (409)** is returned by the feature-flag guard FIRST, before any lookup or GV call
  — mirrors PR4's `send_disabled`. (ADR §8: "with the flag off, the mark routes perform no GV call.")
- **`unread_unsupported` (400)** is returned for `isRead:false` when `AllowMarkUnread`=false (the v1
  default), BEFORE any GV call, so we never promise a toggle we cannot honor (ADR §6.1, reply §5). If a
  future §11 capture confirms GV accepts unread, the owner flips `AllowMarkUnread=true` and `isRead:false`
  flows through to `updateread`.
- **`404`** comes from the SAME list+filter the read routes use: if the id/thread is not present in the GV
  list, it is unknown — return 404 **before** attempting the write (you cannot mark what you cannot find,
  and you need the node to build the response DTO anyway).
- **`502`** covers every "we did not observe success" case — `GvUpdateReadOutcome.AdapterUnavailable`
  (null authenticated client), `.UpstreamError` (GV non-200), `.Timeout` (no response). All collapse to
  502 with `{ "error": ... }` (the reply's table lists a single 502 row; we do NOT subdivide it on the
  wire, unlike send's 502/504 split — the reply promised one 502). The CLIENT still classifies them
  distinctly for logging.
- **Idempotency:** a re-mark of an already-read item → **200** no-op with the true DTO, **never 409**. The
  controller MAY skip the GV call when the freshly-read node is already in the target state (ADR §4.3),
  but the returned DTO must reflect the true (already-applied) state. Tests assert the `(status, error-code)`
  pair per row.

---

## The stable seam: `updateread` payload serialization (ADR §7, UNVERIFIED — §11 step 8)

The real unknown in this PR is the **exact `api2thread/updateread` wire format** — resource name, payload
positions (which slots carry the id and the read bool), and per-thread vs per-message grain. The ADR is
emphatic: the **contract boundary is stable; the GV wire format behind it is not**. We isolate the wire
format behind **one interface** so the §11 step 8 live-capture correction is a **one-file change** —
exactly the discipline `IGvThreadParser`, `GvThreadFolder.ToWireValue()`, and PR4's `ISmsThreadIdResolver`
use.

- `IUpdateReadPayloadBuilder.BuildVoicemail(messageId, threadId, isRead)` → the `updateread` JSON array.
- `IUpdateReadPayloadBuilder.BuildSmsThread(threadId, messageIds, isRead)` → the `updateread` JSON array(s)
  for a **per-thread** mark (the grain RadioConsole sees). If GV's native grain turns out to be
  **per-message** (UNVERIFIED — §11 step 8), this returns one payload per message id (iterate the thread's
  messages); if GV exposes a thread-level arg, it returns one. **Either way the route's contract is
  per-thread** (ADR §4.2, Q4).
- The default impl's payload positions are the **working assumption** (`[threadId, isRead]`-shaped via
  `GvProtobuf.BuildArray`), tagged **UNVERIFIED** in code + tests, identical to how the positional parser
  indices and `SmsThreadIdResolver`'s `t.+<E164>` are tagged. **If live capture reveals different
  positions, fix them HERE only.**

> **Why a seam and not a guess baked into the client:** the ADR (§7) requires the GV wire format to be
> "contained behind the same client seam pattern as `sendsms` — the contract boundary RadioConsole sees
> does not depend on it." This interface IS that containment. `GvReadStateClient` calls the builder; it
> does not know the positions.

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Clients/
  IUpdateReadPayloadBuilder.cs   -- the UNVERIFIED updateread serialization seam (positions/grain)
  UpdateReadPayloadBuilder.cs    -- default impl; payload shape UNVERIFIED pending ADR §11 step 8
  GvReadStateClient.cs           -- MarkVoicemailReadAsync / MarkSmsThreadReadAsync (api2thread/updateread)
                                    + GvUpdateReadOutcome taxonomy (mirrors GvSmsClient.SendAsync exactly)
Api/
  GvBridgeReadDtos.cs            -- (extend) add MarkReadRequest + ReadStateChangedDto
```

### Modified files

```
Api/GvBridgeReadDtos.cs                          -- add MarkReadRequest, ReadStateChangedDto (Task 1)
Api/GvVoicemailController.cs                      -- add [HttpPost("{id}/read")] (Task 6)
Api/GvSmsController.cs                            -- add [HttpPost("threads/{threadId}/read")] (Task 7)
Models/GVBridgeConfig.cs                          -- add EnableMarkRead (default FALSE) + AllowMarkUnread (default FALSE) (Task 5)
Services/IGvMessageEventSource.cs                 -- add OnReadStateChanged event + IGvReadStateSink producer seam (Task 4)
Services/GvThreadPoller.cs                        -- implement OnReadStateChanged + IGvReadStateSink.NotifyReadStateChanged (Task 4)
Extensions/GVBridgeServiceExtensions.cs           -- register IUpdateReadPayloadBuilder, GvReadStateClient, IGvReadStateSink (Task 8)
src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs -- forward a new ReadStateChanged broadcast (Task 4)
src/RotaryPhoneController.Server/appsettings.json -- EnableMarkRead:false + AllowMarkUnread:false (Task 5)
```

### New test files

```
src/RotaryPhoneController.GVBridge.Tests/
  Clients/UpdateReadPayloadBuilderTests.cs
  Clients/GvReadStateClientTests.cs
  Api/GvVoicemailControllerMarkReadTests.cs
  Api/GvSmsControllerMarkReadTests.cs
```

> **Auth-gate coverage (Task 10)** is asserted in a Server-side test
> (`src/RotaryPhoneController.Server.Tests/`, the project PR5 created) — see Task 10.

---

## Task 1: Mark-read DTOs — extend the shared file

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs`

> The two route response bodies are the **already-shipped frozen DTOs** `VoicemailItemDto` /
> `SmsThreadDto` (verified present in this file lines 7–39) — we add ONLY the request body and the push
> payload. **Do not redefine the response DTOs.**

- [ ] **Step 1: Append the mark-read request + push DTOs** to `GvBridgeReadDtos.cs`:

```csharp
/// <summary>
/// Cross-service mark-read request (ADR §4, contract §2). Body for BOTH mark routes
/// (POST /api/gvbridge/voicemail/{id}/read and POST /api/gvbridge/sms/threads/{threadId}/read).
/// IsRead: true = mark read (the v1 contract). false = best-effort mark-UNREAD — honored only when the
/// server's AllowMarkUnread flag is on (default off); otherwise the route returns 400 unread_unsupported
/// (ADR §6.1). RadioConsole sends only IsRead:true until we confirm unread support.
/// </summary>
public record MarkReadRequest(bool IsRead);

/// <summary>
/// Unified read-state change event (ADR §5, reply §4) — broadcast over RotaryHub as "ReadStateChanged",
/// camelCase on the wire. Fired when read-state changes from ANY source:
///   • path (a) — a mark route was called (THIS PR), and
///   • path (b) — the poller detected an externally-originated read flip (FAST-FOLLOW, Task 9).
/// RadioConsole de-dupes by (Id/ThreadId + IsRead); a client's own mark and the echoed event are
/// idempotent on its side, so we broadcast UNCONDITIONALLY (do not suppress the originator).
///
/// • Kind: "Voicemail" | "Sms" (treat unknown defensively on the consumer).
/// • Id: voicemail id when Kind=Voicemail; null/empty for an Sms thread-level change.
/// • ThreadId: thread id when Kind=Sms (required); the voicemail's threadId when Kind=Voicemail.
/// • IsRead: the new read-state; for an Sms thread-level change this is "thread fully read" (!hasUnread).
/// • ChangedAtUtc: ISO-8601 UTC, when the change was observed/applied.
/// </summary>
public record ReadStateChangedDto(
    string Kind,
    string? Id,
    string? ThreadId,
    bool IsRead,
    DateTime ChangedAtUtc);
```

- [ ] **Step 2: Build** — `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj` → Build succeeded.
- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs
git commit -m "feat(gv): add mark-read DTOs (MarkReadRequest, ReadStateChangedDto) — contract §2/§5"
```

---

## Task 2: `IUpdateReadPayloadBuilder` (the UNVERIFIED seam) — TDD (ADR §7)

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/UpdateReadPayloadBuilderTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/IUpdateReadPayloadBuilder.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/UpdateReadPayloadBuilder.cs`

- [ ] **Step 1: Write failing tests** — assert the seam produces a well-formed JSON array carrying the id
  and the read bool. The EXACT positions are UNVERIFIED, so tests assert the **shape contract** (array,
  contains the id, contains the bool) the §11 capture will pin, NOT magic positions — exactly how
  `SmsThreadIdResolverTests` asserts the `t.+<E164>` form it can verify, not Google's internals.

```csharp
using System.Text.Json;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class UpdateReadPayloadBuilderTests
{
    private readonly IUpdateReadPayloadBuilder _builder = new UpdateReadPayloadBuilder();

    [Fact]
    public void BuildVoicemail_ProducesArray_CarryingIdAndReadBool()
    {
        var payload = _builder.BuildVoicemail(messageId: "vm.1", threadId: "t.+19195551234", isRead: true);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var json = doc.RootElement.GetRawText();
        Assert.Contains("vm.1", json);                  // the id is in the payload
        Assert.Contains("true", json);                  // the read bool is in the payload
    }

    [Fact]
    public void BuildSmsThread_PerThreadMark_ProducesAtLeastOnePayload_CarryingThreadIdAndReadBool()
    {
        // Per-thread grain (ADR §4.2 Q4): a thread mark covers every message in the thread. If GV's native
        // grain is per-message (UNVERIFIED — §11 step 8) this yields one payload per id; if thread-level,
        // one. Either way each payload carries the thread id + the read bool.
        var payloads = _builder.BuildSmsThread(
            threadId: "t.abc", messageIds: new[] { "m.1", "m.2" }, isRead: true);

        Assert.NotEmpty(payloads);
        foreach (var p in payloads)
        {
            using var doc = JsonDocument.Parse(p);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Contains("t.abc", doc.RootElement.GetRawText());
            Assert.Contains("true", doc.RootElement.GetRawText());
        }
    }

    [Fact]
    public void BuildSmsThread_NoMessageIds_StillProducesThreadLevelPayload()
    {
        // A thread with no enumerated message ids still produces one thread-level updateread payload
        // (the default impl's thread-level fallback), so the route can mark an empty/preview-only thread.
        var payloads = _builder.BuildSmsThread("t.abc", messageIds: Array.Empty<string>(), isRead: true);
        Assert.Single(payloads);
        Assert.Contains("t.abc", JsonDocument.Parse(payloads[0]).RootElement.GetRawText());
    }
}
```

- [ ] **Step 2: Run → FAIL** (`IUpdateReadPayloadBuilder` does not exist)
  `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~UpdateReadPayloadBuilderTests" -v n`

- [ ] **Step 3: Implement the seam + default**

`src/RotaryPhoneController.GVBridge/Clients/IUpdateReadPayloadBuilder.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Serializes the GV api2thread/updateread payload (ADR §7). The resource name, the payload POSITIONS
/// (which array slot carries the id vs the read bool), and the per-thread-vs-per-message GRAIN are all
/// UNVERIFIED — pending ADR §11 step 8 live capture. Isolating them here means the live-capture correction
/// is a ONE-FILE change, mirroring IGvThreadParser / GvThreadFolder.ToWireValue() / ISmsThreadIdResolver.
/// The contract boundary RadioConsole sees does NOT depend on anything in here.
/// </summary>
public interface IUpdateReadPayloadBuilder
{
    /// <summary>Build the updateread payload for a single voicemail node.</summary>
    string BuildVoicemail(string messageId, string threadId, bool isRead);

    /// <summary>
    /// Build the updateread payload(s) for a PER-THREAD SMS mark (ADR §4.2 Q4). Returns one payload per
    /// message id if GV's grain is per-message (the working assumption), or a single thread-level payload
    /// (also the fallback when messageIds is empty). The caller POSTs each returned payload.
    /// </summary>
    IReadOnlyList<string> BuildSmsThread(string threadId, IReadOnlyList<string> messageIds, bool isRead);
}
```

`src/RotaryPhoneController.GVBridge/Clients/UpdateReadPayloadBuilder.cs`:

```csharp
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Default updateread payload builder. The array shape below is the WORKING ASSUMPTION
/// ([id, isRead]-positional via GvProtobuf.BuildArray) — **UNVERIFIED**, pending ADR §11 step 8 (send a
/// test updateread against a known voicemail + SMS thread, capture the exact resource name + payload
/// positions + per-thread vs per-message grain + whether the response echoes the node). If live capture
/// reveals different positions/grain, fix it HERE only — GvReadStateClient and the routes do not change.
/// </summary>
public class UpdateReadPayloadBuilder : IUpdateReadPayloadBuilder
{
    public string BuildVoicemail(string messageId, string threadId, bool isRead)
        // UNVERIFIED positions — ADR §11 step 8. GvProtobuf.BuildArray emits a JSON array; bool is boxed
        // and written as a JSON string by BuildArray's default arm today, so we pass the literal token via
        // a JsonElement to keep it a real JSON bool (matches the §4.1 sendsms payload discipline).
        => GvProtobuf.BuildArray(messageId, threadId, isRead);

    public IReadOnlyList<string> BuildSmsThread(
        string threadId, IReadOnlyList<string> messageIds, bool isRead)
    {
        // Per-message is the working assumption (UNVERIFIED — §11 step 8): one updateread per message id.
        // Empty list → a single thread-level payload so an empty/preview-only thread can still be marked.
        if (messageIds.Count == 0)
            return new[] { GvProtobuf.BuildArray(threadId, isRead) };

        var payloads = new List<string>(messageIds.Count);
        foreach (var id in messageIds)
            payloads.Add(GvProtobuf.BuildArray(id, threadId, isRead));
        return payloads;
    }
}
```

> **Implementer note on the bool serialization:** `GvProtobuf.BuildArray` (verified) has cases for
> `null/string/int/long/JsonElement` and a `default` arm that calls `val.ToString()` — so a `bool`
> passed as `object?` would be written as the JSON STRING `"True"`, not a JSON bool. Two acceptable
> fixes (implementer's call, keep it in this seam only):
> (1) extend `GvProtobuf.BuildArray`'s switch with a `case bool b: writer.WriteBooleanValue(b);` arm
>     (preferred — one line, benefits any future caller), **or**
> (2) build the bool token in this file via `JsonSerializer.SerializeToElement(isRead)` and pass the
>     resulting `JsonElement` (which `BuildArray` already handles).
> The tests above assert the literal `"true"` appears in the payload — pick whichever makes that pass.
> Either way the **positional layout** stays UNVERIFIED behind this seam.

- [ ] **Step 4: Run → PASS** (3 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/IUpdateReadPayloadBuilder.cs \
        src/RotaryPhoneController.GVBridge/Clients/UpdateReadPayloadBuilder.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/UpdateReadPayloadBuilderTests.cs
git commit -m "feat(gv): add updateread payload seam (UNVERIFIED, ADR §7/§11 step 8)"
```

---

## Task 3: `GvReadStateClient.MarkReadAsync` (the GV write) — TDD (ADR §3, §7)

> Mirror `GvSmsClient.SendAsync` (verified) EXACTLY: dual constructor (test-facing explicit `HttpClient`
> + DI-facing `IGvAuthenticatedClientProvider`), per-call live-client resolution, classified outcome,
> never throws. New client (not a method on `GvSmsClient`) because updateread spans BOTH voicemail and SMS
> — it is its own concern. It composes `IUpdateReadPayloadBuilder` for the wire format.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvReadStateClientTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/GvReadStateClient.cs`

- [ ] **Step 1: Write failing tests** (hermetic — explicit `HttpClient` overload, capturing handler,
  copied from the `GvSmsClientSendTests` pattern):

```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvReadStateClientTests
{
    private static GvReadStateClient NewClient(HttpClient http) =>
        new(new UpdateReadPayloadBuilder(), NullLogger<GvReadStateClient>.Instance);

    [Fact]
    public async Task MarkVoicemail_PostsUpdateread_AndReturnsAppliedOn200()
    {
        string? capturedUrl = null;
        var http = new HttpClient(new CapturingHandler((req, _) =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var client = NewClient(http);

        var result = await client.MarkVoicemailReadAsync(http, "vm.1", "t.+19195551234", isRead: true);

        Assert.Equal(GvUpdateReadOutcome.Applied, result.Outcome);
        Assert.Null(result.Error);
        Assert.Contains("api2thread/updateread", capturedUrl);
        Assert.Contains("alt=protojson", capturedUrl);
    }

    [Fact]
    public async Task MarkSmsThread_PostsOnePerMessageId_AppliedOnAll200()
    {
        var posts = 0;
        var http = new HttpClient(new CapturingHandler((_, _) =>
        {
            posts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var client = NewClient(http);

        var result = await client.MarkSmsThreadReadAsync(
            http, "t.abc", new[] { "m.1", "m.2" }, isRead: true);

        Assert.Equal(GvUpdateReadOutcome.Applied, result.Outcome);
        Assert.Equal(2, posts);                         // one updateread per message id (per-thread grain)
    }

    [Fact]
    public async Task Mark_NonSuccess_ClassifiesUpstreamError_NoThrow()
    {
        var http = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("x") }));
        var client = NewClient(http);

        var result = await client.MarkVoicemailReadAsync(http, "vm.1", "t.1", isRead: true);

        Assert.Equal(GvUpdateReadOutcome.UpstreamError, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task MarkSmsThread_FirstPostFails_StopsAndClassifiesUpstreamError()
    {
        // Honest status: if any message in the thread fails, the thread mark did NOT fully apply → fail
        // (RadioConsole reconciles on the next list/poll). Do not claim "applied" on a partial mark.
        var posts = 0;
        var http = new HttpClient(new CapturingHandler((_, _) =>
        {
            posts++;
            return new HttpResponseMessage(
                posts == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }));
        var client = NewClient(http);

        var result = await client.MarkSmsThreadReadAsync(http, "t.abc", new[] { "m.1", "m.2" }, true);

        Assert.Equal(GvUpdateReadOutcome.UpstreamError, result.Outcome);
        Assert.Equal(1, posts);                         // short-circuits on the first failure
    }

    [Fact]
    public async Task Mark_NullClient_ClassifiesAdapterUnavailable_NoThrow()
    {
        var client = NewClient(new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK))));
        var result = await client.MarkVoicemailReadAsync(
            authenticatedClient: null, "vm.1", "t.1", isRead: true);
        Assert.Equal(GvUpdateReadOutcome.AdapterUnavailable, result.Outcome);
        Assert.NotNull(result.Error);
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return handler(request, body);
        }
    }
}
```

- [ ] **Step 2: Run → FAIL** (`GvReadStateClient` does not exist)

- [ ] **Step 3: Implement `GvReadStateClient`** — copy the `GvSmsClient.SendAsync` structure verbatim
  (verified: dual constructor, `_baseUrl`/`_apiKey` defaults overwritten by the provider ctor, per-call
  resolution, `IsSuccessStatusCode` → applied, exception arms → never throw):

`src/RotaryPhoneController.GVBridge/Clients/GvReadStateClient.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Classified outcome of a GV updateread call (ADR §4.3, §7). The CLIENT only knows what it observed at
/// the GV boundary; the CONTROLLER owns the HTTP mapping (all non-Applied outcomes collapse to 502 on the
/// wire per the reply's single-502 table — the distinction here is for logging). Applied is true ONLY for
/// <see cref="Applied"/>.
/// </summary>
public enum GvUpdateReadOutcome
{
    Applied,            // HTTP 200 from GV — the mark was accepted (NOT a guess)
    AdapterUnavailable, // no authenticated client (cookie decay / recovery window) → 502
    UpstreamError,      // GV returned a non-200 (incl. a partial thread mark) → 502
    Timeout             // timeout / network exception, no response observed → 502
}

/// <summary>
/// Result of a GV updateread call. Outcome==Applied ONLY when Google returned HTTP 200 — an HONEST mark,
/// never a false success (ADR §3.2 honesty constraint; cf. the 603 incident). Error is populated on any
/// failure. Callers MUST NOT auto-retry on failure (ADR §8) — return 502, let RadioConsole reconcile.
/// </summary>
public record GvUpdateReadResult(GvUpdateReadOutcome Outcome, string? Error)
{
    public static GvUpdateReadResult Ok() => new(GvUpdateReadOutcome.Applied, null);
    public static GvUpdateReadResult Fail(GvUpdateReadOutcome outcome, string error) => new(outcome, error);
}

/// <summary>
/// GV read-state write client (ADR §3 write-through). POSTs api2thread/updateread over the shared
/// authenticated HttpClient (same IGvAuthenticatedClientProvider seam GvSmsClient.SendAsync rides — cookie
/// rotation + recovery ladder for free, ADR §1.3, §7). The wire format is delegated to
/// IUpdateReadPayloadBuilder so the UNVERIFIED positions/grain live in exactly one place. Never throws.
/// </summary>
public class GvReadStateClient
{
    private readonly IUpdateReadPayloadBuilder _payloadBuilder;
    private readonly ILogger<GvReadStateClient> _logger;

    private readonly IGvAuthenticatedClientProvider? _provider;
    // Defaults keep the explicit-client overloads usable from the read-only test constructor (tests assert
    // on the path substring, not the host). The provider constructor overwrites both from the live adapter.
    private string _baseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private string _apiKey = "";

    /// <summary>Test-facing constructor (no provider — use the explicit-client overloads).</summary>
    public GvReadStateClient(IUpdateReadPayloadBuilder payloadBuilder, ILogger<GvReadStateClient> logger)
    {
        _payloadBuilder = payloadBuilder;
        _logger = logger;
    }

    /// <summary>DI-facing constructor: adds the auth provider so production resolves the live client.</summary>
    public GvReadStateClient(IUpdateReadPayloadBuilder payloadBuilder,
        IGvAuthenticatedClientProvider provider, ILogger<GvReadStateClient> logger)
        : this(payloadBuilder, logger)
    {
        _provider = provider;
        _baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        _apiKey = provider.ApiKey;
    }

    // ----- Voicemail -----

    /// <summary>Production: resolve the live client per call, mark one voicemail read/unread.</summary>
    public Task<GvUpdateReadResult> MarkVoicemailReadAsync(
        string messageId, string threadId, bool isRead, CancellationToken ct = default)
        => MarkVoicemailReadAsync(_provider?.GetAuthenticatedClient(), messageId, threadId, isRead, ct);

    /// <summary>Core (test-facing explicit client). Applied only on HTTP 200; never throws.</summary>
    public Task<GvUpdateReadResult> MarkVoicemailReadAsync(
        HttpClient? authenticatedClient, string messageId, string threadId, bool isRead,
        CancellationToken ct = default)
        => PostOneAsync(authenticatedClient,
            _payloadBuilder.BuildVoicemail(messageId, threadId, isRead), ct);

    // ----- SMS thread (per-thread grain — ADR §4.2 Q4) -----

    /// <summary>Production: resolve the live client per call, mark a whole SMS thread read/unread.</summary>
    public Task<GvUpdateReadResult> MarkSmsThreadReadAsync(
        string threadId, IReadOnlyList<string> messageIds, bool isRead, CancellationToken ct = default)
        => MarkSmsThreadReadAsync(_provider?.GetAuthenticatedClient(), threadId, messageIds, isRead, ct);

    /// <summary>
    /// Core (test-facing explicit client). POSTs one updateread per payload from the builder (per-message
    /// grain = one per message id; thread-level = one). Applied ONLY if EVERY post returns 200 — a partial
    /// thread mark is an honest failure (no false "applied"), and short-circuits on the first failure.
    /// </summary>
    public async Task<GvUpdateReadResult> MarkSmsThreadReadAsync(
        HttpClient? authenticatedClient, string threadId, IReadOnlyList<string> messageIds, bool isRead,
        CancellationToken ct = default)
    {
        var payloads = _payloadBuilder.BuildSmsThread(threadId, messageIds, isRead);
        foreach (var payload in payloads)
        {
            var result = await PostOneAsync(authenticatedClient, payload, ct);
            if (result.Outcome != GvUpdateReadOutcome.Applied)
                return result;   // honest: partial mark = failure, RadioConsole reconciles on next list
        }
        return GvUpdateReadResult.Ok();
    }

    // ----- shared POST (mirrors GvSmsClient.SendAsync's body exactly) -----

    private async Task<GvUpdateReadResult> PostOneAsync(
        HttpClient? authenticatedClient, string payload, CancellationToken ct)
    {
        if (authenticatedClient is null)
        {
            _logger.LogWarning("updateread skipped — authenticated client unavailable");
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.AdapterUnavailable,
                "GV adapter unavailable (no authenticated client)");
        }
        try
        {
            var url = $"{_baseUrl}/api2thread/updateread?alt=protojson&key={_apiKey}";
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await authenticatedClient.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("updateread applied");
                return GvUpdateReadResult.Ok();   // 200 = GV accepted the mark (honest)
            }
            _logger.LogWarning("updateread returned {Status}", response.StatusCode);
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.UpstreamError,
                $"Google returned {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException
                                      or HttpRequestException)
        {
            _logger.LogWarning(ex, "updateread timed out / network error");
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.Timeout,
                "updateread request timed out (no response)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "updateread failed");
            return GvUpdateReadResult.Fail(GvUpdateReadOutcome.UpstreamError,
                "updateread request failed (exception)");
        }
    }
}
```

- [ ] **Step 4: Run → PASS** (5 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvReadStateClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvReadStateClientTests.cs
git commit -m "feat(gv): GvReadStateClient.MarkReadAsync (api2thread/updateread) — applied != guess, classified outcomes"
```

---

## Task 4: `ReadStateChanged` event seam + push bridge (path a) — TDD (ADR §5)

> Mirror the PR4 `OnSmsSent` / `IGvOutboundSmsSink` pattern EXACTLY (verified in
> `IGvMessageEventSource.cs` + `GvThreadPoller.cs` + `GvMessagePushBridge.cs`): a consumer event on
> `IGvMessageEventSource`, a narrow producer seam the controllers inject, both implemented by
> `GvThreadPoller`, forwarded to `RotaryHub` by `GvMessagePushBridge`. GVBridge has **no SignalR
> dependency** — the controller MUST NOT touch `IHubContext`; it raises through the seam. (Verified:
> `GvSmsController` reaches the hub only via `IGvOutboundSmsSink.NotifySent`.)

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Services/GvThreadPoller.cs`
- Modify: `src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs`
- (test) extend `src/RotaryPhoneController.GVBridge.Tests/Services/GvThreadPollerTests.cs` with a sink-raise test

- [ ] **Step 1: Add the consumer event** to `IGvMessageEventSource` (after `OnSmsSent`, line 24):

```csharp
    /// <summary>
    /// Raised when read-state changes from ANY source. Path (a): a mark route was called (THIS PR). Path
    /// (b): the poller detected an externally-originated read flip (FAST-FOLLOW, Task 9). Forwarded to
    /// RotaryHub as "ReadStateChanged". Broadcast unconditionally — RadioConsole de-dupes by (Id/ThreadId
    /// + IsRead).
    /// </summary>
    event Action<ReadStateChangedDto>? OnReadStateChanged;
```

- [ ] **Step 2: Add the narrow producer seam** below `IGvOutboundSmsSink` (line 37), so the controllers
  raise the event without depending on the raise side (identical rationale to `IGvOutboundSmsSink`):

```csharp
/// <summary>
/// Narrow producer seam for the read-state-change channel (ADR §5 path a). The mark-read controllers call
/// NotifyReadStateChanged after a successful mark so the event reaches RadioConsole. Kept separate from the
/// consumer-only IGvMessageEventSource so the controllers do not depend on the raise side. Implemented by
/// GvThreadPoller (which owns the events); registered as both.
/// </summary>
public interface IGvReadStateSink
{
    /// <summary>Surface a read-state change to the OnReadStateChanged channel.</summary>
    void NotifyReadStateChanged(ReadStateChangedDto dto);
}
```

- [ ] **Step 3: Implement on `GvThreadPoller`** — it already implements `IGvMessageEventSource` +
  `IGvOutboundSmsSink` (verified line 17). Add `IGvReadStateSink` to the class declaration, the event, and
  the raise method (next to `OnSmsSent` / `RaiseSmsSent` / `NotifySent`, lines 33–39):

```csharp
// class declaration — add IGvReadStateSink:
public class GvThreadPoller : BackgroundService, IGvMessageEventSource, IGvOutboundSmsSink, IGvReadStateSink
```

```csharp
    public event Action<ReadStateChangedDto>? OnReadStateChanged;

    /// <summary>IGvReadStateSink — narrow producer seam the mark-read controllers inject (path a).</summary>
    public void NotifyReadStateChanged(ReadStateChangedDto dto) => OnReadStateChanged?.Invoke(dto);
```

> NOTE: this task wires the seam and the on-mark (path a) raise ONLY. The poller's `PollSmsAsync` /
> `PollVoicemailAsync` are NOT modified here — detecting externally-originated flips is **Task 9 (path b,
> fast-follow)**. The event member existing now lets path b attach later with no consumer change.

- [ ] **Step 4: Forward to `RotaryHub`** in `GvMessagePushBridge` (verified pattern — subscribe in
  `StartAsync`, unsubscribe in `StopAsync`, broadcast via `FireAndLog`). Add alongside the existing three:

```csharp
// in StartAsync (after OnSmsSent += ...):
        _eventSource.OnReadStateChanged += BroadcastReadStateChanged;
// in StopAsync (after OnSmsSent -= ...):
        _eventSource.OnReadStateChanged -= BroadcastReadStateChanged;
```

```csharp
    private void BroadcastReadStateChanged(GVBridge.Api.ReadStateChangedDto dto)
    {
        _logger.LogInformation("Broadcasting ReadStateChanged {Kind} {Id}/{Thread} isRead={IsRead}",
            dto.Kind, dto.Id, dto.ThreadId, dto.IsRead);
        FireAndLog(_hubContext.Clients.All.SendAsync("ReadStateChanged", dto),
            "ReadStateChanged", dto.Id ?? dto.ThreadId ?? "");
    }
```

- [ ] **Step 5: Add a poller sink test** to `GvThreadPollerTests.cs` (the event fires when the sink is
  called — the controllers' use is covered in Tasks 6/7):

```csharp
    [Fact]
    public void NotifyReadStateChanged_RaisesOnReadStateChanged()
    {
        var poller = NewPoller();                       // existing test helper in this file
        ReadStateChangedDto? captured = null;
        poller.OnReadStateChanged += d => captured = d;

        ((IGvReadStateSink)poller).NotifyReadStateChanged(
            new ReadStateChangedDto("Voicemail", "vm.1", "t.1", true, DateTime.UtcNow));

        Assert.NotNull(captured);
        Assert.Equal("vm.1", captured!.Id);
        Assert.True(captured.IsRead);
    }
```

> If `GvThreadPollerTests` has no `NewPoller()` helper, construct inline with the same fakes the existing
> tests use (verify by reading the file first — do not invent a constructor signature).

- [ ] **Step 6: Build all three projects + run** the poller tests → PASS.
- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs \
        src/RotaryPhoneController.GVBridge/Services/GvThreadPoller.cs \
        src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs \
        src/RotaryPhoneController.GVBridge.Tests/Services/GvThreadPollerTests.cs
git commit -m "feat(gv): add ReadStateChanged event + IGvReadStateSink seam, forward to RotaryHub (path a, ADR §5)"
```

---

## Task 5: `EnableMarkRead` + `AllowMarkUnread` config flags (default FALSE) — (ADR §8, §6.1)

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`

- [ ] **Step 1: Add the flags** to `GVBridgeConfig` (next to `EnableSmsSend`, line 60 — mirror its
  comment discipline):

```csharp
    // MARK-READ FEATURE FLAG (ADR §8) — DEFAULT FALSE. The GV account-write path (api2thread/updateread)
    // ships DARK: when false, POST /api/gvbridge/voicemail/{id}/read and POST /api/gvbridge/sms/threads/
    // {threadId}/read perform NO GV call and return 409 markread_disabled. This lets the irreversible-write
    // code merge safely; the owner flips this to true to go live (after the ADR §11 step 8 live capture).
    public bool EnableMarkRead { get; set; } = false;

    // MARK-UNREAD support (ADR §6.1) — DEFAULT FALSE. v1 honors isRead:true only. isRead:false (mark
    // unread) is best-effort and UNVERIFIED until the §11 step 8 capture confirms GV updateread accepts an
    // unread transition. While false, a mark route with isRead:false returns 400 unread_unsupported (no GV
    // call) — we never promise a toggle the backend cannot honor. Owner flips to true once unread is confirmed.
    public bool AllowMarkUnread { get; set; } = false;
```

- [ ] **Step 2: Add to `appsettings.json`** under the existing `"GVBridge"` section (verify the exact
  key path by reading the file first; mirror how `EnableSmsSend` is keyed):

```jsonc
    // under "GVBridge":
    "EnableMarkRead": false,
    "AllowMarkUnread": false,
```

- [ ] **Step 3: Build** → succeeded. **Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs \
        src/RotaryPhoneController.Server/appsettings.json
git commit -m "feat(gv): add EnableMarkRead + AllowMarkUnread flags (default FALSE) — ships dark (ADR §8)"
```

---

## Task 6: `GvVoicemailController` POST `{id}/read` — TDD (ADR §4.1, §4.3)

> Add ONE route to the existing `GvVoicemailController` (verified: composes `GvVoicemailClient` +
> `GvVoicemailCache`; has `FindNodeAsync` (list+filter by `MessageId`) + `ToDto`). Inject the new
> `GvReadStateClient`, `IGvReadStateSink`, and `IOptions<GVBridgeConfig>`. Reuse `FindNodeAsync` for the
> 404 check AND to build the response DTO (the same cheap list+filter the read routes already do —
> ADR §4.4). The test seam mirrors `GvSmsController.SetSendClientForTest`.

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs`
- Create: `src/RotaryPhoneController.GVBridge.Tests/Api/GvVoicemailControllerMarkReadTests.cs`

- [ ] **Step 1: Write failing tests** (extend the `GvVoicemailControllerTests` scaffolding — same
  `MockHandler` + `GvThreadClient(http, BaseUrl, ApiKey, parser, ...)` + `VmListResponse()` fixture
  verified in that file; add the new dependencies):

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

public class GvVoicemailControllerMarkReadTests : IDisposable
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vmmr-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    // The list fixture (one voicemail vm.1, isRead=false). Identical shape to GvVoicemailControllerTests.
    private static HttpResponseMessage VmList() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {"threads":[["t.+19195551234",["+19195551234","Alice"],1718841600000,true,
          [["vm.1","t.+19195551234","+19195551234","Alice",1718841600000,23,false,"call me","media-1"]]]],
         "nextPageToken":null}
        """)
    };

    private (GvVoicemailController c, List<ReadStateChangedDto> events) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler,
        bool enableMarkRead = true, bool allowUnread = false)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var fetcher = new StubFetcher();
        var vmClient = new GvVoicemailClient(threadClient, parser, fetcher, NullLogger<GvVoicemailClient>.Instance);
        var cache = new GvVoicemailCache(fetcher,
            Options.Create(new GVBridgeConfig { VoicemailCacheDir = _dir }),
            NullLogger<GvVoicemailCache>.Instance);
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var sink = new TestReadSink(events);
        var config = Options.Create(new GVBridgeConfig
        {
            EnableMarkRead = enableMarkRead, AllowMarkUnread = allowUnread
        });
        var controller = new GvVoicemailController(vmClient, cache, readStateClient, sink, config,
            NullLogger<GvVoicemailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.SetReadStateClientForTest(http);   // test seam: explicit authenticated client
        return (controller, events);
    }

    [Fact]
    public async Task MarkRead_FlagOff_Returns409_markread_disabled_NoGvCall()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmList();
        }, enableMarkRead: false);
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, obj.StatusCode);
        Assert.Equal(0, posts);                          // NO GV call when dark
        Assert.Empty(events);
    }

    [Fact]
    public async Task MarkRead_AppliesAndReturnsUpdatedDto_WithIsReadTrue_AndBroadcasts()
    {
        var (c, events) = NewController(_ => VmList());
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailItemDto>(ok.Value);
        Assert.Equal("vm.1", dto.Id);
        Assert.True(dto.IsRead);                          // authoritative: reflects the applied mark
        Assert.Single(events);                            // path-a ReadStateChanged fired
        Assert.Equal("Voicemail", events[0].Kind);
        Assert.Equal("vm.1", events[0].Id);
        Assert.True(events[0].IsRead);
    }

    [Fact]
    public async Task MarkRead_UnknownId_Returns404_NoGvCall()
    {
        var posts = 0;
        var (c, _) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmList();
        });
        var result = await c.MarkRead("does-not-exist", new MarkReadRequest(true), default);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, posts);                          // 404 before any write
    }

    [Fact]
    public async Task MarkRead_Unread_WhenDisallowed_Returns400_unread_unsupported_NoGvCall()
    {
        var posts = 0;
        var (c, _) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return VmList();
        }, allowUnread: false);
        var result = await c.MarkRead("vm.1", new MarkReadRequest(false), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task MarkRead_UpstreamFailure_Returns502_NoBroadcast()
    {
        // List succeeds (so the node is found) but the updateread POST fails → 502, no false success.
        var (c, events) = NewController(req =>
            req.RequestUri!.ToString().Contains("updateread")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : VmList());
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Empty(events);                            // no broadcast on failure (honest)
    }

    private sealed class TestReadSink(List<ReadStateChangedDto> captured) : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) => captured.Add(dto);
    }
    private sealed class StubFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, new byte[] { 1 }, "audio/mpeg"));
    }
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 2: Run → FAIL** (no `MarkRead` action, constructor mismatch)

- [ ] **Step 3: Implement** — extend `GvVoicemailController`. Add the three constructor deps + the test
  seam + the action. Keep the existing constructor params first (verified order):

```csharp
    // add fields:
    private readonly GvReadStateClient _readStateClient;
    private readonly IGvReadStateSink _readStateSink;
    private readonly GVBridgeConfig _config;
    private HttpClient? _testReadStateClient;   // test-only; null in production

    // replace the constructor signature (add the three params; keep voicemailClient/cache/logger):
    public GvVoicemailController(GvVoicemailClient voicemailClient, GvVoicemailCache cache,
        GvReadStateClient readStateClient, IGvReadStateSink readStateSink,
        Microsoft.Extensions.Options.IOptions<GVBridgeConfig> config,
        ILogger<GvVoicemailController> logger)
    {
        _voicemailClient = voicemailClient;
        _cache = cache;
        _readStateClient = readStateClient;
        _readStateSink = readStateSink;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>Test seam: inject the HttpClient used as the authenticated client for the updateread write.</summary>
    internal void SetReadStateClientForTest(HttpClient client) => _testReadStateClient = client;
```

```csharp
    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkRead(
        string id, [FromBody] MarkReadRequest request, CancellationToken ct = default)
    {
        // 0. FEATURE FLAG (ADR §8) — DEFAULT FALSE → 409 markread_disabled, NO GV call. Checked FIRST.
        if (!_config.EnableMarkRead)
        {
            _logger.LogInformation("Mark-read rejected — EnableMarkRead is false (dark)");
            return StatusCode(409, new { error = "markread_disabled" });
        }

        // 1. Mark-unread gate (ADR §6.1). v1 honors isRead:true; isRead:false only when AllowMarkUnread.
        if (!request.IsRead && !_config.AllowMarkUnread)
            return BadRequest(new { error = "unread_unsupported" });

        // 2. Find the node (also needed to build the response DTO — same list+filter the read routes do).
        var node = await FindNodeAsync(id, ct);
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });

        // 3. Idempotent no-op (ADR §4.3): already in the target state → 200 with the true DTO, no GV call.
        if ((node.IsRead ?? false) == request.IsRead)
            return Ok(ToDto(node));

        // 4. Write through to GV (honest status — 200 means GV accepted, ADR §3.2). No auto-retry (§8).
        var write = _testReadStateClient is not null
            ? await _readStateClient.MarkVoicemailReadAsync(
                _testReadStateClient, node.MessageId ?? id, node.ThreadId ?? "", request.IsRead, ct)
            : await _readStateClient.MarkVoicemailReadAsync(
                node.MessageId ?? id, node.ThreadId ?? "", request.IsRead, ct);
        if (write.Outcome != GvUpdateReadOutcome.Applied)
            return StatusCode(502, new { error = write.Error ?? "Failed to update read-state in Google" });

        // 5. Re-read so the response DTO reflects GV's truth (ADR §4.4). Fall back to the optimistic node
        //    if the re-read can't find it (rare race) — but with the applied IsRead.
        var fresh = await FindNodeAsync(id, ct) ?? node;
        var dto = ToDto(fresh) with { IsRead = request.IsRead };

        // 6. Broadcast path-a ReadStateChanged (ADR §5). Unconditional; RadioConsole de-dupes.
        _readStateSink.NotifyReadStateChanged(new ReadStateChangedDto(
            Kind: "Voicemail", Id: dto.Id, ThreadId: dto.ThreadId,
            IsRead: request.IsRead, ChangedAtUtc: DateTime.UtcNow));

        return Ok(dto);
    }
```

> The `with { IsRead = request.IsRead }` guards the case where GV's list hasn't yet re-reflected the flip
> on the immediate re-read (eventual consistency) — the response stays authoritative-to-the-applied-mark
> without inventing any other field. Add the `using RotaryPhoneController.GVBridge.Models;` import.

- [ ] **Step 4: Run → PASS** (5 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs \
        src/RotaryPhoneController.GVBridge.Tests/Api/GvVoicemailControllerMarkReadTests.cs
git commit -m "feat(gv): POST /api/gvbridge/voicemail/{id}/read — updateread write-through, dark flag, path-a event"
```

---

## Task 7: `GvSmsController` POST `threads/{threadId}/read` — TDD (ADR §4.2, §4.3)

> Add ONE route to the existing `GvSmsController` (verified: composes `GvSmsClient`, `SmsSendRateLimiter`,
> `ISmsThreadIdResolver`, `IGvOutboundSmsSink`, `IOptions<GVBridgeConfig>`). Inject `GvReadStateClient` +
> `IGvReadStateSink`. **Per-thread grain (ADR §4.2 Q4):** resolve the thread's messages via
> `GvSmsClient.ListMessagesAsync(threadId)` (verified: filters the SMS folder by `ThreadId`), pass their
> `MessageId`s to `MarkSmsThreadReadAsync`, and build the response `SmsThreadDto` (`hasUnread=false`) from
> `ListThreadsAsync` filtered to `threadId` — the same list+filter `GetThreads` already does.

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs`
- Create: `src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerMarkReadTests.cs`

- [ ] **Step 1: Write failing tests** (the SMS-folder list fixture must contain BOTH a thread row AND its
  message rows so `ListThreadsAsync` and `ListMessagesAsync` both return data for `t.abc`; mirror the
  `VmListResponse` shape used by the voicemail tests + `GvSmsControllerTests` read fixtures — read those
  files first to copy the exact positional JSON the `PositionalGvThreadParser` expects):

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GvSmsControllerMarkReadTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";

    // SMS-folder list fixture: one thread t.abc (hasUnread=true) + its messages. COPY the exact positional
    // shape from GvSmsControllerTests' read fixtures so PositionalGvThreadParser parses it — do NOT guess.
    private static HttpResponseMessage SmsList() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""<<< copy the real SMS-list fixture JSON from GvSmsControllerTests >>>""")
    };

    private (GvSmsController c, List<ReadStateChangedDto> events) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler,
        bool enableMarkRead = true, bool allowUnread = false)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var limiter = new SmsSendRateLimiter(5, TimeSpan.FromSeconds(10));
        var resolver = new SmsThreadIdResolver();
        var outboundSink = new NoopOutboundSink();
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var readSink = new TestReadSink(events);
        var config = Options.Create(new GVBridgeConfig
        {
            EnableMarkRead = enableMarkRead, AllowMarkUnread = allowUnread, EnableSmsSend = false
        });
        var controller = new GvSmsController(smsClient, limiter, resolver, outboundSink,
            readStateClient, readSink, config, NullLogger<GvSmsController>.Instance);
        controller.SetReadStateClientForTest(http);
        return (controller, events);
    }

    [Fact]
    public async Task MarkThreadRead_FlagOff_Returns409_markread_disabled_NoGvCall()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return SmsList();
        }, enableMarkRead: false);
        var result = await c.MarkThreadRead("t.abc", new MarkReadRequest(true), default);
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, posts);
        Assert.Empty(events);
    }

    [Fact]
    public async Task MarkThreadRead_AppliesAndReturnsThreadDto_HasUnreadFalse_AndBroadcasts()
    {
        var (c, events) = NewController(_ => SmsList());
        var result = await c.MarkThreadRead("t.abc", new MarkReadRequest(true), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<SmsThreadDto>(ok.Value);
        Assert.Equal("t.abc", dto.ThreadId);
        Assert.False(dto.HasUnread);                      // authoritative: thread fully read
        Assert.Single(events);
        Assert.Equal("Sms", events[0].Kind);
        Assert.Equal("t.abc", events[0].ThreadId);
        Assert.True(events[0].IsRead);                    // "thread fully read" == !hasUnread
    }

    [Fact]
    public async Task MarkThreadRead_UnknownThread_Returns404_NoGvCall()
    {
        var posts = 0;
        var (c, _) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return SmsList();
        });
        var result = await c.MarkThreadRead("t.nonexistent", new MarkReadRequest(true), default);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task MarkThreadRead_Unread_WhenDisallowed_Returns400_unread_unsupported()
    {
        var (c, _) = NewController(_ => SmsList(), allowUnread: false);
        var result = await c.MarkThreadRead("t.abc", new MarkReadRequest(false), default);
        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task MarkThreadRead_UpstreamFailure_Returns502_NoBroadcast()
    {
        var (c, events) = NewController(req =>
            req.RequestUri!.ToString().Contains("updateread")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : SmsList());
        var result = await c.MarkThreadRead("t.abc", new MarkReadRequest(true), default);
        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Empty(events);
    }

    private sealed class TestReadSink(List<ReadStateChangedDto> captured) : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) => captured.Add(dto);
    }
    private sealed class NoopOutboundSink : IGvOutboundSmsSink
    {
        public void NotifySent(SmsMessageDto dto) { }
    }
    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

> **Implementer:** replace the `SmsList()` placeholder with the REAL SMS-folder list fixture from
> `GvSmsControllerTests.cs` (read it first) so `PositionalGvThreadParser` yields a thread `t.abc`
> (hasUnread=true) and at least one message on that thread. The placeholder is the ONLY non-literal in
> this plan and is explicitly flagged — do not ship it as-is.

- [ ] **Step 2: Run → FAIL**

- [ ] **Step 3: Implement** — extend `GvSmsController`. Add `GvReadStateClient` + `IGvReadStateSink` +
  the test seam to the constructor (keep existing params + order), and the action:

```csharp
    // add fields:
    private readonly GvReadStateClient _readStateClient;
    private readonly IGvReadStateSink _readStateSink;
    private HttpClient? _testReadStateClient;   // test-only

    // extend the constructor signature (append the two new deps before logger):
    public GvSmsController(GvSmsClient smsClient, SmsSendRateLimiter rateLimiter,
        ISmsThreadIdResolver threadIdResolver, IGvOutboundSmsSink outboundSink,
        GvReadStateClient readStateClient, IGvReadStateSink readStateSink,
        IOptions<GVBridgeConfig> config, ILogger<GvSmsController> logger)
    {
        _smsClient = smsClient;
        _rateLimiter = rateLimiter;
        _threadIdResolver = threadIdResolver;
        _outboundSink = outboundSink;
        _readStateClient = readStateClient;
        _readStateSink = readStateSink;
        _config = config.Value;
        _logger = logger;
    }

    internal void SetReadStateClientForTest(HttpClient client) => _testReadStateClient = client;
```

```csharp
    [HttpPost("threads/{threadId}/read")]
    public async Task<IActionResult> MarkThreadRead(
        string threadId, [FromBody] MarkReadRequest request, CancellationToken ct = default)
    {
        // 0. FEATURE FLAG (ADR §8) — checked FIRST → 409 markread_disabled, NO GV call.
        if (!_config.EnableMarkRead)
        {
            _logger.LogInformation("Mark-thread-read rejected — EnableMarkRead is false (dark)");
            return StatusCode(409, new { error = "markread_disabled" });
        }

        // 1. Mark-unread gate (ADR §6.1).
        if (!request.IsRead && !_config.AllowMarkUnread)
            return BadRequest(new { error = "unread_unsupported" });

        // 2. Resolve the thread summary (404 if unknown) — same list+filter GetThreads does.
        var threadsResult = await _smsClient.ListThreadsAsync(count: 100, pageToken: null, ct);
        if (!threadsResult.Succeeded)
            return StatusCode(502, new { error = "Failed to fetch SMS threads from Google" });
        var thread = threadsResult.Threads.FirstOrDefault(t => t.ThreadId == threadId);
        if (thread is null) return NotFound(new { error = $"SMS thread {threadId} not found" });

        // 3. Idempotent no-op (ADR §4.3): thread already fully read → 200, no GV call. isRead:true means
        //    hasUnread=false; if already !hasUnread, we are already in the target state.
        var alreadyInTargetState = (thread.HasUnread ?? false) != request.IsRead;
        if (alreadyInTargetState)
            return Ok(ToThreadDto(thread));

        // 4. Per-thread grain (ADR §4.2 Q4): mark every message in the thread. Resolve the message ids.
        var messagesResult = await _smsClient.ListMessagesAsync(threadId, count: 200, ct);
        var messageIds = messagesResult.Succeeded
            ? messagesResult.Messages.Where(m => m.MessageId is not null).Select(m => m.MessageId!).ToList()
            : new List<string>();

        // 5. Write through to GV (honest — 200 means accepted; partial = failure). No auto-retry (§8).
        var write = _testReadStateClient is not null
            ? await _readStateClient.MarkSmsThreadReadAsync(_testReadStateClient, threadId, messageIds, request.IsRead, ct)
            : await _readStateClient.MarkSmsThreadReadAsync(threadId, messageIds, request.IsRead, ct);
        if (write.Outcome != GvUpdateReadOutcome.Applied)
            return StatusCode(502, new { error = write.Error ?? "Failed to update read-state in Google" });

        // 6. Build the authoritative thread DTO: hasUnread = !isRead (a mark-read clears unread).
        var dto = ToThreadDto(thread) with { HasUnread = !request.IsRead };

        // 7. Broadcast path-a ReadStateChanged (ADR §5). For SMS, IsRead = "thread fully read" (!hasUnread).
        _readStateSink.NotifyReadStateChanged(new ReadStateChangedDto(
            Kind: "Sms", Id: null, ThreadId: threadId,
            IsRead: request.IsRead, ChangedAtUtc: DateTime.UtcNow));

        return Ok(dto);
    }

    // Map a parsed thread node to the public SmsThreadDto (same projection GetThreads uses inline).
    private static SmsThreadDto ToThreadDto(GvThreadNode t) => new(
        ThreadId: t.ThreadId ?? "",
        CounterpartyNumber: t.CounterpartyNumber ?? "",
        CounterpartyName: t.CounterpartyName,
        LastMessageAt: t.LastMessageEpochMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UnixEpoch,
        HasUnread: t.HasUnread ?? false,
        LastMessagePreview: t.LastMessagePreview);
```

> **Implementer:** the existing `GetThreads` builds `SmsThreadDto` inline (verified lines 54–61). Extract
> that projection into the `ToThreadDto(GvThreadNode)` helper above and have `GetThreads` call it too, so
> the mark route and the read route share one mapping (no drift). Add
> `using RotaryPhoneController.GVBridge.Clients;` if `GvThreadNode` is not already visible (it is — same
> namespace as the other clients the controller imports).

- [ ] **Step 4: Run → PASS** (5 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs \
        src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerMarkReadTests.cs
git commit -m "feat(gv): POST /api/gvbridge/sms/threads/{threadId}/read — per-thread updateread, dark flag, path-a event"
```

---

## Task 8: DI registration — wire the new client + seams (ADR §1.3 activation-order)

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`

> CRITICAL (verified ADR §1.3 note in this file lines 33–38): factories must NOT resolve a live
> `HttpClient` at container-build time. Register `GvReadStateClient` via the **provider-backed
> constructor** (mirrors how `GvSmsClient` is registered, lines 64–68) so it resolves the live client
> PER CALL and the app starts even with the adapter inactive. Register `IGvReadStateSink` to the existing
> `GvThreadPoller` singleton (mirrors `IGvOutboundSmsSink`, line 75).

- [ ] **Step 1: Register the seam + client + sink** (after the `SmsSendRateLimiter` registration, line 81):

```csharp
        // Mark-read / durable read-state (mark-read FF — ships dark behind EnableMarkRead). Provider-backed
        // GvReadStateClient so MarkReadAsync resolves the live authenticated client per call (cookie
        // rotation + recovery ladder, ADR §1.3, §7). UNVERIFIED updateread wire format isolated in the
        // IUpdateReadPayloadBuilder seam (ADR §7, §11 step 8).
        services.AddSingleton<IUpdateReadPayloadBuilder, UpdateReadPayloadBuilder>();
        services.AddSingleton<GvReadStateClient>(sp => new GvReadStateClient(
            sp.GetRequiredService<IUpdateReadPayloadBuilder>(),
            sp.GetRequiredService<IGvAuthenticatedClientProvider>(),
            sp.GetRequiredService<ILogger<GvReadStateClient>>()));
        services.AddSingleton<IGvReadStateSink>(sp => sp.GetRequiredService<GvThreadPoller>());
```

- [ ] **Step 2: Add a DI smoke assertion** to the existing `GVBridgeReadClientDiTests.cs` (verify the
  container resolves `GvReadStateClient` + both controllers without a live adapter — mirror the existing
  read-client DI tests in that file):

```csharp
    [Fact]
    public void Container_ResolvesGvReadStateClient_AndMarkReadControllers_WithoutLiveAdapter()
    {
        var sp = BuildProvider();                        // existing helper in this test file
        Assert.NotNull(sp.GetRequiredService<GvReadStateClient>());
        Assert.NotNull(sp.GetRequiredService<IUpdateReadPayloadBuilder>());
        Assert.NotNull(sp.GetRequiredService<IGvReadStateSink>());
    }
```

> If `GVBridgeReadClientDiTests` has no `BuildProvider()` helper, follow the construction pattern the
> existing tests in that file use (read it first).

- [ ] **Step 3: Build + run** the DI tests → PASS.
- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs \
        src/RotaryPhoneController.GVBridge.Tests/Extensions/GVBridgeReadClientDiTests.cs
git commit -m "feat(gv): register GvReadStateClient + updateread seam + read-state sink (provider-backed, ADR §1.3)"
```

---

## Task 9 (PATH B — FAST-FOLLOW, NOT BUILT IN THIS PR): poller-detected external read-flip

> **🚧 This task is SCOPED here for completeness but is a SEPARATE PR.** Do NOT implement it in the path-a
> PR. It is the heavier half the ADR (§5.2) and the reply (§4) explicitly deferred. The path-a routes ship
> and are useful (cross-client sync #2, durability #1) without it; this lights up "hear on phone → kiosk
> badge clears LIVE" (#3 live). Until it ships, that case still works on the next poll-driven list refresh
> (ADR §5.2) — just not as an instant push.

**Why it is a separate unit (grounded in the as-built poller — verified):** `GvThreadPoller` today diffs
**only on the per-thread high-water mark** (`GvHighWaterMark.IsNewMessage` — new id/timestamp; verified
`PollSmsAsync`/`PollVoicemailAsync` raise events only for items NEW past the mark). It does **not** track
the **read-flag of already-seen items**, so detecting a read/unread **flip** on an item already past the
high-water mark requires **NEW state** the poller does not have today.

**Scope of the fast-follow (for the future Planner/Builder cycle):**
1. Add a per-item last-seen `IsRead` map (keyed by `(threadId, messageId)` for SMS; `messageId` for
   voicemail) to `GvThreadPoller`, populated at SEED time (so a cold start does NOT replay every item as a
   "flip" — same seed-gating discipline the high-water mark uses, verified lines 92–100).
2. On each poll, after the new-item diff, run a SECOND diff pass over the full list: for each already-seen
   item whose `IsRead` differs from the stored value, raise `OnReadStateChanged` (via the SAME
   `IGvReadStateSink`/event added in Task 4 — no consumer change) with `Kind`, `Id`/`ThreadId`, the new
   `IsRead`, and `ChangedAtUtc = DateTime.UtcNow`. For SMS, collapse per-message flips to a per-thread
   `hasUnread` change (the grain RadioConsole consumes).
3. Update the stored map after each diff. Add restart/seed semantics tests (cold start replays nothing;
   a flip after seed raises exactly one event; a re-seen unchanged item raises nothing).
4. Honesty: this is GV's flag as truth (ADR §3.1) — the poller already reads `IsRead`/`HasUnread` 1:1, so
   no new truth source, just new diff state.

**Deliverable:** a separate plan `docs/superpowers/plans/2026-06-20-gv-markread-poller-flip.md` (the future
Planner writes it) + its own PR, still under the same `EnableMarkRead` posture if the owner wants the push
gated. **No code for this task in the path-a PR.**

---

## Task 10: Auth-gate coverage test (ADR §6.2, Q8) — confirm PR5 gate covers the new routes

> The ADR (§6.2) and reply (§5) promise: **no special auth posture — the PR5 prefix gate auto-covers**
> the new `/api/gvbridge/*` routes when `InterServiceAuthKey` is set. This task PROVES that with a test so
> a future change to the middleware can't silently un-gate the write routes. Verified: `GvBridgeAuthMiddleware`
> gates `path.StartsWith("/api/gvbridge", ...)` and exempts only the exact `/api/gvbridge/event` segment —
> both new routes (`.../voicemail/{id}/read`, `.../sms/threads/{threadId}/read`) are under the prefix and
> are NOT the event segment, so they ARE gated. The PR5 test project is `RotaryPhoneController.Server.Tests`.

**Files:**
- Modify: a middleware test in `src/RotaryPhoneController.Server.Tests/` (the PR5 `GvBridgeAuthMiddleware`
  test — read it first to match its harness)

- [ ] **Step 1: Add cases** asserting the two mark-read paths are gated when a key is set (401 without the
  header, pass with it) and NOT exempted like `/event`:

```csharp
    [Theory]
    [InlineData("/api/gvbridge/voicemail/vm.1/read")]
    [InlineData("/api/gvbridge/sms/threads/t.abc/read")]
    public async Task MarkReadRoutes_AreGated_WhenKeySet_NoHeader_Returns401(string path)
    {
        // Reuse the existing PR5 middleware-test harness (validator with a key set, no X-RotaryPhone-Auth).
        var ctx = await InvokeMiddleware(path, header: null, keyConfigured: true);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/gvbridge/voicemail/vm.1/read")]
    [InlineData("/api/gvbridge/sms/threads/t.abc/read")]
    public async Task MarkReadRoutes_PassGate_WithValidHeader(string path)
    {
        var ctx = await InvokeMiddleware(path, header: "the-key", keyConfigured: true);
        Assert.NotEqual(401, ctx.Response.StatusCode);   // gate let it through to next()
    }
```

> `InvokeMiddleware(...)` is illustrative — use whatever harness the existing PR5 middleware test exposes
> (read `src/RotaryPhoneController.Server.Tests/` first; do not invent a helper). The point is two literal
> route paths asserted gated-when-keyed and pass-with-header.

- [ ] **Step 2: Run → PASS**
- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.Server.Tests/
git commit -m "test(gv): assert PR5 auth gate covers the new mark-read routes (ADR §6.2 Q8)"
```

---

## Task 11: Full suite + boundary doc + final commit

- [ ] **Step 1: Run the FULL test suites** (both projects):

```bash
dotnet test src/RotaryPhoneController.GVBridge.Tests
dotnet test src/RotaryPhoneController.Server.Tests
```

All green. (GVBridge was 185 tests after PR4; this adds ~18 across the new files.)

- [ ] **Step 2: Boundary doc — already updated by the Architect at ratification.** Verify (do NOT
  re-add): `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` Integration Points already list both mark
  routes + the `ReadStateChanged` event (confirmed present). If the build changed any route/shape detail
  vs the table, reconcile + add a Change Log entry. **No BT/audio change in this PR** — boundary-doc edit
  is API-table only, so the `RADIO-CONSOLE-BT-AUDIO-BOUNDARY` protocol's BT-adapter rules do not apply.

- [ ] **Step 3: Arc tracker** — already flipped to "plan queued & build-ready (build HELD)" by the
  planning PR (see "Output & process"). Verify the row points at this plan path.

- [ ] **Step 4: Final verification** before opening the implementation PR (do NOT auto-merge — OWNER-HOLD):
  - `EnableMarkRead` defaults FALSE → both routes return `409 markread_disabled` with NO GV call (assert).
  - `AllowMarkUnread` defaults FALSE → `isRead:false` returns `400 unread_unsupported` with NO GV call.
  - No production code path makes a live GV call under the default config (grep the new client is only
    reached after the flag check).
  - PR body Honesty note (REQUIRED): *"Fixture-verified only; `EnableMarkRead` defaults off; first real
    `updateread` pending the ADR §11 step 8 on-box live capture. The `updateread` wire format
    (positions/grain/unread support) is UNVERIFIED, isolated behind `IUpdateReadPayloadBuilder`. Path b
    (poller-detected external read-flip) is a separate fast-follow (Task 9), not in this PR."*

---

## Self-review (Planner — completed before queueing)

- **Spec coverage:** every ratified item is in a task — write-through client (T3) behind UNVERIFIED seam
  (T2); two routes returning the frozen DTOs (T6/T7); 200/404/502/409-disabled/400-unread status table
  (T6/T7, matches the reply VERBATIM); per-thread SMS grain (T7 + T3 `MarkSmsThreadReadAsync`);
  `ReadStateChanged` path-a (T4 seam + T6/T7 raise); `EnableMarkRead` default-false (T5); mark-unread
  best-effort gate (T5 `AllowMarkUnread` + T6/T7 400); auth auto-covered, proven by test (T10); fixtures,
  no browser UAT (T11). Path b explicitly carved out as a non-built fast-follow (T9).
- **Placeholder scan:** one intentional, flagged placeholder — the `SmsList()` fixture JSON in T7 (must be
  copied from the real `GvSmsControllerTests` fixture; flagged twice). Every other code block is literal
  and references verified as-built types (`VoicemailItemDto`, `SmsThreadDto`, `GvThreadNode`, `GvSmsNode`,
  `GvVoicemailNode`, `GvSmsClient`, `GvVoicemailClient`, `GvThreadClient`, `IGvAuthenticatedClientProvider`,
  `IGvMessageEventSource`, `IGvOutboundSmsSink`, `GvMessagePushBridge`, `GvProtobuf.BuildArray`,
  `SmsSendRateLimiter`, `ISmsThreadIdResolver`, `GVBridgeConfig`, `GvBridgeAuthMiddleware`).
- **Type consistency:** response bodies are the EXACT frozen DTOs (no new response shape); the new
  `MarkReadRequest`/`ReadStateChangedDto` match the reply's camelCase payloads field-for-field; the client
  taxonomy mirrors `GvSmsClient`'s `GvSendOutcome` shape; status codes match the reply table exactly.
- **Honesty discipline:** 200 only on a verified GV 200 (or idempotent already-in-state); 502 never masked
  as 200; no auto-retry; partial thread mark = failure; first real mark gated on §11 step 8. Consistent
  with the 603-incident memory.
