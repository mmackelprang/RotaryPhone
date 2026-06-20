# Copy & Microcopy

Voice: plain, calm, never jargon. This is a home appliance for one non-technical user. Sentence case. No exclamation marks. Never blame the user. Errors say what happened + what to do, in that order. Match the existing app's restraint (the Phone dashboard says "Awaiting Call", "Lift the handset to place a call" — that register).

Date format: Today's date for examples is 2026-06-20.

---

## Voicemail

### Headers / labels
- Tab label: `Voicemail`
- Panel header: `VOICEMAIL` (uppercase per `.panel-header`)
- Unheard pill: `{n} unheard` (e.g. `3 unheard`) — `.phone-pill.cyan`
- Refresh icon button: `aria-label="Refresh voicemail"`
- Row chevron: decorative; row `aria-label="Voicemail from {caller}, {duration}, {received-relative}{, unheard}"`

### Row content
- Caller line: resolved contact name → else DTO `FromName` → else formatted number (e.g. `+1 919 555 0123` → `(919) 555-0123`).
- Duration: `m:ss` (e.g. `0:42`), `--font-mono`.
- Transcript preview: first line of transcript, single line, ellipsis. If none: see pending/absent below.

### Player
- Play button: `aria-label="Play voicemail from {caller}"`; when playing: `aria-label="Pause"`.
- Time readout: `{elapsed} / {total}` e.g. `0:14 / 0:42`.
- Buffering hint (optional, under transport): `Fetching recording…`
- Transcript heading: `Transcript`
- Transcript pending: `Transcript pending — Google is still transcribing this voicemail.`
- Transcript absent: `No transcript available.`
- Quick actions (if shipped): `Call back` · `Text back`

### States
- Loading: (skeleton, no text)
- Empty: icon `voicemail` + `No voicemails.`
- Error (initial load): icon `cloud_off` + `Couldn't load voicemail.` + button `Retry`
- Audio error (inline): `Couldn't load this recording.` + button `Retry`

### Toasts
- New voicemail (push): title `New voicemail`, body `{caller} · {duration}` (Info)
- Audio failed: title `Playback failed`, body `Couldn't load this recording. Try again.` (Error)

---

## Texts

### Headers / labels
- Tab label: `Texts`
- Panel header: `TEXTS`
- Unread pill: `{n}` (count of unread threads) — `.phone-pill.cyan`
- New button: `＋ New` (label `New`, icon `add` / `edit` — use `edit_square` or `add`)
- Conversation header: contact name (title) + E.164 sub-line.
- Back chevron (conversation): `aria-label="Back to conversations"`

### Thread row
- Name line: contact name → else formatted number.
- Preview: last message text, single line, ellipsis. Prefix outbound with `You: ` (e.g. `You: On my way`).
- Timestamp (relative): see "Timestamp formatting" below.
- Row `aria-label="Conversation with {name}, last message {preview}, {timestamp}{, unread}"`

### Conversation
- Inbound bubble `aria-label`: `Received, {time}: {text}`
- Outbound bubble `aria-label`: `Sent, {time}: {text}` / sending: `Sending: {text}` / failed: `Failed to send: {text}. Double-tap to retry.`
- Bubble timestamp: time only within a day (e.g. `9:41 AM`); date separators between days (`Yesterday`, `Monday`, `Jun 3`).
- Failed bubble sub-line: `Failed to send` + retry affordance `aria-label="Retry sending"`.

### Compose bar
- Input placeholder: `Message`
- Input `aria-label`: `Type a message`
- Send button: label `Send` (or paper-plane icon `send`); `aria-label="Send message"`
- Segment counter (once long): `{chars} · {n} SMS` e.g. `161 · 2 SMS`
- Texting-unavailable pill: `Texting unavailable`

### New-message composer
- Recipient placeholder: `Phone number`
- Recipient `aria-label`: `Recipient phone number`
- Pick-contact affordance (if shipped): `Pick contact`
- Invalid recipient (inline): `Enter a valid phone number.`

### States
- Loading: (skeleton)
- Empty (no threads): icon `forum` + `No conversations yet.` + button `New message` (if compose ships)
- Empty (thread has no messages — new composer): conversation pane shows `Start the conversation below.`
- Error (load): icon `cloud_off` + `Couldn't load conversations.` + button `Retry`

### Toasts
- New text (push): title `{contact or formatted number}`, body `{message preview}` (Info)
- Sent ok: (no toast — the bubble's sent check is enough; avoid toast noise on every send)
- Send failed (generic): title `Message not sent`, body `Couldn't send your message. Try again.` (Error)
- Rate-limited: title `Slow down`, body `Sending too fast — wait a moment.` (Warning)

---

## §Send-errors (failed-send matrix)

Map RotaryPhone's `SendSmsResponse.Error` / HTTP status to user copy. The user copy is intentionally non-technical; log the raw error.

| Condition | Failed-bubble sub-line | Toast body |
|---|---|---|
| Generic non-200 | `Failed to send` | `Couldn't send your message. Try again.` |
| Auth decay (GV reconnecting) | `Failed to send` | `Couldn't send — Google Voice needs to reconnect. Try again shortly.` |
| Invalid number (INVALID_ARGUMENT) | `Invalid number` | `That number doesn't look right. Check it and try again.` |
| Rate-limited (429) | `Failed to send` | `Sending too fast — wait a moment.` |
| Timeout / network | `Failed to send` | `No response — check the connection and try again.` |

---

## §Auth (GV reconnecting banner)

Shown atop Voicemail and Texts panels when `/api/gvbridge/status` reports unavailable / cookies invalid:

- Banner text: `Google Voice is reconnecting — voicemail and texts may be delayed.`
- Tone: amber accent, calm, non-blocking. Auto-clears on recovery.
- While shown, compose Send is disabled with the `Texting unavailable` pill + tooltip `Google Voice is reconnecting.`

---

## §Generic / §Voicemail (shared)

- Transient refresh failure (list already loaded): no banner, no blank — toast Warning `Couldn't refresh. Showing the last update.`
- Retry buttons everywhere read `Retry`.
- Never show raw HTTP codes, stack traces, or "INVALID_ARGUMENT" to the user. Those go to logs only.

---

## Timestamp formatting (shared rule)

Relative-smart, local time:
- Within today: time, e.g. `9:41 AM` (or `9:41a` in the compact thread-row variant to save width).
- Yesterday: `Yesterday`
- Within last 7 days: weekday, `Monday`
- Older, same year: `Jun 3`
- Older, prior year: `Jun 3, 2025`

Durations (voicemail): `m:ss`, e.g. `0:08`, `1:15`, `12:04`. Use `--font-mono` tabular-nums so they don't jitter.
