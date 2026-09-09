# Known Issues

## 🔴 ACTIVE OUTAGE — the box's Chrome Google Voice session is signed out (2026-08-01 19:36 EDT)

**Status:** 🔴 **OPEN — needs a human.** Nothing in the service can fix this; it needs an **owner re-login
at `voice.google.com` in the box's Chrome**. Discovered while deploying the canonical post-merge B2 build.

**Impact is wider than SMS/voicemail.** SIP registration resolves its credentials through the same
authenticated GV client, so a dead GV session takes the **whole phone** down, not just the data plane:

```
available:false  sipRegistered:false  wsConnected:false  cookiesValid:false
/api/gvbridge/sms/threads → 502
```

**How to confirm it (the obvious check lies).** The Chrome tab title read `Voice - (99+) Voicemail` and the
URL read `https://voice.google.com/u/0/voicemail` — both **stale cached renders** from before the session
died. The reliable test is to force a navigation and see where it lands:

```
https://voice.google.com/u/0/voicemail  →  redirects to  →  https://workspace.google.com/products/voice/
```

That redirect to the signed-out marketing page **is** the signed-out signal. A `RotateCookiesPage` tab was
also parked in the browser. Service-side, the tell is all three recovery rungs failing at once, which the
service already reports in plain language:

```
[WRN] RotateCookies returned 401 — falling back
[WRN] ReloadCookiesAsync: new cookies failed health check
[WRN] GVApi: all cookie-recovery rungs failed. The box's Chrome login may be dead —
      re-login at voice.google.com so the next CDP refresh can pick up a fresh session.
```

### The mechanism that turned a healthy service into a full outage in 5 seconds

**A `refresh-from-browser` against a signed-out Chrome overwrites a *working* cookie set with a dead one,
and the working set is then unrecoverable.** Captured exactly, from the restart log:

```
19:36:49 [INF] Listed 149 recent SMS messages          <- WORKING, cookies loaded from disk
19:36:49 [INF] Listed 50 voicemails from 50 raw threads
19:36:51 [INF] Cookies saved to data/gv-cookies.enc    <- 20 dead cookies overwrite the good set
19:36:51 [WRN] GV health check failed: Unauthorized
19:36:51 [INF] CDP cookie refresh: 20 cookies extracted and activated
```

The only copies of the good set were the old process's memory (gone on restart) and
`data/gv-cookies.enc` (overwritten two seconds later). Both pre-existing on-box backups
(`/opt/rotary-phone.bak.prefix-uat-20260801-160513/data/gv-cookies.enc`, 16:00) were tried and are **also
dead**, so the whole Chrome-derived lineage is invalid — this was a genuine account-session death, not a
local corruption.

### Why B2 makes this *more* consequential, not less — and what it means for the cron

Pre-B2, the app's cookies and Chrome's jar were kept in lockstep by the 20-minute cron, so pulling from
Chrome was near-harmless. **B2's in-process 8-minute refresh keeps the app's lineage alive independently**,
so the two lineages now **diverge** — the app can hold fresh, working credentials while Chrome's jar rots.
Once that is true, `refresh-from-browser` is no longer a harmless idempotent top-up: it is a **downgrade
path**, and the box-side cron fires it **every 20 minutes**.

> **This escalates finding M1 (retire the cron) from "redundant" to "standing hazard."** The cron's
> original justification — keep GV authenticated until the in-process refresh is proven — is not merely
> expired; the cron is now the mechanism most likely to *destroy* working credentials. Retiring it should
> be prioritized accordingly. It remains a box-side change needing its own rollback story.

**Proposed hardening — ✅ IMPLEMENTED 2026-09-08**, five weeks after it was proposed here and **one day
after the delay cost an 83-minute guest-facing outage.** All three rules below now hold, in both of the
two places that persist cookies:

- ✅ **Validate before adopting.** `GVApiAdapter.TryValidateCandidateAsync` adopts a candidate in memory,
  probes it against Google, and persists **only** on success. It writes nothing itself — the caller does,
  and only on `true`.
- ✅ **Keep a last-known-good set and roll back.** A failed probe restores the previous cookie set and the
  previous `_areCookiesValid`, so the adapter is never left holding credentials already proven bad.
- ✅ **Never let an unvalidated refresh overwrite a validated set.** Both write paths are covered:
  recovery rung 3 (`TryCdpRefreshAsync`) and — the one that actually ran for two days — the 20-minute
  cron's `GvCookieManager.SetCookiesAsync`, which used to save on its **first statement** and return
  `true` whenever `SwitchModeAsync` merely failed to throw.

> ⚠ **Read this before deferring a LOW again.** This block was written on 2026-08-01 with the mechanism
> correctly diagnosed and the fix correctly specified, and was not built. On 2026-09-06 the same path
> silently overwrote working credentials with dead ones every 20 minutes for two days; on 2026-09-08 it
> combined with finding **L2** below to produce the outage. The cost of writing the fix was about a day.

**Regression tests** (each verified to FAIL against the unfixed code, not merely to pass against the fix):
`CdpRefresh_WhenExtractedCookiesAreRejected_LeavesTheStoredGoodSetIntact` and
`SetCookiesAsync_GoodCookiesHeld_DeadOnesOffered_ReturnsFalseAndKeepsTheGoodSet`.

⚠ **Behaviour change for operators:** `POST /api/gvbridge/cookies/refresh-from-browser` now answers
**502** (was **200**) when the browser session is stale, and the recovery procedure below therefore
reports honestly instead of silently destroying the working set.

The route also stops giving one answer for several different situations — **502** means Google *tested
and refused* the cookies (re-login), while **503** means Chrome was *unreachable* and **the Google login
was never tested at all** (check Chrome is running first; the session may be perfectly fine). **502** and
**503** send you to two different places, and the old code sent you to the wrong one whenever the browser
was simply down. **202** means the cookies passed but re-activation failed — investigate the call path,
do **not** re-login. **500** means the disk, not Google.

**Recovery procedure:** re-login at `voice.google.com` in the box's Chrome (profile on `radio`, CDP port
9224), confirm the URL stays on `voice.google.com` rather than redirecting, then
`curl -X POST localhost:5004/api/gvbridge/cookies/refresh-from-browser` and restart `rotary-phone`.
Verify `sipRegistered:true` and `/api/gvbridge/sms/threads` → 200.


## ⚠️ OPEN — Deploying clobbers the box's `appsettings.Production.json`, including BT adapter config

