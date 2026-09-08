# Cross-repo batch: XR-2 (verify) + XR-6 (voicemail blackout 404) — plan

**Branch:** `fix/gv-voicemail-blackout-404` — cut fresh from `main`
**Origin:** Radio Console handoff, two items. `XR-2` (`%2F` thread ids) filed
2026-07-31 in `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md` §B1;
`XR-6` (`GetAudio` 404s during an auth blackout) never filed into this repo — request text lives
in `D:/prj/RTest/RTest/design/plans/PHN-1c-event-playback-service-and-route.md` §5 item 2.
**Baseline verified against:** `main` @ `738141f` (*Merge pull request #72 from
mmackelprang/fix/gv-auth-blackout*).

---

## 0. Headline — the batch is one item, not two

**XR-2 is already fixed on `main`.** Commit `3103662` (*fix(gv): decode %2F-encoded thread ids so
group/MMS threads are readable*, 2026-07-31 22:18) implements the decode in **both** routes, adds
the per-thread sanity check the handoff asked for, and ships **13 regression tests** — including
the exact `g.Group Message.d5Mri/NrDUQgXNXNQehOfw` id, keyed on the slash, covering
`MarkThreadRead` as well as `GetThreadMessages`. There is **no code to write for XR-2**. Task 1
is a verification pass only.

**XR-6 is genuinely open** and is the whole of the implementation work here.

⚠ **Why this was not obvious, and the trap to avoid.** The working tree is on branch
`diag/gv-srtp-receive`, which **predates `3103662` and PR #72**. Reading
`src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs` in the working tree shows the *old,
unfixed* controller — no `DecodeThreadId`, no `WarnIfNoMessages` — and
`src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs` shows no `ShapeIsSane`. `git diff main`
on those paths is **8 insertions, 108 deletions**: the branch is behind, not ahead. Every claim in
this plan was verified with `git show main:<path>`, not by reading the working tree. **Builder must
branch from `main` before reading anything.**

---

## 1. Investigation verification

Radio Console read our source rather than our docs, which was right of them. Their citations are
five days old (XR-6) and five weeks old (XR-2). Re-verified against `main` @ `738141f`.

| Claim (theirs) | Status | Current location on `main` |
|---|---|---|
| `GetList` guards its list result and returns 502 | **Confirmed** | `GvVoicemailController.cs:45-46` (as cited) |
| …under a comment saying why | **Confirmed** | `GvVoicemailController.cs:43-44` (as cited) |
| `FindNodeAsync` calls the same `ListVoicemailsAsync` and does not guard it | **Confirmed** | `GvVoicemailController.cs:123-127`; call at `:125`, unguarded `FirstOrDefault` at `:126` |
| `GetAudio` answers 404 "has no recording" | **Confirmed** | `GvVoicemailController.cs:63-65` — **cited as `:65-66` (queue) and `:64-65` (punch list); both drifted by one** |
| A failed authenticated list returns an empty item set | **Confirmed** | `GvVoicemailClient.cs:36` → `GvVoicemailListResult.Empty(succeeded: false)`; record at `GvVoicemailClient.cs:5-9` |
| Kestrel leaves `%2F` encoded while `%20` decodes | **Confirmed** | Now documented in-tree at `GvSmsController.cs:236-255` |
| `GvSmsClient.ListMessagesAsync` exact-string-compares | **Confirmed** | `GvSmsClient.cs:99` — `all.Where(m => m.ThreadId == threadId)` |
| A raw `/` falls through to the SPA fallback | **Confirmed** | `src/RotaryPhoneController.Server/Program.cs:405` — `app.MapFallbackToFile("index.html")` |
| `ShapeIsSane` and `Succeeded` both pass, so the guards cannot catch a filter that matched nothing | **Confirmed** | `ShapeIsSane` at `GvSmsClient.cs:127-139`; see §2 |

### Where Radio Console's description is wrong or stale

Five corrections. None of them changes what needs building; three change how it should be
described back to them.

1. **XR-2 is fixed — their evidence predates the fix by seven hours.** Their reproduction is
   timestamped 15:06 and 01:06Z (evening) on 2026-07-31; `3103662` landed 22:18 EDT that night.
   Both `GetThreadMessages` (`GvSmsController.cs:67-90`) and `MarkThreadRead`
   (`GvSmsController.cs:170-234`) now bind the route value as `rawThreadId` and decode once at the
   top via `DecodeThreadId` (`:254-255`).

2. **"Mark-read on a group thread is silently a no-op today" was never accurate.** On the
   pre-fix code, `MarkThreadRead`'s thread lookup (`GvSmsController.cs:196` on `main`, the
   equivalent line pre-fix) exact-compares and misses, so the route returned **404 "SMS thread …
   not found"** — a visible failure, not a silent one. Our own in-tree comment
   (`GvSmsController.cs:175-178`) states this correctly: *"Undecoded, mark-read on a group thread
   404s (the lookup misses) or silently marks nothing."* Worth saying back to them, because it
   means their client saw a 404 it may have been mapping as permanent.

3. **XR-3 / B2 is also already shipped**, in PR #72 (merge `738141f`). It is not pending. It added
   recover-and-retry on 401/403 at the shared read path (`GvThreadClient.cs:116-127`), a real
   proactive PSIDTS refresh, and health derived from the last real data-plane call. This is
   **background only — plan none of it here**, but it invalidates the next point.

