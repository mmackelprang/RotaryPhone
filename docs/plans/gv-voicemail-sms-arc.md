# Arc: Voicemail + Texts on RadioConsole (via GV API)

**Mode:** Autonomous (Coordinator-orchestrated). Owner reviewing on return.
**Started:** 2026-06-20

## Goal
Surface Google Voice **voicemail** (list, audio playback, transcripts) and **texts**
(read incoming threads + send/reply) on the **RadioConsole** UI, fed by a new
voicemail/SMS API exposed by **RotaryPhone** (which owns the GV integration).

## Scope (locked with owner)
- [x] Voicemail: list + listen
- [x] Voicemail transcripts
- [x] Read incoming texts
- [x] Send / reply to texts
- [x] Surface: RadioConsole consumes a RotaryPhone-exposed API (cross-service contract)

## Tier & pipeline
**Complex** → Research spike (Architect) → Designer + Architect contract (parallel)
→ Planner (per PR) → Builder (per PR) → Tester → Polisher → PRs.

## Autonomy / merge rules (from CLAUDE.md auto-merge policy)
- Auto-merge: fully-green, non-sensitive PRs (RadioConsole UI, read-only display, contract endpoints).
- Hold for owner review: any PR touching GV auth/cookie/secret handling, or irreversible/cross-service-breaking changes.
- Hard-stop: red gate that can't be cleared, or a capability found infeasible.

## Known risks
- **Read-incoming-texts** depends on the signaler subscription format that currently
  returns `INVALID_ARGUMENT`. Spike must determine: crack it, or fall back to threads-polling.
- **Voicemail endpoints** not yet reverse-engineered in this codebase.
- Auth foundation (12-cookie SAPISIDHASH + rotation) already live — extend, don't rebuild.

