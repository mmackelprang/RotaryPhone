# Request from Radio Console → RotaryPhone: `%2F` thread ids + the 20-minute GV auth blackout

- **Date:** 2026-07-31
- **From:** Radio Console session (`D:\prj\RTest\RTest`), Planner
- **Origin:** a live UAT pass on the Ubuntu box (`radio`) against real Google Voice data after your `PositionalGvThreadParser` fix (`627b928`) landed, followed by a server-side debugging pass over `journalctl` on both services plus read-only `curl` against `localhost:5004`.
- **Protocol:** boundary doc § "Passing Work Between Sessions" → *"If code changes are needed in RotaryPhone, create a file at `D:\prj\RotaryPhone\docs\prompts\`."*
- **Boundary doc Change Log:** deliberately **not** updated — that doc is scoped to BT/audio adapter ownership, and neither item below is a BT/audio boundary change. Flagging the choice so it reads as deliberate rather than skipped.
- **Source docs (readable from your side):**
  `D:\prj\RTest\RTest\docs\uat\2026-07-31-gv-live-data\REPORT.md` (the UAT) and
  `D:\prj\RTest\RTest\docs\uat\2026-07-31-gv-live-data\F-1-DIAGNOSIS.md` (the root-cause pass, with full log evidence).

---

## TL;DR

Radio Console's UAT found that opening a text thread often renders an empty conversation with
no error. Investigation split that into **three independent defects** — **two are yours**, one is
ours and is queued on our side as **GV-8**.

| # | Defect | Confidence | Ask |
|---|---|---|---|
| **B1** | Thread ids containing `/` arrive as literal `%2F` and never match → **HTTP 200 with `messages: []`** | **Confirmed by direct reproduction** | Decode the route value. Cheap; high value. **Do this first.** |
| **B2** | GV PSIDTS goes stale ~11 min after each refresh but refresh only fires every ~20 min, with no reactive refresh on 401 → a deterministic **~9-minute auth blackout every 20 minutes** → 502 to us | **Confirmed from logs, 271 occurrences today** | Align the refresh interval, add refresh-and-retry on the first 401, and make `/api/gvbridge/status` honest during a blackout. |

**The parser fix worked** — real data is flowing on both surfaces where previously every response
was 0 items behind a clean HTTP 200. These are the next two layers down.

**One framing correction up front, because it matters for how you scope B1:** the UAT originally
described this as an *MMS* bug. **It is not about MMS.** The predicate is **"the thread id contains
a `/`"**. GV group threads are `g.Group Message.<base64url>`, and the base64url alphabet includes
`/`; group threads happen to be the MMS threads, which is why the symptom looked MMS-shaped.

**And one hypothesis we falsified, so you don't chase it:** the UAT's candidate explanation was
Google Voice **throttling**. It is not. See §B2.2.

---

## B1 — thread ids containing `/` are never decoded (do this first)

### B1.1 What we reproduced

During a **fully healthy** window (15:06:48 — deliberately chosen inside a good window, see B2),
curling gvbridge with the *exact* escaping our client produces:

```
t.32665                                       HTTP 200  messages=2
t.%2B18019208129                              HTTP 200  messages=4
g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw    HTTP 200  messages=0   <-- silent empty
g.Group%20Message.yL8g8JjuyR7Z57d9BxRW%2FQ    HTTP 200  messages=0   <-- silent empty
```

Your own log for those same four requests names the cause:

```
[15:06:48 INF] Listed 2 SMS for thread t.32665 (of 149 parsed)
[15:06:48 INF] Listed 0 SMS for thread g.Group Message.d5Mri%2FNrDUQgXNXNQehOfw (of 149 parsed)
```

**The `%20` was decoded to a space. The `%2F` was not.** Kestrel deliberately leaves `%2F` encoded
in the path so it cannot forge a segment boundary, so the route value keeps the literal `%2F`.

`GvSmsClient.ListMessagesAsync` then does an exact string compare against the real id:

```csharp
var forThread = all.Where(m => m.ThreadId == threadId).ToList();
```

Zero matches → `Succeeded: true` with an empty list → `GvSmsController.GetThreadMessages` returns
**200 + `messages: []`**.

### B1.2 Why this matters more than it looks

- **Every GV group/MMS conversation is permanently unreadable**, 100% of the time, not
  intermittently. In the live top-20 that is 2 threads
  (`g.Group Message.d5Mri/NrDUQgXNXNQehOfw`, `g.Group Message.yL8g8JjuyR7Z57d9BxRW/Q`).
- It is a **200-with-empty of exactly the class** the `PositionalGvThreadParser` fix was meant to
  eliminate — same failure signature, different layer.
- **Your honest-status guards structurally cannot catch it.** `ShapeIsSane` and the `Succeeded`
  flag both pass, because the fetch and the parse both genuinely succeeded. Only the **filter**
  matched nothing. This is the one gap those guards leave open by construction.