4. **XR-6's severity framing is stale.** The request says a guest is told a voicemail is
   permanently gone during "a blackout window that is roughly 9 minutes in every 20" (the punch
   list says ~45% of the time). That was measured *before* PR #72. `ListRawAsync` now recovers and
   replays once on a 401/403, so the blackout window is materially narrower. **XR-6 is still a real
   correctness bug** — when recovery itself fails, `ListRawAsync` still returns `null`
   (`GvThreadClient.cs:127`) → `Empty(succeeded: false)` → `FindNodeAsync` returns null → 404 — but
   it is now a *rare* lie rather than a 45%-of-the-time lie. Do not repeat the 45% figure.

5. **It is not a three-line change, and `GetAudio` is not the only caller.** `FindNodeAsync` has
   **four call sites**: `GetItem` (`:55`), `GetAudio` (`:63`), `MarkRead` step 2 (`:92`), and
   `MarkRead` step 5's re-read (`:110`). Propagating `Succeeded` out of the helper forces a
   decision at every one of them — you cannot propagate it and ignore it. `GetItem` and `MarkRead`
   step 2 inherit exactly the same defect and are fixed here; **step 5 must deliberately keep
   ignoring it**, and Task 5 explains why. Their request text does not make that scope call.

### One thing we should tell them about ourselves

**The fix may not be on the box.** `/mnt/d/prj/rp-deploy` — which Radio Console believes is the
authoritative deployed tree — is an **orphaned git worktree** of this repo. Its `.git` file reads
`gitdir: D:/prj/RotaryPhone/.git/worktrees/rp-deploy`, and that directory **does not exist**, so no
git command works there. Its files are frozen: `GvSmsController.cs` is dated **Jul 29 15:02** and
contains **no `DecodeThreadId`**, while `GvSmsClient.cs` is dated **Jul 31 10:42** and does contain
the `4d58d19` parser fix. So the tree was last synced between 10:08 and 22:18 on Jul 31 — it has
the parser fix and **not** the decode fix.

That is consistent with Radio Console still observing XR-2 after it was fixed. **Confirming what is
actually running on `radio` is a prerequisite for believing any XR-2 retest** (Task 1.3). This plan
does not change deployment; it flags the gap.

---

## 2. Why the honest-status guards cannot catch XR-2's failure mode

Carried forward because the handoff asks us to confirm we understood it, and because the same
reasoning is the entire argument for XR-6.

We have two guards on the read path, and **both pass** during the `%2F` bug:

- **`Succeeded`** (`GvSmsClient.cs:40-44`) means *the HTTP call returned and the JSON parsed*. During
  the bug the fetch genuinely succeeded and the parse genuinely succeeded. `Succeeded: true`.
- **`ShapeIsSane`** (`GvSmsClient.cs:127-139`) compares parsed message count against the raw thread
  count, to catch positional-index drift. During the bug **149 messages parsed fine** from ~20 raw
  threads. `ShapeIsSane: true`.

Neither guard looks at the **filter**. `all.Where(m => m.ThreadId == threadId)`
(`GvSmsClient.cs:99`) is applied *after* both checks, and matching zero rows is not an error state
in either one's vocabulary. This is the gap those guards leave open **by construction**: they
validate the *fetch* and the *shape*, never the *selection*.

That is what `WarnIfNoMessages` (`GvSmsController.cs:257-272`) exists to cover — one Warning line
when a thread that fetched and parsed successfully yields zero messages. It deliberately does not
throw, and it is per user action rather than per poll, because journald churn on this box
correlates with audio distortion.

**XR-6 is the same shape one layer over.** `result.Items.FirstOrDefault(...)` returning null
conflates *"this voicemail is not in the list"* with *"we could not read the list"*. The remedy is
identical in kind: carry the flag that already knows the difference to the place that decides the
status code.

---

## 3. Task list

Nine tasks. Tasks 0–1 are verification; 2–5 are the fix; 6 is tests; 7–8 close it out.

---

### Task 0 — branch from `main`

The working tree is on `diag/gv-srtp-receive` with an **unrelated uncommitted change** to
`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` that is being handled separately. **Do not
commit it, do not stash it into this branch, do not revert it.**

```bash
cd /mnt/d/prj/rotaryphone
git fetch origin
git switch -c fix/gv-voicemail-blackout-404 origin/main
```

**Verify before proceeding** — all three must hold, or stop:

```bash
git log --oneline -1                  # expect 738141f (or later origin/main)
grep -c DecodeThreadId src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs   # expect 3
grep -c ShapeIsSane   src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs    # expect 3
```

If `DecodeThreadId` returns 0 you are on the old branch and every line number below is wrong.

---

### Task 1 — verify XR-2, write no code

**1.1 — confirm the fix is present.** `GvSmsController.cs:74` (`GetThreadMessages`) and `:179`
(`MarkThreadRead`) both call `DecodeThreadId(rawThreadId)`; the helper is at `:254-255`:

```csharp
    private static string DecodeThreadId(string threadId) =>
        threadId.Contains('%') ? Uri.UnescapeDataString(threadId) : threadId;
```

The per-thread sanity check is `WarnIfNoMessages` at `:266-272`, called from `:88` (thread fetch)
and `:216` (mark-read).

**1.2 — run the existing regression suite.**

