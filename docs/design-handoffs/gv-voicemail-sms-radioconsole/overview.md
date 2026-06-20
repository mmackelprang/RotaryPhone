# Design Handoff: Google Voice Voicemail + Texts on RadioConsole

- **Status:** Draft for owner review (Designer phase of the arc)
- **Date:** 2026-06-20
- **Author:** Designer
- **Arc:** `docs/plans/gv-voicemail-sms-arc.md`
- **Designs against:** ADR `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` — the §6.1 DTOs and §6.2/§6.3 contract are STABLE and are the data this design is built on.
- **Target UI:** RadioConsole / "Radio Console" kiosk — **Blazor Server + Radzen Blazor**, repo `D:\prj\RTest\RTest`, project `src/Radio.Web`. RadioConsole is a SEPARATE service/repo from RotaryPhone; RotaryPhone exposes the API + SignalR push, RadioConsole renders it.

---

## follows / extends / deviates

This is the single most important section for the Planner and Polisher.

**FOLLOWS (canonical patterns reused verbatim — do not reinvent):**

- The **Command Surface design system** at `D:\prj\RTest\RTest\src\Radio.Web\wwwroot\css\design-system.css`. All colour/typography/spacing tokens come from its `:root` block (§2). **No new colours, no new fonts.**
- The **Phone surface visual language** established in handoff `D:\prj\RTest\RTest\docs\design-handoffs\design_handoff_phone_page\README.md` and shipped in `design-system.css` §Ph. Specifically:
  - The **left-rail tab shell** — `.phone-shell` / `.phone-tab-rail` / `.phone-rail-tab` / `.phone-rail-label` (design-system.css lines 4956–5014), as used today in `Components/Pages/PhonePage.razor`.
  - The **phone card** — `.phone-card` + `.phone-card.accent-{green|cyan|amber|blue}` + `.phone-card-title` (lines 5032–5064).
  - The **phone pill** — `.phone-pill.{green|red|amber|blue|cyan|gray}` (lines 5265–5288) for status/unread/read markers.
  - The **phone buttons** — `.phone-btn` / `.phone-btn-sm` and their `.btn-answer|.btn-hangup|.btn-ghost|.btn-success|.btn-danger|.btn-warn` variants (lines 5192–5232, 5448–5467).
  - The **phone input** — `.phone-input` (lines 5469–5481) for the compose field.
  - The **empty state** — `.empty-state` / `.empty-state-icon` / `.empty-state-text` (design-system.css §15, lines 836–858) and the page-scoped `.empty-state` in `PhonePage.razor.css` line 12.
  - The **skeleton loading** primitives — `.skeleton`, `.skeleton-list-row`, `.skeleton-thumb`, `.skeleton-list-row-text`, `.skeleton-loading` (design-system.css §17/§17.b, lines 1042–1174) for initial-load states.
  - The **scrubber / progress bar** — `.now-playing-dock-progress` + `.now-playing-dock-progress-bar` (3px cyan bar; consumer `Components/Shared/NowPlayingDock.razor` lines 61–69; CSS ~lines 2432–2448) for voicemail playback.
- The **service + hub plumbing** RadioConsole already uses for the phone surface:
  - REST via a typed-HttpClient `*ApiService` (`Services/ApiClients/`), base URL `http://radio:5004`.
  - SignalR via a `*HubService` exposing C# events (`Services/Hub/`). The hub at `http://radio:5004/hub` already delivers `IncomingCall` / `CallStateChanged`; this design rides the **same hub** for the new `SmsReceived` / `VoicemailReceived` events (ADR §6.3).
  - Toasts via Radzen `NotificationService.Notify(NotificationSeverity.X, title, message)` — the component is mounted globally in `MainLayout.razor` (`<RadzenComponents />`).
  - The unread **count badge** — `.nav-badge` (design-system.css §7, lines 503–519), already used on the topbar Queue pill.

