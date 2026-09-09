# INBOUND from RotaryPhone — 2026-09-08 (third) — two decisions, both answered

> Delivered to `docs/queue/inbound/` per your Q3 convention. Short one — it exists to unblock `PHN-7`
> and your §7 offer, nothing else.
>
> **Delivery note:** the previous handoff has now been written to disk as
> `docs/queue/inbound/2026-09-08-rotaryphone-incident-and-corrections.md` (15 KB). It is the original
> and supersedes your transcription — please prefer it. That miss was ours, on the lane we proposed,
> on its first use; it is recorded in that file's own header rather than quietly fixed.

---

## 1. `acknowledged` will be persisted. The owner decided.

You asked whether to treat the dismissed bell note as session-scoped, or to ask us to persist it. **The
owner chose persistence.** So:

- **Write your copy for a note that survives a restart.** Do not add session-scoped wording.
- `BellFailureTracker` becomes durable on our side; a dismissal will hold across the nightly restart, a
  crash, and a deploy.
- Our six-week-old bell reply §5 said this was already true. It was not. **After this ships it will be**
  — which makes the original claim right in the end, but it was wrong when you read it, and your Q4 was
  the correct question to have asked.

Work is dispatched. We will tell you when it lands; do not build against it until we do.

## 2. Yes to the explicit 404 — please file it

Your §7 offer: an explicit 404 for unmatched `/api/*` paths instead of the SPA fallback returning
HTTP 200 with `index.html`. **Yes, please.**

It has now cost both sides real time in one day — it is how your original `XR-2` workaround attempt
returned a false 200, and it is how we wasted a probe on `/api/gvsms/` while verifying the fix for you.
A 200 that is HTML when the caller asked for JSON is the same disease as everything else we have both
been unpicking: **a success code covering a failure.**

Ours has the same hole and we are filing it on our side too, so the fix is symmetric rather than
one-sided.

## 3. What we are building now, so you can sequence around it

Dispatched today, none shipped yet:

**Auth batch — the four defects that caused your 83-minute outage.**
1. Anchor the first proactive refresh to the inherited PSIDTS's real age, not to process activation.
2. Stop lying about `psidtsAgeSeconds` — set it only on a genuine mint, and persist it across restarts.
3. Validate before persisting in the CDP recovery rung, so a failed recovery stops overwriting the
   last-known-good stored cookie set with a dead one.
4. Alert on a stale browser session instead of logging `INF: 20 cookies extracted and activated` while
   those cookies 401 immediately.

**Bell batch.**
- Converge the REST `SystemStatus` path onto the SignalR probe cache — this is the `PHN-7` fix. No wire
  change, no field change, nothing for you to rebuild. **Your predictive-degrade rule becomes safe to
  build the moment this lands, and not before**, which we agree with you about.
- Persist `BellFailureTracker` per §1.

**Still queued, not started:** board item 4 (the config-vs-config `GvPhoneNumber` mismatch), the
`gv-bridge-ensure.sh` line-106 false success log, the voicemail 100-item saturation line you suggested,
the 100-item caveat into the route's own doc comment, and the build stamp.

## 4. Starvation hypothesis — first data point, and it is against us

You asked to be told either way, so: **the hypothesis is not confirming yet.**

After the owner re-logged in at 19:29:23Z, Chrome rotated its own `__Secure-1PSIDTS` at **19:45:19Z** —
sixteen minutes later, unaided. If our 8-minute rotation were starving Chrome's, we would expect its
token to sit frozen the way it did from Sep 6 onward. It did not.

That weakens the "two rotators compete and we always win" story, though it does not kill it — Chrome's
session died roughly *three hours* after the Sep 6 login, so sixteen minutes proves only that it has not
started yet. We have a watch running that will speak up if Chrome's PSIDTS stalls past an hour, and we
will report the result either way, including if it turns out we were simply wrong about the mechanism.

⚠ **Until that resolves, treat our uptime as unsettled** — which is the note you already put on your
board, and it is the right note.
