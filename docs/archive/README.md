# Archive

This directory holds RotaryPhone's development history: project plans, design specs and
implementation plans, agent prompts and handoffs, spikes, brainstorm mockups, and closed exchanges
with Radio Console.

**These files are not maintained.** They are kept as written, and many describe designs that were later
superseded: the Raspberry Pi and Windows NUC hosts, a real Bluetooth HFP path, a Chrome-extension
bridge. For how the system works today, use the documentation outside `docs/archive/`, and the decision
records in `docs/architecture/decisions/`.

Files were moved here with their names unchanged. The **Original path** column lets you resolve an old
link, including links from the Radio Console repo, by filename. Links inside these files were repointed
when they moved; plain-text path mentions inside them were left as originally written. Dates come from
the filename or the document's own date line; "added" means the date the file was first committed.

Not archived, on purpose: `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` (the live boundary contract)
and the Radio Console exchanges in `docs/prompts/` and `docs/handoffs/` that are still open.

## project-history/

Early project plans and summaries from the Raspberry Pi and Windows NUC eras, and the unadopted "Dialtone" brand guide. The brand assets themselves are still in `branding/`.

| File | Title | Date | Original path |
|---|---|---|---|
| [PROJECT_PLAN.md](project-history/PROJECT_PLAN.md) | Project Plan: Rotary Phone to Raspberry Pi Audio Interface | added 2025-11-03 | `PROJECT_PLAN.md` |
| [2026_PROJECT_PLAN.md](project-history/2026_PROJECT_PLAN.md) | 2026 Project Plan: Windows NUC Migration & TypeScript UI | added 2026-01-08 | `2026_PROJECT_PLAN.md` |
| [IMPLEMENTATION_SUMMARY.md](project-history/IMPLEMENTATION_SUMMARY.md) | Implementation Summary | added 2025-11-05 | `IMPLEMENTATION_SUMMARY.md` |
| [BRANDING.md](project-history/BRANDING.md) | Dialtone — brand guide | added 2026-08-08 | `branding/BRANDING.md` |

## bluetooth/

Bluetooth HFP/PBAP designs and plans from March 2026. Production now runs a mock Bluetooth adapter; voice goes over Google Voice.

| File | Title | Date | Original path |
|---|---|---|---|
| [HFP_IMPLEMENTATION_GUIDE.md](bluetooth/HFP_IMPLEMENTATION_GUIDE.md) | Bluetooth HFP Implementation Guide (Linux/BlueZ) | added 2025-11-05 | `HFP_IMPLEMENTATION_GUIDE.md` |
| [2026-03-12-bt-hfp-call-detection.md](bluetooth/2026-03-12-bt-hfp-call-detection.md) | RotaryPhone Server: BT HFP Call Detection + Hook Fix | 2026-03-12 | `docs/prompts/2026-03-12-bt-hfp-call-detection.md` |
| [2026-03-12-pbap-rotaryphone-callerresolved.md](bluetooth/2026-03-12-pbap-rotaryphone-callerresolved.md) | PBAP CallerResolved — RotaryPhone Implementation Plan | 2026-03-12 | `docs/superpowers/plans/2026-03-12-pbap-rotaryphone-callerresolved.md` |
| [2026-03-13-rotaryphone-standalone-architecture.md](bluetooth/2026-03-13-rotaryphone-standalone-architecture.md) | RotaryPhone Standalone Architecture Implementation Plan | 2026-03-13 | `docs/superpowers/plans/2026-03-13-rotaryphone-standalone-architecture.md` |
| [2026-03-12-bt-hfp-call-detection-design.md](bluetooth/2026-03-12-bt-hfp-call-detection-design.md) | BT HFP Call Detection + SIP REGISTER Fix | 2026-03-12 | `docs/superpowers/specs/2026-03-12-bt-hfp-call-detection-design.md` |
| [2026-03-12-pbap-contact-sync-design.md](bluetooth/2026-03-12-pbap-contact-sync-design.md) | PBAP Contact Sync — Design Spec | 2026-03-12 | `docs/superpowers/specs/2026-03-12-pbap-contact-sync-design.md` |
| [2026-03-13-rotaryphone-standalone-architecture-design.md](bluetooth/2026-03-13-rotaryphone-standalone-architecture-design.md) | RotaryPhone Standalone Architecture Design | 2026-03-13 | `docs/superpowers/specs/2026-03-13-rotaryphone-standalone-architecture-design.md` |