**EXTENDS (new surfaces that did not exist, built FROM the patterns above):**

- Two **new left-rail tabs inside the existing `/phone` page**: **Voicemail** and **Texts**. (Decision rationale in "Navigation model" below.) These reuse `.phone-rail-tab` exactly; only new icons + labels.
- A **Voicemail list + inline player** surface (new component `PhoneVoicemailPanel.razor`). The player extends the `.now-playing-dock-progress` scrubber into a tappable seek control — a behaviour the existing dock does not have (the dock is display-only). See `interactions.md` §Voicemail-Playback.
- A **Texts thread list + conversation view** surface (new component `PhoneTextsPanel.razor`) with **message bubbles** and a **compose bar** — neither exists anywhere in RadioConsole today. Bubbles are a genuinely new pattern; they are specified in full in `interactions.md` and `tokens.md` and assembled only from existing tokens (no new colours).
- A new `.nav-badge` consumer location: on the Voicemail and Texts **rail tabs** (unread counts), not just the topbar.

**DEVIATES (requires explicit owner direction — flagged, NOT done unilaterally):**

- **None proposed.** Every visual decision maps to an existing token or class. The two items that *could* force a deviation are escalated as open questions rather than decided here:
  1. **On-screen keyboard for compose.** The kiosk is touch-first with no physical keyboard guaranteed. A `virtual-keyboard.css` exists (`wwwroot/css/virtual-keyboard.css`) but (a) it is styled for **MudBlazor** (`.mud-overlay .mud-paper` selector) while the live app is **Radzen**, and (b) **no Razor keyboard component consumes it** (confirmed by code search). So text entry for SMS compose is an **unresolved platform dependency**, not a visual deviation. See "Open dependencies" §1.
  2. **A possible top-level `/messages` nav pill** instead of a sub-tab. Flagged in "Navigation model" as the alternative; the recommended choice (sub-tab) needs no deviation.

---

## Who uses this & success criteria

**User:** one person, at a fixed wall-mounted **1920×720 kiosk**, touch-first, glanceable from across a room. Always-on. This is "grandpa's radio/phone" — the bar for legibility and tap-target size is high; the tolerance for fiddly interactions is low.

**Success criteria:**

1. A new voicemail or text **announces itself** without the user staring at the screen (toast + persistent unread badge on the rail tab), and is non-disruptive to music playback (no modal steal, no audio interruption from the UI layer).
2. The user can **listen to a voicemail and read its transcript** in at most two taps from the Phone page (tab → row), with a working scrubber even on the first play (audio is proxied/cached by RotaryPhone, so first play may show a brief fetch spinner).
3. The user can **read a text thread and send a reply** with large tap targets, clear sending/sent/failed feedback, and an honest surfacing of failures (GV auth decay, rate-limit, invalid number).
4. Every surface fits the **600px content area** with no vertical page scrollbar at the shell level (lists scroll internally), matching the rest of the kiosk.
5. Zero new design tokens; zero drift from the Phone-surface visual language. Polisher should find nothing.

---

## Navigation model (decision)

**Decision: add Voicemail and Texts as two NEW left-rail sub-tabs inside the existing `/phone` page**, alongside Dashboard · Contacts · Call History · Diagnostics.

Final rail order (top to bottom):
`Dashboard · Voicemail · Texts · Contacts · Call History · Diagnostics`

Rationale:
- The `/phone` page is the established home for everything phone-shaped; voicemail and texts are phone-shaped. Keeping them here means one SignalR connection, one page lifecycle, one mental model.
- The rail already supports an arbitrary number of `.phone-rail-tab` buttons; adding two is zero new layout work.
- Per-tab scroll position and the page-level SignalR subscription are preserved across tab switches (the existing page disposes/re-subscribes at the page level, not the tab level — `PhonePage.razor` lines 154–164, 477–487).

