# ADR: Google Voice Voicemail + SMS on RadioConsole (cross-service API)

- **Status:** Proposed (research spike — owner review pending)
- **Date:** 2026-06-20
- **Author:** Architect (research spike)
- **Arc:** `docs/plans/gv-voicemail-sms-arc.md`
- **Supersedes / relates to:** `docs/superpowers/specs/2026-03-27-gv-api-migration-design.md`,
  `docs/api-research/signaler-protocol.md`, `docs/research/gv-protocol-notes.md`

> **Honesty constraint (read first).** This spike is **grounded synthesis + design**, not live
> reverse-engineering. No authenticated calls were made to Google's private API from this
> environment (no live cookies; that would be live traffic to Google). Every endpoint not already
> exercised by shipped RotaryPhone code is marked **UNVERIFIED — needs live check** and appears in
> the §11 live-verification checklist. Endpoints marked **VERIFIED (in-repo)** are confirmed only in
> the sense that shipped, working code in this repo calls them with the documented shape — the
> request/response *shape* is trusted; the new endpoints' *exact field positions* are not.

---

## 1. Context

### 1.1 The arc

RotaryPhone owns the live Google Voice (GV) integration. We want to surface, **on the separate
RadioConsole service's kiosk UI**:

- **Voicemail** — list, audio playback, transcripts.
- **Texts** — read incoming threads, send/reply.

The owner has locked the integration boundary: **RadioConsole consumes a RotaryPhone-exposed API.**
RotaryPhone talks to Google; RadioConsole never holds GV cookies or talks to Google directly. This
ADR defines that RotaryPhone→RadioConsole contract and the GV-side feasibility behind each capability.

### 1.2 What already exists in RotaryPhone (verified against current code)

The auth + transport foundation is **live and battle-tested** — this arc extends it, it does not rebuild it.

| Component | File | State |
|---|---|---|
| SAPISIDHASH + cookie injection | `Auth/GvHttpClientHandler.cs`, `Auth/GvSapisidHash.cs` | Live. Injects `Authorization: SAPISIDHASH`, full `Cookie:` header, `Origin`/`Referer`/`X-Goog-AuthUser` on every GV request. |
| 12-cookie set + raw header | `Auth/GvCookieSet.cs` | Live. Typed long-lived cookies + verbatim `RawCookieHeader` for the rotating ones. |
| PSIDTS rotation (the old 401 killer) | `Auth/GvCookieRotator.cs`, `GvCookieSet.WithRefreshedPsidts` | Live. Browser-less `accounts.google.com/RotateCookies` refresh, 5-min cadence, CDP fallback. |
| Account/health client | `Clients/GvAccountClient.cs` | Live. `POST threadinginfo/get`, `POST account/get`. The template every new client follows. |
| Protobuf-JSON helpers | `Protocol/GvProtobuf.cs` | Live. `GetString/GetInt/GetArray(path…)`, `BuildArray(...)`. |
| Call control | `Adapters/GVApiAdapter.cs`, `Sip/GvSipTransport.cs` | Live. **Uses SIP-over-WebSocket (RFC 7118), NOT the signaler.** |
| Cookie API + recovery | `Api/GVBridgeController.cs`, `Services/GvCookieManager.cs` | Live. `GET/POST /api/gvbridge/cookies`, `cookies/refresh-from-browser`. |
| Push channel | `Server/Hubs/RotaryHub.cs`, `Server/Services/SignalRNotifierService.cs` | Live. SignalR hub RadioConsole already consumes (`CallStateChanged`, `IncomingCall`, …). |
| Existing SMS read contract (trunk path) | `GVTrunk/Api/GVTrunkController.cs` `GET /api/gvtrunk/sms`, `GVTrunk/Models/SmsNotification.cs` | Live but **different path** (VoIP.ms trunk, in-memory cache). A precedent to mirror, not reuse. |

**Critical correction to the migration spec.** The 2026-03-27 spec lists `GvSmsClient` and
`GvThreadClient` as "absorbed from GVResearch." **Neither exists in the current source tree** — the
spec's file list was aspirational. The shipped GV path is auth + SIP-WSS call control only. There is
**no** GV-side SMS-read, SMS-send, voicemail, or thread code today. This arc builds all of it.