```bash
dotnet test RotaryPhoneController.sln \
  --filter "FullyQualifiedName~GvSmsControllerThreadIdDecodeTests"
```

Expect **13 passing**. The file is
`src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerThreadIdDecodeTests.cs`; the group id
constants are at `:33-34`:

```csharp
    private const string GroupThreadId   = "g.Group Message.d5Mri/NrDUQgXNXNQehOfw";
    private const string GroupRouteValue = "g.Group Message.d5Mri%2FNrDUQgXNXNQehOfw";
```

Coverage already meets the bar this batch was asked for — keyed on the slash, not on "MMS", with
`MarkThreadRead` covered at `:213`, `:235`, `:247`, `:259`, `:274`. **Add nothing.**

**1.3 — establish what is actually deployed.** This is the open question, not the code. On the box:

```bash
systemctl show -p FragmentPath rotary-phone
journalctl -u rotary-phone --since "10 min ago" | grep -c 'SMS thread .* resolved to 0 messages'
```

`WarnIfNoMessages`'s log line **exists only in the fixed build**. If the deployed binary emits it
at all, the fix is deployed. If a group-thread fetch returns 0 messages and that line is *absent*,
the box is running pre-`3103662` code and XR-2 is a **deployment** problem, not a code one.

⚠ Keep journald reads bounded with `--since` and **do not tail them** — heavy journald reads on
this N100 box correlate with audio distortion.

**Deliverable:** a yes/no on whether the box runs the fix. No source changes in this task.

---

### Task 2 — propagate `Succeeded` out of `FindNodeAsync`

File: `src/RotaryPhoneController.GVBridge/Api/GvVoicemailController.cs`, lines `121-127`.

**Replace:**

```csharp
    // Voicemail is a thread/message subtype — there is no per-id GET on GV; we list and filter.
    // Lists are small (tens of items); a future optimization could cache the last list.
    private async Task<GvVoicemailNode?> FindNodeAsync(string id, CancellationToken ct)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count: 100, pageToken: null, ct);
        return result.Items.FirstOrDefault(v => v.MessageId == id);
    }
```

**With:**

```csharp
    /// <summary>
    /// Resolve one voicemail node by message id. Voicemail is a thread/message subtype — there is no
    /// per-id GET on GV, so we list and filter. Lists are small (tens of items); a future
    /// optimization could cache the last list.
    ///
    /// Returns the LIST's Succeeded flag alongside the node, because a bare null cannot tell the two
    /// failure modes apart. A list that FAILED (auth blackout, GV 5xx, wire-shape drift) yields an
    /// empty item set, so FirstOrDefault returns null for a voicemail that exists — and the caller,
    /// seeing only null, answers 404. RadioConsole reads 404 as "retrying will not help" and tells a
    /// guest the recording is permanently gone. Callers MUST answer 502 on !Succeeded and reserve
    /// 404 for a SUCCESSFUL list that did not contain the id — the same distinction
    /// <see cref="GetList"/> has drawn since it shipped (see the comment above its guard).
    /// </summary>
    private async Task<(bool Succeeded, GvVoicemailNode? Node)> FindNodeAsync(
        string id, CancellationToken ct)
    {
        var result = await _voicemailClient.ListVoicemailsAsync(count: 100, pageToken: null, ct);
        return (result.Succeeded, result.Items.FirstOrDefault(v => v.MessageId == id));
    }
```

**This will not compile until Tasks 3–5 land** — all four call sites break at once. That is
deliberate: the compiler enumerates every site that has to make the decision, so none is missed.

---

### Task 3 — `GetAudio` answers 502 during a blackout

Same file, lines `60-74`. This is the item Radio Console actually asked for.

**Replace lines `60-65`:**

```csharp
    [HttpGet("{id}/audio")]
    public async Task<IActionResult> GetAudio(string id, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(id, ct);
        if (node?.MediaId is null)
            return NotFound(new { error = $"Voicemail {id} has no recording" });
```

**With:**

```csharp
    [HttpGet("{id}/audio")]
    public async Task<IActionResult> GetAudio(string id, CancellationToken ct = default)
    {
        var (listSucceeded, node) = await FindNodeAsync(id, ct);
        // Do not mask an auth/transport failure as "has no recording" — the same rule GetList states
        // above its own guard. RadioConsole maps 404 from this route to "retrying will not help"
        // (GvMediaUnavailableException.IsPermanent), so a 404 here tells a guest a recording that
        // exists is permanently gone. 404 must mean "we looked and it is not there".
        if (!listSucceeded)
            return StatusCode(502, new { error = "Failed to fetch voicemail list from Google" });
        if (node?.MediaId is null)
            return NotFound(new { error = $"Voicemail {id} has no recording" });
```

Leave `:67-73` (the cache fetch, its own 502 at `:69`, and the `PhysicalFileResult`) untouched.

**Out of scope, deliberately:** the surviving 404 still conflates *"no such voicemail"* with
*"found, but no media"*. Splitting that would change the response body Radio Console matches on,
and they have not asked for it. Note it in the reply (Task 7) rather than changing it here.

---

### Task 4 — `GetItem` answers 502 during a blackout

Same file, lines `52-58`. Same defect, same remedy.

**Replace:**

```csharp
    [HttpGet("{id}")]
    public async Task<IActionResult> GetItem(string id, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(id, ct);
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });
        return Ok(ToDto(node));
    }
```