**Alternative (NOT chosen, needs owner override to adopt):** promote Voicemail/Texts to **top-level topbar nav pills** (`/voicemail`, `/messages`) like Home/Phone/Queue. This would surface unread counts in the always-visible topbar (arguably better for glanceability) at the cost of two more top-level routes and split phone context. If the owner wants the unread badge visible from *every* page, not just when on `/phone`, this is the trade to make. See Open Question 1.

> **Unread visibility caveat for the chosen model:** with sub-tabs, the unread badge is only visible when the user is already on `/phone`. To keep new-message awareness global, the **topbar Phone pill itself** should also carry a `.nav-badge` summing unread voicemail + texts (see `interactions.md` §Notifications). This is the compromise that makes the sub-tab model viable.

---

## Per-screen layout

All screens live inside the existing `.phone-shell` (156px rail + content). Content area is 600px tall.

### Screen A — Voicemail tab (list)

Single-column list, full content width (no right rail; voicemail has no stats rail need for v1).

```
┌── rail ──┬──────────────────────── content (1fr) ─────────────────────────┐
│ Dashboard│  ┌ panel-header ─────────────────────────────────────────────┐ │
│ Voicemail│  │ VOICEMAIL            [3 unheard]              [↻ refresh]   │ │
│  ● Texts │  ├───────────────────────────────────────────────────────────┤ │
│ Contacts │  │ ● ⊙  Jane Appleseed        0:42    "Hey, calling about…"  ▸│ │  ← unheard: cyan dot + bold
│ History  │  │   ⊙  +1 919 555 0outlook    1:15    "This is Dr. Smith's…"▸│ │  ← heard: dim, no dot
│ Diag     │  │   ⊙  Unknown (+1 …)         0:08    Transcript pending      │ │
│          │  │   … (scrolls internally)                                   │ │
│          │  └───────────────────────────────────────────────────────────┘ │
└──────────┴───────────────────────────────────────────────────────────────┘
```

- **Panel header:** `.panel-header` — title "VOICEMAIL" (uppercase, existing style) + an unheard-count `.phone-pill.cyan` when >0 + a refresh icon-button (`.phone-btn-sm`, ghost) on the right.
- **Each row:** a `.list-item-touch` (design-system.css §10, min-height 56px) laid out as:
  `[unheard dot 8px][avatar/icon chip 44px][1fr: caller name (title) + transcript preview (subtitle, 1 line, ellipsis)][duration mono][chevron ▸]`.
  - Unheard row: leading 8px cyan dot (reuse the `.phone-pill::before` dot styling or a bare span with `--accent-primary`), caller name in `--text-high` 600. Use the `.list-item-touch.list-item-active` left-border accent only for the *currently selected/open* row, not for unheard — unheard is the dot.
  - Heard row: no dot, caller name `--text-medium`, slightly dimmer overall.
  - Caller name resolution mirrors the existing client-side contact match in `PhoneStatusHero` (last-10-digit suffix match against `MergedContacts`); fall back to `FromName` from the DTO, then to the formatted E.164.
- **Tapping a row** opens Screen B (the player) — see interaction model. On a 1920-wide kiosk the player can render **inline as an expanded row / detail strip below the tapped row** (accordion) rather than a separate route, keeping the list visible. This is the recommended pattern; see `interactions.md` §Voicemail-Playback for the accordion-vs-detail-rail decision.

### Screen B — Voicemail playback (inline detail)

Expands under the tapped row (accordion) within the same panel. Full transcript + audio transport.

```
│ ● ⊙  Jane Appleseed            0:42    "Hey, calling about…"          ▾ │  ← expanded row
│ ┌──────────────────────────────────────────────────────────────────┐  │
│ │  [▶]  ──────●───────────────────────  0:14 / 0:42                 │  │  ← transport + scrubber + time
│ │                                                                    │  │
│ │  Transcript                                                        │  │
│ │  Hey, calling about the thing on Saturday — give me a ring back   │  │
│ │  when you get a sec. Thanks, bye.                                  │  │
│ │                                                                    │  │
│ │  [📞 Call back]   [💬 Text back]                                   │  │  ← optional quick actions (see open Q)
│ └──────────────────────────────────────────────────────────────────┘  │
```