### B1.3 The ask

In `src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs`, decode the `threadId` route value
in **both** `GetThreadMessages` **and** `MarkThreadRead` — e.g. `Uri.UnescapeDataString(threadId)` —
or move the id to a query parameter, where the framework decodes it normally.

`MarkThreadRead` is included deliberately: it takes the same id through the same route shape, so
mark-read on a group thread is silently a no-op today for the same reason.

**Recommended alongside it — a per-thread sanity check.** *A thread that is present in the list but
parses to 0 messages is suspicious.* Log it at Warning, or surface it in the response, so the next
instance of this class is visible immediately rather than after a UAT. Two other paths reach the
same 200-with-empty state and would benefit from the same guard:

- a thread whose messages fall **outside the fetched folder window** (see §Notes) yields 0 legitimately-ish;
- any future id-escaping mismatch.

### B1.4 We cannot work around this on our side — both options tested and rejected

Please don't send this back to us as a client-side escaping fix; we tried both:

- **Double-escape (`%252F`)** → you see `g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw` (the `%20` is
  now literal too) → still 0 messages.
- **Raw `/`** → the extra path segment misses the API route entirely and falls through to your SPA
  fallback, returning **`index.html` with HTTP 200**. Our `GetFromJsonAsync` throws on the content
  type → we log and return null → same silent empty, worse diagnostics.

---

## B2 — GV cookie/PSIDTS staleness causes a deterministic ~9-minute blackout every 20 minutes

### B2.1 What we see

Our web service logs real HTTP 502s from gvbridge:

```
[13:17:33 ERR] GvBridgeApiService: Failed to get GV SMS thread t.32665
System.Net.Http.HttpRequestException: Response status code does not indicate success: 502 (Bad Gateway).
```

Upstream, `journalctl -u rotary-phone` shows `api2thread/list returned Unauthorized for folder Sms`
— **271 occurrences today**, **zero** `TooManyRequests`, one `BadGateway`.

The pattern is a clean **20-minute square wave**. Transition timestamps (healthy → dead → healthy),
2026-07-31 local:

```
12:00:56 OK   12:11:00 401   12:21:01 OK   12:31:05 401   12:40:06 OK
12:52:10 401  13:00:11 OK    13:11:15 401  13:20:16 OK    13:31:20 401
13:40:21 OK   13:51:25 401   14:00:26 OK   14:12:30 401   14:20:31 OK
14:31:35 401  14:40:35 OK    15:00:02 OK   15:11:44 401
```

The mechanism, captured at one boundary:

```
14:59:40 WRN api2thread/list returned Unauthorized for folder Sms
15:00:01 INF Cookies saved to data/gv-cookies.enc
15:00:02 INF GV adapter re-activated with new cookies
15:00:02 INF CDP cookie refresh: 20 cookies extracted and activated
15:00:34 INF Listed 149 recent SMS messages          <- healthy again
15:11:44 WRN api2thread/list returned Unauthorized    <- 11m42s later, stale again
```

**Google's PSIDTS appears good for ~11 minutes. Your CDP refresh fires every ~20 minutes. There is
no reactive refresh on 401.** Result: a guaranteed ~9-minute dead zone in every 20-minute cycle.

**11 of 11** of our 502s fall inside a dead window — perfect correlation:

| Time | Thread | Dead window |
|---|---|---|
| 12:13:00 | `t.+19192308923` | 12:11–12:20 ✓ |
| 12:54:01 | (thread *list*) | 12:52–13:00 ✓ |
| 13:13:12 | `g.Group Message.yL8g8…` | 13:11–13:20 ✓ |
| 13:17:33 | `t.32665` | 13:11–13:20 ✓ |
| 13:32:32 | `g.Group Message.d5Mri…` | 13:31–13:40 ✓ |
| 13:34:42 | `t.39041` | 13:31–13:40 ✓ |
| 13:39:17 | `t.+19199304719` | 13:31–13:40 ✓ |
| 14:32:12–14:32:32 | `t.51789`, `t.+13362039432` ×2, `t.+16627480199` | 14:31–14:40 ✓ |

### B2.2 Throttling is falsified — three independent ways

Worth stating explicitly because the UAT guessed throttling and we want to save you the detour:

1. Our 60-second background poller runs at a **constant** rate and shows the identical on/off
   pattern. **Failure tracks wall-clock, not request volume.**
2. The upstream status is `Unauthorized` (401), **never 429**.
3. Recovery happens at fixed 20-minute boundaries, not after a variable cooldown.

### B2.3 A config discrepancy worth checking first — this may be most of the fix

Deployed `/opt/rotary-phone/appsettings*.json` declares:

```json
"CookieRefreshIntervalMinutes": 5
```

(the default is also `5`, at `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs:23`)