## Phase log
| Phase | Status | Artifact |
|---|---|---|
| 1. Architect research spike (ADR) | ✅ merged | ADR — PR #51 → `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` |
| 2. Designer UX spec + handoff | ✅ merged | PR #52 — `docs/design-handoffs/gv-voicemail-sms-radioconsole/` + spec |
| 2. Architect API contract | ✅ folded into the spike ADR (§6) | ADR §6 / §6.1 DTOs |
| 3. Planner (read-side PR1–3) | ✅ merged | PR #53 — `docs/superpowers/plans/2026-06-20-gv-pr{1,2,3}-*.md` |
| 4a. Builder — PR1 read clients | ✅ merged | PR #54 — parser seam + GvThreadClient/GvVoicemailClient (list); parser provisional pending ADR §11 live capture |
| 4b. Builder — PR2 voicemail REST + audio proxy | ✅ merged | PR #56 — voicemail list/{id}/{id}/audio + GvVoicemailCache (proxy+disk cache, range stream); media-fetch shape provisional pending ADR §11 step 3 |
| 4c. Builder — PR3 SMS read + poll push | ✅ merged | PR #57 — GvSmsClient read + GvThreadPoller (adaptive poll + high-water diff) + SmsReceived/VoicemailReceived push over RotaryHub; field positions provisional pending ADR §11 steps 1 & 5 |
| 4d. Builder — PR4 SMS send | ✅ merged | PR #60 — `POST /api/gvbridge/sms/send` (GvSmsClient.SendAsync `api2thread/sendsms`, E.164 normalize, 429 limiter, honest taxonomy 409/400/429/502/504, no auto-retry) + PR3-side `OnSmsSent` outbound surface w/ shared `csid:` id. **Ships DARK behind `EnableSmsSend` (default FALSE)** — merge changes no behavior; 185 GVBridge tests green; review found no HIGH (2 MEDIUM fixed). **Fixture-verified only; no live send** — `ISmsThreadIdResolver` `t.+<E164>` stays UNVERIFIED; first real send + payload field positions pending ADR §11 step 4 (owner flips the flag + on-box live capture). |
| 4e. Builder — PR5 inter-service auth gate | ✅ merged | PR #61 — `X-RotaryPhone-Auth` gate: constant-time `InterServiceAuthValidator`, `GvBridgeAuthMiddleware` over all `/api/gvbridge/*` (401; exempts only the exact `/event` segment), `HubAuthFilter` over `/hub` (header or `access_token`; abort). **`InterServiceAuthKey` defaults `""` = DISABLED** — merge is byte-identical to today (no 401 storm). New `RotaryPhoneController.Server.Tests` project (21 tests); review found no HIGH (2 MEDIUM fixed: segment-anchored `/event` exemption + hub default-off pass-through). Boundary-doc + handoff updated. **ENABLING requires coordinated config on BOTH RotaryPhone and RadioConsole** (owner action); on-box auth-gate smoke = ADR §11 step 7, not done here. |
| 5. Tester (UAT) | ⬜ deferred — RadioConsole UI lives in RTest repo; no browser UAT for backend PRs | — |
| 6. Polisher | ⬜ deferred — applies to UI work (separate repo) | — |
| FF. GV mark-read (durable read-state) — Path A | ✅ **merged (Path A — ships DARK behind `EnableMarkRead`=false)** | **PR #64** — two mark routes (`POST /api/gvbridge/voicemail/{id}/read`, `POST /api/gvbridge/sms/threads/{threadId}/read`) returning the frozen `VoicemailItemDto`/`SmsThreadDto`; `GvReadStateClient.MarkReadAsync` → GV `api2thread/updateread` behind the UNVERIFIED `IUpdateReadPayloadBuilder` seam (positions/grain pending ADR §11 step 8); status taxonomy 200 idempotent / 404 / 502 / 409 `markread_disabled` (flag off, checked FIRST, no GV call) / 400 `unread_unsupported`; on-mark `ReadStateChanged` (path a) via the existing `IGvMessageEventSource` → `GvMessagePushBridge` → `RotaryHub` pattern; auth auto-covered by the PR5 prefix gate (proven by test). **Fixture-verified only; 232 tests green (GVBridge 207 + Server 25); review found no HIGH (5 findings fixed). Nothing mutates GV until the owner flips `EnableMarkRead` + runs ADR §11 step 8.** Plan: `docs/superpowers/plans/2026-06-20-gv-markread-readstate.md`; decision record: `docs/architecture/decisions/2026-06-20-gv-markread-readstate-contract.md`; reply: `docs/handoffs/radioconsole-gv-markread-reply.md`. **Path B (Task 9 poller-flip → live "hear-on-phone clears the kiosk badge") still PENDING — fast-follow, NOT in this PR.** |

