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
| 1. Architect research spike (ADR) | ✅ done | `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` |
| 2. Designer UX spec | ⬜ unblocked (contract in ADR §6.1 is stable) | — |
| 2. Architect API contract | ✅ folded into the spike ADR (§6) | ADR §6 |
| 3. Planner (per PR) | ⬜ | docs/BUILDER_QUEUE.md |
| 4. Builder (per PR) | ⬜ | PRs |
| 5. Tester (UAT) | ⬜ | — |
| 6. Polisher | ⬜ | — |

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

## Open decisions for owner (on return) — see ADR §12
1. SMS-send autonomy: ship behind auth-gate + rate-limit? per-send confirm in UI for v1?
2. Inter-service `X-RotaryPhone-Auth` gate now (default-off, LAN-safe)?
3. Voicemail cache retention (7 days / 200 MB proposed)?
4. Fund the timeboxed signaler retest (PR6), or ship poll-only?
