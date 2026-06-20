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
| 4e. Builder — PR5 inter-service auth gate | ✅ **owner-approved to build + auto-merge on green** | `docs/superpowers/plans/2026-06-20-gv-pr5-inter-service-auth-gate.md` — plan written; middleware is endpoint-agnostic (gates `/api/gvbridge/*`). Owner-approved to build; touches auth/secret handling so the boundary-doc updates land with it. |
| 5. Tester (UAT) | ⬜ deferred — RadioConsole UI lives in RTest repo; no browser UAT for backend PRs | — |
| 6. Polisher | ⬜ deferred — applies to UI work (separate repo) | — |

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

## Builder follow-ups (read-side complete — PRs #54/#56/#57 merged, 151 tests green)
- **Live-capture gate (ADR §11):** parser field positions are PROVISIONAL — fixture-verified,
  not live-verified. Must run the §11 capture on the `radio` box with live cookies before the
  feature is trusted end-to-end. Quarantined behind seams (one-file corrections).
- **Deferred to Planner — PR1 review HIGH-2:** `IGvAuthenticatedClientProvider.GetAuthenticatedClient()`
  gates on `IsAvailable`; a successful rung-1 cookie rotation leaves `IsAvailable=false` until the
  next health-check tick, so the seam can return `null` despite a valid client during a recovery
  window. No PR1-3 consumer harmed today (clients degrade to `Succeeded=false`); reconcile before
  live use under auth blips. Touches the auth-recovery ladder → out of read-side scope.

## Open decisions for owner (on return) — see ADR §12
> **PR4 + PR5 are now OWNER-APPROVED to build + auto-merge on green** (rows 4d/4e). PR4 ships DARK behind
> a server-side `EnableSmsSend` flag (default FALSE), so the irreversible GV write merges safely; the
> owner's remaining go-live action is flipping the flag + the ADR §11 first-real-send live capture.
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