## brainstorm/

HTML mockups produced during a March 2026 brainstorming session.

| File | Title | Date | Original path |
|---|---|---|---|
| [architecture-overview.html](brainstorm/architecture-overview.html) | GV Audio Bridge — Data Flow Architecture | added 2026-03-23 | `.superpowers/brainstorm/1337-1774198075/architecture-overview.html` |
| [diagnostics-ui.html](brainstorm/diagnostics-ui.html) | SIP & Call Diagnostics Dashboard | added 2026-03-23 | `.superpowers/brainstorm/1337-1774198075/diagnostics-ui.html` |

## gv-call-path/

Google Voice call-path work: the browser-bridge PRD, the GV trunk, the move to the direct GV API, SIP-over-WebSocket and DTLS-SRTP, and the resilience fixes that followed.

| File | Title | Date | Original path |
|---|---|---|---|
| [PRD-GVBrowserBridge.md](gv-call-path/PRD-GVBrowserBridge.md) | PRD: Google Voice Browser Bridge | added 2026-03-15 | `docs/PRD-GVBrowserBridge.md` |
| [TODO-caller-cancel-deferred-answer.md](gv-call-path/TODO-caller-cancel-deferred-answer.md) | TODO — Caller-cancel-keeps-ringing (inbound) — future fix via deferred answer | added 2026-06-13 | `docs/TODO-caller-cancel-deferred-answer.md` |
| [TODO-remaining-work.md](gv-call-path/TODO-remaining-work.md) | Remaining Work — RotaryPhone | added 2026-03-14 | `docs/TODO-remaining-work.md` |
| [remaining-work.md](gv-call-path/remaining-work.md) | GV API — Remaining Work | added 2026-03-28 | `docs/api-research/remaining-work.md` |
| [google-voice-trunk.md](gv-call-path/google-voice-trunk.md) | PRD: Google Voice Trunk Integration | added 2026-03-14 | `docs/prompts/google-voice-trunk.md` |
| [followup-incall-ordering-and-rotatecookies.md](gv-call-path/followup-incall-ordering-and-rotatecookies.md) | Plan — Follow-ups: Outbound `InCall` ordering + RotateCookies request shape | 2026-06-13 | `docs/plans/followup-incall-ordering-and-rotatecookies.md` |
| [gv-registration-resilience.md](gv-call-path/gv-registration-resilience.md) | GV Registration Resilience — plan | added 2026-06-19 | `docs/plans/gv-registration-resilience.md` |
| [gv-websocket-keepalive-reconnect.md](gv-call-path/gv-websocket-keepalive-reconnect.md) | Plan: GV SIP-over-WebSocket Keep-Alive + Auto-Reconnect + Honest Status | added 2026-08-01 | `docs/plans/gv-websocket-keepalive-reconnect.md` |
| [2026-03-14-google-voice-trunk.md](gv-call-path/2026-03-14-google-voice-trunk.md) | Google Voice Trunk Implementation Plan | 2026-03-14 | `docs/superpowers/plans/2026-03-14-google-voice-trunk.md` |
| [2026-03-15-gvbridge-phase-a-core-interfaces.md](gv-call-path/2026-03-15-gvbridge-phase-a-core-interfaces.md) | GV Bridge Phase A: Core Interfaces + CallAdapter Registry | 2026-03-15 | `docs/superpowers/plans/2026-03-15-gvbridge-phase-a-core-interfaces.md` |
| [2026-03-22-gv-audio-bridge-and-sip-diagnostics.md](gv-call-path/2026-03-22-gv-audio-bridge-and-sip-diagnostics.md) | GV Audio Bridge & SIP Diagnostics Implementation Plan | 2026-03-22 | `docs/superpowers/plans/2026-03-22-gv-audio-bridge-and-sip-diagnostics.md` |
| [2026-03-27-gv-api-migration.md](gv-call-path/2026-03-27-gv-api-migration.md) | GV API Migration Implementation Plan | 2026-03-27 | `docs/superpowers/plans/2026-03-27-gv-api-migration.md` |
| [2026-03-30-sip-wss-dtls-srtp-integration.md](gv-call-path/2026-03-30-sip-wss-dtls-srtp-integration.md) | SIP-over-WebSocket + DTLS-SRTP Integration Plan | 2026-03-30 | `docs/superpowers/plans/2026-03-30-sip-wss-dtls-srtp-integration.md` |
| [2026-03-22-gv-audio-bridge-and-sip-diagnostics-design.md](gv-call-path/2026-03-22-gv-audio-bridge-and-sip-diagnostics-design.md) | GV Audio Bridge & SIP Diagnostics Design | 2026-03-22 | `docs/superpowers/specs/2026-03-22-gv-audio-bridge-and-sip-diagnostics-design.md` |
| [2026-03-27-gv-api-migration-design.md](gv-call-path/2026-03-27-gv-api-migration-design.md) | GV API Migration Design — CDP/Extension to Direct HTTP API | 2026-03-27 | `docs/superpowers/specs/2026-03-27-gv-api-migration-design.md` |
| [2026-03-30-sip-wss-dtls-srtp-integration-design.md](gv-call-path/2026-03-30-sip-wss-dtls-srtp-integration-design.md) | SIP-over-WebSocket + DTLS-SRTP Audio Integration | 2026-03-30 | `docs/superpowers/specs/2026-03-30-sip-wss-dtls-srtp-integration-design.md` |