| **B2. GV auth blackout** (PSIDTS staleness → ~9-min dead window per ~20-min cycle) | ✅ **SHIPPED — PR #72 merged 2026-08-01 after live on-box UAT passed (6/7 acceptance criteria by measurement, 1 partial, 0 fail)** | Spec: `docs/plans/gv-auth-blackout-b2-design.md` (§8 records all five decisions); plan: `docs/plans/gv-auth-blackout-b2-plan.md`. From RadioConsole handoff `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md` §B2 (271 `Unauthorized`/day; 11 of 11 of their 502s inside a dead window). **Owner decisions (2026-08-01, spec §8):** refresh interval **8 min**; the box-side cron (`/opt/rotary-phone/refresh-gv-cookies.sh`, every 20 min — F3 now **confirmed**, not hypothesis) **stays running through UAT**, so the in-process refresh must be **idempotent** and retiring the cron is a separate box-side change; `available` stays **true** during a blackout (ship `degraded`/`cookiesValid:false`/`authBlackout` — RadioConsole must bind their banner to those); the stale keepalive plan doc is **committed with a SHIPPED banner** (done, PR #71); and the **F6/F7 re-entrancy fix STAYS IN SCOPE** of the implementation PR (owner chose this over the spec's split-it-out option), making acceptance criterion #6 a **hard gate**. **7 root-cause findings**, 2 of them beyond the handoff: `CookieRefreshIntervalMinutes` has **zero readers** (dead knob, F1); the ~20-min cadence is an **external** caller POSTing `cookies/refresh-from-browser`, not in this repo (F3); the reactive-401 ladder exists but only on the **SIP** leg (F4); `_areCookiesValid` is a 30-min-stale **probe** of a *different* endpoint (F5); **`ActivateAsync` is not re-entrant** — every external refresh leaks a timer + a whole `GvSipTransport`, ~72/day (F6); and consequently **the 30-min watchdog is starved and never fires** — the only timed path into cookie recovery is dead on the box (F7). Plan = 6 tasks + a bounded on-box diagnostic (Task 0). **Deps:** Task 3 before Task 4; Task 0 before Task 4 is enabled. **B1 (`%2F` decode, `fix/gv-threadid-decode`) is a parallel PR — UAT of B2 must use non-group threads until it lands.** Closes open decision #6 below. **IMPLEMENTED 2026-08-01 (Builder):** 5 commits — awaitable/outcome-reporting recovery ladder (shared `Task<bool>` single-flight + failure-only 60 s cooldown + `SetAvailable(true)`); 401 read-path recover-and-retry-once with per-attempt client re-resolution (write paths signal, never replay — ADR §4.2 #4); `ActivateAsync` re-entrancy teardown with an `_activeCallId` guard; real proactive PSIDTS timer (`CookieRefreshIntervalMinutes` 5→8, `0` = kill switch) + new `AuthRecoveryFailureCooldownSeconds`; honest status (`authBlackout`/`lastApiSuccessAt`/`lastApiAuthFailureAt`, `cookiesValid` = probe AND NOT blackout). **GVBridge 365 tests green (334→+31), Server.Tests 25 green**; all four status-contract tests pass unchanged. **Plan correction found in build:** `Degraded` read the raw `_areCookiesValid` **field**, not the property — the spec asserted it needed no change; left alone, acceptance criterion 3 would have failed. Fixed. **NOT verified locally (needs the box):** acceptance criteria 4 (window-blind 30-min soak, 0×502), 5 (`Unauthorized`/hour → ~0), and the live half of 1/3/6 — plus whether rung 1 (`RotateCookies`, shape still UNVERIFIED) actually rotates live or silently no-ops, in which case the proactive cadence is inert and the reactive path carries the whole fix. Task 0's on-box diagnostic and the deploy were **not run by Builder — no SSH credentials in that session**; handed to Tester. Reply to RadioConsole: `docs/handoffs/radioconsole-gv-auth-blackout-reply.md` (flags the `degraded`-not-`available` ask, which **needs their agreement**). **UAT PASSED + MERGED 2026-08-01:** live on-box run — **932 requests / ~88 min, zero 502s, zero non-200s** (pre-fix baseline 15/49 = 31% 502s); `api2thread/list returned Unauthorized` **33/hr → 0/hr**; RadioConsole-side `Failed to get GV SMS thread` **0** over 90 min. AC-1 proactive cadence measured at 7 ticks exactly 8m00s apart with the `0` kill switch confirmed; AC-2 full recovery ladder captured live (rung 1 401 → rung 2 fail → rung 3 CDP → replay 200 in **920 ms**, caller got 200 not 502); AC-6 stronger than asked — **one** transport across **6** re-activations (`lastConnectedAt` a single distinct value) and the health timer ticking twice exactly 30 min apart on its *original* anchor, which is **F7 fixed by measurement**. `RotateCookies` is **not inert** (rung 1's UNVERIFIED-shape risk resolved): it rotates for real proactively, returns 401 reactively where CDP carries the recovery. **Shipped shape differs from the plan on one point — conditional transport reuse, not unconditional teardown** (owner instruction: remove the 20-min SIP churn, don't monitor it); plan + design carry AMENDED/SUPERSEDED banners and the UAT step-3 pass signal is **inverted** (a teardown line while SIP is registered is now a **failure**). **Carried forward, see `docs/KNOWN-ISSUES.md`:** AC-3 **PARTIAL** (`authBlackout:true` never observed live — recovery is faster than any practical sampling rate; derived flags during a *sustained* blackout remain unit-test-only, and RadioConsole must be told the flag may be true for **under a second**); **inbound call ringing NOT TESTED** (no way to originate a call; Task 3 touches `_sipTransport` teardown and reuse makes teardown rare, so a regression would be intermittent — owner merged knowingly); **M1** the cron's justification has expired (retire it as a follow-up — 429s on 29% of proactive ticks); **L3** the publish output clobbers the box's `appsettings.Production.json` including `BluetoothAdapter: hci1` (**crosses the Radio Console audio boundary**, recurs every deploy). |

