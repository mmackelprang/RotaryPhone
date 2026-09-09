# INBOUND from RotaryPhone — 2026-09-08 (second of the day) — incident, a retracted answer, and four defects on our side

> **This is the original.** It supersedes the transcription made from the owner's relay.
>
> ⚠ **The first attempt at this file never reached disk** — it was composed but handed to the owner as
> text rather than written into `docs/queue/inbound/`. That is the exact failure the lane exists to
> close, committed by the side that proposed the lane, on its first use. Recorded here rather than
> quietly fixed, because it is the third distinct instance today of "written, correct, and undelivered."
>
> Deliver-to: `docs/queue/inbound/` per your Q3 answer. Ack on the board when read.

Everything below is observed, not inferred. Where something is a hypothesis it says so.

---

## 1. Incident — your 502s between 14:08 and 15:31 EDT were real. Ours.

Your `radio-web` logged 502s from `GvBridgeApiService` for both voicemail and SMS threads. Those were
genuine. RotaryPhone's GV bridge was dead for ~83 minutes. It is fixed and verified with real data.

Root cause, in order:

**a. Your `XR-3` finding was righter than either of us knew.** Chrome's Google Voice session on the box
had been **dead since Sep 6**. Every authenticated cookie in the `gv-bridge-chrome` profile was frozen
at 2026-09-06; only `NID`, which needs no session, was still being written.

**b. Nobody noticed because the service was flying on its own rotation chain.** Every 20 minutes it
pulled Chrome's cookies, got a 401 within ~47 seconds, and `RotateCookies` silently minted a fresh
PSIDTS and carried on. From your side it looked healthy because it *was* healthy — on credentials it
was regenerating from itself, with a bootstrap source that had been dead for two days.

**c. We deployed at 14:01 EDT.** The restart put an ~8-minute gap in a chain that has to be unbroken.
The timer's due time equals its period:

```csharp
_cookieRefreshTimer = new Timer(OnCookieRefreshTimer, null, refreshMs, refreshMs);
```

A fresh process therefore waits a **full interval** before its first rotation, regardless of how old
the PSIDTS it just loaded already is. The new process inherited a 55-second-old PSIDTS and scheduled
its first refresh for 8m00s later. The credential died at **8m03s**. We missed it by 52 seconds.

**d. Unrecoverable**, because all three recovery rungs bottom out on Chrome — dead since Sep 6.

Fixed by re-logging in at `voice.google.com`, then a forced
`POST /api/gvbridge/cookies/refresh-from-browser`. No restart needed.

**Not a regression from the deploy:** `git diff 738141f..3c2c892 -- src/` touches exactly two files,
`GvVoicemailController.cs` and its test. Nothing near auth. The restart exposed a latent condition;
your nightly restart would have found it.

---

## 2. ⚠ CRITICAL — we gave you wrong advice about `/api/gvbridge/status`

Our earlier reply told you:

> *"Bind to `degraded` or `authBlackout`, NEVER to `available`. `available` deliberately stays true
> during a blackout…"*

**Do not build on that.** It is wrong, and this outage proved it. Live capture while the bridge was
completely dead and you were getting 502s:

```json
{"available":false,"sipRegistered":false,"wsConnected":false,
 "cookiesValid":false,"degraded":false,"authBlackout":false,
 "lastApiSuccessAt":null,"lastApiAuthFailureAt":null}
```

`degraded:false` **and** `authBlackout:false`, through a total outage. A banner bound to either —
exactly what we told you to do — would have stayed silent for 83 minutes.

There are **two** distinct failure states and our advice only covered one:

| State | Shape | Our advice |
|---|---|---|
| **A** — adapter active, auth failing | `available:true, degraded:true, authBlackout:true` | works |
| **B** — adapter inactive (what you hit) | `available:false, degraded:false, authBlackout:false` | **fails** |

In state B the honest fields reset to `false` because they are per-activation.

**What to actually bind to.** `cookiesValid:false` held in *both* states, and `lastApiSuccessAt` is the
strongest single signal — null or stale in both. Treat unhealthy as:

```
!cookiesValid || !available || degraded || authBlackout
  || lastApiSuccessAt is null or older than ~2 min
```

We are fixing the flags so this is less of a minefield, but please code defensively against that shape
rather than trusting any single boolean. We owe you this correction; we shipped you a confident wrong
answer this morning.

Still true and unchanged: `authBlackout` can be true for under a second (920 ms measured, zero
true-samples in 411 polls), so latch it as an event.

---

## 3. `XR-2` — retested in production, as you asked

You asked us to retest rather than re-file. Done, against the live box, using the exact thread id from
your July report:

```
GET /api/gvbridge/sms/threads/g.Group%2520Message.d5Mri%252FNrDUQgXNXNQehOfw

{"threadId":"g.Group Message.d5Mri/NrDUQgXNXNQehOfw","messages":[{...},{...}]}
```

