# Request Prompt — Add GV mark-read / durable read-state to the gvbridge API

> Copy everything below the line and paste it into the RotaryPhone session/agent.
> It is self-contained. This is a **request from the RadioConsole side** for a new capability on
> **your** `gvbridge` API. The contract is yours to ratify — the shapes below are a precise proposal,
> not a fait accompli. Reply with the final routes/shapes/persistence decision the same way you sent
> us the SMS `send` endpoint spec, and we will wire it as a fast-follow.

---

## Mission

The voicemail + texts read UI we built on RadioConsole (against your
`radioconsole-gv-voicemail-sms-ui-handoff.md`) shipped **read-state as UI-local only** — a voicemail
or SMS thread marked "heard/read" in the kiosk loses that state on reload and does not sync to your
other clients or to Google Voice.

The RadioConsole owner has **declined UI-local as the end state.** Read-state must be **durable**:

1. **Survive a RadioConsole reload** (re-fetching the list must return the correct `isRead`/`hasUnread`).
2. **Sync across RadioConsole clients** (two kiosk tabs/devices agree without a manual refresh).
3. **Reflect to Google Voice** (ideally) so the kiosk badge is consistent with the owner's phone / GV
   web client — e.g. **hearing a voicemail on the phone should clear the kiosk badge**, and clearing
   it on the kiosk should be visible in GV.

We are therefore requesting a **mark-read capability** on the gvbridge API. Scope is **voicemail**
(the primary driver) plus **SMS thread read** (companion — same badge model). We are **not** requesting
**delete** here; the original handoff bundled mark-read with delete, and we explicitly **defer delete**.

This maps directly onto something your ADR already anticipated: your
`2026-06-20-gv-voicemail-sms-radioconsole.md` §3.4 notes that GV-side
`api2thread/updateread` / `…/delete`-style write endpoints exist and were deferred until
list+listen+transcript shipped. List+listen+transcript have shipped. This request asks you to build the
**`updateread`** half (not delete) and expose it on the gvbridge contract.

---

## Current state on our side (RadioConsole)

- The full **read** experience is built against your frozen DTOs (`VoicemailItemDto`, `SmsThreadDto`,
  `SmsMessageDto`) and the `/api/gvbridge/*` namespace, with push on the existing `/hub` `RotaryHub`
  (`VoicemailReceived`, `SmsReceived`).
- We already built a **feature-flagged no-op `MarkVoicemailReadAsync` seam** — the same pattern as the
  not-yet-built SMS `send`. It is wired into the UI but currently does nothing durable. The moment your
  endpoint ships, we flip a config flag and point that seam at your route. No UI rework is needed on our
  side to adopt this.
- Our consumer-side decisions are recorded in our ADR-022
  (`design/decisions/2026-06-20-gvbridge-voicemail-sms-integration.md`). The relevant open question
  there (our §12.3 / your §12 retention discussion) is exactly this: pull GV mark-read forward, or stay
  UI-local. The owner has chosen: **pull it forward, durable.**

---

## Requested capability — precise proposed contract (for you to ratify)

Same conventions as the rest of gvbridge: typed records, **camelCase** JSON on the wire, **502** on an
upstream-GV failure (not an empty 200), LAN-only today with the future `X-RotaryPhone-Auth` gate
applied to these routes the same way as the other write/read routes.

### Route 1 — Mark a voicemail read/unread

```
POST /api/gvbridge/voicemail/{id}/read
```

**Request body** (`application/json`):
```jsonc
{
  "isRead": true   // bool, required. true = mark read; false = mark unread (see Q6 on unread support)
}
```

**Success response** — we **recommend returning the updated DTO** (not 204) so we can reconcile our
local state against the server's truth in one round-trip. The DTO is your **frozen `VoicemailItemDto`**,
verbatim shape:

```jsonc
// 200 OK
{
  "id": "string",                 // unchanged — the {id} that was marked
  "threadId": "string",
  "fromNumber": "+15551234567",   // E.164
  "fromName": "string|null",
  "receivedAt": "2026-06-20T18:03:11Z",  // ISO-8601 UTC
  "durationSeconds": 0,
  "isRead": true,                 // reflects the applied change (authoritative)
  "transcript": "string|null",
  "audioUrl": "/api/gvbridge/voicemail/{id}/audio"
}
```

**Status-code table:**

| Code | When | Body |
|---|---|---|
| `200 OK` | Mark applied (or already in that state — see idempotency) | updated `VoicemailItemDto` |
| `404 Not Found` | Unknown voicemail `{id}` | error record / empty |
| `502 Bad Gateway` | Upstream GV `updateread` call failed (auth blip, GV 5xx, timeout) | error record |
| *(409 not expected)* | — | — |

**Idempotency:** re-marking an already-read voicemail (POST `{ "isRead": true }` when it is already
read) is a **no-op success → 200** with the same DTO (`isRead: true`). Do **not** 409/error on
re-mark; we may retry on a flaky network and must be safe to do so. If you can satisfy the mark locally
without an upstream call when GV is already in the target state, that is fine — but the returned DTO must
still reflect the true state.