### 1.3 The GV API request shape (the one pattern everything reuses)

Every shipped GV HTTP call follows the identical shape (confirmed in `GvAccountClient`,
`GvSipCredentialProvider`):

```
POST https://clients6.google.com/voice/v1/voiceclient/<RESOURCE>?alt=protojson&key=<API_KEY>
Content-Type: application/json+protobuf
Authorization: SAPISIDHASH <ts>_<sha1(ts + " " + SAPISID + " " + "https://voice.google.com")>
Cookie: <verbatim 12+-cookie header, PSIDTS kept fresh by GvCookieRotator>
Origin: https://voice.google.com
Referer: https://voice.google.com/
X-Goog-AuthUser: 0

Body: <a protobuf-JSON positional array, e.g. [] or [3,"deviceId"]>
```

`<API_KEY>` is the public web key already in config (`GvApiKey =
"AIzaSyDTYc1N4xiODyrQYK0Kl6g_y279LjYkrBg"`). **New clients add an endpoint name and a request body;
they get auth, cookies, and PSIDTS freshness for free** by sharing the adapter's `HttpClient`. This
is the single most important architectural fact for the arc: the hard part (auth) is done.

---

## 2. Decision (summary)

1. **Build three new GV-side clients** in `src/RotaryPhoneController.GVBridge/Clients/`:
   `GvVoicemailClient`, `GvSmsClient`, `GvThreadClient` — each a thin wrapper over the shared
   authenticated `HttpClient`, following the `GvAccountClient` template.
2. **Expose two new REST controllers** in the GVBridge project:
   `/api/gvbridge/voicemail/*` and `/api/gvbridge/sms/*`, returning **typed records** (camelCase).
   Voicemail audio is delivered by a **RotaryPhone-side proxy+cache** endpoint that streams bytes —
   RadioConsole never gets a Google URL or cookies.
3. **SMS-read: poll the threads/list endpoint, do not crack the signaler** (confidence-rated in §5).
   A `GvThreadPoller` background service polls on an adaptive interval, diffs against last-seen, and
   pushes new inbound messages over the **existing SignalR hub** (`SmsReceived` event), exactly
   mirroring how `IncomingCall` already reaches RadioConsole.
4. **Inter-service auth:** add a shared-secret header (`X-RotaryPhone-Auth`) gate on the GV
   voicemail/SMS endpoints **before any non-LAN exposure** — closing the credential-adjacent gap the
   boundary doc already flags for `/api/gvbridge/cookies`.
5. **Sequence the arc as 6 PRs** (§10), with the two auth/secret-sensitive ones (SMS-send write path;
   inter-service auth) held for owner review per the auto-merge policy.

---

## 3. Voicemail API surface (GV side)

Voicemail in GV is a **thread/message subtype**, not a separate product. Voicemails appear as
messages of a voicemail type inside the thread list, carrying a media reference (the recording) and,
when Google has transcribed it, a transcript string. This matches the GV web client's behavior (the
"Voicemail" tab is a filtered thread view).

### 3.1 List voicemails — **UNVERIFIED — needs live check**

Two candidate endpoints, both on the standard `voice/v1/voiceclient/` base:

- **Candidate A (preferred): `api2thread/list`** with a folder/filter argument selecting the
  voicemail folder. The web client loads each tab (All / Voicemail / Recorded / Missed) by listing
  threads with a folder enum. Request body is a small positional array `[<folder>, <pageToken?>,
  <count?>]` — **exact positions UNVERIFIED.**
- **Candidate B: `voicemail/list`** (a dedicated endpoint). Some GV reverse-engineering references
  cite a voicemail-specific list. Lower confidence it still exists; `api2thread/list` is the one the
  current web client demonstrably uses.

**Recommendation:** implement against `api2thread/list` with a voicemail folder filter; keep
`voicemail/list` as a documented fallback to try during live verification. Response is the standard
thread array; each voicemail message node is expected to carry: message id, thread id, counterparty
E.164, timestamp (epoch ms), read/unread flag, a **media id** for the recording, and an optional
**transcript** string. Field positions are **UNVERIFIED** — capture once live and pin them in
`GvProtobuf.Get*(node, <index>)` calls.