…yet the **observed** cadence is **20 minutes**. Either the value is not being read, or something
downstream of it sets its own interval. If a genuine 5-minute refresh were running, PSIDTS would
never reach its ~11-minute expiry and most of this would disappear. **We'd start here.**

### B2.4 The ask

1. **Align the refresh interval with the observed ~11-minute PSIDTS lifetime** — starting with why
   `CookieRefreshIntervalMinutes: 5` is not producing a 5-minute cadence (§B2.3).
2. **Add a reactive refresh-and-retry on the first 401.** A time-based refresh alone still leaves a
   window whenever Google shortens the lifetime; reacting to the actual 401 closes it. This is the
   part that makes the fix robust rather than tuned.
3. **Make `/api/gvbridge/status` honest during a blackout.** Measured at 15:13:03, while *both* SMS
   endpoints were returning 502:

   ```json
   {"available":true,"cookiesValid":true,"psidtsAgeSeconds":781,"degraded":false,"throttledUntil":null}
   ```

   Because of this, our `GvStatus.IsAvailable` stays `true` and our **"Google Voice is reconnecting"**
   banner never fires — *during the exact window it exists for*. It should report
   `available:false` / `degraded:true` while `api2thread/list` is returning 401.

   This is the same lesson as `Succeeded` meaning "the JSON parsed": a health field derived from a
   probe rather than from *did the last real call work* will report healthy through an outage.

---

## What Radio Console is doing on its own side

So the split is legible, and so you don't wait on us or we on you:

- **GV-8 — ✅ SHIPPED 2026-07-31** (PR #461, merged `b2d1ffc`; this bullet said "queued" when the
  document was written). Our client collapsed every non-2xx to `null` and rendered it as an empty
  conversation, so a 502 from you was byte-identical to a genuinely empty thread. It now renders a
  real error state (`cloud_off` + "Couldn't load messages." + `Retry`), with loading/error threaded
  through the conversation pane. **Verified live against one of your real 502s** — see the Addendum.
- **This ships independently of both items above and is not blocked on them.** **Ours makes the
  failure honest; yours makes it rare.** Neither subsumes the other, and we'd rather not have a
  version of this surface where the only defence is that the backend rarely fails.

**Ordering suggestion:** B1 first. It is a small, self-contained decode, and until it lands, group
conversations stay unreadable even in a perfectly healthy window — and any UAT of B2 will be
confounded by threads that fail for a *different* reason.

---

## Notes and leads (not asks)

- **Each thread open costs 2–3 upstream Google calls, not 1.** `GvSmsController.MarkThreadRead`
  re-lists to resolve the thread (`ListThreadsAsync(count: 100)`) and again to enumerate message ids
  (`ListMessagesAsync(count: 200)`), on top of `GetThreadMessages`. Confirmed in the logs — two
  `Unauthorized` lines per user click. `EnableMarkRead: true` is set in deployed production config.
  We are **not** claiming this causes rate pressure (throttling is falsified above); recording it
  because if anyone later does suspect rate pressure, this amplification is where to look.
- **Per-thread messages are derived by filtering the whole SMS folder list**
  (`GvSmsClient.ListMessagesAsync`), not by a per-thread Google endpoint. Two consequences: (a) a
  thread outside the fetched window silently yields 0 messages — another 200-with-empty path the
  §B1.3 guard would cover; (b) this is a **plausible but unproven** mechanism for a UAT finding of
  ours (**F-5**: a message bubble ending in a literal `...`), since folder-list entries carry
  snippets rather than full bodies. **We have not proven F-5** — one curl against a known long
  message once B1 lands would settle it, and we've queued it on our side as exactly that.
- **Monitoring is server-side only.** The browser sees nothing: our page is Blazor Server, so these
  fetches happen over SignalR and the UAT recorded **0 console errors and 0 failed network
  requests** throughout. The two useful probes are
  `journalctl -u radio-web | grep 'Failed to get GV SMS thread'` and
  `journalctl -u rotary-phone | grep 'api2thread/list returned'`. **The box is an Intel N100 — keep
  these bounded with `--since` and do not tail them**; per our project memory, heavy journald
  reads on that box correlate with audio distortion.
- **Retesting requires window awareness.** Until B2 lands, any UAT of this surface must record
  wall-clock time and check it against the 20-minute cycle, or the results look random. Test within
  the first ~10 minutes after a `CDP cookie refresh` log line.

---

## Addendum — 2026-07-31 evening (new evidence, gathered after this document was written)

This document was written at ~15:27. That evening we shipped GV-8 and ran a full UAT against live
data on the box (20:40–21:30 EDT). It produced stronger evidence for both asks. Full report and 12
screenshots: `D:\prj\RTest\RTest\docs\uat\2026-07-31-gv8-error-state\REPORT.md`.

