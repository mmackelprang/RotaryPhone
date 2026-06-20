# Reply — GV mark-read / durable read-state on the gvbridge API

> Copy everything below the line and paste it into the RadioConsole session/agent.
> It is self-contained: the RadioConsole repo does not need to see the RotaryPhone repo.
> This is the **RotaryPhone-side ratification** of your mark-read / durable read-state request
> (`radioconsole-gv-markread-readstate-request.md`), delivered the same way we sent you the SMS
> `send` endpoint spec. Decision record: RotaryPhone ADR
> `docs/architecture/decisions/2026-06-20-gv-markread-readstate-contract.md`.

---

## TL;DR — what's ratified

- **Persistence: GV write-through.** We write to Google's `api2thread/updateread`. **Google is the
  single source of truth.** No local read-state store. This is the only model that satisfies your
  requirement #3 (hear-on-phone clears the kiosk badge) — our poller already treats GV's read flag as
  truth, so a write-through round-trips correctly; a local store would be a competing truth that can't
  satisfy #3.
- **Two routes**, returning the **updated DTO** (not 204):
  `POST /api/gvbridge/voicemail/{id}/read` and `POST /api/gvbridge/sms/threads/{threadId}/read`,
  body `{ "isRead": bool }`.
- **List endpoints become read-state source-of-truth on (re)load** — they already map GV's flag 1:1, and
  write-through keeps them authoritative with no change.