### 3.2 Download voicemail audio — **UNVERIFIED — needs live check**

GV serves recording audio as an authenticated media blob, **not** under the JSON `voiceclient` base.
The web client fetches it from a media host. Best-known shape:

- **`GET https://clients6.google.com/voice/v1/voiceclient/recording/get?id=<mediaId>&key=<API_KEY>`**
  returning audio bytes (historically MP3, ~8 kHz mono; some accounts AMR). **UNVERIFIED** — the path
  may instead be a `media/...` or `mediastream` URL embedded in the list response.
- The fetch needs the **same SAPISIDHASH + cookie auth** as every other GV call, so it rides the
  existing `GvHttpClientHandler` cleanly — **this is why audio must be fetched by RotaryPhone, not
  RadioConsole** (RadioConsole has no cookies).

The robust design (regardless of exact URL): the list response contains a media reference; the client
resolves it to a fetchable URL or an `id` and fetches via the authenticated `HttpClient`. We do **not**
hand a Google URL to RadioConsole.

### 3.3 Transcripts — **Medium confidence (inline)**

Transcripts are **expected to be inline in the list response** (a string field on the voicemail
message node), populated by Google asynchronously after the voicemail arrives. No separate call is
needed when present. Two caveats, both **UNVERIFIED**:

- Transcript may be **absent or partial** for recent voicemails (Google transcribes on a delay) — the
  UI must handle "transcript pending."
- A small number of accounts have transcription disabled — treat transcript as **optional**.

If transcripts turn out **not** to be inline, the fallback is a per-item `voicemail/transcript?id=…`
call — **UNVERIFIED**, lower confidence, only pursue if §11 shows the inline field is empty when the
web UI clearly shows a transcript.

### 3.4 Mark voicemail read / delete — out of scope for v1

Mentioned for completeness. `api2thread/updateread` / `…/delete`-style endpoints exist but are
write operations on the GV account; defer until list+listen+transcript ship and are validated.

---

## 4. SMS send (GV side)

### 4.1 Endpoint and payload — **VERIFIED (spec) / UNVERIFIED (live)**

From the migration spec and the GVResearch reference, the send shape is:

```
POST voice/v1/voiceclient/api2thread/sendsms?alt=protojson&key=<API_KEY>
Body: [null, null, null, null, "<message text>", "<threadId>"]
```

where `<threadId>` for a new conversation to a single recipient is **`t.+<E164>`** (e.g.
`t.+19195551234`). This is exactly the payload the (never-built) `GvSmsClient` in the plan used:
`GvProtobuf.BuildArray(null, null, null, null, body, threadId)`.

### 4.2 Gotchas (these will bite if not handled)

1. **Thread id is NOT always `t.+<E164>`.** That literal form works for *new* 1:1 threads, but for
   replies you should send to the thread id Google already assigned (returned by the list/thread
   endpoints), which may be an opaque `t.<hash>` for group threads or number changes. **Design rule:
   reply-to-existing uses the thread's real id from the list response; only fall back to `t.+<E164>`
   when starting a fresh conversation.** — exact id format **UNVERIFIED — needs live check**.
2. **Number format.** Recipient must be **E.164** (`+1NNNNNNNNNN`). RadioConsole will send whatever
   the user types; RotaryPhone must normalize (reuse existing dialing normalization) before building
   the thread id. Bare 10-digit or `1NNNNNNNNNN` forms will likely produce an `INVALID_ARGUMENT`.
3. **Response is not the message body.** `sendsms` returns a thread/transaction ack array, not the
   echoed text. Do not parse it for the message; treat 200 = queued, non-200 = failed, and let the
   next thread poll surface the sent message in context.
4. **Send is a real account write** — it costs nothing but is irreversible and user-visible. This is
   the one capability that can do harm if a bug loops it. Rate-limit at the RotaryPhone controller
   (e.g. reject > N sends/10s) and never auto-retry a send on ambiguous failure.

**Confidence:** Medium-High that the endpoint+payload work as specced (it is the most-documented GV
write); the **thread-id-for-replies** detail is the real unknown.

---

## 5. SMS read (incoming) — the critical-risk decision

### 5.1 The signaler problem (from existing research)