### Route 2 — Mark an SMS thread read

We request **per-thread** read (the whole conversation), not per-message — it matches the badge model
(`hasUnread` is a thread-level flag in your `SmsThreadDto`) and is the only thing our Texts UI needs.
See Q3 if per-message is meaningfully cheaper or more natural on the GV side.

```
POST /api/gvbridge/sms/threads/{threadId}/read
```

**Request body** (`application/json`):
```jsonc
{
  "isRead": true   // bool, required. true = mark whole thread read (hasUnread → false)
}
```

**Success response** — again we **recommend returning the updated thread summary** (your frozen
`SmsThreadDto`) rather than 204, so we reconcile the badge in one round-trip:

```jsonc
// 200 OK
{
  "threadId": "string",                 // the {threadId} that was marked
  "counterpartyNumber": "+15551234567", // E.164
  "counterpartyName": "string|null",
  "lastMessageAt": "2026-06-20T18:03:11Z",
  "hasUnread": false,                    // reflects the applied change (authoritative)
  "lastMessagePreview": "string|null"
}
```

**Status-code table:**

| Code | When | Body |
|---|---|---|
| `200 OK` | Thread marked read (or already read) | updated `SmsThreadDto` |
| `404 Not Found` | Unknown `{threadId}` | error record / empty |
| `502 Bad Gateway` | Upstream GV failure | error record |

**Idempotency:** marking an already-read thread → **200** no-op with `hasUnread: false`. Safe to retry.

### Why DTO-over-204 (our recommendation, restated)

A `204 No Content` would force us to re-`GET` the item/thread (or trust our optimistic local flip) to
learn the authoritative post-change state — an extra round-trip and a race window. Returning the updated
`VoicemailItemDto` / `SmsThreadDto` lets us **reconcile in place** and is consistent with how the GV web
client treats a mark as a state mutation that returns the new state. If returning the DTO is materially
harder on your side (e.g. `updateread` doesn't echo the item and a re-fetch is expensive), **204 is an
acceptable fallback** and we'll re-fetch — but DTO is preferred.

---

## Real-time read-state push — precise proposed event (for you to ratify)

The durability requirement #2 (sync across clients) and #3 (reflect changes that originate **outside**
RadioConsole — e.g. heard on the phone) both need a **push when read-state changes from ANY source**,
not only when RadioConsole itself calls the mark route. Your `GvThreadPoller` already reads the GV
read/unread flag on every poll (your ADR §5.3 diffs against a high-water mark) — so it is the natural
place to detect an externally-originated read flip and broadcast it.

**Recommended: one unified event on the existing `/hub` `RotaryHub`:**

```csharp
// RadioConsole subscribes alongside the existing VoicemailReceived / SmsReceived handlers:
hub.On<ReadStateChangedDto>("ReadStateChanged", OnReadStateChanged);
```

**`ReadStateChanged` payload** (camelCase on the wire):
```jsonc
{
  "kind": "Voicemail",          // "Voicemail" | "Sms"  (string enum; treat unknown defensively)
  "id": "string",               // voicemail id when kind=Voicemail; may be null/empty when kind=Sms+thread-level
  "threadId": "string|null",    // thread id when kind=Sms (required for Sms); voicemail's threadId when kind=Voicemail
  "isRead": true,               // the new read-state (for Sms thread-level this is "thread fully read", i.e. !hasUnread)
  "changedAtUtc": "2026-06-20T18:05:00Z"  // ISO-8601 UTC, when the change was observed/applied
}
```

**Fire it when read-state changes from ANY source:**
- when RadioConsole (or any client) calls a mark route above, **and**
- when `GvThreadPoller` observes a read/unread flip that originated elsewhere (phone, GV web) on its
  next poll — this is the case that makes "hear it on the phone → kiosk badge clears" work.

**Semantics on our side:** push = "freshen + reconcile." On `ReadStateChanged` we update the matching
row/thread badge in place; REST remains source-of-truth on (re)load. We will **not** echo-loop: a mark
we initiated and a subsequent `ReadStateChanged` for the same id are idempotent on our side (we key by
`id`/`threadId` + `isRead`), so re-broadcasting our own change is harmless. Please broadcast
unconditionally rather than trying to suppress the originator — simpler and we handle the dedupe.

**Fallback if a unified event is awkward on your side** — two split events with the same field
discipline:
```csharp
hub.On<VoicemailReadChangedDto>("VoicemailReadChanged", ...);  // { id, threadId, isRead, changedAtUtc }
hub.On<SmsReadChangedDto>("SmsReadChanged", ...);              // { threadId, isRead, changedAtUtc }
```
We prefer the **unified `ReadStateChanged`** (one handler, one DTO, `kind`-discriminated) but will adapt
to whichever you ship. **Real-time is desirable but not blocking** — see Q5; if the poll-diff broadcast
is more work than the routes, ship the routes first and the event as a fast-follow.