- **Transport:** a circular play/pause `.transport-btn-primary` (design-system.css §11) on the left.
- **Scrubber:** the `.now-playing-dock-progress` bar, extended to be **seekable** (pointer/touch drag sets position). Time readout to the right in `--font-mono` tabular-nums `0:14 / 0:42` (mirror `.now-playing-dock-elapsed` / `-total`).
- **Transcript:** body text, `--text-high`, wraps freely, scrolls with the panel. When `Transcript == null` → show the "Transcript pending" / "No transcript" copy (see `copy.md`).
- **Audio element:** an HTML5 `<audio src="http://radio:5004/api/gvbridge/voicemail/{id}/audio">` (ADR §6.4 — RotaryPhone proxies + range-streams). First play may stall while RotaryPhone fetches from Google → show a small spinner on the play button (loading state). Range support means the scrubber can seek (ADR §6.4).
- **Mark-heard:** opening the player (or play start) flips the row to heard locally and decrements the unread badge. NOTE: ADR §3.4 says GV-side mark-read is **out of scope v1** — so "heard" is a **local/UI-only** state for v1 unless the owner wants it persisted. Flagged as Open Question 3.

### Screen C — Texts tab (thread list)

Two-pane on the 1920 kiosk: thread list (left) + conversation (right). This is the master-detail pattern the kiosk affords at this width.

```
┌── rail ──┬───────── threads (380px) ──────────┬──────── conversation (1fr) ────────┐
│ …        │ ┌ panel-header ──────────────────┐ │ ┌ conv-header ──────────────────┐  │
│ ● Texts  │ │ TEXTS         [2]   [＋ New]    │ │ │ ◂  Jane Appleseed             │  │
│ …        │ ├────────────────────────────────┤ │ │    +1 919 555 0123            │  │
│          │ │● Jane Appleseed      9:41a     │ │ ├────────────────────────────────┤  │
│          │ │  Sounds good, see you then   ▸ │ │ │                  ┌───────────┐ │  │ ← outbound (right, cyan)
│          │ │  Mom               Yesterday   │ │ │                  │ On my way │ │  │
│          │ │  Did you eat?                ▸ │ │ │                  └───────────┘ │  │
│          │ │  +1 800 …          Mon          │ │ │ ┌───────────┐                 │  │ ← inbound (left, raised)
│          │ │  Your code is 4471…         ▸ │ │ │ │ Sounds gd │                 │  │
│          │ │                                │ │ │ └───────────┘                 │  │
│          │ │                                │ │ ├────────────────────────────────┤  │
│          │ │                                │ │ │ [ type a message…    ] [Send] │  │ ← compose bar
│          │ └────────────────────────────────┘ │ └────────────────────────────────┘  │
└──────────┴────────────────────────────────────┴──────────────────────────────────┘
```

- **Thread list pane (380px):**
  - `.panel-header` "TEXTS" + unread-thread-count `.phone-pill.cyan` + "New" button (`.phone-btn-sm`).
  - Each thread row: `.list-item-touch` → `[unread dot][avatar 44px][1fr: contact name (title) + last-message preview (subtitle, 1 line ellipsis)][timestamp mono, right][chevron]`. Selected thread gets `.list-item-touch.list-item-active` (cyan left border + accent bg).
  - Unread thread: leading cyan dot + name in `--text-high` 600 + preview in `--text-medium`. Read: dimmer, no dot.
  - Timestamp formatting: relative-smart — `9:41a` today, `Yesterday`, `Mon`, then `Jun 3` (see `copy.md`).