The signaler long-poll (`signaler-pa.clients6.google.com/punctual/multi-watch/channel`) is the GV
web client's real-time push channel. Its 3-step flow (chooseServer → bind → poll) is **verified
working** in `docs/api-research/signaler-protocol.md`, but the **subscription payloads return
`INVALID_ARGUMENT`** and Google closes those subscriptions. Key facts from
`signaler-subscriptions-todo.md`:

- The channel **survives** the error and returns `noop` heartbeats — but with no valid subscription,
  **no GV events are delivered.**
- The browser sends the **byte-identical** subscription format (verified via Playwright) and it
  **works for the browser**. So the wire format is probably right; something about **session
  context** (a cookie the headless path lacks, the `zx` cache-buster, UA, or account-specific
  subscription values) is missing.
- The signaler uses the **same rotating PSIDTS cookies** — and at the time that research was written,
  PSIDTS refresh did not exist. **It exists now** (`GvCookieRotator`). That is a genuinely new
  variable: at least one plausible root cause (stale PSIDTS during the bind) is now eliminated.

### 5.2 Verdict: **recommend the threads-polling fallback (option b). Confidence: High.**

Do **not** block the arc on cracking the signaler. Reasons:

1. **Risk profile.** The signaler is a `High`-complexity, open-ended reverse-engineering task that has
   already resisted one investigation. Polling `api2thread/list` is `Low` complexity and reuses the
   exact request pattern five shipped clients already use.
2. **The push UX is already solved without the signaler.** RotaryPhone polls Google and pushes to
   RadioConsole over the **existing SignalR hub** — so RadioConsole still gets a *push* (no
   RadioConsole-side polling), and the only polling is RotaryPhone→Google, which we fully control.
3. **The decision is reversible and additive.** If the signaler is later cracked, it becomes a
   drop-in replacement for the poller behind the same internal event (`OnSmsReceived`) and the same
   SignalR `SmsReceived` push — zero contract change for RadioConsole.

**One cheap experiment worth doing once, before committing forever (not blocking):** re-run the
existing signaler bind **now that PSIDTS is kept fresh**, with `count=1` and a single subscription, to
see if fresh cookies alone fix `INVALID_ARGUMENT`. Timebox to a few hours during live verification
(§11). If it suddenly works, we keep the poller as the shipped default and file the signaler as a
fast-follow optimization. If it still fails, we have lost nothing.

### 5.3 The polling design (what actually ships)

`GvThreadPoller : IHostedService` (in GVBridge), active only when `GVApiAdapter.IsAvailable`:

- Polls `POST api2thread/list` (inbox/SMS folder) on an **adaptive interval**:
  - **15 s** baseline when the box is "active" (a UI client connected in the last N minutes, or a
    recent call/text), **60 s** idle. Cheap: one small authenticated POST.
  - Back off to **120 s** and log on repeated non-200 (don't hammer Google during an auth blip; the
    existing cookie-recovery ladder handles the auth side).
- Diffs the returned messages against a **last-seen high-water mark** (max message timestamp / id per
  thread, persisted in-memory; optionally to the existing SQLite call-log db for restart durability).
- For each **new inbound** message: raise `OnSmsReceived(SmsMessage)` → `SignalRNotifierService`
  broadcasts `SmsReceived` to RadioConsole, mirroring `IncomingCall`.

**Trade-offs (quantified):**

| Concern | Polling fallback | Signaler (if cracked) |
|---|---|---|
| Inbound latency | 0–15 s (active), 0–60 s (idle) | ~real-time (<2 s) |
| Request load on Google | ~4/min active, ~1/min idle, 1 account | 1 long-poll held open |
| Rate-limit risk | Low at this cadence (single personal account; web client polls comparably) | Lower |
| Battery/box load | Negligible (N100, always-on, wall power) | Negligible |
| Implementation risk | **Low** | **High (unresolved)** |
| Reversibility | Swap behind `OnSmsReceived` later | n/a |

For a single always-on personal account on a wall-powered N100, **15–60 s inbound latency is an
entirely acceptable price** to de-risk the whole arc. The owner can later fund the signaler as a
latency optimization.

---

## 6. Cross-service API contract (RotaryPhone → RadioConsole)

