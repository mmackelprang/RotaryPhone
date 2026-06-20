# Interactions & States

Every surface, every state. This is the section the Planner turns into per-component task lists and the Tester turns into a UAT punch-list. States are exhaustive: empty / loading / error / partial, plus the write-path states (sending / sent / failed) for Texts.

All animations reuse existing keyframes in `design-system.css` §16 and respect `@media (prefers-reduced-motion: reduce)` (§26) automatically since they are token-driven.

---

## Global: data lifecycle (mirror the existing Phone page)

The new tabs must follow `PhonePage.razor`'s established lifecycle (file `D:\prj\RTest\RTest\src\Radio.Web\Components\Pages\PhonePage.razor`):

- **On `OnInitializedAsync`:** fetch initial lists (voicemail list, thread list) in parallel; subscribe to the SignalR hub events. Do NOT block tab switching on these.
- **SignalR push (primary freshness):** subscribe to `VoicemailReceived(VoicemailItemDto)` and `SmsReceived(SmsMessageDto)` on the existing hub (ADR §6.3). On receipt, update the relevant list/badge and, if the affected thread/voicemail is open, append in place.
- **Poll (backstop only):** a slow poll of the thread list (30–60 s per the boundary doc's cadence guidance) catches anything a missed push dropped — same belt-and-suspenders pattern as `PollStatusAsync` (lines 196–233). Never poll faster than the ADR's read endpoints expect.
- **Dispose:** unsubscribe all hub handlers and stop timers (mirror lines 477–487). Switching tabs must NOT tear down the page-level subscription.

---

## A. Voicemail list

### States

| State | Trigger | What renders |
|---|---|---|
| **Loading (initial)** | First fetch in flight, no cached list | A `.skeleton` block of ~5 `.skeleton-list-row` rows (thumb + two text lines), per design-system.css §17.b. NOT a centered spinner. |
| **Loaded, has items** | List returned ≥1 item | The row list (Screen A). Unheard rows sorted to behave naturally: default order = newest first (`ReceivedAt` desc). Unheard rows are marked by the dot, not by reordering. |
| **Empty** | List returned 0 items | `.empty-state`: icon `voicemail` (`.empty-state-icon`), text "No voicemails." (see `copy.md`). |
| **Error** | Fetch threw / non-200 | `.empty-state` variant: icon `cloud_off`, text per `copy.md` error block, plus a "Retry" `.phone-btn-sm`. Do NOT blank an already-loaded list on a transient refresh error — keep the last good list and surface the error as a toast (`NotificationSeverity.Warning`) instead. |
| **Refreshing (manual)** | User taps the header refresh icon | Icon spins (`.spinner`); list stays interactive; replace on success. |
| **New arrival (push)** | `VoicemailReceived` event | New row animates in at the top with `.list-item-add` (slide from left, §17). Unread badge increments. A toast fires (see §F). |

### Interactions

| Event | Trigger | Result |
|---|---|---|
| Tap a voicemail row | Pointer up on `.list-item-touch` | Expand the inline player accordion under that row (Screen B). Collapse any other open player. Flip row to **heard** (local), decrement unread badge. |
| Tap an already-open row's chevron | Pointer up | Collapse the player. |
| Tap header refresh | Pointer up | Re-fetch list; spinner on icon. |
| Tap Retry (error state) | Pointer up | Re-fetch. |

---

## B. Voicemail playback

### Decision: inline accordion (not a separate route, not a right rail)

At 1920 wide the list is narrow enough that an inline expanded detail under the tapped row keeps the rest of the list visible and needs no navigation. This matches the kiosk's "everything on one screen" ethos and avoids a back-button round trip. (A right-detail-rail like Contacts was considered; rejected because voicemail playback wants the transcript to have full width for readability.)

### States

| State | Trigger | What renders |
|---|---|---|
| **Idle / ready** | Player opened, audio not yet requested | Play button enabled (▶), scrubber at 0, time `0:00 / {duration}`. Duration comes from `DurationSeconds` in the DTO (no audio fetch needed to show it). |
| **Buffering (first play)** | User taps ▶, RotaryPhone is fetching from Google | Play button shows a small `.spinner` in place of the glyph; scrubber inert; copy under it optional ("Fetching recording…"). ADR §6.4: first play proxies+caches, so a 0.5–few-second stall is expected and must read as intentional, not broken. |
| **Playing** | Audio playing | Pause glyph (❚❚); scrubber advances (bind to `<audio>` `timeupdate`); time readout ticks (mono tabular-nums). |
| **Paused** | User taps pause | Play glyph returns; position held. |
| **Ended** | Audio reached end | Reset to ▶ at position 0 (or hold at end — pick reset for re-listen ease). |
| **Audio error** | `<audio>` `error` event / proxy 5xx | Replace transport with an inline error row: icon `error_outline` + "Couldn't load this recording." + "Retry" (`.phone-btn-sm`). Toast `NotificationSeverity.Error`. |
| **Transcript present** | `Transcript != null && != ""` | Render transcript body text (wraps, selectable-for-read, scrolls with panel). |
| **Transcript pending** | `Transcript == null` AND voicemail is recent | Italic `--text-medium`: "Transcript pending — Google is still transcribing this voicemail." (`copy.md`). |
| **Transcript absent** | `Transcript == null` AND not recent / account has it disabled | `--text-low`: "No transcript available." |

### Interactions

| Event | Trigger | Result |
|---|---|---|
| Tap Play/Pause | Pointer up on `.transport-btn-primary` | Toggle `<audio>` play/pause. First play triggers buffering state. |
| Drag/tap scrubber | Pointer down + move on `.now-playing-dock-progress` | Seek: set `<audio>.currentTime` from the tap x-fraction. Range support (ADR §6.4) makes this work without a full re-download. Touch target: the bar's hit area must be ≥24px tall even though the visual bar is 3px (pad the hit region). |
| Tap "Call back" (if shipped) | Pointer up | Hand the E.164 to the existing dial path (`SimulateDialAsync` / the real dial endpoint). Owner Q5. |
| Tap "Text back" (if shipped) | Pointer up | Open the Texts tab pre-targeted to that number's thread (or new composer). Owner Q5. |

### Keyboard / screen-reader (voicemail)

- The play/pause button is a real `<button>` with `aria-label` "Play voicemail from {caller}" / "Pause".
- The scrubber gets `role="slider"`, `aria-valuemin=0`, `aria-valuemax={durationSeconds}`, `aria-valuenow`, `aria-label="Playback position"` (mirror the existing `role="progressbar"` markup in `NowPlayingDock.razor` lines 61–69 but upgraded to a slider since it's now interactive).
- Transcript is plain text in the DOM (screen-reader reads it directly). Mark "Transcript pending" with `aria-live="polite"` so it announces when it later fills in via push.

---

## C. Texts — thread list

### States

| State | Trigger | What renders |
|---|---|---|
| **Loading (initial)** | First fetch | `.skeleton` list of ~6 `.skeleton-list-row`. |
| **Loaded** | ≥1 thread | Thread rows (Screen C), newest activity first (`LastMessageAt` desc). |
| **Empty** | 0 threads | `.empty-state`: icon `forum`, text "No conversations yet." + (if compose ships) "New message" `.phone-btn-sm`. |
| **Error** | Fetch failed | `.empty-state` error variant + Retry; keep last good list on transient refresh failure (toast warning), same rule as voicemail. |
| **New inbound (push)** | `SmsReceived`, thread not open | The thread bumps to top with `.list-item-add`; unread dot appears; badge increments; toast fires (§F). |
| **New inbound (push), thread open** | `SmsReceived` for the open thread | Append the bubble to the open conversation (see §D); do NOT mark unread; do NOT toast (user is looking at it) — or toast suppressed-style. Bump thread to top of list, keep selected. |

### Interactions

| Event | Trigger | Result |
|---|---|---|
| Tap a thread row | Pointer up | Load + show the conversation (Screen C right pane). Mark thread read (local; GV-side mark-read is out of scope v1 — Open Q3). Decrement badge. Selected row gets `.list-item-active`. |
| Tap "New" | Pointer up | Open the new-message composer (Screen D). |

---

## D. Texts — conversation view

### Message bubble states (outbound write path — the important part)

| State | Trigger | Bubble appearance |
|---|---|---|
| **Sending** | User tapped Send; `POST /api/gvbridge/sms/send` in flight | Bubble appears immediately (optimistic), right-aligned, slightly dimmed (opacity ~0.6), with a small `.spinner` where the timestamp/status glyph goes. Compose input clears; Send disabled until response. |
| **Sent (queued)** | 200 from send (`SendSmsResponse.Queued == true`) | Bubble goes full opacity; status glyph becomes a single check (`done`, `--text-low`). ADR §4.1: 200 = queued, not echoed — so "sent" here means "accepted by GV", confirmed for real when the next poll/push surfaces it in-thread. |
| **Delivered/confirmed** | The sent message reappears via `SmsReceived`/poll as an outbound message | De-dupe against the optimistic bubble (match on text + recency, or on returned `ThreadId`/`Id`); collapse to a single confirmed bubble. No visual jump if de-dupe is clean. |
| **Failed** | non-200 / `Queued == false` / `Error` present / timeout | Bubble turns to a failed style: a thin `--signal-red` left edge or a red `error_outline` glyph + "Failed to send" sub-line + a **Retry** affordance (tap the bubble or a small retry icon). Toast `NotificationSeverity.Error` with the reason (see `copy.md` error matrix). The text is preserved (never silently lost). ADR §4.2 #4: **never auto-retry** a send on ambiguous failure — retry is user-initiated only. |
| **Inbound** | `SmsReceived` or loaded history, `Direction=="Inbound"` | Left-aligned `.surface-raised` bubble, no status glyph, timestamp below. New inbound while open animates in with `.list-item-add` (or a gentle `slideInUp`). |

### Bubble visual spec → see `tokens.md` §Bubbles (assembled from existing tokens only).

### Compose bar states

| State | Appearance |
|---|---|
| **Empty** | `.phone-input` with placeholder "Message"; Send `.phone-btn-sm` disabled (`:disabled` opacity 0.35). |
| **Has text** | Send enabled (cyan). If text length crosses the SMS-segment threshold, show the segment counter (below). |
| **Sending** | Input cleared and briefly disabled; Send shows spinner; re-enabled on response. |
| **Send disabled by rate-limit** | ADR §4.2 #4 rate-limits sends server-side (reject > N/10s). If the server 429s, surface a toast "Sending too fast — wait a moment." and re-enable after a short cooldown; keep the text. |
| **Offline / RotaryPhone unreachable** | If the GV bridge is unavailable (`/api/gvbridge/status` Available==false), disable Send with a hint pill "Texting unavailable" and a tooltip; don't let the user type into a dead send path. |

### Character / SMS-segment counter (Compose)

- Hidden until the message reaches ~120 chars (don't nag for short texts).
- Then show `chars / segments` in `--font-mono` `--text-low`, e.g. `142 · 1 SMS` then `161 · 2 SMS`.
- GSM-7 vs UCS-2: a message containing an emoji or non-GSM char drops the per-segment limit (160 → 70). v1 may use a simple heuristic (if any non-ASCII char present, use the 70-char boundary) and label it "2 SMS" once over one segment. This is informational only — GV sends regardless; the counter just sets expectations. Mark as nice-to-have if it complicates PR4.

### Keyboard / touch / screen-reader (conversation)

- **Touch:** compose input must summon on-screen text entry on tap (Open Dependency 1). Send target ≥48px (`--touch-min`). Bubbles are not interactive except failed ones (whole failed bubble is a retry tap target ≥48px tall).
- **Hardware keyboard (if present):** `Enter` in the compose input sends (matches the dev-tray dial input's Enter-to-act convention noted in the phone handoff interactions table); `Shift+Enter` inserts a newline. Focus returns to the input after send.
- **Screen-reader:** the message region is an `aria-live="polite"` log so new inbound messages announce. Each bubble has an accessible label like "Received, 9:41 AM: Sounds good" / "Sent: On my way" / "Failed to send: On my way. Double-tap to retry." The compose input has `aria-label="Type a message"`.
- **Focus management:** opening a thread moves focus to the compose input (so a keyboard user can type immediately) only if a hardware keyboard is the input method; on pure-touch, do not auto-focus (it would pop the on-screen keyboard unbidden).

---

## E. New-message composer (Screen D)

| State | Appearance |
|---|---|
| **Recipient empty** | Recipient `.phone-input` focused/placeholder "Phone number"; compose Send disabled until both recipient and message are non-empty. |
| **Recipient invalid** | On send, if RotaryPhone returns the E.164-normalization failure (ADR §4.2 #2 — bare/short numbers → INVALID_ARGUMENT), surface inline under the recipient field: "Enter a valid phone number." Don't pre-validate aggressively client-side (let the server normalize), but block obviously-empty/non-numeric input. |
| **Sending → sent** | Same as conversation send states; on success, transition the composer into the normal conversation view for the resolved thread. |

---

## F. New-message notification UX (cross-cutting — the §5 ask)

The constraint: **non-disruptive.** This is a music appliance; a new text must never steal the screen or interrupt audio.

Three coordinated, non-modal signals:

1. **Toast (transient).** On `SmsReceived` / `VoicemailReceived` push, fire a Radzen toast via the existing `NotificationService`:
   - SMS: `NotificationService.Notify(NotificationSeverity.Info, "{Contact or number}", "{message preview, truncated}")`.
   - Voicemail: `NotificationService.Notify(NotificationSeverity.Info, "New voicemail", "{caller} · {duration}")`.
   - Severity `Info` (blue) — not Warning/Error; this is good news, low urgency. Auto-dismiss (Radzen default ~5s). Toasts stack at the existing mount (`<RadzenComponents />` in `MainLayout.razor`) and do not block touch elsewhere.
   - **Suppress** the toast if the user is currently viewing that exact thread/voicemail (they already see it).
2. **Persistent unread badge (until addressed).**
   - On the **rail tabs**: `.nav-badge` (amber, mono) on the Voicemail and Texts `.phone-rail-tab`, showing unheard-voicemail and unread-thread counts respectively.
   - On the **topbar Phone pill** (`MainLayout.razor`): a single `.nav-badge` summing unread voicemail + texts, so awareness survives leaving the `/phone` page (this is the compromise that makes the sub-tab nav model viable — see `overview.md` Navigation model).
   - Badge clears per-item as the user opens/reads/listens.
3. **Inbox bump + in-place append.** The relevant list reorders (new item to top, `.list-item-add` animation) and, if the conversation/voicemail is open, the content appears in place (`aria-live`). No navigation forced.

**Explicitly NOT used:** modal dialogs, full-screen takeovers, audio chimes from the web layer (the box already does call-ring TTS via the Radio.API integration service — the *text/voicemail* UI must stay silent to avoid competing), or anything that pauses music. If the owner later wants an *audible* new-text chime, that belongs in the Radio.API audio layer (ducking-aware), not the Blazor UI — flag as future work, not v1.

### Notification states

| Condition | Behaviour |
|---|---|
| Push arrives, app foreground, not on relevant item | Toast + badge + bump. |
| Push arrives, user on the exact thread/voicemail | Append in place; badge not incremented; toast suppressed. |
| Push arrives during a SignalR reconnect gap | The backstop poll reconciles on reconnect; badge/list catch up; no toast for the catch-up batch (avoid a toast storm) — only live pushes toast. |
| Multiple pushes in a burst | Toasts stack but cap (Radzen handles); badge reflects true count. Consider collapsing >3 simultaneous into one "{n} new messages" toast — nice-to-have. |

---

## G. Failure & auth-decay surfacing (the graceful-degradation ask)

GV auth decays (the known 603/cookie-expiry class of failure — see project memory `project_gv_registration_603_incident.md`). The UI must surface this honestly without crying wolf.

| Failure | Where it shows | Copy ref |
|---|---|---|
| GV bridge unavailable (cookies invalid / not registered) | A thin non-blocking banner at the top of the Voicemail and Texts panels: "Google Voice is reconnecting — voicemail and texts may be delayed." `--signal-amber` accent. Compose Send disabled with the "Texting unavailable" pill. | `copy.md` §Auth |
| Send failed due to auth decay | Failed bubble + toast: "Couldn't send — Google Voice needs to reconnect. Try again shortly." | `copy.md` §Send-errors |
| Transient list refresh error | Keep last good list; toast Warning; no banner. | `copy.md` §Generic |
| Audio fetch failed | Inline player error row + toast Error. | `copy.md` §Voicemail |

The banner is driven by the **already-polled** `/api/gvbridge/status` (`Available` / `CookiesValid`) that the Phone dashboard consumes today — no new awareness mechanism. When status recovers, the banner auto-clears and Send re-enables. This makes auth decay a visible-but-calm condition, matching the "honest status" direction the 603 fix established.