**Status:** 🔴 **OPEN** — recurs on **every** deploy that falls back to the tar path. Found during PR #72
UAT (finding **L3**); the tester caught it and restored the file by hand.
**Why this is the most dangerous item on the list:** the clobbered values include
**`BluetoothAdapter: hci1`** and `UseActualBluetoothHfp`. **This crosses the Radio Console audio boundary**
(`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`) — a silent BT-config change on this side can break the
*other* service's audio, and nothing in the deploy surfaces that it happened. It will happen again on the
next deploy unless the tooling is fixed.

**Mechanism:**

1. The publish output **contains** `appsettings.Production.json` — the SDK's default `Content` glob picks
   up `appsettings*.json`, and nothing in `RotaryPhoneController.Server.csproj` excludes it. So the repo's
   *template* ships in the artifact alongside the binaries.
2. `deploy/Deploy-ToLinux.ps1` has two sync paths. The **rsync** path is safe — it passes
   `--exclude 'appsettings.Production.json'` (`:89`). The **tar-pipe fallback** (`:126-129`) is not: it
   relies on a backup/restore *around* the extract
   (`cp -f … /tmp/rp-prod.bak` … `tar -xzf - --unlink-first …` … `mv -f /tmp/rp-prod.bak …`).
3. Reproducing that tar-pipe **on Linux**, `--unlink-first` errors on directories, so `tar` exits **2**.
   With `set -e -o pipefail` the chain aborts **before** the restore `mv` runs — leaving the box on the
   repo's template config. The backup survives in `/tmp/rp-prod.bak`, but nothing puts it back.

The box's copy is **authoritative** (see `docs/HT801-ADDRESS.md`) and carries values the repo template
does not: `EnableMarkRead`, the GV number (`GvPhoneNumber: +1XXXXXXXXXX` — redacted; this repo is public),
and the HT801 address, in addition to the BT keys above.

> ### ⛔ Correction 2026-09-09 — step 3's mechanism is wrong; the defect is not. Entry stays OPEN.
>
> **Falsified twice: locally** (`deploy/tests/repro-tar-clobber.sh` case A) **and by a live deploy on the
> box.** Step 3 above says `set -e -o pipefail` aborts the chain before the restore `mv` runs. That
> `set -e` is in the **local** PowerShell-invoked script (`Deploy-ToLinux.ps1:125`). Backup → extract →
> restore is a `;`-separated string executed by the **remote** shell, which does not inherit it, and
> `pipefail` can only abort the local script after the remote work has finished. **The restore runs.**
>
> The live deploy proved the sequence with three facts rather than assuming it: the config's sha256 was
> **unchanged**, its mtime **moved**, and `/tmp/rp-prod.bak` was **gone**. tar overwrote the file and the
> restore put it back.
>
> What step 3 got right: `--unlink-first` really does fail on the archive's directory members and tar
> really does exit 2 — on **every** run, because `tar -czf - .` always carries a `./` member.
>
> **The mechanism that actually clobbers — and there are two of them, not one.** Both ends of the dance
> are best-effort (`2>/dev/null || true`), so *either* end can fail silently and each produces a
> different bad outcome. Measured 2026-09-09:
>
> - **Backup-side failure** (`repro-tar-clobber.sh` case **B1**). `cp -f … /tmp/rp-prod.bak` fails — no
>   config on the box yet on a first deploy, `/tmp` unwritable, disk full — and is swallowed. The
>   `[ -f ]` guard is nonetheless **true**, because a `rp-prod.bak` from an earlier run is still sitting
>   in `/tmp`, and the restore installs that **stale** content. ⛔ **This is the worst case:** the box
>   does not get the repo template, which is at least a reviewable file in version control — it gets
>   arbitrary config from a previous deploy, and the chain exits 0.
> - **Restore-side failure** (case **B2**). The backup **succeeds**, and the restore `mv` is what fails —
>   a sticky `/tmp` holding an `rp-prod.bak` this uid cannot unlink, for instance. tar's template stays
>   on the box and the backup is stranded in `/tmp`. **This is exactly the state PR #72 UAT found**
>   (finding L3): clobbered config, backup still present.
>
> ⚠ **A note on how nearly this correction repeated the original error.** The implementation plan for
> this fix proposed a single case B, using a read-only parent directory, and attributed it to the
> **backup** side. Measured, that fixture's `cp -f` exits **0** and it is the `mv` that fails — writing
> to an already-existing writable file needs no write permission on the containing directory, only
> unlinking it does. The assertions passed either way. A green check with a wrong stated mechanism is the
> same defect as the one this entry records, one level up, so the fixture was kept as B2 and correctly
> labelled rather than quietly re-explained.
>
> **A second, worse thing came out of the same measurement.** The remote chain's exit status is
> `chmod`'s, so it reports **0** while tar has failed. The comment at `Deploy-ToLinux.ps1:113-114`
> claiming the exit-code check prevents a silent stale deploy is therefore **false on the tar path** —
> the check is real but structurally blind. Tracked as Defect 4 in
> [`docs/plans/deploy-tooling-honest-deploy-plan.md`](plans/deploy-tooling-honest-deploy-plan.md).
>
> ⚠ **And this is not a rare path.** `rsync` is absent from the deploying machine's PowerShell `PATH`, so
> `Get-Command rsync` finds nothing and **every deploy from that machine takes the tar path.** Installing
> rsync changes the default; it does not fix the fallback.
>
> ⭐ **The lesson, which is the part worth keeping.** The recorded explanation was written after the
> defect was correctly observed, and it was wrong. It stayed plausible for five weeks because it named a
> real flag (`set -e`) doing a real thing (aborting a chain) in the wrong shell. Fixing what it described
> — making the restore unconditional, or wrapping it in a `trap` — would have changed nothing and looked
> like a fix. The chosen fix instead removes the file from the tar stream, so the property holds
> whichever way the dance fails.

