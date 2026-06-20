# ADR: GV mark-read / durable read-state — gvbridge contract ratification

- **Status:** Accepted (contract ratified — **implementation HELD by owner**)
- **Date:** 2026-06-20
- **Author:** Architect (contract ratification)
- **Arc:** `docs/plans/gv-voicemail-sms-arc.md`
- **Addendum to / extends:** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md`
  (the parent ADR — §3.4 anticipated `api2thread/updateread`; §11 is the live-verification checklist).
- **Request being answered:** `docs/prompts/radioconsole-gv-markread-readstate-request.md` (RadioConsole side).
- **Reply produced:** `docs/handoffs/radioconsole-gv-markread-reply.md`.

> **Honesty constraint (inherited from the parent ADR).** This ratifies the **gvbridge contract
> boundary** (routes, request/response shapes, the SignalR event, persistence model) — that boundary
> is **STABLE** and RadioConsole can wire against it now. It does **NOT** verify the GV-side
> `api2thread/updateread` wire format. No authenticated calls were made to Google from this
> environment. The exact `updateread` payload/positions are **UNVERIFIED — pending the parent ADR §11
> live capture** (a new `updateread` step is added to that checklist below). The contract boundary is
> deliberately decoupled from the GV wire format: whatever `updateread` turns out to look like on the
> wire, the routes/shapes RadioConsole sees do not change.

---

## 1. Context

The read experience (voicemail list/listen/transcript + SMS thread read, with SignalR push) shipped
across PRs #54/#56/#57. The RadioConsole UI built against it (their handoff) shipped read-state as
**UI-local only** — "heard/read" is lost on reload and does not sync across kiosk clients or to Google.

The RadioConsole owner has **declined UI-local as the end state** and requested a **durable mark-read
capability** on the gvbridge API. Three durability requirements:

1. **Survive a RadioConsole reload** — re-fetching the list returns the correct `isRead`/`hasUnread`.
2. **Sync across RadioConsole clients** — two kiosk tabs/devices agree without a manual refresh.
3. **Reflect to/from Google Voice** — e.g. **hearing a voicemail on the phone clears the kiosk badge**,
   and clearing it on the kiosk is visible in GV.

Scope: **voicemail read** (primary) + **SMS thread read** (companion). **Delete is explicitly deferred**
(agreed both sides). This is exactly the write half the parent ADR §3.4 anticipated and deferred until
list+listen+transcript shipped — they have shipped, so this builds the `updateread` half (not delete).

This ADR ratifies the contract. **The build is HELD by the owner** — this document records the agreed
shape so RadioConsole can wire its existing seam and so a future Planner/Builder cycle has a frozen target.

---

## 2. Decision (summary)

| # | Question | Ratified decision |
|---|----------|-------------------|
| 1 | **Persistence** | **GV write-through** — call GV `api2thread/updateread`. Google stays the single source of truth. **No local read-state store.** |
| 2 | **Routes** | `POST /api/gvbridge/voicemail/{id}/read` and `POST /api/gvbridge/sms/threads/{threadId}/read`. Body `{ "isRead": bool }` (camelCase). |
| 3 | **Response** | **Return the updated DTO** (`VoicemailItemDto` / `SmsThreadDto`), not 204. `200` applied-or-already (idempotent), `404` unknown id/thread, `502` upstream-GV failure. |
| 4 | **SMS grain** | **Per-thread** — "mark all messages in the thread read" → `hasUnread=false`. (GV native grain may be per-message; a thread mark means mark-all.) |
| 5 | **Real-time event** | **Unified `ReadStateChanged`** on `RotaryHub`. **Routes ship first (in-scope); poller-diff `ReadStateChanged` is a fast-follow.** |
| 6 | **Mark-unread** | v1 honors `isRead:true`. `isRead:false` is **best-effort**, may `400`/be unsupported until live capture confirms GV supports unread. No toggle promised. |
| 7 | **Delete** | **Deferred** (agreed). |
| 8 | **Auth** | **No special posture.** PR5's prefix middleware already gates `/api/gvbridge/*` — the new routes are auto-covered when `InterServiceAuthKey` is set. |
| — | **Safety posture (future build)** | Behind `EnableMarkRead` config flag, **default-off** (mirrors `EnableSmsSend`); fixture-verified; first real mark pending §11. Stated for rollout shape — **not built here.** |

---

## 3. The central decision — persistence = GV write-through (Q1)

**Decision: write through to Google's `api2thread/updateread`. Google is the single source of truth.
No local read-state store.** (Decided jointly by both owners; ratified here.)

### 3.1 Why write-through is the only option that satisfies all three requirements

The deciding requirement is **#3** — "hear it on the phone → the kiosk badge clears." Our shipped
`GvThreadPoller` **already treats GV's read flag as truth**: on every poll it parses `IsRead`
(`GvSmsNode.IsRead`, `GvVoicemailNode.IsRead`) and `HasUnread` (`GvThreadNode.HasUnread`) straight off
the GV `api2thread/list` response (see `PositionalGvThreadParser`, consumed in
`GvThreadPoller.PollSmsAsync`/`PollVoicemailAsync` and `GvSmsController`/`GvVoicemailController`). When
the owner hears a voicemail on the phone, **GV flips that flag**, and our next poll already observes it.

A **local read-state store** would create a **competing source of truth**:

- It cannot satisfy #3 in the **phone → kiosk** direction without a reconciliation rule for "the local
  store says unread but GV says read" — and the only sane resolution is "GV wins," which makes the local
  store redundant for that case.
- It introduces a **divergence class** the read endpoints must then arbitrate on every list call (local
  flag vs GV flag). Today the list endpoints map the GV flag 1:1 (`HasUnread: t.HasUnread ?? false`,
  `IsRead: m.IsRead ?? false`). A local store forces a merge step into the hottest read path.

Write-through has none of this: a mark is a write to GV; the next poll/list reads the same truth back.
The list endpoints (§5) **stay a 1:1 map of GV's flag** — no merge, no arbitration.

### 3.2 Cost / risk accepted

- `updateread` is a **real GV account write** (like `sendsms`). It is reversible in spirit (re-mark),
  but it is a live mutation — so the future build gets the **same safety treatment as send**: a
  default-off `EnableMarkRead` flag (§8), and it does not go live until the §11 capture confirms the
  wire format.
- The **exact `updateread` payload is UNVERIFIED** (§7). This is contained behind the same client seam
  pattern as `sendsms` — the contract boundary RadioConsole sees does not depend on it.
- A mark is only as durable as GV accepts it; on an upstream failure we return **502** (§4), never a
  false 200. RadioConsole keeps its optimistic flip and reconciles on the next list/poll.

### 3.3 Consequence for the list endpoints (Q2)

**Confirmed: after this ships, the list endpoints are the read-state source-of-truth on (re)load.**
`GET /api/gvbridge/voicemail`'s `isRead` and `GET /api/gvbridge/sms/threads`'s `hasUnread` reflect GV's
durable flag (they already do — they map it 1:1). Because we write through to GV and do not keep a local
store, **no change to the list endpoints is required** for them to be authoritative — a mark mutates GV,
and the existing list path reads GV. RadioConsole drops UI-local read-state in favor of these on reload.

---

## 4. Routes, shapes, status codes (Q2, Q3, Q4)

Two new routes on the existing controllers (`GvVoicemailController`, `GvSmsController`). Same gvbridge
conventions: typed records, **camelCase** on the wire, **502** on upstream-GV failure (never an empty 200).

### 4.1 Mark a voicemail read

```
POST /api/gvbridge/voicemail/{id}/read
Body: { "isRead": true }            // camelCase, bool, required
```

Returns the **frozen `VoicemailItemDto`** (verbatim the shipped shape in `GvBridgeReadDtos.cs`) with the
authoritative `isRead`:

```jsonc
// 200 OK — VoicemailItemDto
{
  "id": "string", "threadId": "string",
  "fromNumber": "+15551234567", "fromName": "string|null",
  "receivedAt": "2026-06-20T18:03:11Z", "durationSeconds": 0,
  "isRead": true,                         // reflects the applied change (authoritative)
  "transcript": "string|null",
  "audioUrl": "/api/gvbridge/voicemail/{id}/audio"
}
```

### 4.2 Mark an SMS thread read (per-thread — Q4)

```
POST /api/gvbridge/sms/threads/{threadId}/read
Body: { "isRead": true }            // true = mark whole thread read → hasUnread=false
```

Returns the **frozen `SmsThreadDto`** with the authoritative `hasUnread`:

```jsonc
// 200 OK — SmsThreadDto
{
  "threadId": "string",
  "counterpartyNumber": "+15551234567", "counterpartyName": "string|null",
  "lastMessageAt": "2026-06-20T18:03:11Z",
  "hasUnread": false,                     // reflects the applied change (authoritative)
  "lastMessagePreview": "string|null"
}
```

**Grain (Q4):** per-thread. "Mark the thread read" → `hasUnread=false`. GV's native `updateread` grain
**may be per-message**; in that case a thread-level mark means **mark every message in the thread read**
(iterate the thread's message ids, or use a thread-level updateread arg if GV exposes one). Which it is,
is **UNVERIFIED — pin at the §11 capture** (§7). RadioConsole's contract is per-thread regardless.

### 4.3 Status table (both routes)

| Code | When | Body |
|---|---|---|
| `200 OK` | Mark applied **or already in that state** (idempotent no-op) | updated `VoicemailItemDto` / `SmsThreadDto` |
| `404 Not Found` | Unknown `{id}` / `{threadId}` | `{ "error": "..." }` |
| `502 Bad Gateway` | Upstream GV `updateread` failed (auth blip, GV 5xx, timeout) | `{ "error": "..." }` |

- **Idempotency: re-marking an already-read item is a 200 no-op, never 409.** Safe to retry on a flaky
  network. If GV is already in the target state the server MAY satisfy the mark without an upstream call,
  but the returned DTO must still reflect the true state. (Mirrors the §4.2 send taxonomy discipline:
  honest status, never mask.)
- **502 mirrors the existing read routes** — `GvSmsController.GetThreads`/`GetThreadMessages` and
  `GvVoicemailController.GetList` already return 502 on `!Succeeded` rather than an empty 200; the mark
  routes follow the same rule so RadioConsole can't confuse "marked" with "GV unreachable."

### 4.4 Why DTO-over-204 (Q3)

Returning the updated DTO lets RadioConsole **reconcile the badge in one round-trip** against server
truth, with no extra `GET` and no race window. We **can** echo it: the voicemail path already does a
list+filter to resolve a single item (`GvVoicemailController.FindNodeAsync`), and the SMS thread summary
is a list+filter too — so a post-mark re-read to build the response DTO is the same cheap call the read
routes already make. **DTO is ratified, not 204.** (204 was offered by RadioConsole only as a fallback if
echoing were expensive; it is not, so we take the DTO.)

---

## 5. Real-time event — unified `ReadStateChanged`, routes-first (Q5)

### 5.1 Ratified event

A single unified event on the existing `/hub` `RotaryHub`, alongside `SmsReceived` / `VoicemailReceived`
/ `SmsSent`:

```jsonc
// "ReadStateChanged" — camelCase on the wire
{
  "kind": "Voicemail",                    // "Voicemail" | "Sms" (treat unknown defensively)
  "id": "string",                         // voicemail id when kind=Voicemail; null/empty for Sms thread-level
  "threadId": "string|null",              // thread id when kind=Sms (required); voicemail's threadId when kind=Voicemail
  "isRead": true,                         // new read-state; for Sms thread-level this is "thread fully read" (!hasUnread)
  "changedAtUtc": "2026-06-20T18:05:00Z"  // ISO-8601 UTC, when the change was observed/applied
}
```

Fired when read-state changes from **ANY source**:
- **(a)** when a mark route (§4) is called, **and**
- **(b)** when `GvThreadPoller` observes an **externally-originated** read flip (phone / GV web) on its
  next poll — this is the case that makes "hear on phone → kiosk badge clears" reach the kiosk live.

Broadcast **unconditionally** (do not try to suppress the originator). RadioConsole de-dupes by
(`id`/`threadId` + `isRead`); a client's own mark and the echoed `ReadStateChanged` are idempotent on
their side. We will **not** ship the split `VoicemailReadChanged`/`SmsReadChanged` fallback — unified is
cleaner and RadioConsole prefers it.

### 5.2 Sequencing recommendation — **routes in-scope-first; poller-diff event is a fast-follow**

RadioConsole's Q5 explicitly permits routes-first. **Recommendation: ship the two mark routes first; the
poller-diff `ReadStateChanged` is a fast-follow.** Rationale grounded in the as-built code:

- **Path (a) — fire on mark — is trivial.** The mark route already builds the authoritative DTO for its
  response; firing `ReadStateChanged` from there is a one-line broadcast through the **existing**
  `GvMessagePushBridge` pattern (it already does exactly this for `SmsReceived`/`VoicemailReceived`/
  `SmsSent` via `_hubContext.Clients.All.SendAsync(...)`). This could even ship **with** the routes at
  near-zero marginal cost, and the recommendation is to include path (a) in the routes PR.
- **Path (b) — fire on externally-originated flip — is genuinely heavier.** `GvThreadPoller` today
  diffs **only on the per-thread high-water mark** (`GvHighWaterMark.IsNewMessage` — new id/timestamp).
  It does **not** track the **read-flag of already-seen items**, so detecting a read/unread **flip** on
  an item already past the high-water mark requires **new state**: a per-item last-seen `IsRead` map and
  a second diff pass over the full list each poll, plus seed/restart semantics so a cold start doesn't
  replay every item as a "flip." That is a real, separable unit of work — exactly the kind RadioConsole
  said to defer if heavier than the routes.

So: **in-scope-first = the two routes + path-(a) on-mark `ReadStateChanged`. Fast-follow = path-(b)
poller-diff `ReadStateChanged`** (the externally-originated detection). RadioConsole consumes the routes
via its existing `MarkVoicemailReadAsync`/`MarkSmsThreadReadAsync` seam immediately, adds the
`ReadStateChanged` handler when path (a) lands, and the "phone clears the kiosk badge live" case lights
up when path (b) follows. Until path (b) ships, that case still works on the **next poll-driven list
refresh** — just not as an instant push.

---

## 6. Mark-unread (Q6) and auth (Q8)

### 6.1 Mark-unread — best-effort, `isRead:true` is the v1 contract

The body is `{ "isRead": bool }` to leave room for unread, but **v1 honors `isRead:true` (mark read)**.
`isRead:false` (mark unread) is **best-effort and may be unsupported** until the §11 capture confirms GV
`updateread` accepts an unread transition. **Do not promise a toggle we can't honor:** if live capture
shows unread is unsupported, the build returns **400** (a coded "unread_unsupported") for `isRead:false`,
and RadioConsole sends only `isRead:true`. RadioConsole should **not surface an unread toggle in the UI**
until we confirm unread works (stated in the reply).

### 6.2 Auth posture — already covered by PR5, no special handling (Q8)

**Confirmed: no special auth posture for mark-read.** PR5's `GvBridgeAuthMiddleware` gates on
`path.StartsWith("/api/gvbridge", ...)` (exempting only the exact `/api/gvbridge/event` segment). Both
new routes live under `/api/gvbridge/...`, so they are **auto-covered the moment `InterServiceAuthKey`
is set** — zero new wiring, no per-route attribute. Default-off today (LAN-only, no header), exactly like
every other gvbridge route. This is the "one gate, applied consistently" property from parent ADR §6.5
paying off: new write routes inherit the gate for free.

---

## 7. GV-side `updateread` — UNVERIFIED; added to the §11 checklist

The **gvbridge contract boundary above is stable**; the **GV wire format behind it is not**. Known/assumed:

- The write rides the **same authenticated `HttpClient`** as every other GV call (SAPISIDHASH + 12-cookie
  + PSIDTS freshness), via the same `IGvAuthenticatedClientProvider.GetAuthenticatedClient()` seam
  `GvSmsClient.SendAsync` uses. A new `GvReadStateClient.MarkReadAsync(...)` (future build) mirrors
  `GvSmsClient.SendAsync`: resolve live client → `POST api2thread/updateread` → classify outcome into an
  honest taxonomy (`Queued`/`InvalidArgument`/`AdapterUnavailable`/`UpstreamError`/`Timeout`) → never throw.
- **UNVERIFIED:** the `updateread` resource name (`api2thread/updateread` is the working assumption), the
  **payload positions** (which array slots carry the thread/message id and the read bool), whether the
  grain is **per-thread or per-message** (Q4), whether **unread** (`isRead:false`) is accepted (Q6), and
  whether the response **echoes** the item (if not, we re-read via the existing list+filter to build the
  response DTO — which is what the routes do anyway).

**New §11 live-verification step (append to the parent ADR §11 checklist):**

> **8. `updateread` round-trip.** With live cookies on the `radio` box, `POST api2thread/updateread`
> against a known voicemail and a known SMS thread. Capture: the exact resource name, the payload
> positions (id slot, read-bool slot), whether the grain is per-message (→ a thread mark iterates the
> message ids) or per-thread, whether `isRead:false` (unread) is accepted or rejected, and whether the
> response echoes the updated node (→ no re-read needed) or returns a bare ack (→ re-read to build the DTO).
> Pin positions in `GvProtobuf.Get*`/`BuildArray`. → unblocks the mark-read build + de-UNVERIFYs Q4/Q6.

---

## 8. Planned safety posture for the (future) build — NOT built here

For the eventual Planner/Builder cycle (stated so both teams know the rollout shape):

- **`EnableMarkRead` config flag on `GVBridgeConfig`, default `false`** — mirrors `EnableSmsSend`. The
  account-write path ships **dark**: with the flag off, the mark routes perform **no GV call** and return
  a coded "disabled" response. Merge changes no behavior; the owner flips it to go live.
- **Fixture-verified only at merge.** First **real** `updateread` is the §11 step 8 capture (owner flips
  the flag + on-box live capture), exactly like the first real `sendsms`.
- **Rate-limit** the write (reuse the `SmsSendRateLimiter` shape) — a mark is cheaper/safer than a send,
  but a looped bug should still be capped.
- **No auto-retry** on ambiguous upstream failure — return 502, let RadioConsole reconcile on next list.

**This ADR does not authorize the build.** It records the agreed contract + safety shape so a future
cycle has a frozen target. The build is **HELD by the owner.**

---

## 9. Consequences

**Good:**
- One source of truth (GV). The list endpoints stay a 1:1 map of GV's flag — no local store, no merge
  step in the read path, requirement #3 ("phone clears the badge") satisfied by construction.
- The contract boundary is **stable now** — RadioConsole wires its existing seam immediately, decoupled
  from the UNVERIFIED GV wire format.
- Reuses proven seams end-to-end: `GvSmsClient.SendAsync` (write-client shape), `GvMessagePushBridge`
  (event broadcast), `GvBridgeAuthMiddleware` (auth), `EnableSmsSend` (dark-flag rollout). The build,
  when funded, is additive and low-novelty.
- Routes-first sequencing de-risks: RadioConsole gets durability (#1, #2) and the on-mark event quickly;
  the heavier poller-diff externally-originated detection (#3 live) follows without blocking.

**Bad / costs:**
- `updateread` is a real GV account write with an **UNVERIFIED** wire format — gated behind `EnableMarkRead`
  default-off + §11 step 8, but it is live-mutation surface.
- Mark-unread may be unsupported in v1 (best-effort) — RadioConsole must not surface an unread toggle until
  confirmed.
- The poller-diff (path b) event adds new per-item read-flag state to `GvThreadPoller` (a fast-follow cost),
  and adds a second diff pass per poll — minor, but real.

**Neutral / explicitly deferred:**
- **Delete** — deferred (Q7), agreed. Will be a separate request if the owner wants it.
- Group MMS read-state — out of scope (group threads are out of scope v1 in the parent ADR).

---

## 10. Related decisions

- Parent ADR `2026-06-20-gv-voicemail-sms-radioconsole.md` — §3.4 (anticipated updateread/delete),
  §5.3 (the poller + high-water diff this extends), §6.1 (the frozen DTOs reused verbatim),
  §6.3 (`GvMessagePushBridge` push pattern the event extends), §6.5 (the auth gate that auto-covers
  these routes), §11 (the live-verification checklist, now +1 step).
- Reply to RadioConsole: `docs/handoffs/radioconsole-gv-markread-reply.md`.
- Request being answered: `docs/prompts/radioconsole-gv-markread-readstate-request.md`.
- Boundary doc Integration Points: `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` (updated — the two
  routes + `ReadStateChanged` event + a Change Log entry).

## 11. Open questions

1. **`updateread` wire format** — resource name, payload positions, per-message vs per-thread grain,
   unread support, response echo. → parent ADR §11 step 8 (live capture). Blocks the build, not the contract.
2. **Build scheduling** — the build is HELD by the owner. When funded: routes + path-(a) event first
   (one PR, `EnableMarkRead` default-off), poller-diff path-(b) event as a fast-follow PR.