> **Note — no `BUILDER_QUEUE.md` in this project.** Builder work is driven directly from the
> plan docs above. The earlier tracker reference to a queue file was wrong; corrected here.

> **Infra note (2026-06-20):** Designer + Planner subagents were interrupted by an auth
> (401) lapse at their commit step; their content was verified complete and landed via the
> recovery PRs #52/#53 above. No work lost.

## Spike findings (2026-06-20)
- **Feasibility:** voicemail list/listen/transcript = Medium; SMS-read = Med-High **via polling**;
  SMS-send = Med-High. Nothing infeasible — dominant risk is exact GV field positions, resolvable
  with one live capture (ADR §11).
- **Auth foundation is done.** 12-cookie SAPISIDHASH + PSIDTS rotation (`GvCookieRotator`) is live;
  new clients are thin wrappers over the shared authenticated `HttpClient`.
- **Correction:** the 2026-03-27 migration spec's `GvSmsClient`/`GvThreadClient` were **never built**;
  this arc creates them.
- **SMS-read = POLL, not signaler.** Threads-polling fallback chosen (High confidence). Signaler
  stays `INVALID_ARGUMENT`; routed around, kept as an optional later optimization (PR6).
- **Push to RadioConsole reuses the existing SignalR hub** (`SmsReceived`/`VoicemailReceived`),
  identical in shape to `IncomingCall` — so poll-vs-signaler is invisible to RadioConsole.
- **Audio = RotaryPhone proxy+cache**, never a Google redirect (RadioConsole has no cookies).

## Cross-repo: RadioConsole UI (parallel track)
- Handoff prompt for the RadioConsole team/agent: `docs/handoffs/radioconsole-gv-voicemail-sms-ui-handoff.md`.
  Self-contained as-built contract (routes + DTOs + SignalR events + audio/auth posture) + UX
  to build. Read experience buildable now; SMS-send UI to be feature-flagged until PR4 ships.
- This unblocks the UI from being designed/built in parallel while RotaryPhone finishes the
  API side (live capture, PR4 send, PR5 auth gate).

### GV mark-read / durable read-state (fast-follow — contract ratified, build HELD)
- RadioConsole requested a **durable mark-read** capability (their UI-local read-state was declined by
  the owner). Request: `docs/prompts/radioconsole-gv-markread-readstate-request.md`.
- **Contract RATIFIED** (Architect): **persistence = GV write-through** (Google is the single source of
  truth — no local store; satisfies "hear-on-phone clears the kiosk badge"). Two routes
  `POST /api/gvbridge/voicemail/{id}/read` + `POST /api/gvbridge/sms/threads/{threadId}/read`
  (`{ "isRead": bool }` → updated `VoicemailItemDto`/`SmsThreadDto`; 200 idempotent / 404 / 502). Unified
  `ReadStateChanged` event on `/hub` — **routes ship first** (on-mark broadcast); the poller-detected
  externally-originated read flip is a **fast-follow** (heavier — needs new per-item read-flag diff state).
  Mark-unread best-effort; delete deferred; auth auto-covered by the PR5 prefix gate.