This is the core architectural decision the owner asked for. It extends the **existing** pattern:
typed-record REST under `/api/gvbridge/*` for state, **SignalR hub** for push, RadioConsole polls
slow-changing state and reacts to pushes. JSON conventions per the boundary doc (typed records →
PascalCase property names serialized camelCase; RadioConsole's `Radio.Web` is case-insensitive).

### 6.1 Data models (typed records, in `GVBridge/Api/GvBridgeDtos.cs`)

```csharp
// ---- Voicemail ----
public record VoicemailItemDto(
    string Id,                 // GV message id (stable)
    string ThreadId,           // GV thread id (for context / future actions)
    string FromNumber,         // E.164 of caller
    string? FromName,          // GV-provided display name, if any (UI may override via contacts)
    DateTime ReceivedAt,       // UTC
    int DurationSeconds,       // recording length (0 if unknown)
    bool IsRead,
    string? Transcript,        // null = none/pending; UI shows "Transcript pending"
    string AudioUrl);          // RotaryPhone-relative proxy URL, e.g. /api/gvbridge/voicemail/{id}/audio

public record VoicemailListDto(
    IReadOnlyList<VoicemailItemDto> Items,
    string? NextPageToken,     // null when no more pages
    DateTime FetchedAtUtc);

// ---- SMS ----
public record SmsMessageDto(
    string Id,                 // GV message id
    string ThreadId,
    string Direction,          // "Inbound" | "Outbound"
    string CounterpartyNumber, // E.164
    string? Text,
    DateTime SentAt,           // UTC
    bool IsRead);

public record SmsThreadDto(
    string ThreadId,
    string CounterpartyNumber, // E.164 (1:1); group threads out of scope v1
    string? CounterpartyName,
    DateTime LastMessageAt,
    bool HasUnread,
    string? LastMessagePreview);

public record SmsThreadListDto(
    IReadOnlyList<SmsThreadDto> Threads,
    DateTime FetchedAtUtc);

public record SmsThreadMessagesDto(
    string ThreadId,
    IReadOnlyList<SmsMessageDto> Messages,
    DateTime FetchedAtUtc);

public record SendSmsRequest(string ToNumber, string Text, string? ThreadId);  // ThreadId optional: reply vs new
public record SendSmsResponse(bool Queued, string? ThreadId, string? Error);
```

### 6.2 REST endpoints (new, in GVBridge controllers)

| Method | Route | Purpose | Returns | Notes |
|---|---|---|---|---|
| GET | `/api/gvbridge/voicemail` | List voicemails (paged) | `VoicemailListDto` | `?pageToken=&count=` |
| GET | `/api/gvbridge/voicemail/{id}` | Single voicemail metadata | `VoicemailItemDto` | |
| GET | `/api/gvbridge/voicemail/{id}/audio` | **Stream the recording bytes** | `audio/mpeg` stream | RotaryPhone proxies + caches; see §6.4 |
| GET | `/api/gvbridge/sms/threads` | List SMS threads | `SmsThreadListDto` | `?count=` |
| GET | `/api/gvbridge/sms/threads/{threadId}` | Messages in a thread | `SmsThreadMessagesDto` | `?count=` |
| POST | `/api/gvbridge/sms/send` | Send / reply to a text | `SendSmsResponse` | **write path; rate-limited; auth-gated** |

All read endpoints are safe to poll. **Slow-changing reads** RadioConsole may poll directly (threads
list at 30–60 s as a backstop); **fresh-message awareness comes from the SignalR push**, not polling.

### 6.3 How RadioConsole learns of new messages — **push via existing SignalR hub**

Tie directly to the §5 decision. New SignalR events on `RotaryHub` (RadioConsole already has the
connection and the consumption pattern for `IncomingCall`):

| Event | Payload | Fired when |
|---|---|---|
| `SmsReceived` | `SmsMessageDto` | `GvThreadPoller` detects a new inbound message |
| `VoicemailReceived` | `VoicemailItemDto` | poller detects a new voicemail (same poll, voicemail folder) |

RadioConsole reacts (toast / badge / refresh the open thread) without polling for *freshness*. This
keeps RadioConsole's load identical to today (it already holds one SignalR connection) and means the
signaler-vs-poll choice is **invisible to RadioConsole** — the contract is push-shaped either way.

### 6.4 Audio delivery — **proxy + disk cache on RotaryPhone (not a redirect)**

**Decision: stream through RotaryPhone, cache on disk. Do not redirect RadioConsole to Google.**

- RadioConsole's `<audio src="http://radio:5004/api/gvbridge/voicemail/{id}/audio">` hits RotaryPhone.
- On first request, RotaryPhone fetches the recording from Google via the authenticated `HttpClient`,
  writes it to a small on-disk cache (`data/gv-voicemail-cache/{id}.mp3`), and streams it back with
  `Accept-Ranges: bytes` support (so the HTML5 `<audio>` scrubber works).
- Subsequent requests serve from cache. Evict by age/size (e.g. keep 7 days or 200 MB, whichever
  first). Voicemails are small (tens of KB to low MB); growth is trivial.

**Why proxy, not redirect:**
1. **RadioConsole has no GV cookies** — a redirect to a Google media URL would 401. Auth lives only in
   RotaryPhone. This is the decisive reason.
2. Google media URLs are short-lived/signed; caching the bytes once is more robust than re-resolving.
3. Range support gives a working scrubber and lets the browser re-seek without re-hitting Google.

This is the single most load-bearing piece of the contract: **all GV media flows
Google → RotaryPhone → RadioConsole, never Google → RadioConsole.**

### 6.5 Inter-service auth between the two services on the box

The boundary doc already flags that `/api/gvbridge/cookies` is **LAN-only with no auth** and becomes a
credential-theft vector if RotaryPhone is ever exposed beyond the LAN. The new SMS-send (a write to
the user's phone number) and the voicemail content (private audio) **raise the stakes**.

**Decision:** add an optional shared-secret gate, defaulted off to preserve today's LAN behavior, and
**required before any non-LAN exposure**:

- Config `GVBridgeConfig.InterServiceAuthKey` (default `""` = disabled, LAN-only as today).
- When set, a minimal middleware requires header `X-RotaryPhone-Auth: <key>` on the new
  voicemail/SMS endpoints (and retrofit the existing cookie endpoints). 401 otherwise.
- RadioConsole stores the key in its config and sends the header on every call (REST + SignalR
  connection via an access-token provider).
- **This is a config + middleware change, not a redesign.** It is intentionally the same mechanism the
  boundary doc already suggests for the cookie endpoints — one gate, applied consistently.

Document the change in the boundary doc's Change Log and the Integration Points table (the new
endpoints + the auth header) so the RadioConsole session picks it up.

---

## 7. Internal RotaryPhone design (what the clients look like)

New files, all mirroring `GvAccountClient` (constructor takes the shared `HttpClient` + base URL +
api key + logger; each method does one authenticated POST and parses with `GvProtobuf`):

```
src/RotaryPhoneController.GVBridge/
  Clients/
    GvThreadClient.cs       // ListThreadsAsync(folder, count, pageToken) -> raw thread array
    GvVoicemailClient.cs    // ListVoicemailsAsync(...), GetRecordingStreamAsync(mediaId) -> Stream
    GvSmsClient.cs          // SendAsync(toNumber, text, threadId?), ListMessagesAsync(threadId)
  Services/
    GvThreadPoller.cs       // IHostedService: adaptive poll + diff + raise OnSmsReceived/OnVoicemailReceived
    GvVoicemailCache.cs     // disk cache + range-stream helper for /audio
  Api/
    GvVoicemailController.cs // /api/gvbridge/voicemail/*
    GvSmsController.cs        // /api/gvbridge/sms/*
    GvBridgeDtos.cs          // (extend) the records in §6.1
```

Shape rules learned from the live code:
- **Share the adapter's `HttpClient`**, do not new up your own — that is how you inherit PSIDTS
  freshness and the cookie-recovery ladder. Resolve it indirectly (the adapter already exposes the
  pattern via `SingleHttpClientFactory`) so cookie rotation that swaps `_httpClient` propagates.
- **Parse defensively** with `GvProtobuf.Get*` and treat every field as possibly-null; GV positional
  arrays shift and new positions appear. Never index `[7]` without a length guard.
- **Clients return DTOs or raw `JsonDocument`**, controllers map to the §6.1 records. Keep Google's
  wire format out of the public contract entirely.

---

## 8. Feasibility call per capability

| Capability | Confidence | Blockers / notes |
|---|---|---|
| **Voicemail — list** | **Medium** | Endpoint (`api2thread/list` w/ voicemail folder) and auth are known; exact request positions + response field indices **UNVERIFIED**. Reuses live auth → no new infra. |
| **Voicemail — listen (audio)** | **Medium** | Recording fetch URL/`id` shape **UNVERIFIED**; auth model and proxy+cache design are solid. Biggest unknown is the exact media endpoint, resolvable in one live capture. |
| **Voicemail — transcript** | **Medium** | Expected inline; may be pending/absent (handle in UI). Separate-call fallback is low-confidence. |
| **SMS — read (inbound)** | **Med-High via polling** / Low via signaler | Polling `api2thread/list` reuses live auth and is low-risk; field positions **UNVERIFIED**. Signaler remains `INVALID_ARGUMENT` (High difficulty) — **explicitly not the chosen path.** |
| **SMS — send** | **Med-High** | Endpoint+payload specced; **reply thread-id format** is the real unknown; **write path** needs rate-limit + auth gate. |

No capability is **infeasible**. The dominant risk is not "can it be done" but "exact field
positions," all resolvable with one authenticated capture session (§11). The only `Low`-confidence
item (signaler) is deliberately routed around.

---

## 9. Consequences

**Good:**
- Reuses the hardest, already-solved part (12-cookie SAPISIDHASH + PSIDTS rotation). New work is
  additive thin clients + DTOs + one poller.
- The push contract to RadioConsole is **identical in shape** whether SMS-read is poll or signaler —
  the risky decision is hidden behind a stable seam.
- Audio proxy keeps **all** GV credentials and media on RotaryPhone; RadioConsole stays a pure
  consumer. Cleanly honors the owner's boundary choice.
- Inter-service auth gate finally closes the credential-adjacent gap the boundary doc has flagged
  since Phase B, applied uniformly to cookies + new endpoints.

**Bad / costs:**
- 15–60 s inbound SMS latency under polling (acceptable for this product; documented).
- Field-position fragility against GV's undocumented API — mitigated by defensive parsing and the
  live-verification checklist, but a future GV redesign can still break list parsing. Same risk class
  the call path already accepts.
- A small on-disk media cache + eviction policy is new operational surface (minor).
- SMS-send is an irreversible account write — needs rate-limiting and owner sign-off.

**Neutral / explicitly deferred:**
- Group MMS threads, voicemail delete/mark-read, outbound MMS/media — out of scope v1.
- The signaler stays in the repo as researched-but-unused; if cracked later it slots in behind
  `OnSmsReceived` with no contract change.

---

## 10. Recommended PR breakdown

Dependency order top-to-bottom. **Sensitivity** flags which PRs touch GV auth/secret/write surface
and must be **held for owner review** (auto-merge policy); the rest are auto-merge-eligible on green.

| PR | Title | Delivers | Depends on | Sensitivity |
|----|-------|----------|------------|:-----------:|
| **1** | `feat(gv): thread + voicemail read clients` | `GvThreadClient`, `GvVoicemailClient` (list only), `GvProtobuf` parsing of thread/voicemail nodes; unit tests with captured fixtures. No endpoints yet. | live auth (exists) | **Auto-merge** (read-only, no new exposure) |
| **2** | `feat(gv): voicemail REST + audio proxy/cache` | `GvVoicemailController` (`list`, `{id}`, `{id}/audio`), `GvVoicemailCache` (disk cache + range streaming). | PR1 | **Auto-merge** (read-only; cache is local) |
| **3** | `feat(gv): SMS read + thread polling push` | `GvSmsClient.ListMessages`, `GvSmsController` read endpoints, `GvThreadPoller` (adaptive poll + diff), new SignalR `SmsReceived`/`VoicemailReceived` events. | PR1 | **Auto-merge** (read-only; push reuses existing hub) |
| **4** | `feat(gv): SMS send` | `GvSmsClient.SendAsync`, `POST /api/gvbridge/sms/send`, rate-limit, E.164 normalization, reply-thread-id handling. | PR3 | **HOLD — owner review** (irreversible account **write** to GV) |
| **5** | `feat(gvbridge): inter-service auth gate` | `X-RotaryPhone-Auth` middleware + config, applied to voicemail/SMS **and** existing cookie endpoints; boundary-doc update. | PR2, PR4 | **HOLD — owner review** (touches **auth/secret** handling + cross-service contract) |
| **6** | `chore(gv): signaler fresh-PSIDTS experiment` *(optional, timeboxed)* | One-shot live retest of the signaler bind with fresh cookies + `count=1`; if it works, wire it behind `OnSmsReceived` as an optimization; else document and close. | PR3 | **HOLD — owner review** (touches GV auth/session; experimental) |

PRs 1–3 deliver the **entire read experience** (voicemail list/listen/transcript + SMS read with
push) and are all auto-merge-eligible. The two write/secret PRs (4, 5) and the experimental signaler
PR (6) are the ones the Coordinator should flag for the owner. Designer can spec the RadioConsole UI
against PRs 1–3's contract in parallel (the DTOs in §6.1 are stable regardless of GV-side field
positions).

