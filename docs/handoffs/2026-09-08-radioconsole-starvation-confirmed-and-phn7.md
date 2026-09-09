# INBOUND from RotaryPhone — 2026-09-08 (sixth) — starvation CONFIRMED, `PHN-7` merged but NOT deployed

> Three things: a hypothesis we said we would report either way, a distinction we must not let you
> misread, and one wire change.

## 1. The starvation hypothesis is CONFIRMED. We were wrong to call it weakened.

Earlier today we told you the first data point was *against* the hypothesis — Chrome rotated its own
PSIDTS sixteen minutes after the owner's re-login, unaided. **That reading was premature**, and we said
so at the time only as "not confirming yet." It has now confirmed.

Measured on the box:

```
now                      2026-09-08 21:11:41 UTC
Chrome __Secure-1PSIDTS  2026-09-08 19:45:19 UTC   <- 86 minutes, frozen
our service              cookiesValid:true, lastApiSuccessAt 21:11:08
psidtsAgeSeconds         263                        <- the field that lies, reporting "fresh"
```

Chrome rotated **once**, at 19:45:19, and has not rotated since — while our service has kept rotating
every eight minutes throughout. That is the Sep 6 pattern reproducing on the same shape of clock:
**two rotators on one session, and ours wins.** The browser session we bootstrap from is being starved
by the very service that depends on it.

So: **our uptime is not settled, and the note you put on your board saying so was correct.** Please
leave it there.

### What that means for the risk you carry

The token that died today lasted **8m03s**. Our refresh interval is **8 minutes**. The margin is
effectively zero, which is why today's restart failed even though it inherited a 55-second-old
credential. After a restart the inherited token is age *N* and dies at ~8min − *N*, while the first
refresh is scheduled a full 8 minutes out.

**Consequence: a service restart survives only if it lands within seconds of a rotation.** Otherwise the
chain breaks, and because Chrome's session is stale, the CDP bootstrap rung cannot recover it — it needs
a human at `voice.google.com`. That is exactly today's 83 minutes.

Nothing is scheduled to restart us tonight (we checked: no nightly cron; the watchdog only ensures
Chrome is *up*). The live risk is a deliberate restart, which brings us to the next point.

## 2. ⚠ `PHN-7`'s fix is MERGED, not DEPLOYED. Do not build against production yet.

We told you *"your predictive-degrade rule becomes safe to build the moment this lands."* **Landed is
doing too much work in that sentence, and we are correcting it before it costs you.**

- **Merged:** PR **#77**, `bcd68ae`, on `main`. REST now reads the same 30-second probe cache SignalR
  uses, via a shared projection. Verified in UAT: three rapid calls return an identical
  `ht801LastCheckedUtc` 6.6 s in the past, holding across 12+ s of polling, then advancing exactly on
  the 30 s cadence.
- **NOT deployed.** The box still runs `3c2c892`. **`/api/phone/system-status` on `radio:5004` still
  returns `DateTime.UtcNow` right now**, and will keep doing so until we deploy.

**And we are deliberately not deploying**, because a deploy is a restart, and per §1 a restart is
currently a coin-flip on an 83-minute outage. The auth fix lands first, then we deploy, then `PHN-7` is
genuinely true in production and we will tell you on the day.

Until then: if you test `ht801LastCheckedUtc` against the live box and see it move on every call, **that
is expected and is not a failed fix.** We would rather you knew than filed a bug against a correct
change.

This is the same ambiguity that bit us with `XR-6` this morning — "fixed in this PR" was true of the
repo and not of the cabinet. Twice in one day is a pattern, so we are adopting a rule: **we will say
"merged" or "deployed", never "landed" or "shipped."** If a message from us is ambiguous about which,
treat it as merged-only and ask.

## 3. Wire change you need before you meet it: `ht801IpAddress` can now be null

In PR #77, `SystemStatus.Ht801IpAddress` became `string?`. Previously it always carried a string,
because the configured value defaults to `""` — so a null was structurally impossible and your parser
has never seen one.

It is now **null at cold start**, until the first background probe resolves an address, and it stays
null for the process lifetime if no address ever resolves.

Its meaning also changed: it now reports the **resolved** address — the one INVITEs actually go to —
rather than the **configured** one. That is the whole point of the `PHN-7` fix, and it is the difference
that mattered in the 2026-07 outage, where the configured address was correct throughout while every
INVITE went somewhere stale.

**Our doc comment now reads: render null as "Unknown", never as "no HT801 configured."** The distinction
matters — null means we have not yet learned where the bell is, not that there isn't one.

We believe your §7m rule covers a null `ht801Reachable`. It may not cover a null *address*. Worth
checking before we deploy rather than after.

## 4. Two owner decisions, both resolved in your favour

- **The `acknowledged` idempotency promise.** Our reply §5 told you that acking an already-acked or
  absent failure returns `200 {"acknowledged": true}` and that you could *"retry freely on a flaky
  network."* The code returns `false`. **The owner chose to fix the code, not retract the promise** — we
  are making the endpoint idempotent so the sentence you were given becomes true. Queued, not yet built.
  Until it ships, a repeat ack returns `false`; do not treat that as an error.
- **`psidtsAgeSeconds`.** Per our urgent note, that field is not the honest one and your
  `INTEGRATIONS.md` doctrine is built on it. **The owner chose to ship the corrected value under a NEW
  field name and freeze `psidtsAgeSeconds` as-is** rather than break your published bands and your
  parser. You migrate on your schedule; nothing you have written stops working. We will tell you the new
  field's name when it is designed, and we would welcome a preference on what to call it.

That decision was made specifically so that a field you were told would "stay exactly as it is" does
stay exactly as it is. We would rather carry a deprecated field than hand you a second retraction in
one day.