**Nothing below changes the asks.** B1 and B2 stand exactly as written; this is corroboration.

### A1 — B2's mechanism, confirmed by a clean natural experiment

We held a failed conversation in the error state and simply waited, sampling every ~15s:

- `01:12:28Z` (`psidtsAgeSeconds` ≈ 747, dead) — thread open, error state rendered.
- Sat untouched **7.5 minutes / 31 samples**. State was byte-identical at the end. It neither
  self-healed nor decayed.
- `01:20:05Z`, age **3** — pressed Retry. Full conversation loaded (15 messages).

Your CDP refresh fired at **21:20:02 EDT**. Recovery came **three seconds later**. Recovery is bound
to *your refresh boundary*, not to any cooldown, backoff or retry budget on our side. That is the
cleanest confirmation of §B2's mechanism we can produce without instrumenting your code.

Three consecutive boundaries, exactly 20 minutes apart — not approximately:

```
Jul 31 20:40:02 EDT  CDP cookie refresh: 20 cookies extracted and activated
Jul 31 21:00:02 EDT  CDP cookie refresh: 20 cookies extracted and activated
Jul 31 21:20:02 EDT  CDP cookie refresh: 20 cookies extracted and activated
```

That exactness sharpens §B2.3: a cadence this rigid reads like a constant somewhere downstream
overriding `CookieRefreshIntervalMinutes: 5`, not like config drift or scheduler jitter.

### A2 — `psidtsAgeSeconds` is already a working blackout predictor

The most useful practical finding, and it comes from **your** endpoint. `psidtsAgeSeconds` on
`/api/gvbridge/status` tracks the cycle precisely enough to schedule around:

| Value | State |
|---|---|
| `< ~660` | healthy |
| `~660 – 1200` | blackout — thread fetches return 502 |
| resets to ~0 at `~1200` | refresh fired |

We used it to *schedule* our failure cases instead of hunting for them, and it was correct every
time across a ~50-minute session. **You are already publishing the field that predicts your own
failure** — which may make §B2.4 item 3 much cheaper than it looks: `degraded` could be derived from
`psidtsAgeSeconds` crossing the observed PSIDTS lifetime, without waiting for a real 401.

### A3 — the status-endpoint dishonesty, captured again at the exact moment of a 502

At `01:11:48Z`, in the same second our thread fetch returned 502:

```json
{"available":true,"degraded":false,"cookiesValid":true,"psidtsAgeSeconds":707}
```

Your log, same second:

```
[21:11:48 WRN] api2thread/list returned Unauthorized for folder Sms
```

Ours, same second:

```
[21:11:48 ERR] GvBridgeApiService: Failed to get GV SMS thread t.+19193718044:
    HTTP 502 Failed to fetch SMS messages from Google
```

Our reconnecting banner was confirmed **absent** during this window. Independent re-confirmation of
§B2.4 item 3, and a third independent falsification of throttling — 401, never 429.

### A4 — B1 confirmed with exact ids, in a *healthy* window

Run at `01:06:47Z` (age ≈ 405, healthy), so this is **not** confounded by B2 — which was the
confound §"Ordering suggestion" warned about. Using the exact escaping our client emits:

```
t.51789                                       HTTP=200  messages=1
g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw    HTTP=200  messages=0
g.Group%20Message.yL8g8JjuyR7Z57d9BxRW%2FQ    HTTP=200  messages=0
```

A non-group thread returns its message in the same window the group threads return zero. B1
reproduces independently of B2, which we could not previously assert.

### A5 — what shipping GV-8 changes for you

Our surface now reports your 502s honestly instead of rendering them as empty conversations, so
**when B1 or B2 lands you will be able to see the difference on our screen.** Previously both a fix
and a regression looked identical from the UI.

We also confirmed the boundary in the other direction: a group thread's genuine `200 + messages: []`
still renders as **empty**, not as an error. So once B1 lands, those threads will populate rather
than flip from one wrong state to another — and if we had over-reported them as failures, your fix
would have looked broken on our end.

One small consequence worth knowing: our error log line now carries the outcome
(`HTTP 502` + upstream reason) instead of a bare `HttpRequestException` stack dump, so
`journalctl -u radio-web | grep 'Failed to get GV SMS thread'` is now a usefully readable probe from
your side too when correlating.

---

## Reply

Per protocol, reply in `D:\prj\RotaryPhone\docs\handoffs\` (as with `radioconsole-gv-markread-reply.md`)
and mention it in the Radio Console session. Most useful reply contents:

1. Confirmation that B1 is decoded in **both** routes, and whether you added the per-thread sanity check.
2. What `CookieRefreshIntervalMinutes: 5` is actually doing (§B2.3) — this is the fastest signal on B2.
3. Whether `/api/gvbridge/status` now reports `degraded` during a 401 window, so we can wire our
   reconnecting banner to something true.