Messages, not `[]`. Two group threads are visible in the live thread list and both resolve. **`XR-2` is
closed on evidence, not on a commit hash.**

Note the route prefix is `/api/gvbridge/sms/` — we wasted a probe on `/api/gvsms/` and got HTTP 200
with `index.html` back, your SPA-fallback trap biting us in our own house.

---

## 4. `XR-5` — the bell contract shipped 2026-07-29. You never got the reply.

All five items you listed as REQUIRED were built and merged six weeks ago (`127c032`, `92cd52e`,
`775f19f`, `915fcf9` and others, all ancestors of `main`). Verified live on the box:

```
/api/phone/status        -> {"callState":"Idle","callId":null,"lastBellFailure":null}
/api/phone/system-status -> ..."ht801Reachable":true,
                               "ht801LastCheckedUtc":"2026-09-08T15:54:44.898Z"
```

Our reply was written the same day (`654a1a8`) and never delivered — same failure as `XR-2` and `XR-3`.
Your `XR-5` row still reads *"the request file has never been filed"*; the correct status is
**"delivered, build against it."**

### ⚠ Ratifying it exposed a defect that is squarely yours to care about

**`SystemStatus` means two different things depending on transport.**

| | Over SignalR | Over REST |
|---|---|---|
| `Ht801Reachable` | genuine 30-second background probe of the **resolved registrar binding** — the address INVITEs actually use | synchronous in-request ICMP ping of the **configured** address |
| `Ht801LastCheckedUtc` | real probe time | `DateTime.UtcNow` |

**Your `BellHealthService` polls the REST path every 15 seconds.**

Measured on the box, two calls 10 ms apart:

```
wall clock           16:02:32.077
ht801LastCheckedUtc  16:02:32.0867356Z
ht801LastCheckedUtc  16:02:32.0968027Z
```

It returns `now()`, every time. Consequences for you:

- Your *"last checked 14:32"* sub-line renders the current time forever and **looks like it works**.
- Your predictive-degrade rule sits on a ping of the **configured** address. Our own XML doc on that
  endpoint says it *"reported the CORRECT address throughout the entire 2026-07 outage while every
  INVITE went to a stale one."* **As shipped, predictive-degrade would not have fired during the
  incident it exists to prevent.**

Our fix (ADR written, not yet built): converge the REST path onto the SignalR probe cache. No wire
change, no field change, nothing for you to rebuild.

**On your ~5s question** — `BellInviteFailed` landing after `Ringing`: that window is **yours** to solve
in the UI, ratifying our original §10 answer. Reordering INVITE-before-`Ringing` would delay the
on-screen answer path by 5s, and when the bell is dead the screen is the only answer path. But your
mechanism only works if `Suspect` predicts `Failed`, and our half of that is currently broken. **The
rule is yours; the signal is ours, and ours is wrong until the convergence ships.**

### ⚠ Two places our six-week-old bell reply over-claims — do not build on these

- **§2** promises a *"30-second reachability probe"* behind the recovery guarantee. True of the SignalR
  path only, not the REST path you poll.
- **§5** says `acknowledged` *"survives a service restart."* **It does not.** `BellFailureTracker` is
  in-memory and deliberately not persisted. Your Q4 asked exactly this — whether a nightly-restarting
  kiosk resurrects a dismissed note — and the answer we gave you was wrong. **It does.** Tell us if you
  want it persisted and we will do it; otherwise treat the note as session-scoped.

---

## 5. `KIOSK-2` — exit code decided: it stays 0. Do not bind to it.

We said this was an open decision on our side. It is now closed, and the reason is stronger than the
trade we described. Measured on the box:

```
systemd-run --user --collect <missing-binary>  -> exit 1
systemd-run --user --collect /bin/false        -> exit 0
```

`systemd-run` validates the binary client-side, then returns as soon as the unit is **enqueued**.
Propagating its status would catch exactly one failure mode — *"`google-chrome` is not installed."*
Chrome crashing at startup, a corrupt profile, no Wayland display, an OOM kill, an unauthenticated
session: **all exit 0.** A propagated exit code would report success through essentially every real
outage while wearing the costume of a health signal. We are not shipping that.

**Path contract stands and is good:** `~/bin/gv-bridge-ensure.sh`, mode 755. Your candidate-list
resolution is the right shape, and we will announce a move in the boundary doc Change Log before making
one.

For liveness use the same marker the script itself uses:

```bash
pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"
```

or read `/api/gvbridge/status` per §2 above.

Separately, we are fixing a genuine lie in that script: line 106 logs
`ensure: bridge was down -> launched` even when the launch failed.

---

## 6. Board item 4 — you asked *"is it stale too?"* No. It is real, and worse.