**With:**

```csharp
    [HttpGet("{id}")]
    public async Task<IActionResult> GetItem(string id, CancellationToken ct = default)
    {
        var (listSucceeded, node) = await FindNodeAsync(id, ct);
        if (!listSucceeded)
            return StatusCode(502, new { error = "Failed to fetch voicemail list from Google" });
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });
        return Ok(ToDto(node));
    }
```

---

### Task 5 — `MarkRead`: guard step 2, deliberately **not** step 5

Same file. Two edits, and the second one is a decision to *not* change behaviour.

**5a — step 2, lines `91-93`. Replace:**

```csharp
        // 2. Find the node (also needed to build the response DTO — same list+filter the read routes do).
        var node = await FindNodeAsync(id, ct);
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });
```

**With:**

```csharp
        // 2. Find the node (also needed to build the response DTO — same list+filter the read routes do).
        var (listSucceeded, node) = await FindNodeAsync(id, ct);
        // Same blackout hazard as GetAudio: an unread list yields null and we would 404 a voicemail
        // that exists, before any write is attempted.
        if (!listSucceeded)
            return StatusCode(502, new { error = "Failed to fetch voicemail list from Google" });
        if (node is null) return NotFound(new { error = $"Voicemail {id} not found" });
```

**5b — step 5, lines `108-111`. Replace:**

```csharp
        // 5. Re-read so the response DTO reflects GV's truth (ADR §4.4). Fall back to the optimistic node
        //    if the re-read can't find it (rare race) — but with the applied IsRead.
        var fresh = await FindNodeAsync(id, ct) ?? node;
        var dto = ToDto(fresh) with { IsRead = request.IsRead };
```

**With:**

```csharp
        // 5. Re-read so the response DTO reflects GV's truth (ADR §4.4). Fall back to the optimistic node
        //    if the re-read can't find it (rare race) — but with the applied IsRead.
        //
        //    The re-read's Succeeded flag is DELIBERATELY DISCARDED. Step 4 already wrote successfully
        //    to Google; returning 502 now would tell RadioConsole the mark-read did not happen when it
        //    did, and it would reconcile away a change that is real — a worse lie than a marginally
        //    stale DTO. The `with { IsRead = ... }` below already carries the applied truth, so the
        //    fallback node is correct on the field that matters. This is the one call site where
        //    !Succeeded must NOT become a 502.
        var (_, freshNode) = await FindNodeAsync(id, ct);
        var fresh = freshNode ?? node;
        var dto = ToDto(fresh) with { IsRead = request.IsRead };
```

**Build gate — must be clean before Task 6:**

```bash
dotnet build RotaryPhoneController.sln -warnaserror
```

Four call sites changed; if the compiler still reports a conversion error on
`Task<(bool, GvVoicemailNode?)>`, one was missed.

---

### Task 6 — regression tests

New file:
`src/RotaryPhoneController.GVBridge.Tests/Api/GvVoicemailControllerAuthBlackoutTests.cs`.

Follows the precedent of `GvSmsControllerThreadIdDecodeTests.cs` — a dedicated file for a
cross-repo defect, so the reason the tests exist stays legible. Helpers mirror
`GvVoicemailControllerMarkReadTests.cs:41-70`.

**The load-bearing tests are the pairs.** For each route, one test proves a *failed* list becomes
502 and its twin proves a *successful* list that lacks the id is **still 404**. A fix that turned
every miss into a 502 would satisfy the first half and break the contract; only the pair pins it.

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
using RotaryPhoneController.GVBridge.Tests.Support;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