---

## 11. Live-verification checklist (run once cookies are live, on the `radio` box)

All steps reuse the shipped authenticated `HttpClient` (or `curl` with a freshly-captured cookie
header + a computed SAPISIDHASH). For each, **capture the raw response and pin the field positions.**

1. **Thread list shape.** `POST api2thread/list` with a few candidate bodies (`[]`, `[<folder>]`,
   `[<folder>,null,<count>]`). Identify: the folder enum for **SMS/inbox** vs **voicemail**, the
   page-token position, and per-message field positions (id, threadId, counterparty, timestamp,
   read-flag, text). → unblocks PR1, PR3.
2. **Voicemail node shape.** In the voicemail-folder list, find the **media reference** (id vs URL)
   and the **transcript** field. Confirm whether transcript is inline and whether it's ever empty for
   a voicemail the web UI shows transcribed. → unblocks PR1, PR2.
3. **Recording fetch.** Resolve the media reference to a fetch (`recording/get?id=…` or an embedded
   URL). Confirm content-type, that auth is required, and that **range requests** are honored (or that
   we must buffer fully before serving ranges). → unblocks PR2 audio proxy.
4. **SMS send round-trip.** `POST api2thread/sendsms` with `[null,null,null,null,"test","t.+<E164>"]`
   to a known test number. Confirm 200, confirm the message appears, **then capture the thread id GV
   assigned** and test a *reply* using that id vs `t.+<E164>` to pin the reply-thread-id rule. → unblocks PR4.