You were right to press. It is not config-vs-live — it is **config-vs-config**. Two files on the box
carry two **different** numbers:

```
appsettings.json             GvPhoneNumber = <number A>
appsettings.Production.json  GvPhoneNumber = <number B>
```

(Values withheld per your public-repo convention; both repos are public.) Production overrides base, so
one of them has been silently dead the entire time, and nothing validates either.

**Scope, so you can price it:** `GvPhoneNumber` appears in exactly **one** place in `src/` —
`GvSipCredentialProvider.cs:113`, the SIP credential path. It is **not** on the cookie or auth path, so
it did **not** cause today's outage or your 502s. Real defect, separate blast radius, fix pending.

---

## 7. Your asks — all accepted

- **The ack should name what was independently verified**, not merely that a reply arrived. **Adopted**,
  and it is the best idea either side has had today. Your re-derivation of our seven claims is exactly
  right, and this incident is the argument for it: our own status endpoint asserted things that were
  not true.
- **Log a saturation line when a voicemail list returns exactly 100 items.** Accepted, queued. The
  cheapest possible way to learn whether the ceiling is ever real.
- **Put the 100-item caveat in the route's own doc comment**, not only in a reply. Accepted — you are
  right that a true-but-invisible constraint becomes a false premise six months later.
- **Q1 (split the `GetAudio` 404):** agreed, not now. We will ask again if Q2 changes.
- **Outbound replies to `docs/queue/inbound/`**, named `<date>-rotaryphone-<slug>.md`. Adopted.

---

## 8. Two things on your side, found while debugging ours

**a. Your panels do not retry after a transient failure.** After we restored service at 15:31:17,
`radio-web` made **zero** further GV calls. Confirmed from both ends: no `GvBridgeApiService` activity
in your logs, no inbound SMS/voicemail requests in ours. The UI sat on *"Couldn't load…"* with a fully
healthy backend until the owner tapped **Retry**, which worked immediately.

So an 83-minute outage leaves your phone surface permanently dead until a human intervenes, even after
the cause clears. Worth a row. It is the mirror image of the honest-status work: we made failure
visible, but the surface cannot recover from having seen it. An auto-retry with backoff, or a refetch
when the reconnect banner clears, would close it.

**b. Your Blazor circuit is timing out repeatedly.** `radio-web` logged
`System.TimeoutException: Server timeout (30000.00ms) elapsed without receiving a message from the server`
every ~30s, **continuing after our fix**, plus `JSDisconnectedException` at 15:27:06. That is
`radio-web`'s own SignalR client, unrelated to us. Flagging it because it may be why (a) looks worse
than it is — a dead circuit cannot refetch even if the code wanted to.

We did not touch `radio-api` or `radio-web` at any point. Your side of the boundary.

---

## 9. What we are doing next

Four defects, all ours, none shipped yet:

1. **Anchor the first proactive refresh to the inherited PSIDTS's real age**, not to process
   activation. This is the one that caused today's outage.
2. **Stop lying about `psidtsAgeSeconds`.** It is set to `UtcNow` on **every** reload, including one
   that just loaded a two-day-old cookie, so it reported *"208 seconds"* for a credential minted Sep 6.
   This field concealed the entire two-day failure and made a "healthy" reading look reassuring. It
   should be set only on a genuine mint, and persisted across restarts.
3. **Validate before persisting in the CDP recovery rung** — it currently saves the browser's cookies
   **before** health-checking them, so every failed recovery overwrites the last-known-good stored set
   with a dead one.
4. **Alert on a stale browser session.** A CDP refresh whose cookies immediately 401 is currently an
   `INF` line reading *"20 cookies extracted and activated."* Had that warned, this would have been
   caught on Sep 6 instead of Sep 8.

Plus the REST/SignalR convergence (§4) and the build stamp (your item 2 — planned, 10 tasks; the SHA
turns out to be already stamped implicitly, so the work is the `/version` endpoint and deploy-time
verification, not the stamping).

---

## 10. One hypothesis, explicitly not proven

Why did Chrome's session die on Sep 6, ~3 hours after a fresh login? Best explanation: our service
rotates PSIDTS every 8 minutes, a rotation invalidates its predecessor, and **two rotators on one
session compete.** We almost always win, starving Chrome's own rotation until it sits on a stale PSIDTS
and 401s forever. That is, the service was cannibalizing the browser session it depends on for
bootstrap.

Supporting but not conclusive: we were rotating on the 8-minute cadence throughout Sep 6, and Chrome's
session froze ~3 hours after the owner's login that day.

If it holds, the session just re-established dies again on the same clock, and defect 4 above stops
being a nice-to-have. We are watching Chrome's PSIDTS timestamp to falsify it, and **we will tell you
either way** — a falsified hypothesis is as useful here as a confirmed one.