- **Conversation pane (1fr):**
  - `conv-header`: back chevron (collapses to list on narrow — but at 1920 both panes are always visible; the back affordance is for completeness / future) + contact name (title) + E.164 sub-line (mono, `--text-medium`).
  - **Message scroll region:** bubbles, newest at bottom, auto-scrolls to bottom on open and on new message. Inbound bubbles left-aligned on `.surface-raised`; outbound right-aligned on a cyan-tinted surface. Each bubble: text + a tiny timestamp + (outbound only) a status glyph (sending spinner / sent check / failed ⚠). Full bubble spec in `tokens.md` §Bubbles.
  - **Compose bar:** pinned to the bottom of the pane. `.phone-input` (grows to fill) + a `.phone-btn-sm` Send button (disabled while empty or sending). A live **character / SMS-segment counter** appears once the message is long enough to matter (see `interactions.md` §Compose). Tapping the input on the kiosk must summon text entry — see Open Dependency 1.

### Screen D — New message (compose to a new recipient)

Triggered by "New" in the Texts header. For v1 keep it minimal:
- A lightweight composer that replaces the conversation pane: a **recipient field** (number or contact pick) at top where `conv-header` would be, then the same compose bar.
- Recipient entry: a `.phone-input` for a number (normalized server-side to E.164 per ADR §4.2) plus an optional "pick from contacts" affordance reusing the existing contacts list. Contact-pick is **nice-to-have**; number-entry is the floor. See Open Question 4.
- On send: ADR §4.1 — new 1:1 thread id is `t.+<E164>`; RotaryPhone normalizes. On success the poller surfaces the sent message and the composer becomes a normal conversation view for that thread.

---

## Open dependencies (must resolve before/with implementation)

1. **On-screen keyboard for SMS compose (BLOCKING for compose on a keyboardless kiosk).** No usable Radzen text-entry-for-touch exists today. Options, for owner/Architect+Planner to pick:
   - (a) Build a small Radzen-native on-screen keyboard component (re-skin `virtual-keyboard.css` off MudBlazor selectors onto `.virtual-keyboard-container` + Radzen, wire to the `.phone-input`). Highest effort, best kiosk UX. *Recommended.*
   - (b) Assume a USB/BT keyboard is attached to the kiosk and ship compose as a plain focusable `.phone-input` (read paths — voicemail, transcript, thread reading — have **no** such dependency and can ship first).
   - (c) Ship voicemail + text **read** first (no keyboard needed), defer **send/compose** to a fast-follow once the keyboard question is answered. This cleanly matches the ADR PR split (read PRs 1–3 vs send PR4).
2. **RadioConsole-side DTO/service/hub additions.** The §6.1 DTOs are defined on the RotaryPhone side; RadioConsole needs matching client records + a `GvVoicemailApiService` / extended `GvSmsApiService` + the `SmsReceived` / `VoicemailReceived` hub subscriptions. This is RadioConsole-repo work (`D:\prj\RTest\RTest`) and must be coordinated via the boundary doc, exactly as prior phases were (see `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` Integration Points). Architect/Planner own the split; this spec only defines what the UI consumes.
3. **Audio first-play latency.** ADR §6.4 notes first play fetches from Google through the proxy. The play button needs a loading state (spinner) and a failure state (see `copy.md`). Confirmed handled in design; no owner decision needed.

## RadioConsole design-system dependency status

**FOUND.** The full design system and the canonical Phone-surface patterns are reachable and read from `D:\prj\RTest\RTest` (design-system.css, the `design_handoff_phone_page` package, and the live `Phone*Panel.razor` components). This spec is built directly on real token names and class names — no "needs tokens to finalize" placeholder is required for the **visual** layer. The only genuinely-new visual primitive (message bubbles) is fully specified from existing tokens in `tokens.md`. The one true unknown is the **keyboard component** (Open Dependency 1), which is a platform/interaction gap, not a missing token.