---

## Open questions to confirm (where we have a firm recommendation, it's stated)

1. **Persistence target — GV vs local.** Do you write through to Google's `api2thread/updateread`
   (durable + cross-client + reflects on the phone), or persist read-state **locally** in RotaryPhone
   (durable + cross-client across RadioConsole, but **not** reflected to GV/phone)?
   **Our recommendation: write through to GV** — requirement #3 (hear-on-phone clears the kiosk badge,
   and vice-versa) is only satisfiable with GV as the source of truth. If GV write-through is risky or
   high-effort, a **local read-state store that you reconcile against the poll** is an acceptable v1
   that still satisfies #1 and #2; please say which you're shipping so we set expectations.
   *(This is the single most important answer we need.)*

2. **Do the list endpoints become source-of-truth for read-state?** We assume `GET .../voicemail`'s
   `isRead` and `GET .../sms/threads`'s `hasUnread` will, after this ships, reflect durable read-state
   (so a RadioConsole reload shows correct badges). Confirm. If you persist locally rather than to GV,
   confirm the list endpoints read from that local store (so reload is consistent).

3. **SMS: per-thread vs per-message.** We requested **per-thread** read (matches `hasUnread`). Confirm
   that's the right grain, or tell us if GV's mark is naturally per-message and a thread-level mark
   means "mark all messages in the thread read." Per-thread is all our UI needs.

4. **Return the DTO vs 204.** We **recommend returning the updated DTO** (reconcile in one round-trip).
   Confirm you can echo the item/thread, or tell us it'll be **204** and we'll re-fetch.

5. **Real-time event in scope for this round?** We **recommend** the unified `ReadStateChanged` so
   externally-originated reads (phone/GV web) reach the kiosk. If that's more work than the routes,
   **ship the routes first**; we'll consume them via our mark seam and add the event handler when it
   lands. Tell us whether the event is in this PR or a fast-follow.

6. **Mark-unread / toggle in v1?** Our body uses `{ "isRead": bool }` to allow **unread** too. Confirm
   whether GV (or your local store) supports mark-**unread**. If unread is not supported in v1, we'll
   send only `isRead: true` and you can `400`/ignore `isRead: false` — just tell us which, so we don't
   surface a toggle the backend can't honor.

7. **Delete — explicitly deferred.** We are **not** requesting delete in this round (the original
   handoff bundled mark-read with delete). Confirm you're fine deferring delete; we'll request it
   separately if/when the owner wants it.

8. **Auth posture.** These are write-ish routes (a GV account mutation, if you write through). When the
   `X-RotaryPhone-Auth` gate ships (your PR5), apply it to these routes the same as the others. Until
   then, LAN-only / no header — consistent with the current read routes. Confirm no special auth posture
   for mark-read specifically.

---

## What we'll do when it ships

- Point our existing **feature-flagged `MarkVoicemailReadAsync` seam** (and a sibling
  `MarkSmsThreadReadAsync`) at `POST /api/gvbridge/voicemail/{id}/read` and
  `POST /api/gvbridge/sms/threads/{threadId}/read`, parse the returned `VoicemailItemDto` /
  `SmsThreadDto`, and reconcile the local badge from the authoritative response.
- Add a `ReadStateChanged` (or the split `*ReadChanged`) handler to our existing `PhoneHubService`
  (`/hub` consumer), so externally-originated read flips clear the kiosk badge live.
- Drop the UI-local-only read-state behavior in favor of the durable server state; the list endpoints'
  `isRead`/`hasUnread` become our source-of-truth on (re)load.
- Idempotent + safe-to-retry on our side: we key reconciliation by `id`/`threadId` + `isRead`, so a mark
  we initiated and the echoed `ReadStateChanged` will not double-apply.

This is a **fast-follow to our Texts PR (GV-3)** — small consumer change, gated behind the same flag
pattern as SMS `send`, lit up in one config flip when your endpoint ships.

---

## Coordination

Please reply the same way you sent us the SMS `send` endpoint spec — with:

1. **Final routes** (confirm/adjust `POST /api/gvbridge/voicemail/{id}/read` and
   `POST /api/gvbridge/sms/threads/{threadId}/read`).
2. **Final request/response shapes** (confirm the `{ isRead }` body and that the success body is the
   updated `VoicemailItemDto` / `SmsThreadDto` — or tell us it's 204).
3. **The persistence decision** (Q1 — GV write-through vs local store) and whether the list endpoints
   become read-state source-of-truth (Q2).
4. **The real-time event decision** (Q5 — unified `ReadStateChanged` in this PR, split events, or
   fast-follow).
5. **Unread support** (Q6) and the **auth posture** (Q8).

Once we have those five, we wire it as a fast-follow to GV-3. Please also note the change in the
boundary doc's Integration Points table (the two new mark-read routes + the `ReadStateChanged` event)
so future sessions on both sides pick it up.
