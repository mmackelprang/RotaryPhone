# Handoff Prompt — Build the Voicemail + Texts UI on RadioConsole

> Copy everything below the line and paste it into the RadioConsole session/agent.
> It is self-contained: the RadioConsole repo does not need to see the RotaryPhone repo.

---

## Mission

You're building a **Voicemail + Texts (SMS) UI** for RadioConsole. The voice/messaging
backend lives in a **separate service, RotaryPhone**, which owns the Google Voice (GV)
integration end-to-end (all Google credentials, cookie rotation, media proxying). Your job
is the **RadioConsole-side UI/UX only** — you consume a stable HTTP + SignalR API that
RotaryPhone already exposes. You never talk to Google directly and you hold **no Google
credentials**; everything flows through RotaryPhone.

The **read side of the API is already built and merged** (list/read voicemail, listen to
recordings, list/read SMS threads, real-time push of new messages). **SMS send is not built
yet** (held for owner review) — so build send/reply behind a feature flag and stub the call.
You can build the full read experience today.

Develop on a branch and open a PR (per the repo's workflow). Coordinate on the contract:
if anything below is ambiguous against live responses, flag it rather than guessing — some
GV field *values* are still being verified (see "Provisional data" below), though the DTO
**shapes are frozen**.

---

## The integration contract (as built)

**Base host (confirm for your environment):** `http://radio:5004`
- REST base: `http://radio:5004/api/gvbridge`
- SignalR hub: `http://radio:5004/hub` (the existing `RotaryHub` you already use for
  `IncomingCall` / call-state events — just add the new event handlers below)

**Auth / networking posture today:** LAN-only, **no auth header required** right now. A
future `X-RotaryPhone-Auth: <key>` header will become optional/required when the inter-service
auth gate ships (RotaryPhone PR5, owner-hold). Design your API client so a single auth header
can be switched on later via config — but **do not send it today**.

### Voicemail — HTTP

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/gvbridge/voicemail?count=20&pageToken=<token>` | List voicemails (newest first, paged) |
| `GET` | `/api/gvbridge/voicemail/{id}` | Single voicemail metadata |
| `GET` | `/api/gvbridge/voicemail/{id}/audio` | Stream the recording (proxied + cached) |

`GET /api/gvbridge/voicemail` → **VoicemailListDto**:
```jsonc
{
  "items": [
    {
      "id": "string",              // GV message id
      "threadId": "string",
      "fromNumber": "string",      // E.164, e.g. "+15551234567"
      "fromName": "string|null",   // GV display name if known
      "receivedAt": "2026-06-20T18:03:11Z",  // ISO-8601 UTC
      "durationSeconds": 0,        // 0 if unknown
      "isRead": false,
      "transcript": "string|null", // null = pending or unavailable
      "audioUrl": "/api/gvbridge/voicemail/{id}/audio"  // relative
    }
  ],
  "nextPageToken": "string|null",  // null = no more pages
  "fetchedAtUtc": "2026-06-20T18:05:00Z"
}
```
`GET /api/gvbridge/voicemail/{id}` → a single **VoicemailItemDto** (same item shape as above).

**Audio endpoint** `GET /api/gvbridge/voicemail/{id}/audio`:
- `Content-Type: audio/mpeg`, `Accept-Ranges: bytes` — **HTTP Range supported**, so a normal
  `<audio>`/seekable scrubber works out of the box.
- First request may take ~0.5–few seconds (RotaryPhone fetches from Google + caches to disk);
  subsequent plays are cache-fast. **Show a buffering state on first play.**
- On upstream failure the endpoint returns **502** (not an empty 200) — treat as an audio error.

### SMS (read) — HTTP

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/gvbridge/sms/threads?count=20` | List conversations (newest activity first) |
| `GET` | `/api/gvbridge/sms/threads/{threadId}?count=50` | Messages in a thread |

`GET /api/gvbridge/sms/threads` → **SmsThreadListDto**:
```jsonc
{
  "threads": [
    {
      "threadId": "string",
      "counterpartyNumber": "string",     // E.164
      "counterpartyName": "string|null",
      "lastMessageAt": "2026-06-20T18:03:11Z",
      "hasUnread": true,
      "lastMessagePreview": "string|null"
    }
  ],
  "fetchedAtUtc": "2026-06-20T18:05:00Z"
}
```
`GET /api/gvbridge/sms/threads/{threadId}` → **SmsThreadMessagesDto**:
```jsonc
{
  "threadId": "string",
  "messages": [
    {
      "id": "string",
      "threadId": "string",
      "direction": "Inbound",   // "Inbound" | "Outbound"
      "counterpartyNumber": "string",
      "text": "string|null",
      "sentAt": "2026-06-20T18:03:11Z",
      "isRead": false
    }
  ],
  "fetchedAtUtc": "2026-06-20T18:05:00Z"
}
```

### Real-time push — SignalR (on the existing hub)

RotaryPhone polls GV and pushes new items over `RotaryHub`. Subscribe alongside your existing
call events:
```csharp
hub.On<SmsMessageDto>("SmsReceived", OnSmsReceived);            // new inbound SMS
hub.On<VoicemailItemDto>("VoicemailReceived", OnVoicemailReceived); // new voicemail
```
- `SmsReceived` payload = the **message** shape (`id, threadId, direction, counterpartyNumber,
  text, sentAt, isRead`).
- `VoicemailReceived` payload = the **VoicemailItemDto** shape (same as REST).
- Latency: ~0–15 s when a client is active, ~60 s idle (RotaryPhone's adaptive poll). Treat
  push as "freshen the list + notify," and keep REST as the source of truth on (re)load.

---

## What is NOT built yet — stub/flag these

1. **SMS send/reply.** No `POST /api/gvbridge/sms/send` endpoint exists yet (it's specified but
   held for owner review). **Build the compose/reply/Retry UI behind a feature flag**, and put
   the actual network call behind a single client method that currently no-ops or shows
   "coming soon." When the endpoint ships, the request shape will be roughly
   `{ "threadId": "...", "text": "..." }` → returns the created outbound message — wire it then.
2. **Voicemail mark-read / delete.** No GV-side endpoints in v1. Treat "heard/read" as
   **UI-local state** only (don't expect it to persist to Google).
3. **Inter-service auth header.** Don't send `X-RotaryPhone-Auth` yet (see posture above).

---

## Provisional data (code defensively)

RotaryPhone's GV response parsing is **shape-frozen but value-provisional** — the exact GV
field positions are being verified against live data on the box. The **DTO shapes above will
not change**, but you should code defensively:
- `transcript` may be `null` (pending/absent) — handle gracefully.
- `text` may be `null`.
- `direction` is `"Inbound"`/`"Outbound"` — treat unknown values as inbound rather than crashing.
- Timestamps are UTC ISO-8601 — format to local for display.
- `durationSeconds` may be `0` (unknown) — don't show "0:00" as if real.

---

## The UX to build

(These are the intended screens + states from our design exploration. **Adapt them to
RadioConsole's own design system / tokens — reuse your existing components and CSS variables;
do not introduce a new visual language.** Target the kiosk form factor / resolution you
already build for.)

**Placement:** two new surfaces — **Voicemail** and **Texts** — next to your existing phone
surfaces (e.g. as sub-tabs of the phone area). Add unread badges on the tab + any global
phone indicator.

**Screens & required states:**
1. **Voicemail list** — newest first. States: loading (skeleton ~5 rows), loaded, **empty**
   ("No voicemails."), **error** + Retry, new-arrival (row animates in at top, badge++).
2. **Voicemail player** (inline) — play/pause + **seekable scrubber** (range-backed). States:
   idle, **buffering** (first play), playing, paused, ended, audio-error. Transcript: render
   when present, "Transcript pending…" when `null`-but-recent, "No transcript available." when
   absent. Optional "Call back" / "Text back" actions.
3. **Texts thread list** — newest activity first. States: loading, loaded, empty
   ("No conversations yet."), error + Retry, new-inbound (thread bumps to top, unread dot,
   badge++).
4. **Texts conversation** — message bubbles (inbound/outbound), read history. **Compose/reply
   (feature-flagged)** with: optimistic **sending** bubble (dimmed), **sent** (check), **failed**
   (Retry, and **preserve the typed text** — never auto-retry). New inbound while open =
   **append silently, no toast**.
5. **New-recipient composer** — pick/enter an E.164 number, type, send (flagged).
6. **New-arrival notifications** — toast + badge increment + list bump. **Hard rule: a new
   arrival must never steal the screen or pause audio.** No toast if the relevant conversation
   is already open (append in place instead).
7. **Auth-decay degradation** — poll `GET /api/gvbridge/status` (≈10 s); if the GV bridge is
   unavailable, show a **calm reconnecting banner** (not a red alert) and **disable Send** with
   a "Texting unavailable" affordance. Auto-recover when status returns available — RotaryPhone
   handles the actual cookie recovery; you just reflect state.

**Send guardrails (for when send ships):** disable Send while a send is in flight; on HTTP 429
show "Sending too fast — wait a moment," keep the typed text; no auto-retry.

---

## Decisions you need to make / surface to the owner

1. **On-screen keyboard for compose (blocks SMS send on a touch kiosk).** If there's no
   physical keyboard, you need a touch keyboard component. Options: (a) build one for your UI
   framework [recommended], (b) assume a physical keyboard, (c) **ship read-only first**, defer
   compose. Recommend shipping the full **read** experience first regardless, since send is
   backend-blocked anyway.
2. **Heard/read persistence.** v1 is UI-local. Confirm that's acceptable or request GV mark-read
   be pulled forward on the RotaryPhone side.

---

## Deliverables

- A branch + PR implementing the Voicemail and Texts surfaces against the contract above.
- Read experience fully working (list/listen voicemail incl. transcript; list/read SMS;
  live push via SignalR; new-arrival notifications; auth-decay degradation).
- Compose/reply UI built but **feature-flagged off** until RotaryPhone's send endpoint ships.
- Defensive handling of the provisional/null fields noted above.
- A short note back on: the host:port you targeted, any contract mismatches you hit against
  live responses, and your on-screen-keyboard decision.

**Open a thread back to the RotaryPhone side for:** the send endpoint's exact request/response
when PR4 ships, the `X-RotaryPhone-Auth` rollout, and any field-value corrections from the
live capture.