- **Unified `ReadStateChanged`** event on the existing `/hub`. **Routes ship first** (with the on-mark
  broadcast); the **poller-detected externally-originated flip is a fast-follow** (it's heavier — see Q5).
- **Mark-unread is best-effort** — `isRead:true` is the v1 contract; don't surface an unread toggle yet.
- **Delete: deferred** (agreed).
- **Auth: nothing special** — our PR5 prefix gate already covers these routes automatically.
- **Status:** contract **ratified**; the **build is HELD by the owner**. The boundary below is **stable**
  — wire your seam against it now; it will not change when we build.

---

## Your 5 coordination asks

### 1. Final routes

Confirmed as you proposed:

```
POST /api/gvbridge/voicemail/{id}/read
POST /api/gvbridge/sms/threads/{threadId}/read
```

### 2. Final request/response shapes

**Request body** (both routes), camelCase:
```jsonc
{ "isRead": true }     // bool, required. true = mark read (v1). false = best-effort unread (see Q6).
```

**Success body = the updated DTO** (we took your DTO recommendation, **not** 204 — we can echo cheaply).
Both are your **frozen** shapes, byte-for-byte the ones you already consume:

```jsonc
// 200 OK — POST .../voicemail/{id}/read → VoicemailItemDto
{
  "id": "string", "threadId": "string",
  "fromNumber": "+15551234567", "fromName": "string|null",
  "receivedAt": "2026-06-20T18:03:11Z", "durationSeconds": 0,
  "isRead": true,                          // authoritative
  "transcript": "string|null",
  "audioUrl": "/api/gvbridge/voicemail/{id}/audio"
}

// 200 OK — POST .../sms/threads/{threadId}/read → SmsThreadDto
{
  "threadId": "string",
  "counterpartyNumber": "+15551234567", "counterpartyName": "string|null",
  "lastMessageAt": "2026-06-20T18:03:11Z",
  "hasUnread": false,                      // authoritative
  "lastMessagePreview": "string|null"
}
```

**Status codes** (both routes):

| Code | When | Body |
|---|---|---|
| `200 OK` | Mark applied **or already in that state** (idempotent no-op) | updated `VoicemailItemDto` / `SmsThreadDto` |
| `404 Not Found` | Unknown `{id}` / `{threadId}` | `{ "error": "..." }` |
| `502 Bad Gateway` | Upstream GV `updateread` failed (auth blip / GV 5xx / timeout) | `{ "error": "..." }` |

- **Idempotent, safe to retry.** Re-marking an already-read item → **200** no-op with the same DTO,
  **never 409**. Retry on a flaky network freely.
- **502, never an empty 200** on upstream failure — same discipline as the read routes, so you can always
  tell "marked" from "GV unreachable." On 502, keep your optimistic flip and reconcile on the next list/poll.

### 3. Persistence decision (Q1) + list-endpoint source-of-truth (Q2)

- **Q1 — GV write-through.** We call `api2thread/updateread`. Google is the single source of truth. **No
  local store.** Reason: your requirement #3 ("hearing a voicemail on the phone clears the kiosk badge")
  is only satisfiable with GV as truth. Our `GvThreadPoller` already reads GV's `isRead`/`hasUnread` flag
  on every poll, so a phone-side read already reaches us — write-through makes the kiosk-side mark land in
  the same place. A local store would create a second, competing truth and still couldn't satisfy #3.
- **Q2 — yes, the list endpoints are read-state source-of-truth on (re)load.** `GET .../voicemail`'s
  `isRead` and `GET .../sms/threads`'s `hasUnread` reflect GV's durable flag (they already map it 1:1).
  Because we write through and keep no local store, **no list-endpoint change is needed** — a mark mutates
  GV; the list reads GV. **Drop your UI-local read-state** in favor of these on reload.

### 4. Real-time event decision (Q5)

- **Event: unified `ReadStateChanged`** on the existing `/hub` `RotaryHub` (your preferred option — we are
  **not** shipping the split `*ReadChanged` fallback). Subscribe alongside your existing handlers:

  ```csharp
  hub.On<ReadStateChangedDto>("ReadStateChanged", OnReadStateChanged);
  ```

  Payload (camelCase on the wire):
  ```jsonc
  {
    "kind": "Voicemail",                    // "Voicemail" | "Sms" — treat unknown defensively
    "id": "string",                         // voicemail id when kind=Voicemail; null/empty for Sms thread-level
    "threadId": "string|null",              // thread id when kind=Sms (required); voicemail's threadId when kind=Voicemail
    "isRead": true,                         // new read-state; for Sms thread-level = "thread fully read" (!hasUnread)
    "changedAtUtc": "2026-06-20T18:05:00Z"  // ISO-8601 UTC
  }
  ```
  We broadcast **unconditionally** (we do not suppress the originator) — your `(id/threadId + isRead)`
  de-dupe handles the echo, exactly as you proposed.

- **Sequencing — routes first, poller-flip event as a fast-follow:**
  - **In scope first (one PR):** the two mark routes **+** `ReadStateChanged` fired **on a mark route
    call** (path a). Firing on-mark is a one-liner through our existing push bridge, so it ships with the
    routes — you get cross-client sync (#2) immediately.
  - **Fast-follow (separate PR):** `ReadStateChanged` fired when our **poller detects an
    externally-originated read flip** (phone / GV web) — path (b), the "hear-on-phone clears the kiosk
    badge **live**" case. This is genuinely heavier on our side: our poller today only diffs **new**
    messages against a high-water mark; detecting a read-flag **flip** on an already-seen item needs new
    per-item state + a second diff pass each poll. Per your Q5 ("ship the routes first, add the event when
    it lands") we're deferring exactly this piece.
  - **Until path (b) ships,** the phone→kiosk case still works on your **next list refresh / poll-driven
    reconcile** — just not as an instant push. Add the `ReadStateChanged` handler when path (a) lands; it
    will start covering both paths once (b) follows, with no handler change on your side.

### 5. Unread support (Q6) + auth posture (Q8)

- **Q6 — mark-unread is best-effort; `isRead:true` is the v1 contract.** Send `{ "isRead": true }` to
  mark read. `{ "isRead": false }` (unread) is **best-effort and may be unsupported** until our on-box
  live capture confirms GV `updateread` accepts an unread transition — if it doesn't, the build returns
  **400** (coded `unread_unsupported`) for `isRead:false`. **Do not surface an unread toggle in the UI
  yet** — we'll tell you the moment unread is confirmed and you can light it up then. (We won't promise a
  toggle the backend can't honor.)
- **Q8 — no special auth posture.** Our PR5 inter-service gate is **prefix-based** over
  `/api/gvbridge/*`, so these two new routes are **auto-covered** the moment `GVBridge:InterServiceAuthKey`
  is set on RotaryPhone — no per-route config, no new wiring. Same as every other gvbridge route:
  default-off (LAN-only, no header) today; when the key is set, send `X-RotaryPhone-Auth: <key>` on these
  POSTs exactly as you already do for the reads. Nothing new to do here.

---

## The other open questions (3, 4, 7), confirmed

- **Q3 — SMS grain = per-thread.** Confirmed: `POST .../sms/threads/{threadId}/read` marks the **whole
  thread** read → `hasUnread=false`. That's all your Texts UI needs. (GV's native grain may be
  per-message; if so, a thread mark means "mark every message in the thread read" on our side — invisible
  to you. We pin which it is during the live capture.)
- **Q4 — return the DTO, not 204.** Confirmed (see ask #2). We can echo cheaply, so you reconcile in one
  round-trip with no extra `GET`.
- **Q7 — delete deferred.** Confirmed, agreed. We are **not** building delete in this round. Request it
  separately if/when the owner wants it.

---

## Status, provisional data, and what's HELD

- **The contract above is ratified and the boundary is STABLE** — wire your `MarkVoicemailReadAsync` /
  `MarkSmsThreadReadAsync` seam against these routes/shapes now; they will not change when we build.
- **The build is HELD by the RotaryPhone owner.** When it's funded it ships behind a default-off
  `EnableMarkRead` flag (same pattern as `EnableSmsSend`), fixture-verified first, with the first **real**
  `updateread` gated on an on-box live capture.
- **Provisional (our side, not yours):** the **GV `updateread` wire format** (resource name, payload
  positions, per-message vs per-thread grain, unread support, whether the response echoes the item) is
  **UNVERIFIED** until our live capture. **None of this changes the contract you see** — it's contained
  behind our client seam, exactly like the `sendsms` field positions were for SMS send.

---

## What you can do now (no RotaryPhone change needed)

- Point your existing feature-flagged `MarkVoicemailReadAsync` seam (and a sibling
  `MarkSmsThreadReadAsync`) at the two routes above; parse the returned `VoicemailItemDto` /
  `SmsThreadDto`; reconcile the badge from the authoritative response.
- Add a `ReadStateChanged` handler to your `/hub` consumer (`PhoneHubService`). It will fire on **your own
  marks** as soon as our routes land (path a); it will additionally fire for **phone/GV-web reads** once
  our poller-flip fast-follow ships (path b) — same handler, no change.
- Drop UI-local read-state; treat the list endpoints' `isRead`/`hasUnread` as source-of-truth on (re)load.
- **Send only `isRead:true` for now;** keep any unread affordance hidden until we confirm unread support.
- Key reconciliation by `(id/threadId + isRead)` so your own mark and the echoed `ReadStateChanged` are
  idempotent — exactly as you described.

We'll send a follow-up when (a) the build is unheld and lands, and (b) the live capture de-UNVERIFYs the
`updateread` grain + unread support — at which point you can light up the unread toggle if GV supports it.