**Proposed fix (not done in PR #72 — deploy tooling, needs its own change + rollback story):**

- **Primary:** add `--exclude=./appsettings.Production.json` to the `tar -C … -czf -` invocation in
  `deploy/Deploy-ToLinux.ps1`, matching what the rsync path already does. Then the file is never in the
  stream and the fragile backup/restore dance stops being load-bearing.
  > ✅ **Adopted** — `fix/deploy-honest-status`, Task 3. The dance is deleted, not repaired.
- **Belt and braces:** drop it from the publish output entirely — in
  `src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`, exclude
  `appsettings.Production.json` from `Content` (or set `CopyToPublishDirectory=Never`), so no artifact
  can carry a config that only the box should own.
  > ✅ **Adopted** — `fix/deploy-honest-status`, Task 5.
- **Either way:** make the restore unconditional (run it in a `trap`/`||` rather than after a `set -e`
  command that can abort), and have the deploy **print** the post-deploy `BluetoothAdapter` value so a
  clobber is loud instead of silent.
  > ⛔ **First half superseded** — the restore already runs; see the correction above. Making it
  > unconditional would have changed nothing, because in case B2 the `mv` runs and *fails*, and in case
  > B1 it runs and installs the wrong file. There is no version of "run the restore harder" that fixes
  > either.
  > 📌 **Second half still open** — printing the post-deploy `BluetoothAdapter` value is Task 6 of the
  > plan and is **not** in `fix/deploy-honest-status`; its acceptance needs a live deploy to demonstrate.

**Until it is fixed — mandatory manual step on every deploy:** back up
`/opt/rotary-phone/appsettings.Production.json` **before** the sync and verify it **after**, explicitly
confirming `BluetoothAdapter` is still `hci1`. Restore it by hand if it changed.


## `/api/gvbridge/event` had no route, but two middlewares still special-cased it (RESOLVED 2026-09-09)

**Status:** ✅ **Resolved by `fix/remove-gvbridge-event-carveouts`.** The owner's decision was to
**remove both carve-outs**. Recorded below as found, then the resolution — the finding's reasoning is
kept intact rather than rewritten, because *why* it stayed invisible is the reusable part.

Found while fixing the `/api/*` fallback below; **not** introduced by it, and deliberately left out of
that PR rather than fixed in an unrelated change.

**There is no controller route for `/api/gvbridge/event`.** No `[HttpPost("event")]`, no
`[Route("event")]` — anywhere. Two middlewares nonetheless still carve it out:

- `Program.cs:395-412` — a bespoke CORS block that answers its `OPTIONS` preflight with `204`.
- `GvBridgeAuthMiddleware.cs:38-40` — an **auth-gate exemption**, so the path is deliberately open.

`docs/superpowers/specs/2026-03-27-gv-api-migration-design.md:222` lists *"Service worker HTTP relay
for call events — no longer needed (signaler handles detection)"* under **What Gets Deleted**, and
there is no extension source (no `manifest.json`) in the repo. So this is vestigial wiring for a
relay that was removed by design; the middleware carve-outs outlived the endpoint.

**Why it stayed invisible:** a `POST` here used to hit the SPA fallback and return **`200` with
`index.html`** — so any caller checking `response.ok` was told it succeeded. The `/api/*` 404 fix
below makes it honest:

```
POST /api/gvbridge/event  →  404 application/json  {"error":"No API route matches POST /api/gvbridge/event"}
```

**This is a second instance of the same disease as the fix below**, found by the fix: a success code
covering a failure. Anything still posting call events here has been silently failing, and was
already failing before the 404 change — the change only makes it audible.

**Two things for the owner to decide:**
1. **Is anything still POSTing here?** If yes, it has been broken for some time and needs a route,
   not a fallback. If no (which the design doc implies), both carve-outs should be deleted.
2. **The auth exemption is the part that matters.** It punches a permanent hole in the
   `/api/gvbridge/*` gate for a path that does not exist. Harmless today — there is nothing behind
   it to reach — but a future route added at that path would be **born unauthenticated**, silently.

### Resolution (2026-09-09) — both carve-outs removed

The owner chose **removal**. `GvBridgeAuthMiddleware`'s exemption and the bespoke CORS block in
`Program.cs` are both gone; every `/api/gvbridge/*` path is now gated uniformly, and the gate remains
default-off when no key is configured.

**The CORS block's removal was a security improvement in its own right**, for a reason not visible in
the original finding: it set **`Access-Control-Allow-Origin: *`** and matched on
`path.Contains("gvbridge/event")` — a **substring**. Review MEDIUM-1 had anchored the *auth*
exemption to a segment boundary precisely so `/api/gvbridge/eventlog` would not be wrongly exempted;
**the CORS block never got that fix.** So that sibling path was correctly gated by auth while still
being handed wildcard CORS.

Pinned by four tests in `GvBridgeAuthMiddlewareTests` — `/api/gvbridge/event` and `/event/status` now
`401` without a header, `200` with a valid one (proving it is gated, not hard-denied), and still
ungated when no key is set. Negative control: restoring the exemption fails exactly the two tests
that pin its removal.

⚠ **The published contract changed.** The boundary doc's Inter-service auth row previously promised
Radio Console that this path *"stays open — never gated"*. That sentence is withdrawn, with a dated
Change Log entry and a notice at
`docs/handoffs/2026-09-09-radioconsole-gvbridge-event-carveouts-removed.md`.

**The lesson worth keeping:** the carve-outs had been reviewed, hardened, documented, and published as
a cross-repo contract — and nobody checked whether the endpoint they protected still existed. It had
been deleted by design six months earlier. Careful work, correctly executed, on something that should
not have been there.

## Unmatched `/api/*` returned HTTP 200 with `index.html` instead of a 404 (RESOLVED 2026-09-09)

**Status:** ✅ Resolved by `fix/api-404-not-spa-fallback`.
**Symptom (was):** `Program.cs` ended in a bare `app.MapFallbackToFile("index.html")`, so **any**
unmatched `/api/*` path returned **`200 text/html`** — the React SPA shell — to a caller that asked
for JSON. A typo'd or wrong-shaped API path looked like a success.
**Impact (was):** A success code covering a failure, in the one place a caller has no way to
second-guess it. `GetFromJsonAsync` throws on the content type, gets logged as a parse failure, and
the caller concludes the *data* was bad rather than the *route*. It burned a probe on each side of
the RotaryPhone/Radio Console boundary in a single day.

**Fixed by** registering an explicit `MapFallback("/api/{**rest}", ...)` ahead of the SPA fallback,
returning `404` with `{ "error": "No API route matches <method> <path>" }` and
`Content-Type: application/json`. Verified against the running service:

```
GET /api/gvsms/          → 404 application/json  {"error":"No API route matches GET /api/gvsms/"}
GET /settings/audio      → 200 text/html         (SPA shell, unchanged)
GET /api/contacts        → 200 application/json  (real routes unaffected)
```

⚠️ **Do NOT "modernise" this to `UseStatusCodePagesWithReExecute("/not-found")`.** The .NET 10
template ships it and current Microsoft docs steer you toward it, but it re-executes into the SPA
pipeline and gives every `/api/*` 404 an **HTML body** — this same defect back through the front
door, **with every test still green.** Flagged by Radio Console's own investigation before we hit it.

**The misattribution is the more useful lesson.** For a day both repos recorded this as *Radio
Console's* fallback; Radio Console filed it as `UI-11` and offered to fix it on their side. `Radio.Web`
has no SPA fallback and never had one — `git log -S "MapFallback" --all -- src/` returns zero commits
there. Both incidents were on `:5004`, and the route under test (`/api/gvbridge/sms/threads/...`)
only exists here. **Neither session re-derived which server sent the bytes**; the refuting evidence
sat in Radio Console's own archive the whole time. Full retraction and the corrected records:
`docs/prompts/2026-09-09-radioconsole-ui11-was-never-ours.md`, plus annotations in
`docs/handoffs/2026-09-08-radioconsole-{bell-persistence-and-404,incident-and-corrections}.md` and
`docs/prompts/2026-09-08-radioconsole-ack-2-and-three-rows.md`.

⚠️ **Two pre-existing verification steps changed meaning** and were annotated in place:
`docs/plans/gv-crossrepo-xr2-verify-and-xr6-blackout-404.md` A7 (now expects `404 application/json`,
not `200 text/html`) and `docs/plans/build-stamp-and-deploy-verification.md` P2, **which this fix
silently weakened** — it detected an unregistered route by content-type alone, and a missing route
now answers `404 application/json`, satisfying its PASS condition. It now asserts the status line.

## Voicemail routes 404 a recording that exists, during a GV auth blackout (RESOLVED 2026-09-08)

**Status:** ✅ Resolved by the XR-6 PR (`fix/gv-voicemail-blackout-404`).
**Symptom (was):** During an auth blackout, `GET /api/gvbridge/voicemail/{id}/audio` answered
**`404 "Voicemail {id} has no recording"`** for a recording that exists and plays fine minutes later.
`GET /api/gvbridge/voicemail/{id}` and `POST /api/gvbridge/voicemail/{id}/read` had the same defect.
**Impact (was):** Radio Console's `GvMediaUnavailableException.IsPermanent` maps `NotFound` to
*"retrying will not help"*, so a guest was told a voicemail was **permanently gone** when it would
play shortly. A transient condition was reported as a terminal one.

**Root cause — a dropped flag, one layer above the `XR-2` gap.** `GvVoicemailClient` already returns
`GvVoicemailListResult.Empty(succeeded: false)` when an authenticated list fails, so the information
was present and correct. `FindNodeAsync` — the private list-and-filter helper the per-id routes share
— returned only `GvVoicemailNode?` and **threw the `Succeeded` flag away**. A failed list therefore
produced an empty item set, `FirstOrDefault` yielded `null`, and every caller read that `null` as
*"not found"* rather than *"not read"*.

`GetList` never had this bug: it guards its own list result and returns 502, under a comment stating
exactly why — *"Do not mask an auth/transport failure as 'no voicemails' — RadioConsole cannot tell
the difference from an empty 200."* The defect was that its sibling helper did not carry the same
flag to the routes that needed it.

**This is the same shape as the `XR-2`/`ShapeIsSane` gap one layer up.** `Succeeded` validates the
*fetch*; `ShapeIsSane` validates the *shape*; neither validates the *selection*. A filter matching
zero rows is not an error state in either vocabulary. The remedy is the same in kind both times:
carry the flag that already knows the difference to the place that decides the status code.

**Fix.** `FindNodeAsync` now returns `(bool Succeeded, GvVoicemailNode? Node)`, which makes the
compiler enumerate every call site so none can be missed. **All four were reviewed and they do not
all want the same treatment:**

| Call site | Now | Rationale |
|---|---|---|
| `GetItem` | **502** on `!Succeeded` | Same defect, same remedy |
| `GetAudio` | **502** on `!Succeeded` | The reported bug |
| `MarkRead` step 2 (pre-write lookup) | **502** on `!Succeeded` | Same defect; 404s before any write is attempted |
| `MarkRead` step 5 (post-write re-read) | **deliberately still 200** | ⚠️ The write to Google **already succeeded**. A 502 here would tell Radio Console a real state change did not happen, and they would reconcile away a change that is real — a worse lie than a marginally stale DTO. The `with { IsRead = … }` already carries the applied truth. **This is the one call site where `!Succeeded` must NOT become a 502**; it has a pinning test and an in-source comment so it is not "fixed" later. |

**Resulting contract:** `502` = *"we could not look."* `404` = *"we looked and it is not there."*

⚠️ **The 404 half is bounded at the 100 most recent voicemails, and this is still open.**
`FindNodeAsync` requests `count: 100` with no page token, and `GvThreadClient.ListRawAsync`
**deliberately ignores** a page token because the paging field position is UNVERIFIED (it logs a
warning rather than guess and silently re-read page 1 forever). So a voicemail older than the 100th
returns `Succeeded: true` with the id absent and is reported as a genuine miss — **a 404 for a
voicemail that exists**. That is pre-existing and was not introduced or changed here, but it is the
same guest-facing lie described above reached by a different trigger, and it means `404` from these
routes means *"not in the 100 most recent"* rather than *"does not exist"*. **Do not harden anything
on 404 meaning "permanently gone" until paging is verified** — which needs a paged capture from the
box first. Raised with Radio Console in the reply.

**Tests.** Each route is covered by a **pair** — one proving a failed list becomes 502, its twin
proving a successful list that lacks the id is **still 404**. The pair is load-bearing: a fix that
turned every miss into a 502 would pass the failure half alone and silently break the 404 semantics
Radio Console depends on.

**Severity note.** Radio Console's original report put this at *"~45% of the time / ~9 minutes in
every 20"*. That figure predates PR #72, which added recover-and-retry on 401/403 at the shared read
path, and **should not be repeated** — the blackout window is now far narrower, so this was a rare
lie rather than a frequent one by the time it was fixed. It was still a real correctness bug.

**Still open, deliberately out of scope:** the surviving 404 on `GetAudio` conflates *"no such
voicemail"* with *"found, but no media"*. Splitting them would change a response body Radio Console
matches on, so it was raised with them for a decision rather than changed unilaterally. See
`docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md`.

**Provenance.** Found by Radio Console by reading our source, filed as their punch-list `XR-6` on
2026-09-03, and **never sent to us** through the boundary doc's inbound lane — so it sat unknown on
our side while five already-fixed items stayed open on theirs. See the reply above for the delivery
gap that caused it.

---

## GV SMS/voicemail 502s in a repeating ~9-minute dead window (RESOLVED 2026-08-01)

**Status:** ✅ Resolved by the B2 auth-blackout PR (**#72**, `fix/gv-auth-blackout`), merged 2026-08-01.
**Live on-box soak PASSED** — 932 HTTP requests over ~88 minutes, **zero 502s, zero non-200s**, against a
pre-fix baseline of 15/49 (31%) 502s for the same shape. 6 of 7 acceptance criteria verified by
measurement, 1 partial, 0 failed. See "Verified live" and "Open verification items" below.
**Symptom (was):** Radio Console saw HTTP 502 from `/api/gvbridge/sms/*` in a clean repeating pattern —
roughly **9 dead minutes inside every ~20-minute cycle**. Upstream, `journalctl -u rotary-phone` showed
`api2thread/list returned Unauthorized for folder Sms` **271 times on 2026-07-31**, and zero
`TooManyRequests`. 11 of 11 of Radio Console's 502s fell inside a dead window.
**Impact (was):** Every SMS and voicemail read path, roughly 45% of the time. Invisible to
`/api/gvbridge/status`, which reported `cookiesValid:true`, `degraded:false` straight through the
outage — so the dashboard said healthy while the feature was dead.
**Not throttling.** Falsified early: a constant-rate poller showed the same on/off pattern, upstream
status was always 401 and never 429, and recovery landed on fixed wall-clock boundaries rather than
after a variable cooldown. This was an **auth-freshness** defect. Google's rotating
`__Secure-1PSIDTS` / `__Secure-3PSIDTS` cookies are good for about **11 minutes**.

**Root cause — seven findings, and the last two are the interesting ones:**

- **F1.** `CookieRefreshIntervalMinutes` was a **dead config knob** — declared, set in `appsettings.json`,
  and read by *nothing*. There was no proactive cookie/PSIDTS refresh timer in the service at all.
- **F2.** The only periodic auth mechanism was a **30-minute probe**, not a refresh — and it probed
  `threadinginfo/get`, a *different endpoint* from the `api2thread/list` that was failing.
- **F3.** The ~20-minute cadence came from **outside this process**: a box-side cron running
  `/opt/rotary-phone/refresh-gv-cookies.sh` every 20 minutes, POSTing
  `/api/gvbridge/cookies/refresh-from-browser`. A wall clock, which is why recovery was second-exact on
  20-minute boundaries.
- **F4.** The reactive-401 escalation **already existed but only on the SIP leg**.
  `GvThreadClient.ListRawAsync` collapsed *every* non-2xx to `null` with no status discrimination, so
  the SMS/voicemail data plane reached none of it. This is precisely why SIP recovered while SMS
  blacked out.
- **F5.** `_areCookiesValid` was a **cached probe result** — up to 30 minutes stale, of the wrong
  endpoint. `psidtsAgeSeconds: 781` (13m01s, already past the ~11-minute lifetime) was in the same
  payload: the endpoint was carrying the evidence of its own staleness and not using it.
- **F6.** `GVApiAdapter.ActivateAsync` was **not re-entrant**. `CallAdapterRegistry.SwitchModeAsync`
  skips `DeactivateAsync` when the mode is unchanged, so the cron's refresh re-entered `ActivateAsync`
  on the *live* adapter every ~20 minutes, each pass leaking an armed 30-minute `Timer`, an
  `HttpClient`, and a whole `GvSipTransport` (WebSocket + keep-alive timer + Opus codecs) with its
  event handlers still subscribed — **~72 leaked objects/day**.
- **F7.** …and therefore **the 30-minute watchdog was starved and effectively never fired.** Each
  refresh installed a *fresh* 30-minute timer; refreshes arrived every ~20 minutes; the newest timer
  never reached its due time. Since that watchdog was the **only timed entry into the recovery ladder
  in the entire service**, the deployed reality was that *the only thing that ever restored auth was
  the external cron*. There was no in-process recovery cadence — not a slow one, none.

**Fix:**
- **Proactive refresh is real.** `CookieRefreshIntervalMinutes` now governs an actual timer, defaulting
  to **8 minutes** (comfortably under the ~11-minute PSIDTS lifetime, without the 60% extra call volume
  that 5 would cost). Rung-1 only (browser-less `RotateCookies`) — CDP is heavy and stays reserved for
  reactive recovery. Setting it to **`0` disables the timer** — a kill switch with no redeploy. The
  proactive path deliberately does **not** re-register SIP (that would re-create the 2026-06-19
  REGISTER-storm risk).
- **Reactive refresh-and-retry on the read path.** On `401`/`403` only, `ListRawAsync` runs the shared
  recovery ladder, **re-resolves** the authenticated client (rungs 1 and 2 dispose and re-create it) and
  replays **exactly once**. `429`/`5xx`/network faults deliberately do not retry. Write paths
  (`sendsms`, `updateread`) *signal* recovery but **never replay** — ADR §4.2 #4 forbids auto-retry on
  irreversible GV writes.
- **One ladder, two doors.** `RecoverFromAuthFailureAsync` now reports its outcome and is guarded by a
  shared `Task<bool>` instead of an int flag, so concurrent callers **await the same run** rather than
  being turned away. SIP keeps its fire-and-forget entry point; the data plane gets an awaitable one.
  A **failure-only** 60-second cooldown (`AuthRecoveryFailureCooldownSeconds`) stops a real Google
  outage from driving `RotateCookies` at the poll rate.
- **`ActivateAsync` is re-entrant, and a healthy SIP transport is reused rather than rebuilt.** The
  decision is one predicate, `CanReuseTransport => _sipTransport?.IsRegistered == true`, evaluated after
  the incoming cookie set is loaded:
  - **registered + unchanged credentials** → total no-op; transport, `HttpClient` and both timers untouched.
  - **registered + changed credentials** (the common case — the cron pulls every 20 min while PSIDTS
    rotates every ~11, so the header differs on nearly every fire) → cookies adopted and clients rebuilt,
    **transport and timers kept**, deliberately **no re-register** (re-registering on a cadence re-creates
    the 2026-06-19 REGISTER-storm risk).
  - **absent or unregistered transport** → the full teardown via the existing `DeactivateAsync`, then
    rebuild — with an `_activeCallId` guard re-checked after timer disposal so a call that starts mid-
    teardown is never dropped.

  This is safe because `GvSipTransport` caches no credentials: it holds a `Func<Task<SipCredentials>>`
  invoked fresh on every register (`Sip/GvSipTransport.cs:1021`) that resolves the `HttpClient` lazily by
  field — recovery rung 2 already relied on it. **Reuse fixes F7 harder than a teardown would:** an
  unconditional teardown re-arms a fresh 30-minute health timer on every 20-minute cron fire, which is
  precisely the starvation shape F7 describes.
- **Honest status.** New `authBlackout`, `lastApiSuccessAt`, `lastApiAuthFailureAt` fields, written by
  the **real** data-plane calls rather than by a probe. `AreCookiesValid` became
  `probe && !authBlackout`, so `cookiesValid` — and `degraded`, which derives from it — go false the
  moment a real call is rejected. Field names are append-only; the four contract names are untouched.
- **`available` deliberately stays `true` during a blackout.** Radio Console asked for `available:false`;
  we declined for a concrete reason. `GetAuthenticatedClient()` gates on `IsAvailable` and returns
  `null` when false, so flipping it during a transient 401 would make the adapter **refuse its own
  recovery retry** — turning a 9-minute blackout into a hard stop. Bind status UIs to `degraded` or
  `authBlackout` instead.

**Verify:**
- `journalctl -u rotary-phone --since '-60min' -n 5000 --no-pager | grep -c 'api2thread/list returned Unauthorized'`
  should drop from ~40/hour toward 0. **Bounded reads only — never `-f`, never `tail -f`** (the box is
  an N100 shared with Radio Console and journald churn correlates with audible audio distortion there).
- Poll `GET /api/gvbridge/sms/threads` every 60 s for 30 minutes starting at an **arbitrary** wall-clock
  time — deliberately *not* aligned to a `CDP cookie refresh` line. Expect **zero 502s**. The retirement
  of the old "test inside a healthy window" discipline *is* the acceptance criterion.
- `proactive PSIDTS refresh succeeded` should appear at ~8-minute intervals.
- `POST /api/gvbridge/cookies/refresh-from-browser` twice should leave **one** health-check timer and
  **one** `GvSipTransport` (the F6/F7 gate). **⚠️ The log line to expect is inverted from the original
  plan text.** With SIP registered, expect
  `re-activation adopting new credentials — SIP transport is healthy, keeping it` (or
  `re-activation is a no-op …`), and `sipRegistered` must stay **true** across both refreshes with
  `lastConnectedAt` holding a single distinct value.
  `re-activating — tearing down the previous generation first` is correct **only** when the transport is
  absent or unregistered; seeing it while SIP is registered is a **failure**. Scoring this by the
  pre-amendment text turns a passing run into a false failure — see finding M2 on PR #72.
- If a 502 does occur, `curl -s localhost:5004/api/gvbridge/status` should show `degraded:true`,
  `cookiesValid:false`, `authBlackout:true` — and `available:true`, **by design**.

**Verified live (on-box UAT, 2026-08-01 16:05–17:36 EDT, PR head `b5b8444`):**

| # | Acceptance criterion | Result |
|---|---|---|
| 1 | `CookieRefreshIntervalMinutes` governs a real cadence; `0` disables it | ✅ 7 ticks exactly 8m00s apart; `0` produced zero proactive lines in 25 min while reactive recovery still worked |
| 2 | One shared recovery, exactly one replay, caller gets 200 | ✅ full ladder captured live at 17:32:53 — rung 1 401 → rung 2 fail → rung 3 CDP → replay 200 |
| 3 | Honest status during a 401; `available` stays true | ⚠️ **PARTIAL** — see Open verification items |
| 4 | Window-blind 30-min soak, 0 × 502 | ✅ 932 requests, **0** non-200 |
| 5 | `api2thread/list returned Unauthorized` drops toward 0 | ✅ **33/hr → 0/hr** |
| 6 | F6/F7 leak gate: one health timer, one `GvSipTransport` | ✅ stronger than asked — one transport across **6** re-activations, and the health timer ticked twice exactly 30 min apart on its original anchor (**F7 fixed, measured**) |
| 7 | Existing status-contract tests pass unchanged | ✅ |

`RotateCookies` (rung 1) is **not inert** — it rotated for real 5 times — but its usefulness splits by
cookie freshness: it works **proactively** (fresh cookies), and returns 401 **reactively** (already-stale
PSIDTS), where CDP carries the recovery. The design's layering is validated by evidence rather than
assumption. This resolves the "UNVERIFIED request shape" caveat previously carried here.

**Open verification items (not defects — merged knowingly):**

- ⛔ **Inbound call ringing was never tested for this change.** The tester had no way to originate a call
  to the GV number, so test-plan step 8 did not run. This matters because **Task 3 touches `_sipTransport`
  teardown**, and because conditional reuse makes teardown *rare*, any ringing regression would be
  **intermittent and hard to trace** — it would only surface on the path where the transport was absent or
  unregistered (restart, dropped WebSocket). Proxies were good throughout (`sipRegistered`/`wsConnected`
  true across 411 samples, one transport surviving six re-activations, and a server-side WebSocket close at
  17:07 that auto-recovered within the same second), but an actual ring is unverified. **Action: ring the
  phone once and confirm two-way audio.**
- ⚠️ **AC-3 is partial: `authBlackout:true` was never observed live.** Zero `authBlackout:true` samples
  across 411 status polls — not because the flag is broken, but because **recovery is faster than any
  practical sampling rate**: the one live blackout lasted **920 ms** (`lastApiAuthFailureAt 21:32:53.529`
  → `lastApiSuccessAt 21:32:54.449`) against a 4-second sampling floor. What *is* verified live:
  `available` never went false, and `lastApiAuthFailureAt` latched from a genuine data-plane 401. What
  remains **unit-test-only**: the derived `authBlackout` / `cookiesValid:false` / `degraded:true` trio
  during a *sustained* blackout.
  **⚠️ Radio Console must be told: `authBlackout` may be true for well under a second.** A reconnecting
  banner bound naively to it will effectively never appear. Bind to it only with a minimum-display or
  debounce window, or drive the UI from a sustained-failure signal instead. This is in the handoff reply.

**Follow-ups (do NOT do these in the B2 PR):**

- **Retire the box-side cron — its justification has expired.** Resolved decision 2 kept
  `*/20 * * * * /opt/rotary-phone/refresh-gv-cookies.sh` running "until the in-process refresh is proven."
  **It is now proven:** 33/hr → 0 `Unauthorized`, 932 requests with zero 502s, and a measured 8-minute
  proactive cadence. Meanwhile the accepted double-refresh cost (spec §8.2) has **materialized as
  measurable 429s on 29% of proactive ticks** (2 of 7) — the 8-minute timer, the 20-minute cron and
  reactive recovery together exceed what `accounts.google.com/RotateCookies` will serve. It degrades
  gracefully (warns, returns `NotRotated`, backstop intact, no 502s resulted), so this is not urgent.
  **This is a box-side change and needs its own rollback story** — retire the cron (or raise its interval),
  then re-measure the 429 rate. (Finding **M1**, PR #72 UAT.)
  > 🔴 **ESCALATED the same day — see the ACTIVE OUTAGE entry at the top of this file.** The cron is not
  > merely redundant now. Because B2's in-process refresh keeps the app's cookie lineage alive
  > **independently of Chrome's jar**, the two diverge — and a `refresh-from-browser` against a stale or
  > signed-out Chrome **overwrites working credentials with dead ones**. The cron fires exactly that path
  > every 20 minutes. Treat retiring it as **hazard removal**, not a tidy-up.
- **Path A (`re-activation is a no-op`) is dead code in production.** It fired **0** times in 6
  re-activations — every cron fire carries changed credentials, exactly as predicted. Correct and
  unit-tested, but never exercised on the box. Worth knowing before anyone relies on it. (Finding **L1**.)
- **`psidtsAgeSeconds` resets on activation regardless of the cookies' true issue time**
  (`_psidtsRefreshedAt = DateTime.UtcNow` on load) — it read `6` right after a restart whose on-disk PSIDTS
  was ~7 minutes old. Pre-existing, not introduced by B2, but B2's re-activation path hits it more often,
  so the field is a **less trustworthy staleness signal** than the pre-fix traces implied. (Finding **L2**.)

  > **Status 2026-09-08: ✅ RESOLVED — by REMOVAL, not by correction (PR #79).**
  >
  > **The field is gone.** `psidtsAgeSeconds` has been removed from `/api/gvbridge/status`, and the
  > `_psidtsRefreshedAt` state behind it has been deleted along with every one of its write sites. It had
  > exactly one reader — the removed property — so nothing else depended on it.
  >
  > **Why removal, when PR #78 had just deliberately frozen it.** The freeze existed because Radio Console
  > consumed the field as a documented blackout clock, and changing values underneath a live consumer is
  > the same class of mistake as the defect itself. **That premise is now dead.** Radio Console retracted
  > the published bands (their PR #622) and the owner verified independently that there are **zero
  > references to `psidtsAgeSeconds` anywhere in their `src/`** — it lived only in prose. A field that
  > protects nothing, whose *name* asserts "credential age" while its *value* reports the age of a cache
  > operation, is a trap set for whoever reads it next. That is exactly how the six-week doctrine formed.
  > A deprecation notice does not stop that; absence does.
  >
  > **What replaces it:** `psidtsMintedAtUtc` — the instant Google actually minted the credential, carried
  > on the cookie set, persisted across restarts, and unfakeable by a reload. ⚠ Its honest failure mode is
  > `null` = UNKNOWN, which is **not** healthy and must never render as fresh or as `0`. That is a real
  > limitation, but it is the *opposite* failure mode from L2's: the new field can decline to answer,
  > where the old one answered reassuringly and wrongly.
  >
  > **Two things this does NOT resolve — stated so "RESOLVED" is not read more widely than it should be:**
  > - **The operational consequence was already fixed separately**, by `ComputeFirstRefreshDelayMs` in
  >   PR #78. This finding's real cost was never the misleading number: it was that *the scheduler could
  >   not know the credential's age either*, so a restarted process waited a full 8-minute interval on a
  >   credential already 7 minutes old — defect 1 of the 2026-09-08 outage. Removing the field fixes the
  >   misleading signal, and only that.
  > - **Resolved in the code, not on the box.** The deployed build still serves the old field with the old
  >   behaviour until the owner deploys. Anything reading `/api/gvbridge/status` on `radio:5004` *today*
  >   still sees `psidtsAgeSeconds`, and it is still lying.

**See:** [`docs/plans/gv-auth-blackout-b2-design.md`](plans/gv-auth-blackout-b2-design.md) (findings
F1-F7, design, owner decisions), [`docs/plans/gv-auth-blackout-b2-plan.md`](plans/gv-auth-blackout-b2-plan.md)
(task breakdown + test plan), [`docs/handoffs/radioconsole-gv-auth-blackout-reply.md`](handoffs/radioconsole-gv-auth-blackout-reply.md)
(cross-repo reply, including the `available` vs `degraded` ask).


## UI says "Ringing" but the bell never rings — INVITE sent to a stale HT801 address (RESOLVED 2026-07-29)

**Status:** ✅ Resolved by the config-binder fix (PR #67, `fix/ht801-invite-target`) and hardened by the
registrar-binding PR (`feat/ht801-registrar-binding`).
**Symptom (was):** An inbound call showed **Ringing** in the Radio.Web UI for the full 60-second window
while the physical rotary phone bell stayed silent. Nothing on screen, in the API, or in the logs said
anything was wrong. `/api/phone/system-status` reported the *correct* HT801 address throughout — that
being the endpoint's behaviour at the time; it was changed on 2026-09-08 (see **Verify** below).
**Impact (was):** Every inbound call. The condition persisted for months undetected because the only
obvious verification signal was the one signal that could not see it.
**Root cause:** `AppConfiguration.Phones` was pre-seeded with one element carrying a hardcoded
`192.168.86.22`, and .NET's `ConfigurationBinder` **appends** to a non-null `List<T>` rather than
replacing it or binding into existing elements. A single-phone config therefore bound to *two* phones —
the compiled default first, the real configuration second — and `PhoneManagerService` registration was
first-wins, so it kept the hardcoded one and discarded the real entry with a single
`Phone default is already registered` warning. Every INVITE went to `.22`. **No edit to any
configuration file could fix it**, because the stale value was in the binary, not the config.
Meanwhile `/api/phone/system-status` read a *different*, last-wins projection (`HT801ConfigService`)
and truthfully reported the configured `.240` — a value that had nothing to do with the INVITE target.
(That projection was removed from the endpoint on 2026-09-08; it now reports the resolved address.)
**Fix:**
- **PR #67 (bell restoration):** `Phones` starts empty; `Program.cs` fails fast via a new
  `AppConfigurationValidator` instead of re-seeding a default phone; `PhoneManagerService` throws on a
  duplicate phone Id instead of silently keeping the first. Regression test `ConfigurationBindingTests`
  exercises the real binder plus the real `PhoneManagerService` and fails on the pre-fix code.
- **PR2 (durable):** no site-specific HT801 address anywhere in source; startup validation also rejects
  a missing/unparseable address or extension; the service **learns** the HT801's address from the source
  address of its SIP REGISTER and prefers that fresh binding over configuration, so a DHCP move
  self-heals within one registration interval; `GVBridge:HT801Ip` deleted so there is exactly one
  address key; new `GET /api/diagnostics/sip-registrations` reports where INVITEs will actually go.
**Verify:** use `INVITE target endpoint: udp:<ip>:5060` in the journal, the
`Learned registrar binding:` line, and `/api/diagnostics/sip-registrations`.
**Updated 2026-09-08 — `/api/phone/system-status` → `ht801IpAddress` is no longer a trap.** For the
duration of this bug it reported the *configured* projection rather than the INVITE target, which is
why it showed the correct address throughout and why this line used to say "do not use it". The
endpoint has since been converged onto the background reachability probe of the **resolved** binding,
so it now agrees with the three signals above. Two caveats keep it second-choice: it is cached (up to
~30 s old) and it is `null` until the first probe completes, where `null` means *unknown*, not
*offline*.
**See:** [`docs/HT801-ADDRESS.md`](HT801-ADDRESS.md) (address locations, change procedure, verification),
[`docs/plans/ht801-address-resolution-and-config-binder-fix.md`](plans/ht801-address-resolution-and-config-binder-fix.md)
(full analysis, including the empirical binder repro).

## Outbound: bridge started at placement → errno-101 blip + early-audio clipping (RESOLVED 2026-06-13)

**Status:** ✅ Resolved by the outbound InCall-ordering PR (`fix/outbound-incall-ordering`).
**Symptom (was):** On an outbound call (rotary → cell), the HT801↔GV audio bridge started and the
state flipped to `InCall` at call *placement* — roughly 6–10s before the far end actually answered.
This streamed audio while the far end was still ringing (potential clipped first syllable) and produced
a one-shot `errno-101` "Network is unreachable" cold-send blip as RTP was pushed before the peer was up.
The genuine answer signal (GV `CallStatusType.Active` → `OnCallAnswered`) was ignored for outbound
because the answer handler guarded on `Ringing` and the call was already `InCall`.
**Note:** This was NOT the 0-RTP / one-way-audio bug — that was fixed separately by the HT801
`Content-Type: application/sdp` fix (PR #35) and the outbound-RTP-port-from-INVITE-SDP fix (PR #34),
both shipped and UAT-verified. This ordering fix is purely about *when* the (working) bridge starts.
**Fix:** `PlaceGvCallAsync` now stays in `Dialing` after sending the GV INVITE (stashing the negotiated
RTP details), and defers both the bridge-start and the `InCall` transition to the GV-answered path.
`HandleCallAnsweredOnCellPhone` gained an outbound-`Dialing` branch that starts the bridge and goes
`InCall` when `Active` arrives — mirroring the proven BT outbound path (`HandleDeviceCallActive`). A
~45s outbound no-answer timeout resets a never-answered call cleanly to `Idle`. The bridge-start is
idempotent (guarded by `_outboundConnectPending`) so a duplicate `Active` (e.g. re-INVITE 200 OK)
starts it at most once.
**Verify in UAT:** Outbound two-way audio still works; no early audio/clipping before answer;
`State changed to: InCall` now logs at answer time (not ~6–10s earlier at placement); the `errno-101`
cold-send blip is gone (or, if present, a single benign blip). Inbound ring + answer unaffected.

## GV BYE not terminating calls (2026-05-25)

**Status:** Workaround in place (SRTP media teardown forces Google timeout)
**Impact:** When hanging up the rotary phone, the cell phone call ends after ~5-10 seconds (Google media timeout) instead of immediately.
**Root cause:** Our SIP BYE over WebSocket is structurally correct but Google's SIP proxy silently ignores it. Likely a dialog state mismatch (From/To tags, Contact URI, or CSeq) that requires proper SIP tracing to diagnose.
**Workaround:** On hangup, `GvSipTransport.HangupAsync()` now closes the DTLS-SRTP `RTCPeerConnection` immediately (sending `close_notify`) before sending the SIP BYE. This stops all media flow, and Google's RTP timeout detection terminates the far-end call within 5-10 seconds.
**Next step:** Set up WebSocket frame capture to compare our BYE with what Google's own web client sends when terminating a call. Compare headers field-by-field.
**PRs:** #25 (initial BYE), #26 (CSeq fix), #27 (diagnostic logging), #28 (session race fix), #29 (force media teardown)

## Idle SIP WebSocket never reconnects → inbound calls stop ringing (RESOLVED 2026-06-13)

**Status:** ✅ Resolved by the keep-alive / auto-reconnect / honest-status PR (`fix/gv-ws-keepalive-reconnect`).
**Symptom (was):** After the line sat idle for ~256s, Google closed the idle SIP-over-WebSocket signaling socket. The receive loop just `break`d with no event and no reconnect, so inbound `INVITE`s never arrived and the rotary phone never rang — yet `/api/gvbridge/status` still reported `sipRegistered:true` on the dead socket.
**Root cause:** No keep-alive was sent (Google advertises `keep=240` in the REGISTER 200-OK Via per RFC 6223), the channel raised no `Closed` event, and `GvSipTransport._registered` was never reset on socket death.
**Fix:**
- **Keep-alive (primary fix):** parse the RFC 6223 `keep=` frequency from the REGISTER 200-OK first Via (default 120s) and send the RFC 5626 §3.5.1 double-CRLF (`\r\n\r\n`) ping every `max(15, keep/2)`s, plus a secondary protocol-level `ClientWebSocket.Options.KeepAliveInterval` (defense-in-depth). A failed ping is treated as a dropped link and triggers reconnect.
- **Auto-reconnect:** the channel now raises a `Closed` event (with a `WasIntentional` flag); the transport runs a single-flight (`Interlocked`-guarded) reconnect loop with capped exponential backoff (1,2,4,8,16,30s) + ±20% jitter, retrying indefinitely until success or disposal, reusing the existing `RegisterAsync` path. The old channel is disposed and its handlers unsubscribed before a new one is created (fixes a latent handler/channel leak).
- **401 auth-recovery:** a real post-Digest 401/403 (or a 401/403 from `sipregisterinfo/get`) now escalates to a browser-less `RotateCookies` refresh of the rotating `__Secure-1PSIDTS/3PSIDTS` (primary), falling back to the CDP `cookies/refresh-from-browser` flow. Plain network drops do NOT trigger cookie work. (RotateCookies request shape is best-effort / unconfirmed — see `docs/research/gv-protocol-notes.md` §3.2 and the `GvCookieRotator` TODO.)
- **Honest status:** `IsRegistered` is now `registered AND socket-connected`; `/api/gvbridge/status` adds `wsConnected`, `lastConnectedAt`, and `psidtsAgeSeconds` (the original four field names are unchanged). ⚠ *Historical record: `psidtsAgeSeconds` was **removed** on 2026-09-08 — see finding **L2** above. `wsConnected` and `lastConnectedAt` are unaffected.*
**Next step:** Confirm the exact `RotateCookies` request shape for the voice.google.com origin via a packet capture and tighten `GvCookieRotator` (fast-follow).