- Decision record: `docs/architecture/decisions/2026-06-20-gv-markread-readstate-contract.md`.
  Reply to RadioConsole: `docs/handoffs/radioconsole-gv-markread-reply.md`. Boundary-doc Integration
  Points + Change Log updated (API only; no BT/audio change).
- **Build is HELD by the owner.** When funded: routes + on-mark event first (one PR, `EnableMarkRead`
  default-off), poller-diff event as a fast-follow. First real `updateread` pending the ADR §11 live
  capture (new step 8 added: capture the `updateread` wire format, per-thread vs per-message grain, unread
  support, response-echo).
- **Plan QUEUED & build-ready (Planner, 2026-06-20):**
  `docs/superpowers/plans/2026-06-20-gv-markread-readstate.md` — bite-sized TDD tasks against the real
  as-built types (`GvReadStateClient.MarkReadAsync` → `api2thread/updateread` behind the UNVERIFIED
  `IUpdateReadPayloadBuilder` seam; two routes returning the frozen `VoicemailItemDto`/`SmsThreadDto`;
  `EnableMarkRead` default-FALSE; path-a `ReadStateChanged` via the existing `IGvMessageEventSource` →
  `GvMessagePushBridge` → `RotaryHub` pattern; auth-gate coverage test). Carries a prominent
  **🔒 OWNER-HOLD** banner — the plan is queued, the **build is still HELD** (GV account write). **Path b**
  (poller-detected external read-flip → live "hear-on-phone clears the kiosk badge") is scoped in the plan
  as a clearly-separated fast-follow (Task 9), NOT built in path a.

## Builder follow-ups (read-side complete — PRs #54/#56/#57 merged, 151 tests green)
- **Live-capture gate (ADR §11):** parser field positions are PROVISIONAL — fixture-verified,
  not live-verified. Must run the §11 capture on the `radio` box with live cookies before the
  feature is trusted end-to-end. Quarantined behind seams (one-file corrections).
- **Deferred to Planner — PR1 review HIGH-2:** `IGvAuthenticatedClientProvider.GetAuthenticatedClient()`
  gates on `IsAvailable`; a successful rung-1 cookie rotation leaves `IsAvailable=false` until the
  next health-check tick, so the seam can return `null` despite a valid client during a recovery
  window. No PR1-3 consumer harmed today (clients degrade to `Succeeded=false`); reconcile before
  live use under auth blips. Touches the auth-recovery ladder → out of read-side scope.
  → ~~**SCHEDULED into B2, Task 1**~~ → ✅ **CLOSED 2026-08-01** by the B2 implementation PR: every successful
  cookie-recovery rung now calls `SetAvailable(true)`, so `GetAuthenticatedClient()` stops returning `null`
  during the recovery window. Locked in by `TryRecoverAuthAsync_Success_SetsAvailable`.

### Carried out of B2 (PR #72) — open at merge, 2026-08-01

Full detail in `docs/KNOWN-ISSUES.md`; this is the tracker view.

- ⛔ **Verify inbound call ringing on the deployed B2 build.** Test-plan step 8 never ran — the tester
  could not originate a call to the GV number. Task 3 touches `_sipTransport` teardown, and because
  conditional reuse makes teardown **rare**, a ringing regression would be **intermittent and hard to
  trace** (it would only appear on the absent/unregistered-transport path: restart, dropped WebSocket).
  Owner merged knowingly. **This is an open verification item, not a known defect** — one manual ring
  plus two-way audio closes it.
- ⚠️ **AC-3 remains PARTIAL — `authBlackout:true` never observed live.** Zero `true` samples across 411
  polls, because the one live blackout lasted **920 ms** — faster than any practical sampling rate. The
  *recording* mechanism is proven live (`lastApiAuthFailureAt` latched from a real 401; `available` never
  went false); the *derived* trio during a **sustained** blackout is unit-test-only. **RadioConsole has
  been told the flag may be true for under a second** — a banner bound naively to it will effectively
  never show; they should latch it or use the timestamps. In the boundary doc + handoff reply.