/// <summary>
/// XR-6 (RadioConsole cross-repo handoff, PHN-1c §5 item 2). GetAudio resolved a recording through
/// FindNodeAsync, which called ListVoicemailsAsync and ignored its Succeeded flag. A failed
/// authenticated list returns Empty(succeeded: false), so FirstOrDefault yielded null and the route
/// answered 404 "has no recording" for a recording that exists. RadioConsole maps 404 from this
/// route to "retrying will not help", so a guest was told a voicemail was permanently gone.
///
/// The distinction under test: 502 means "we could not look", 404 means "we looked and it is not
/// there". Each route is covered by a PAIR — the failure case and the genuine-miss case — because a
/// fix that turned every miss into a 502 would pass the failure half alone.
/// </summary>
public class GvVoicemailControllerAuthBlackoutTests : IDisposable
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vmbo-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    /// <summary>A healthy list containing exactly one voicemail, vm.1 (isRead=0/UNREAD).</summary>
    private static HttpResponseMessage VmList() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(GvWireBuilder.VoicemailResponse(
            threadId: "t.+19195551234", messageId: "vm.1", counterparty: "+19195551234",
            epochMs: 1718841600000, durationSeconds: 23, isRead: 0, transcript: "call me",
            mediaUrl: "https://www.google.com/voice/media/svm/acct/media-1"))
    };

    /// <summary>
    /// What a GV auth blackout looks like at our boundary: api2thread/list answers 401. After PR #72
    /// ListRawAsync recovers and replays once, but this controller-level test has no provider, so
    /// the retry is skipped and the list fails — exactly the state that remains when recovery itself
    /// fails on the box.
    /// </summary>
    private static HttpResponseMessage Blackout() =>
        new(HttpStatusCode.Unauthorized);

    private (GvVoicemailController c, List<ReadStateChangedDto> events) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> listHandler)
    {
        var http = new HttpClient(new MockHandler(listHandler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser,
            NullLogger<GvThreadClient>.Instance);
        var fetcher = new StubFetcher();
        var vmClient = new GvVoicemailClient(threadClient, parser, fetcher,
            NullLogger<GvVoicemailClient>.Instance);
        var config = Options.Create(new GVBridgeConfig
        {
            VoicemailCacheDir = _dir, EnableMarkRead = true, AllowMarkUnread = false
        });
        var cache = new GvVoicemailCache(fetcher, config, NullLogger<GvVoicemailCache>.Instance);
        var readStateClient = new GvReadStateClient(new UpdateReadPayloadBuilder(),
            NullLogger<GvReadStateClient>.Instance);
        var events = new List<ReadStateChangedDto>();
        var controller = new GvVoicemailController(vmClient, cache, readStateClient,
            new TestReadSink(events), config, NullLogger<GvVoicemailController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.SetReadStateClientForTest(http);
        return (controller, events);
    }

    // ---- GetAudio: the route XR-6 names ------------------------------------------------

    [Fact]
    public async Task GetAudio_WhenListFails_Returns502_Not404()
    {
        var (c, _) = NewController(_ => Blackout());
        var result = await c.GetAudio("vm.1", default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetAudio_WhenListSucceedsButIdAbsent_Still404()
    {
        // The twin. A successful list that simply does not contain the id is a genuine miss and
        // must stay 404 — RadioConsole's "retrying will not help" is CORRECT here.
        var (c, _) = NewController(_ => VmList());
        var result = await c.GetAudio("vm.does-not-exist", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ---- GetItem: same helper, same defect ----------------------------------------------

    [Fact]
    public async Task GetItem_WhenListFails_Returns502_Not404()
    {
        var (c, _) = NewController(_ => Blackout());
        var result = await c.GetItem("vm.1", default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetItem_WhenListSucceedsButIdAbsent_Still404()
    {
        var (c, _) = NewController(_ => VmList());
        var result = await c.GetItem("vm.does-not-exist", default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ---- MarkRead step 2 -----------------------------------------------------------------

    [Fact]
    public async Task MarkRead_WhenListFails_Returns502_AndNeverWrites()
    {
        var posts = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread")) posts++;
            return Blackout();
        });
        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Equal(0, posts);      // 502 before any write is attempted
        Assert.Empty(events);        // and no broadcast on a failure
    }

    [Fact]
    public async Task MarkRead_WhenListSucceedsButIdAbsent_Still404()
    {
        var (c, _) = NewController(_ => VmList());
        var result = await c.MarkRead("vm.does-not-exist", new MarkReadRequest(true), default);
        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ---- MarkRead step 5: the deliberate exception ---------------------------------------

    [Fact]
    public async Task MarkRead_WhenReReadFailsAfterSuccessfulWrite_Still200_NotA502()
    {
        // Pins Task 5b. The list succeeds and the updateread POST succeeds; only the step-5 re-read
        // fails. The write really happened, so answering 502 would make RadioConsole reconcile away
        // a change that is real. Must stay 200 with IsRead reflecting the applied value.
        var listCalls = 0;
        var (c, events) = NewController(req =>
        {
            if (req.RequestUri!.ToString().Contains("updateread"))
                return new HttpResponseMessage(HttpStatusCode.OK);
            listCalls++;
            return listCalls >= 2 ? Blackout() : VmList();   // 1st list OK, re-read fails
        });

        var result = await c.MarkRead("vm.1", new MarkReadRequest(true), default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<VoicemailItemDto>(ok.Value);
        Assert.True(dto.IsRead);          // the applied truth survives the failed re-read
        Assert.Equal("vm.1", dto.Id);     // fell back to the optimistic node, not to an empty DTO
        Assert.Single(events);            // the write happened, so the broadcast must fire
    }

    private sealed class StubFetcher : IGvRecordingFetcher
    {
        public Task<GvRecordingFetchResult> FetchAsync(string mediaRef, CancellationToken ct = default)
            => Task.FromResult(new GvRecordingFetchResult(true, new byte[] { 1, 2, 3 }, "audio/mpeg"));
    }

    private sealed class TestReadSink(List<ReadStateChangedDto> sink) : IGvReadStateSink
    {
        public void NotifyReadStateChanged(ReadStateChangedDto dto) => sink.Add(dto);
    }

    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

**Seven new tests.** Run them, then run the two neighbouring suites — both contain cases that must
keep passing unchanged:

```bash
dotnet test RotaryPhoneController.sln \
  --filter "FullyQualifiedName~GvVoicemailController"
```

Specifically these must stay green, because they encode the boundary the fix must not cross:

| Existing test | File:line | Why it matters |
|---|---|---|
| `GetAudio_UnknownId_Returns404` | `GvVoicemailControllerTests.cs:100-106` | A genuine miss stays 404 |
| `GetItem_NotFound_Returns404` | `GvVoicemailControllerTests.cs:82-88` | Same, for `GetItem` |
| `GetList_OnUpstreamFailure_Returns502NotEmpty200` | `GvVoicemailControllerTests.cs:70-80` | `GetList`'s guard is untouched |
| `MarkRead_UnknownId_Returns404_NoGvCall` | `GvVoicemailControllerMarkReadTests.cs:101-113` | Successful list + missing id → 404 |
| `MarkRead_UpstreamFailure_Returns502_NoBroadcast` | `GvVoicemailControllerMarkReadTests.cs:147-159` | The *write* failing is a different 502, still 502 |

⚠ If any test in that last row starts failing, the change has been applied at the wrong layer.

**Deliberately not tested:** "node found but `MediaId` is null → still 404".
`GvWireBuilder.VoicemailResponse` takes a non-nullable `string mediaUrl`
(`GvWireBuilder.cs:107-112`), so constructing that fixture means changing the shared builder — and
the builder is the one place in the test project that encodes the wire layout, changed only when
Google moves a field. `GetAudio_WhenListSucceedsButIdAbsent_Still404` already pins the property
that matters (a successful list never yields a 502).

---

### Task 7 — docs

**7a — known issue.** Add an entry to `docs/KNOWN-ISSUES.md` (which currently has no XR-6 row),
matching the format of the B2 entries added by `28c1014`. Record: the symptom (404 during a
blackout for a recording that exists), the mechanism (`FindNodeAsync` dropped `Succeeded`), the
fix, and that all four call sites were reviewed with step 5 deliberately exempt.

**7b — reply to Radio Console.** Per the boundary doc's *"Passing Work Between Sessions"*
protocol, write `docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md`. This is the
highest-value artifact in the batch, because most of it is news to them. Cover, in order:

1. **XR-2 has been fixed since 2026-07-31 22:18** (`3103662`) — both routes, plus the per-thread
   sanity check they recommended, plus 13 regression tests keyed on the slash. Answers questions 1
   and 3 of the request's *Reply* section.
2. **Their evidence predates the fix by ~7 hours.** Not a criticism — say it plainly so they know
   to retest rather than re-file.
3. **The deployed tree may be stale** (§1, *One thing we should tell them about ourselves*).
   Give them the `WarnIfNoMessages` log line as the probe that distinguishes a deployed fix from an
   undeployed one, and tell them `/mnt/d/prj/rp-deploy` is an orphaned worktree, not authoritative.
4. **Correction: pre-fix mark-read returned 404, not a silent no-op** — relevant if they were
   mapping that 404 as permanent.
5. **XR-3 / B2 also shipped** (PR #72). Point them at
   `docs/handoffs/radioconsole-gv-auth-blackout-reply.md`, and flag the one deliberate deviation:
   we ship `degraded:true` / `authBlackout:true` but **decline `available:false`**, because
   `IsAvailable` gates `GetAuthenticatedClient()` internally and flipping it would make the adapter
   refuse its own recovery retry. **Their banner should bind to `degraded`/`authBlackout`.**
6. **XR-6 fixed here** — and that we widened it to `GetItem` and `MarkRead`, with step 5 exempt and
   why.
7. **Their 45%/9-minutes-in-20 severity figure is stale** post-PR #72. XR-6 is now a rare lie, not
   a frequent one. Worth their re-prioritising.
8. **`GetAudio`'s 404 still conflates "no such voicemail" with "no media"** (Task 3, out of scope).
   Ask whether they want them split; it would change the response body they match on.

---

### Task 8 — quality gates and PR

```bash
dotnet build RotaryPhoneController.sln -warnaserror
dotnet test  RotaryPhoneController.sln
```

Full suite green, not just the filtered runs. Then open the PR against `main`.

**PR body must include a Docs Impact section** listing `docs/KNOWN-ISSUES.md`,
`docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md`, and this plan.

Per the auto-merge policy this may merge on green gates without a further check-in: it is a
backend change with no user-facing surface, so the unit suite plus the Test Plan below stand in
for UAT. **Pause and ask** if Task 1.3 shows the box is running unexpected code — that is a
deployment question the owner should see before anything merges.

---

## 4. Test Plan (for a Tester, against a running service)

The service listens on **`http://<host>:5555`** (`Properties/launchSettings.json:8`). Radio Console's
transcripts say `localhost:5004`; that is `HT801RtpPort` (`appsettings.json:69`), a different thing.
**Confirm the real port before starting** — `systemctl show -p ExecStart rotary-phone`, or the
`Now listening on:` line in the journal.

⚠ **Bound every journald read with `--since` and never tail.** Heavy journald reads on this N100
box correlate with audio distortion.

### Part A — XR-2 regression (verification only; no code changed in this batch)

**A0 — window awareness.** Post-PR #72 the blackout is much narrower, but check anyway before
trusting a negative: `curl -s http://<host>:5555/api/gvbridge/status` and confirm
`degraded:false` / `authBlackout:false`. If degraded, wait for recovery — otherwise an XR-2
failure and an XR-6 failure are indistinguishable at the UI.

**The predicate under test is "the thread id contains `/`" — not "the thread is MMS."** Group
threads are `g.Group Message.<base64url>` and the base64url alphabet includes `/`; group threads
merely happen to be the MMS threads. Every case below is chosen on the slash.

**A1 — get the real ids.**

```bash
curl -s 'http://<host>:5555/api/gvbridge/sms/threads?count=20' | jq -r '.threads[].threadId'
```

Pick one id **containing a `/`** (expected shape `g.Group Message.d5Mri/NrDUQgXNXNQehOfw`) and one
**without** (`t.32665`). If no id contains a slash, the account has no group threads and A2–A5
cannot be run — say so rather than reporting a pass.

**A2 — `GetThreadMessages`, slash id, client escaping.** Use exactly what Radio Console emits
(`Uri.EscapeDataString` → `%20` for the space, `%2F` for the slash):

```bash
curl -s -o /dev/null -w '%{http_code} ' \
  'http://<host>:5555/api/gvbridge/sms/threads/g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw'
curl -s 'http://<host>:5555/api/gvbridge/sms/threads/g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw' \
  | jq '.messages | length'
```

**PASS:** `200` and a message count **> 0**.
**FAIL (the original bug):** `200` with `0`. That is the exact silent-empty signature.

**A3 — control, no slash.** Same call against `t.32665`. **PASS:** `200`, count > 0. This
separates "the fix regressed" from "the account is quiet".

**A4 — `MarkThreadRead` on the same slash id.** The route the request flagged as also broken:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  -H 'Content-Type: application/json' -d '{"isRead":true}' \
  'http://<host>:5555/api/gvbridge/sms/threads/g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw/read'
```

**PASS:** `200`. **FAIL:** `404` — the pre-fix behaviour (the thread lookup missed).
`409 markread_disabled` means `EnableMarkRead` is false; enable it or record the case as not run.

**A5 — the sanity check is wired.** Immediately after A2:

```bash
journalctl -u rotary-phone --since "2 min ago" | grep 'resolved to 0 messages'
```

**PASS (healthy):** no output — the thread returned messages, so the guard correctly stayed quiet.
**Diagnostic:** if A2 returned 0 messages **and** this line is present, the fix is deployed and the
thread is genuinely empty or outside the fetched folder window — a different problem.
**Deployment red flag:** if A2 returned 0 messages and this line is **absent**, the box is running
pre-`3103662` code. Report that as a deployment finding, not a code regression.

**A6 — idempotency, both `+` spellings.** The decode must not corrupt ids that already worked:

```bash
curl -s -o /dev/null -w '%{http_code} ' 'http://<host>:5555/api/gvbridge/sms/threads/t.%2B18019208129'
curl -s -o /dev/null -w '%{http_code}\n' 'http://<host>:5555/api/gvbridge/sms/threads/t.+18019208129'
```

**PASS:** both `200`, and both return the same messages. `Uri.UnescapeDataString` is not form
decoding, so a literal `+` must stay a `+` and never become a space.

**A7 — the SPA-fallback trap.** Radio Console found that a raw `/` misses the API route and falls
through to `MapFallbackToFile` (`Program.cs:405`), returning `index.html` with HTTP 200:

```bash
curl -s -w '\n%{http_code} %{content_type}\n' \
  'http://<host>:5555/api/gvbridge/sms/threads/g.Group Message.d5Mri/NrDUQgXNXNQehOfw' | tail -2
```

**EXPECTED (unchanged, and correct):** `200 text/html`. This is *not* a regression — it documents
why the id must stay in the path with `%2F` and must never be sent raw. **This is the case that
disqualifies any future "move it to a query parameter" refactor** that leaves a route shape able to
fall through to `index.html`.

### Part B — XR-6, the fix in this batch

**B1 — the happy path still works.** During a healthy window
(`status` shows `degraded:false`):

```bash
curl -s 'http://<host>:5555/api/gvbridge/voicemail?count=5' | jq -r '.items[].id'
ID=<one id from above>
curl -s -o /dev/null -w '%{http_code} %{content_type}\n' \
  "http://<host>:5555/api/gvbridge/voicemail/$ID/audio"
```

**PASS:** `200 audio/mpeg`. Also confirm `Accept-Ranges: bytes` is present
(`curl -sI`), since the HTML5 scrubber depends on it and `PhysicalFileResult` is untouched by this
change.

**B2 — a genuine miss is still 404.** The regression that matters most:

```bash
curl -s -o /dev/null -w '%{http_code}\n' \
  'http://<host>:5555/api/gvbridge/voicemail/vm.definitely-not-real/audio'
curl -s -o /dev/null -w '%{http_code}\n' \
  'http://<host>:5555/api/gvbridge/voicemail/vm.definitely-not-real'
```

**PASS:** both `404`. **FAIL:** `502` — the fix was applied too broadly and every miss now reads as
a transport failure, which is the opposite lie.

**B3 — a blackout is 502, not 404.** This is the acceptance criterion. The window is narrow after
PR #72, so **induce it rather than wait**. In order of preference:

1. **Preferred — break the credential.** Stop the service, corrupt or move
   `data/gv-cookies.enc`, start it, and call B1's audio URL for a **known-good** id.
2. **Fallback — catch a natural blackout.** Poll `/api/gvbridge/status` every 15s until
   `authBlackout:true` (or `degraded:true`), then immediately call the same audio URL. Record the
   wall-clock time and the `psidtsAgeSeconds` value with the result.

```bash
curl -s -o /dev/null -w '%{http_code}\n' "http://<host>:5555/api/gvbridge/voicemail/$ID/audio"
curl -s "http://<host>:5555/api/gvbridge/voicemail/$ID/audio" | jq .
```

**PASS:** `502`, body `{"error":"Failed to fetch voicemail list from Google"}`.
**FAIL (the bug):** `404` with `"... has no recording"` for an id that returned audio in B1.

**B4 — `GetItem` and `MarkRead` under the same induced blackout.** Same condition as B3:

```bash
curl -s -o /dev/null -w '%{http_code}\n' "http://<host>:5555/api/gvbridge/voicemail/$ID"
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  -H 'Content-Type: application/json' -d '{"isRead":true}' \
  "http://<host>:5555/api/gvbridge/voicemail/$ID/read"
```

**PASS:** both `502`. `409` on the second means `EnableMarkRead` is false — record as not run.

**B5 — restore and confirm recovery.** Undo B3's induced failure, wait for the next successful
refresh, and re-run B1. **PASS:** back to `200 audio/mpeg`. This proves the 502 was the blackout
and not a fault the test introduced permanently.

### Reporting

For each case record: case id, exact URL, HTTP status, response body (or byte count for audio),
wall-clock time, and the `status` payload at that moment. **B3 and B4 are meaningless without the
`status` payload** — it is the only evidence the service was actually in a blackout.

---

## 5. Explicitly out of scope

- **XR-3 / B2** — already shipped in PR #72; `docs/plans/gv-auth-blackout-b2-{design,plan}.md`
  are its documents. Do not re-plan, re-open, or extend it. Its only contact with this work is
  that its 401 retry makes XR-6's window narrower; it changes nothing about what `FindNodeAsync`
  does when a list *does* fail, and it never touches `GvVoicemailController.cs`.
- **`/api/gvbridge/status` honesty** — B2's Task 5, already shipped. Not a voicemail-route concern.
- **Splitting `GetAudio`'s 404** into "no such voicemail" vs "found, no media" — raised with Radio
  Console in Task 7b instead, because it changes a response body they match on.
- **Deploying to the box** — Task 1.3 *diagnoses* the deployment gap; fixing it, and the orphaned
  `/mnt/d/prj/rp-deploy` worktree, is separate work for the owner.
- **The uncommitted `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` change** on
  `diag/gv-srtp-receive` — handled separately, do not touch.

---

## 6. Should this repo have a `docs/BUILDER_QUEUE.md`?

It does not have one today, and this plan does not create one.

**Recommendation: no — keep plan-as-handoff, but fix the one thing that actually hurt.**

The reasoning is what this batch just demonstrated. The queue file is not where the failure was.
Both XR-2 and XR-3 were fully shipped, with tests, and the cross-repo partner still had them filed
as open — while XR-6, the one genuinely open item, had never been filed here at all. A queue row
would have recorded "XR-2: done" in a file Radio Console cannot see. What was missing was a **reply
on the handoff**, which is what the boundary doc's protocol already specifies and what Task 7b
delivers.

Three more reasons the shape here is fine as it is:

- **Volume doesn't warrant it.** `docs/plans/` holds seven documents. Each is self-describing and
  carries its own branch name, task list, and gates. A queue over seven items is an index of things
  you can already see in one `ls`.
- **A queue duplicates state that already has a home.** In-flight is a branch; shipped is a merge
  commit; known-but-unfixed is `docs/KNOWN-ISSUES.md`, which the B2 work already used well.
  A queue file would be a fourth place to forget to update — and stale queue rows are worse than
  no queue rows, which is precisely the failure mode on Radio Console's side.
- **The real coordination boundary is cross-repo**, and a file in this repo cannot fix it. The two
  repos already have a working protocol (`docs/prompts/` in, `docs/handoffs/` out). The cheap
  improvement is *using the reply half consistently*, not adding a queue neither side reads.

**The one thing worth adding instead**, if the owner wants a single place to look: a short
**status line at the top of each plan** in `docs/plans/` — `Status: Draft | In flight (branch) |
Shipped (PR #N)`. `gv-auth-blackout-b2-plan.md:3` already does this (it reads `Status: Draft`, and
is now stale — it shipped as PR #72, which is itself a small illustration of the point). Making
that line mandatory and accurate gives 90% of a queue's value for none of its upkeep.

**This is the owner's call, not Builder's.** If the answer is "yes, stand one up", it should be a
deliberate task with a chosen schema — not a file invented as a side effect of this batch.

---

## 7. Self-review

- **Placeholder scan:** no `TBD`, no "similar to Task N", no "implement later". Every code block is
  literal and complete.
- **Line numbers:** every citation verified against `main` @ `738141f` via `git show main:<path>`,
  not the working tree. Three drifts in Radio Console's citations corrected in §1.
- **Spec coverage:** XR-2 → Tasks 1, Test Plan Part A (keyed on the slash, real
  `g.Group Message.<base64url>` id, `MarkThreadRead` covered at A4). XR-6 → Tasks 2–6, Test Plan
  Part B. Per-thread sanity check → §2, Task 1.1, A5. Why `ShapeIsSane`/`Succeeded` cannot catch
  it → §2.
- **Type consistency:** `FindNodeAsync` returns `Task<(bool Succeeded, GvVoicemailNode? Node)>` in
  Task 2 and is destructured as `var (listSucceeded, node)` / `var (_, freshNode)` at all four call
  sites in Tasks 3–5.
- **Scope:** XR-3 planned nowhere; named only where it changes XR-6's severity or the reply.
- **Assumptions flagged:** the port (§4 preamble), `EnableMarkRead` being on (A4, B4), the account
  having at least one slash-bearing thread (A1), and the `MediaId`-null test being deliberately
  omitted (Task 6).