## gv-messaging/

The voicemail and SMS arc for Radio Console (PRs #54, #56, #57, #60, #61, #64), its per-PR plans and design spec.

| File | Title | Date | Original path |
|---|---|---|---|
| [gv-voicemail-sms-arc.md](gv-messaging/gv-voicemail-sms-arc.md) | Arc: Voicemail + Texts on RadioConsole (via GV API) | added 2026-06-20 | `docs/plans/gv-voicemail-sms-arc.md` |
| [2026-06-20-gv-markread-readstate.md](gv-messaging/2026-06-20-gv-markread-readstate.md) | Plan — `feat(gv): mark-read / durable read-state` | 2026-06-20 | `docs/superpowers/plans/2026-06-20-gv-markread-readstate.md` |
| [2026-06-20-gv-pr1-thread-voicemail-read-clients.md](gv-messaging/2026-06-20-gv-pr1-thread-voicemail-read-clients.md) | PR1 Plan — `feat(gv): thread + voicemail read clients` | 2026-06-20 | `docs/superpowers/plans/2026-06-20-gv-pr1-thread-voicemail-read-clients.md` |
| [2026-06-20-gv-pr2-voicemail-rest-audio-proxy.md](gv-messaging/2026-06-20-gv-pr2-voicemail-rest-audio-proxy.md) | PR2 Plan — `feat(gv): voicemail REST + audio proxy/cache` | 2026-06-20 | `docs/superpowers/plans/2026-06-20-gv-pr2-voicemail-rest-audio-proxy.md` |
| [2026-06-20-gv-pr3-sms-read-thread-polling-push.md](gv-messaging/2026-06-20-gv-pr3-sms-read-thread-polling-push.md) | PR3 Plan — `feat(gv): SMS read + thread polling push` | 2026-06-20 | `docs/superpowers/plans/2026-06-20-gv-pr3-sms-read-thread-polling-push.md` |
| [2026-06-20-gv-pr4-sms-send.md](gv-messaging/2026-06-20-gv-pr4-sms-send.md) | PR4 Plan — `feat(gv): SMS send` | 2026-06-20 | `docs/superpowers/plans/2026-06-20-gv-pr4-sms-send.md` |
| [2026-06-20-gv-pr5-inter-service-auth-gate.md](gv-messaging/2026-06-20-gv-pr5-inter-service-auth-gate.md) | PR5 Plan — `feat(gv): inter-service auth gate (X-RotaryPhone-Auth)` | 2026-06-20 | `docs/superpowers/plans/2026-06-20-gv-pr5-inter-service-auth-gate.md` |
| [2026-06-20-gv-voicemail-sms-radioconsole-design.md](gv-messaging/2026-06-20-gv-voicemail-sms-radioconsole-design.md) | Design Spec: GV Voicemail + Texts on RadioConsole | 2026-06-20 | `docs/superpowers/specs/2026-06-20-gv-voicemail-sms-radioconsole-design.md` |

## gv-messaging/design-handoff/

The RotaryPhone-side UI design exploration for voicemail and texts. Radio Console built its own design instead.

| File | Title | Date | Original path |
|---|---|---|---|
| [overview.md](gv-messaging/design-handoff/overview.md) | Design Handoff: Google Voice Voicemail + Texts on RadioConsole | 2026-06-20 | `docs/design-handoffs/gv-voicemail-sms-radioconsole/overview.md` |
| [interactions.md](gv-messaging/design-handoff/interactions.md) | Interactions & States | added 2026-06-20 | `docs/design-handoffs/gv-voicemail-sms-radioconsole/interactions.md` |
| [copy.md](gv-messaging/design-handoff/copy.md) | Copy & Microcopy | added 2026-06-20 | `docs/design-handoffs/gv-voicemail-sms-radioconsole/copy.md` |
| [tokens.md](gv-messaging/design-handoff/tokens.md) | Tokens | added 2026-06-20 | `docs/design-handoffs/gv-voicemail-sms-radioconsole/tokens.md` |

## gv-auth/

Google Voice authentication work: the B2 auth-blackout fix, cookie lineage, the session alarm and auto-relogin designs and plans, and the sign-in spike.

| File | Title | Date | Original path |
|---|---|---|---|
| [2026-09-09-gv-signin-cdp-recording.md](gv-auth/2026-09-09-gv-signin-cdp-recording.md) | Spike recording — one real Google sign-in, driven by hand over CDP (plan Task 4) | 2026-09-09 | `docs/spikes/2026-09-09-gv-signin-cdp-recording.md` |
| [gv-auth-blackout-b2-design.md](gv-auth/gv-auth-blackout-b2-design.md) | Design Spec: B2 — GV auth blackout (PSIDTS staleness → deterministic ~9-min dead window) | 2026-07-31 | `docs/plans/gv-auth-blackout-b2-design.md` |
| [gv-auth-blackout-b2-plan.md](gv-auth/gv-auth-blackout-b2-plan.md) | Plan: B2 — GV auth blackout (refresh cadence + reactive 401 recovery + honest status) | added 2026-07-31 | `docs/plans/gv-auth-blackout-b2-plan.md` |
| [gv-auth-first-refresh-anchor-and-cookie-lineage.md](gv-auth/gv-auth-first-refresh-anchor-and-cookie-lineage.md) | Plan: anchor the first PSIDTS refresh, and stop the cookie lineage from lying | added 2026-09-08 | `docs/plans/gv-auth-first-refresh-anchor-and-cookie-lineage.md` |
| [gv-auto-relogin.md](gv-auth/gv-auto-relogin.md) | Plan — GV auto-relogin: automate the routine case, and stop hard when it stops being routine | 2026-09-09 | `docs/plans/gv-auto-relogin.md` |
| [gv-crossrepo-xr2-verify-and-xr6-blackout-404.md](gv-auth/gv-crossrepo-xr2-verify-and-xr6-blackout-404.md) | Cross-repo batch: XR-2 (verify) + XR-6 (voicemail blackout 404) — plan | added 2026-09-08 | `docs/plans/gv-crossrepo-xr2-verify-and-xr6-blackout-404.md` |
| [gv-session-alarm.md](gv-auth/gv-session-alarm.md) | Plan — GV session alarm: transport an existing, already-correct signal to a human | 2026-09-09 | `docs/plans/gv-session-alarm.md` |
| [2026-09-09-gv-auto-relogin-design.md](gv-auth/2026-09-09-gv-auto-relogin-design.md) | GV auto-relogin — design | 2026-09-09 | `docs/superpowers/specs/2026-09-09-gv-auto-relogin-design.md` |
| [2026-09-09-gv-session-alarm-design.md](gv-auth/2026-09-09-gv-session-alarm-design.md) | GV session alarm — design | 2026-09-09 | `docs/superpowers/specs/2026-09-09-gv-session-alarm-design.md` |

## ht801/

The HT801 address-resolution and config-binder plan (implemented; see the 2026-07-29 ADR).

| File | Title | Date | Original path |
|---|---|---|---|
| [ht801-address-resolution-and-config-binder-fix.md](ht801/ht801-address-resolution-and-config-binder-fix.md) | HT801 address resolution + config binder fix — plan | added 2026-07-29 | `docs/plans/ht801-address-resolution-and-config-binder-fix.md` |

## sessions/

Session-state notes.

| File | Title | Date | Original path |
|---|---|---|---|
| [2026-08-03-session-state.md](sessions/2026-08-03-session-state.md) | Session state — GV auth blackout arc (B1/B2) + open incident | 2026-08-03 | `docs/handoffs/2026-08-03-session-state.md` |

## radio-console/

Closed exchanges with Radio Console (the RTest repo). Files that were in `docs/handoffs/` were sent by RotaryPhone; files that were in `docs/prompts/` were received from Radio Console. The live boundary contract and the still-open exchanges did not move.

| File | Title | Date | Original path |
|---|---|---|---|
| [2026-09-08-radioconsole-bell-persistence-and-404.md](radio-console/2026-09-08-radioconsole-bell-persistence-and-404.md) | INBOUND from RotaryPhone — 2026-09-08 (third) — two decisions, both answered | 2026-09-08 | `docs/handoffs/2026-09-08-radioconsole-bell-persistence-and-404.md` |
| [2026-09-08-radioconsole-gv12-refinement.md](radio-console/2026-09-08-radioconsole-gv12-refinement.md) | INBOUND from RotaryPhone — 2026-09-08 (fourth) — `GV-12` is narrower than we told you | 2026-09-08 | `docs/handoffs/2026-09-08-radioconsole-gv12-refinement.md` |
| [2026-09-08-radioconsole-incident-and-corrections.md](radio-console/2026-09-08-radioconsole-incident-and-corrections.md) | INBOUND from RotaryPhone — 2026-09-08 (second of the day) — incident, a retracted answer, and four defects on our side | 2026-09-08 | `docs/handoffs/2026-09-08-radioconsole-incident-and-corrections.md` |
| [2026-09-08-radioconsole-psidts-field-is-not-honest.md](radio-console/2026-09-08-radioconsole-psidts-field-is-not-honest.md) | ⚠ URGENT INBOUND from RotaryPhone — 2026-09-08 (fifth) — `psidtsAgeSeconds` is NOT the honest field | 2026-09-08 | `docs/handoffs/2026-09-08-radioconsole-psidts-field-is-not-honest.md` |
| [2026-09-08-radioconsole-starvation-confirmed-and-phn7.md](radio-console/2026-09-08-radioconsole-starvation-confirmed-and-phn7.md) | INBOUND from RotaryPhone — 2026-09-08 (sixth) — starvation CONFIRMED, `PHN-7` merged but NOT deployed | 2026-09-08 | `docs/handoffs/2026-09-08-radioconsole-starvation-confirmed-and-phn7.md` |
| [2026-09-08-rotaryphone-auth-lineage-fixes.md](radio-console/2026-09-08-rotaryphone-auth-lineage-fixes.md) | RotaryPhone → Radio Console — GV auth lineage fixed, and the field-name answer | 2026-09-08 | `docs/handoffs/2026-09-08-rotaryphone-auth-lineage-fixes.md` |
| [2026-09-09-radioconsole-deploy-handoff.md](radio-console/2026-09-09-radioconsole-deploy-handoff.md) | RotaryPhone → Radio Console — you are managing the next `rotary-phone` deploy | 2026-09-09 | `docs/handoffs/2026-09-09-radioconsole-deploy-handoff.md` |
| [2026-09-09-radioconsole-gv-auth-wire-changes.md](radio-console/2026-09-09-radioconsole-gv-auth-wire-changes.md) | INBOUND from RotaryPhone — GV auth fix merged, and the wire changes it brings | 2026-09-09 | `docs/handoffs/2026-09-09-radioconsole-gv-auth-wire-changes.md` |
| [2026-09-09-radioconsole-gvbridge-event-carveouts-removed.md](radio-console/2026-09-09-radioconsole-gvbridge-event-carveouts-removed.md) | RotaryPhone → Radio Console — `/api/gvbridge/event` is no longer exempt from the auth gate | 2026-09-09 | `docs/handoffs/2026-09-09-radioconsole-gvbridge-event-carveouts-removed.md` |
| [2026-09-09-radioconsole-lane-repair-and-gate-ack.md](radio-console/2026-09-09-radioconsole-lane-repair-and-gate-ack.md) | RotaryPhone → Radio Console — the lane failed a third way, and your consumer finding is accepted | 2026-09-09 | `docs/handoffs/2026-09-09-radioconsole-lane-repair-and-gate-ack.md` |
| [2026-09-25-radioconsole-ensure-install-exit-code-question-DRAFT.md](radio-console/2026-09-25-radioconsole-ensure-install-exit-code-question-DRAFT.md) | DRAFT, NOT DELIVERED: installing the current `gv-bridge-ensure.sh`, and its exit code | 2026-09-25 | `docs/handoffs/2026-09-25-radioconsole-ensure-install-exit-code-question-DRAFT.md` |
| [radioconsole-bell-failure-reply.md](radio-console/radioconsole-bell-failure-reply.md) | Reply — bell-failure surfacing contract ("the phone won't ring") | added 2026-07-29 | `docs/handoffs/radioconsole-bell-failure-reply.md` |
| [radioconsole-gv-auth-blackout-reply.md](radio-console/radioconsole-gv-auth-blackout-reply.md) | Reply — B2: the 20-minute auth blackout | added 2026-08-01 | `docs/handoffs/radioconsole-gv-auth-blackout-reply.md` |
| [radioconsole-gv-threadid-decode-b1-reply.md](radio-console/radioconsole-gv-threadid-decode-b1-reply.md) | Reply — B1: `%2F` thread ids are decoded on both thread routes | added 2026-07-31 | `docs/handoffs/radioconsole-gv-threadid-decode-b1-reply.md` |
| [radioconsole-gv-voicemail-blackout-404-reply.md](radio-console/radioconsole-gv-voicemail-blackout-404-reply.md) | Reply — XR-6 fixed, and five corrections to the cross-repo board | added 2026-09-08 | `docs/handoffs/radioconsole-gv-voicemail-blackout-404-reply.md` |
| [2026-03-14-bt-cross-adapter-pairing-guard.md](radio-console/2026-03-14-bt-cross-adapter-pairing-guard.md) | RotaryPhone: Prevent Cross-Adapter BT Pairing Conflicts | 2026-03-14 | `docs/prompts/2026-03-14-bt-cross-adapter-pairing-guard.md` |
| [2026-09-08-radioconsole-ack-2-and-three-rows.md](radio-console/2026-09-08-radioconsole-ack-2-and-three-rows.md) | Radio Console → RotaryPhone — second reply acknowledged, three of ours filed, one decision pending | 2026-09-08 | `docs/prompts/2026-09-08-radioconsole-ack-2-and-three-rows.md` |
| [2026-09-08-radioconsole-ack-6-timestamp-proposal.md](radio-console/2026-09-08-radioconsole-ack-6-timestamp-proposal.md) | Radio Console → RotaryPhone — sixth ack, the null-address answer, and a counter-proposal on the field name | 2026-09-08 | `docs/prompts/2026-09-08-radioconsole-ack-6-timestamp-proposal.md` |
| [2026-09-08-radioconsole-reply-ack-and-answers.md](radio-console/2026-09-08-radioconsole-reply-ack-and-answers.md) | Radio Console → RotaryPhone — receipt acknowledged, everything verified, one ask declined | 2026-09-08 | `docs/prompts/2026-09-08-radioconsole-reply-ack-and-answers.md` |
| [2026-09-09-radioconsole-deploy-gate-answers.md](radio-console/2026-09-09-radioconsole-deploy-gate-answers.md) | Radio Console → RotaryPhone — your two gate questions, answered. **You were right to insist on one of them. | 2026-09-09 | `docs/prompts/2026-09-09-radioconsole-deploy-gate-answers.md` |
| [2026-09-09-radioconsole-lane-agreed-and-rederivation.md](radio-console/2026-09-09-radioconsole-lane-agreed-and-rederivation.md) | Radio Console → RotaryPhone — lane protocol agreed, one refinement, and what the stale draft cost us | 2026-09-09 | `docs/prompts/2026-09-09-radioconsole-lane-agreed-and-rederivation.md` |
| [2026-09-09-radioconsole-revision-fault-and-hash-domain.md](radio-console/2026-09-09-radioconsole-revision-fault-and-hash-domain.md) | Radio Console → RotaryPhone — the in-place revision was ours, both asks accepted, and the hash needs a defined domain | 2026-09-09 | `docs/prompts/2026-09-09-radioconsole-revision-fault-and-hash-domain.md` |
| [2026-09-09-radioconsole-ui11-was-never-ours.md](radio-console/2026-09-09-radioconsole-ui11-was-never-ours.md) | Radio Console → RotaryPhone — `UI-11` retracted: the SPA fallback is yours, not ours | 2026-09-09 | `docs/prompts/2026-09-09-radioconsole-ui11-was-never-ours.md` |
| [radioconsole-gv-markread-readstate-request.md](radio-console/radioconsole-gv-markread-readstate-request.md) | Request Prompt — Add GV mark-read / durable read-state to the gvbridge API | added 2026-06-20 | `docs/prompts/radioconsole-gv-markread-readstate-request.md` |
| [radioconsole-gv-threadid-decode-and-auth-blackout-request.md](radio-console/radioconsole-gv-threadid-decode-and-auth-blackout-request.md) | Request from Radio Console → RotaryPhone: `%2F` thread ids + the 20-minute GV auth blackout | 2026-07-31 | `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md` |

## deploy/

Scope and plan for making `deploy/Deploy-ToLinux.ps1` report failures honestly. Most tasks shipped; tasks 6, 10, 11 and 12 were never started.

| File | Title | Date | Original path |
|---|---|---|---|
| [deploy-tooling-honest-deploy-plan.md](deploy/deploy-tooling-honest-deploy-plan.md) | Plan — `Deploy-ToLinux.ps1`: stop reporting success without doing the job | 2026-09-09 | `docs/plans/deploy-tooling-honest-deploy-plan.md` |
| [deploy-tooling-honest-deploy.md](deploy/deploy-tooling-honest-deploy.md) | Scope — `Deploy-ToLinux.ps1`: stop reporting success without doing the job | 2026-09-09 | `docs/plans/deploy-tooling-honest-deploy.md` |