- 🔁 **M1 — retire the box-side cron; its justification has expired.** Resolved decision 2 kept
  `*/20 * * * * /opt/rotary-phone/refresh-gv-cookies.sh` "until the in-process refresh is proven."
  **It is now proven** (33/hr → 0 `Unauthorized`; 932 requests, zero 502s; measured 8-minute cadence),
  and the accepted double-refresh cost has **materialized as 429s on 29% of proactive ticks** (2 of 7).
  Degrades gracefully, so not urgent. **Deliberately NOT done in PR #72 — it is a box-side change and
  needs its own rollback story.** Retire (or lengthen) the cron, then re-measure the 429 rate.
- 🔴 **L3 — deploy clobbers the box's `appsettings.Production.json`, including `BluetoothAdapter: hci1`.**
  The file ships inside the publish artifact and `Deploy-ToLinux.ps1`'s tar-pipe fallback loses it when
  `tar --unlink-first` exits 2 and `set -e` skips the restore (the rsync path is safe). **This crosses
  the Radio Console audio boundary** — a silent BT-config change breaks the *other* service's audio — and
  **it recurs on every deploy**, which makes it the most dangerous item on this list. Fix proposed
  (exclude from the tar + drop from publish output + make the restore unconditional); boundary doc has
  the mandatory manual backup-and-verify until then.
- ℹ️ **L1/L2, low.** Path A (`re-activation is a no-op`) is **dead code in production** — 0 of 6
  re-activations took it, since every cron fire carries changed credentials. And `psidtsAgeSeconds`
  resets on activation regardless of the cookies' true issue time (pre-existing, but B2 hits that path
  more often, so the field is a weaker staleness signal than pre-fix traces implied).

## Open decisions for owner (on return) — see ADR §12
> **PR4 + PR5 SHIPPED + MERGED** (rows 4d/4e — PR #60, PR #61) on green gates, both safe-by-default
> (PR4 `EnableSmsSend`=false, PR5 `InterServiceAuthKey`=""), so neither merge changed live behavior.
> **Owner go-live actions remain (NOT done by Builder — no live cookies / dev box can't send):**
> (a) flip `EnableSmsSend=true` on `radio` + run the ADR §11 first-real-send capture (de-UNVERIFY
> `SmsThreadIdResolver` `t.+<E164>` / send-payload field positions); (b) if non-LAN exposure is wanted,
> set the SAME `InterServiceAuthKey` on BOTH RotaryPhone and RadioConsole (coordinated config) + run the
> ADR §11 step 7 auth-gate smoke. Everything is fixture-verified only to here.
1. SMS-send autonomy: ship behind auth-gate + rate-limit? per-send confirm in UI for v1?
   → **Resolved:** ships dark behind `EnableSmsSend` (default off) + rate-limited 5/10s; per-send confirm
   is a RadioConsole-side flag, not a RotaryPhone change. See `docs/superpowers/plans/2026-06-20-gv-pr4-sms-send.md` §"ADR §12".
2. Inter-service `X-RotaryPhone-Auth` gate now (default-off, LAN-safe)?
   → **Planned default-OFF** (zero behavior change when unset; enabling requires coordinated config on
   BOTH services). See `docs/superpowers/plans/2026-06-20-gv-pr5-inter-service-auth-gate.md` §"ADR §12".
3. Voicemail cache retention (7 days / 200 MB proposed)?
4. Fund the timeboxed signaler retest (PR6), or ship poll-only?
5. Run the ADR §11 live capture on the `radio` box to de-provisionalize the parsers?
6. Reconcile the PR1 HIGH-2 auth-recovery window (Planner follow-up) — schedule it?
   → ✅ **RESOLVED 2026-08-01** — implemented in the B2 PR (Task 1), not merely scheduled. A
   successful cookie-recovery rung now calls `SetAvailable(true)`, so `GetAuthenticatedClient()` stops
   returning `null` during the recovery window. B2 is the right home because its reactive-401 retry
   operates in exactly that window and would be silently defeated by the gate.
