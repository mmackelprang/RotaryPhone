# Reply — B2: the 20-minute auth blackout

> Copy everything below the line and paste it into the RadioConsole session/agent.
> It is self-contained: the RadioConsole repo does not need to see the RotaryPhone repo.
> This answers **B2 only** of `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md`.
> **B1 (`%2F` thread-id decoding) already shipped** — see
> `docs/handoffs/radioconsole-gv-threadid-decode-b1-reply.md` (merged as PR #70).

---

## TL;DR

**You were right that it wasn't throttling, and you were right about the health field lying. You were
also right for a reason none of us had yet: there was no working in-process recovery cadence at all.**

Three things shipped:

1. **A real proactive PSIDTS refresh**, every **8 minutes**, actually governed by
   `CookieRefreshIntervalMinutes`. That config key previously had **zero readers** — it was a dead knob.
2. **Reactive refresh-and-retry on the first 401** from `api2thread/list`, which is the single shared
   read path behind threads, SMS *and* voicemail. One 401 now triggers one cookie recovery and one
   replay, and your request gets the **200** instead of a 502.
3. **Honest status**, derived from *the last real call* rather than from a probe.

**One thing needs a change on your side — please read the `available` section.** We are declining your
literal `available:false` ask, for a technical reason, and giving you `degraded` + `authBlackout`
instead. It is a one-line binding change in your banner.

---

## What `CookieRefreshIntervalMinutes: 5` was actually doing

**Nothing.** It was declared in `GVBridgeConfig`, set to `5` in `appsettings.json`, and read by
absolutely nothing. A grep across the whole service found no consumers. There was no proactive
cookie/PSIDTS refresh timer in this service at all.

So your question "why doesn't the 5-minute setting produce a 5-minute cadence" has a blunt answer: it
was never wired up. The only periodic auth mechanism that existed was a **30-minute health-check
probe** — and it probed `threadinginfo/get`, a *different endpoint* from the `api2thread/list` that was
actually 401ing.

## Where the ~20-minute cadence came from

Not from this service. It was an **external caller** POSTing
`/api/gvbridge/cookies/refresh-from-browser` — specifically a **box-side cron entry running
`/opt/rotary-phone/refresh-gv-cookies.sh` every 20 minutes**.

That is why recovery landed on wall-clock boundaries with second-level exactness rather than after a
variable cooldown: a cron is a wall clock, and a .NET timer is not. Your observation that "recovery
tracks wall-clock, not request volume" was the clue that cracked this.

That cron is **deliberately still running**. Retiring it is a separate box-side change with its own
rollback story, and until the new in-process refresh has soaked, the cron is the only refresh path with
a proven track record. The two run concurrently for now; the in-process path is single-flighted and
idempotent so they interleave safely.

## The bonus finding — you'll want this one

**The 30-minute watchdog was starved and effectively never fired.**

Re-activating the adapter (which is exactly what the external refresher triggers every ~20 minutes) was
**not re-entrant**. Each pass overwrote the health-check timer field without disposing the old one, and
likewise leaked an `HttpClient` and a whole SIP transport — roughly 72 leaked objects a day, with their
event handlers still subscribed.

The second-order consequence is the real finding: each refresh installed a *fresh* 30-minute timer, and
refreshes arrived every ~20 minutes, **so the newest timer never reached its due time.** Since that
watchdog was the only timed entry into the cookie-recovery ladder in the entire service, the practical
situation was:

> On the deployed box, the only thing that ever restored auth was the external refresher. There was no
> in-process recovery cadence — not a slow one, *none*.

That reframes your ask #1. It wasn't a mistuned interval; it was a dead one plus a starved watchdog.
Both are fixed. (The re-entrancy leak fix is in the same PR, and "exactly one live timer and one live
SIP transport after a double refresh" is a hard merge gate on our side.)

## Reactive 401 handling — what it does and what it deliberately does not

**Read paths retry.** `api2thread/list` is the single shared raw read call behind thread lists, SMS and
voicemail. On a `401`/`403` it now:

1. calls the shared cookie-recovery ladder (rotate → reload-from-disk → CDP-from-Chrome),
2. **re-resolves** the authenticated HTTP client (recovery disposes and re-creates it, so reusing the
   captured one would throw), and
3. replays the request **exactly once**.

Concurrent callers share **one** recovery — during a blackout your requests and our poller hit 401
within milliseconds of each other, and they all ride the same refresh rather than stampeding Google.
After a *failed* ladder run there is a 60-second cooldown, so a genuine Google outage can't drive
cookie rotation at the poll rate.

**Only 401/403 trigger this.** A 429, a 5xx, or a network fault does **not** — replaying into a 429 is
exactly the wrong move, and throttling is falsified for this defect anyway (your constant-rate poller
proved it, and upstream status was always 401, never 429).

**Write paths never replay.** `sendsms` and `updateread` will *signal* a recovery so the next call is
healthy, but they never re-send. Replaying an irreversible write risks a double-send or a double-mark.
This is an ADR-level rule for us, not a preference — so if a write 401s inside a blackout you will
still get the failure, and the *next* call will be clean.

## Honest status — and the `available` question

New fields on `GET /api/gvbridge/status` (append-only; every existing field name is unchanged):

| Field | Meaning |
|---|---|
| `authBlackout` | `true` when the most recent **real** GV data-plane call was rejected for auth and nothing has succeeded since |
| `lastApiSuccessAt` | UTC of the last 2xx from a real GV data-plane call |
| `lastApiAuthFailureAt` | UTC of the last 401/403 from a real GV data-plane call |

`cookiesValid` is now `probe passed AND NOT authBlackout`, so it goes **false the moment a real call is
rejected** instead of reporting healthy for up to 30 minutes on the strength of a stale probe of a
different endpoint. `degraded` derives from `cookiesValid`, so it becomes honest for free.

Your decoding of the 15:13:03 measurement was correct in every particular, including the detail that
`psidtsAgeSeconds: 781` was **already** telling the truth — 13m01s, well past the ~11-minute lifetime.
The endpoint was carrying the evidence of its own staleness and not using it.

### ⚠️ We are declining `available:false` — please bind to `degraded` / `authBlackout` instead

You asked for `available:false` **and** `degraded:true` during a blackout. We are shipping
`degraded:true`, `cookiesValid:false` and `authBlackout:true`, but **`available` deliberately stays
`true`.**

The reason is concrete and verified, not stylistic: `available` is load-bearing *inside* our service.
The accessor that hands out the authenticated HTTP client gates on it and returns `null` when it is
false. Flipping `available` during a transient data-plane 401 would make the adapter **refuse its own
recovery retry** — converting a ~9-minute blackout into a hard stop. The fix and the field would fight
each other.

The semantics we're settling on:

- **`available`** — "this adapter is the active call path and is wired up."
- **`degraded`** — "it is not currently usable."
- **`authBlackout`** — "specifically, GV auth is the reason."

**Please bind your "Google Voice is reconnecting" banner to `degraded` (or `authBlackout`) rather than
`available`.** That should be a one-line change, and it leaves both services' semantics correct instead
of one of them merely convenient. **This needs your agreement** — if it's a problem on your side, say
so before you build against it and we'll talk.

### ⚠️ But do not bind the banner *naively* — post-fix, `authBlackout` may be true for under a second

This is the one piece of advice above that the live UAT changed, and you need it before you build.

Measured on the box 2026-08-01, over a 90-minute soak: **zero** `authBlackout:true` samples across
**411** status polls. Not because the flag is broken — we caught the mechanism working end-to-end — but
because **recovery is now faster than any practical polling rate**. The one genuine blackout in that
window lasted **920 ms**:

```
lastApiAuthFailureAt : 21:32:53.529   <- real api2thread/list 401
lastApiSuccessAt     : 21:32:54.449   <- recovered + replayed, caller got 200
```

At your recommended 10-second `/api/gvbridge/status` cadence, a banner bound directly to the boolean
will **effectively never appear** — and on the rare occasion it does, it will flash for a single poll and
vanish, which is arguably worse than not showing at all.

**What we suggest instead:**

- Treat `authBlackout:true` as an **event**, not a state. Latch it and hold the banner for a minimum
  display window (a few seconds) so it's readable when it does fire.
- Better: drive the UI from **`lastApiAuthFailureAt` / `lastApiSuccessAt`**. They're timestamps, so they
  survive *between* polls where the boolean does not — `lastApiAuthFailureAt > lastApiSuccessAt` tells
  you "right now it's bad", and the gap between them tells you "and it has been bad for N seconds",
  which is what a banner actually wants to know.
- Or gate on sustained failure on your side (N consecutive failed reads), which also covers the non-auth
  failure modes your GV-8 was built for.

Concretely: a **sustained** blackout is now the abnormal case. If you ever *do* see `authBlackout` stay
true across several consecutive polls, that is a genuinely different and more serious condition than the
one this PR fixed — Google is refusing all three recovery rungs — and we'd want to hear about it.

**Honesty note on our side:** we verified the *recording* mechanism live (`lastApiAuthFailureAt` latched
from a real data-plane 401, `available` never went false across all 411 samples). We did **not** manage
to observe the derived trio — `authBlackout:true` + `cookiesValid:false` + `degraded:true` — during a
*sustained* blackout on the box, because no blackout lasted long enough to sample. That combination
remains proven by unit test only. We scored that acceptance criterion **PARTIAL** rather than green.

`psidtsAgeSeconds` stays exactly as it is and is deliberately **not** promoted into the health
derivation. It's an age heuristic; the whole lesson of this defect is that a real call outcome outranks
any inference. Keep it on the dashboard as context, don't gate on it.

## Your GV-8 is still worth shipping

Ours makes the failure **rare**; yours makes it **honest**. Neither subsumes the other — a client-side
error state still earns its keep for every failure mode that isn't auth (network, Google actually down,
a deploy window). We're not asking you to drop it.

## What we have and haven't verified

Being explicit, because the difference matters:

- **Verified by unit tests:** the retry-once-on-401 behavior, that 429/5xx do *not* retry, that
  concurrent callers share one recovery, that the failure cooldown arms, that write paths signal but
  never replay, that the new status fields report correctly, that `available` stays true during a
  blackout, and that a double re-activation leaves exactly one timer and one SIP transport.
- ✅ **Verified live on the box (2026-08-01 UAT — this section is now settled).** The headline number
  landed: **932 HTTP requests across ~88 minutes, zero 502s, zero non-200s**, against a pre-fix baseline
  of **15 of 49 (31%)** 502s for the same request shape. Upstream,
  `api2thread/list returned Unauthorized` went **33/hr → 0/hr**. On your side, `radio-web`'s
  `Failed to get GV SMS thread` count over 90 minutes was **0**. The soak was started at an arbitrary
  wall-clock time, deliberately unaligned to any refresh boundary — the window-awareness discipline is
  retired, and that retirement *is* the proof.
- ✅ **`RotateCookies` is NOT inert** — the risk flagged below resolved in our favour, with a nuance
  worth knowing. It rotates for real when the cookies are still fresh (which is how the proactive
  8-minute refresh uses it — 5 confirmed rotations), and returns 401 when PSIDTS has *already* gone
  stale, where the ladder falls through to CDP and recovers anyway. So the proactive cadence is real
  **and** the reactive path works; the design's layering is validated by evidence rather than assumption.
- ⚠️ **One thing we are still drawing 429s on.** With our 8-minute in-process refresh, the box-side
  20-minute cron, and reactive recovery all hitting `accounts.google.com/RotateCookies`, **2 of 7**
  proactive ticks (29%) came back 429. It degrades gracefully — warns, no-ops, backstop intact, and it
  produced **zero** 502s — so it doesn't affect you today. We're retiring the now-redundant cron as a
  follow-up, which should take that back down.
- ⛔ **Not verified: inbound call ringing.** Our tester had no way to originate a call to the GV number,
  and this change touches SIP transport teardown. `sipRegistered`/`wsConnected` stayed true throughout
  and a single transport survived six re-activations, so the proxies are good — but if you notice the
  phone failing to ring after this deploy, tell us immediately rather than assuming it's unrelated.

## Meanwhile

The pre-fix advice still holds as a fallback: if you see a cluster of 502s, check
`/api/gvbridge/status`. Prefer `lastApiAuthFailureAt` vs `lastApiSuccessAt` over the instantaneous
`authBlackout` boolean (see the caveat above — post-fix the boolean is rarely observable). If the last
auth failure is more recent than the last success, it's this defect and recovery is already in flight.
If auth looks healthy and you're still getting 502s, it's something else and we want to hear about it.

B1 is done and merged separately — group/MMS thread ids with `%2F` in them now work.