5. **Inbound diff.** Send a text *to* the GV number from a phone; confirm the next `api2thread/list`
   poll shows it and the high-water-mark diff fires exactly once. → validates PR3 poller.
6. **(Optional, timeboxed) Signaler retest.** With `GvCookieRotator` keeping PSIDTS fresh, redo the
   bind from `signaler-protocol.md` with `count=1` and one subscription; check whether
   `INVALID_ARGUMENT` is gone. Record the result either way. → decides PR6.
7. **Auth gate smoke.** With `InterServiceAuthKey` set, confirm the new endpoints 401 without the
   header and 200 with it, and that RadioConsole can still reach them once configured. → validates PR5.

---

## 12. Open questions (for the owner)

1. **SMS-send autonomy.** Send is an irreversible GV account write. Confirm PR4 should ship behind
   the auth gate **and** a rate-limit, and whether you want a per-send confirmation in the
   RadioConsole UI for v1.
2. **Inter-service auth rollout.** OK to add the `X-RotaryPhone-Auth` gate now (default-off to
   preserve LAN behavior) and require RadioConsole to send it? This is the right moment, before
   private voicemail audio + SMS-send exist.
3. **Voicemail retention.** Local audio cache eviction policy — 7 days / 200 MB proposed. Acceptable,
   or do you want voicemails cached indefinitely (they're small)?
4. **Signaler effort.** Fund the timeboxed PR6 signaler experiment, or ship poll-only and revisit
   only if 15–60 s SMS latency proves annoying in real use?
```

