# Design Spec: GV Voicemail + Texts on RadioConsole

- **Status:** Draft for owner review → Planner
- **Date:** 2026-06-20
- **Author:** Designer
- **Arc:** `docs/plans/gv-voicemail-sms-arc.md`
- **Handoff package:** `docs/design-handoffs/gv-voicemail-sms-radioconsole/` (overview · interactions · copy · tokens · mockups)
- **Designs against:** ADR `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` §6.1 DTOs / §6.2 endpoints / §6.3 push (STABLE).

This spec is the WHAT for the Planner. The HOW (component file plan, task ordering) is Planner's. The full visual/interaction/copy detail lives in the handoff package; this spec is the contract-level summary + the decision record.

---

## follows / extends / deviates (authoritative)

- **FOLLOWS:** RadioConsole "Command Surface" design system (`D:\prj\RTest\RTest\src\Radio.Web\wwwroot\css\design-system.css`) and the Phone-surface handoff (`D:\prj\RTest\RTest\docs\design-handoffs\design_handoff_phone_page\`). Reuses `.phone-shell` / `.phone-tab-rail` / `.phone-rail-tab`, `.phone-card`, `.phone-pill.*`, `.phone-btn` / `.phone-btn-sm`, `.phone-input`, `.list-item-touch`, `.empty-state`, `.skeleton*`, `.now-playing-dock-progress` scrubber, `.nav-badge`, and the existing `*ApiService` / `*HubService` / `NotificationService` plumbing. Full citations in `overview.md` → "follows / extends / deviates".
- **EXTENDS:** two new left-rail sub-tabs inside `/phone` (**Voicemail**, **Texts**); a seekable voicemail player (extends the display-only dock scrubber); a thread list + conversation view with a **new message-bubble primitive** (specified from existing tokens in `tokens.md` §Bubbles); a new `.nav-badge` consumer on the rail tabs and the topbar Phone pill.
- **DEVIATES:** none proposed. The two items that could force deviation are escalated as open questions, not decided unilaterally: (1) the on-screen keyboard for compose; (2) sub-tab vs top-level `/messages` nav.

---

## Scope → screens → states (what ships)

| # | Screen | Reuses | New | All states specified in |
|---|---|---|---|---|
| 1 | Voicemail list | `.phone-rail-tab`, `.list-item-touch`, `.phone-pill`, `.empty-state`, `.skeleton` | unread-dot, transcript-preview row | `interactions.md` §A |
| 2 | Voicemail playback (inline accordion) | `.transport-btn-primary`, `.now-playing-dock-progress`, `<audio>` proxy | seekable scrubber, transcript states | §B |
| 3 | Texts thread list | `.list-item-touch`, `.phone-pill`, `.empty-state` | unread-dot, "You:" preview | §C |
| 4 | Texts conversation (read + send/reply) | `.phone-input`, `.phone-btn-sm`, toasts | **message bubbles**, sending/sent/failed states, segment counter | §D |
| 5 | New-message composer | `.phone-input`, contacts list | recipient entry | §E |
| 6 | New-arrival notification UX | `NotificationService` toasts, `.nav-badge` | rail+topbar badges, in-place append | §F |
| 7 | Failure / auth-decay surfacing | `/api/gvbridge/status`, toasts | reconnecting banner | §G |

Every empty / loading / error / sending / failed state is enumerated in `interactions.md`; every label/placeholder/error string is in `copy.md`; the bubble + badge + banner CSS (existing tokens only) is in `tokens.md`.

## Data this design binds to (from ADR §6.1 — do not redefine)

- Voicemail: `VoicemailItemDto` (Id, ThreadId, FromNumber, FromName?, ReceivedAt, DurationSeconds, IsRead, Transcript?, AudioUrl), `VoicemailListDto`.
- SMS: `SmsThreadDto`, `SmsThreadListDto`, `SmsMessageDto` (Direction "Inbound"/"Outbound"), `SmsThreadMessagesDto`, `SendSmsRequest`, `SendSmsResponse(Queued, ThreadId?, Error?)`.
- Push (ADR §6.3): `VoicemailReceived(VoicemailItemDto)`, `SmsReceived(SmsMessageDto)` on the existing hub.
- Audio (ADR §6.4): `<audio src=".../voicemail/{id}/audio">` — RotaryPhone proxies + range-streams; first play may buffer.

## Navigation decision

Voicemail and Texts are **new sub-tabs inside `/phone`** (rail order: Dashboard · Voicemail · Texts · Contacts · Call History · Diagnostics). The topbar Phone pill also carries a combined unread `.nav-badge` so awareness survives leaving the page. Alternative (top-level `/messages` nav) is documented but not chosen — see Open Question 1.

## Mapping to the ADR's PR breakdown

The DTOs are stable regardless of GV-side field positions (ADR §10), so UI work can proceed in parallel. UI maps cleanly onto the ADR PRs:

- **ADR PR1–PR2 (voicemail read + audio proxy)** → UI screens 1, 2 (Voicemail list + player). No keyboard dependency. Ship-first candidate.
- **ADR PR3 (SMS read + push)** → UI screens 3, 6, 7 (thread list, conversation *read*, notifications, auth banner). No keyboard dependency.
- **ADR PR4 (SMS send — HOLD for owner)** → UI screens 4 (send states), 5 (composer). **Gated on Open Dependency 1 (keyboard).**

This means the **entire read experience (voicemail listen+transcript + text read + notifications) ships with no on-screen-keyboard dependency**; only compose/send needs the keyboard question resolved. Recommend Planner sequence read-first to match.

## Open dependencies / questions for the owner

1. **On-screen keyboard for compose (BLOCKING for send).** No Radzen touch-keyboard exists; the stray `virtual-keyboard.css` targets MudBlazor and is unwired. Pick: (a) build a Radzen on-screen keyboard *(recommended)*, (b) assume a physical keyboard on the kiosk, or (c) ship read-only first and defer send. Read paths are unaffected either way.
2. **Nav model:** sub-tabs in `/phone` (recommended) vs top-level `/voicemail` + `/messages` pills. The latter surfaces unread in the always-visible topbar at the cost of split context.
3. **"Heard"/"read" persistence:** ADR §3.4 puts GV-side mark-read out of scope v1, so heard/read is **UI-local** for v1. OK, or should we pull mark-read forward so state survives restart / matches the GV app?
4. **New-message recipient entry:** number-only (floor) vs number + contact-picker (nicer). Contact-pick reuses the existing contacts list.
5. **Voicemail quick actions:** include "Call back" / "Text back" in the player for v1, or defer?
6. **Audible new-text chime:** v1 is silent (UI-layer). If an audible cue is wanted later it belongs in Radio.API's ducking-aware audio layer, not Blazor. Confirm silent-for-v1.

## Cross-repo / boundary note

The UI lives in the **RadioConsole repo** (`D:\prj\RTest\RTest`, `src/Radio.Web`), not RotaryPhone. Implementing it requires RadioConsole-side DTO records, a voicemail/SMS `*ApiService`, and the new hub subscriptions — coordinated through `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` Integration Points, exactly as prior phases. Architect/Planner own that split; this spec defines only what the UI consumes and how it looks/behaves.

## Acceptance (design-fidelity, for Tester/Polisher)

- No new design tokens; no `dark:`-style forks; all colour/typography from existing `:root`.
- No shell-level vertical scrollbar at 1920×720; lists scroll internally; content fits 600px.
- Every state in `interactions.md` is reachable and matches `copy.md` strings.
- Unread badges (rail + topbar) increment on push and clear on read/listen.
- New arrivals never steal the screen or pause audio (toast + badge + bump only).
- Send failures preserve the typed text and never auto-retry (ADR §4.2 #4).
- Auth-decay shows the calm reconnecting banner + disables Send; auto-recovers.

---

**Designer status:** Spec + handoff package written. Awaiting owner review before handing to Planner. No implementation, no plan, no data-model/API changes proposed (those are stable in the ADR).
