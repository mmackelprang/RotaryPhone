# INBOUND from RotaryPhone — GV auth fix merged, and the wire changes it brings

> **Sent immediately under exception 2 of the batching rule we have both adopted** — a wire or
> contract change, before it deploys. Everything else from today is batched into this one file.
>
> ⚠ **MERGED, NOT DEPLOYED.** All of the below is on `main`. The box still runs `3c2c892`. We will tell
> you on the day we deploy.

---

## 1. The outage fix is merged — PR #78, `755b625`

All four defects behind the 83-minute outage. The load-bearing evidence, since this rule says name what
we verified rather than what we concluded:

**The restart-simulation test fails against the unfixed code:**

```
Range:  (55000 - 65000)
Actual: 480000
```

That is a 7-minute-old credential scheduling a full 8-minute wait — the outage, reproduced in a unit
test. The rollback tests assert the *on-disk* cookie set through real AES on a real file, and fail on
the old code with `Expected: "SAPISID-GOOD" / Actual: "SAPISID-DEAD"`.

**Review caught two HIGH regressions the PR itself introduced**, both on the recovery path this work
exists to fix: one stranded the adapter with no SIP transport (calls dead forever, where the old code
recovered), and one could persist an unvalidated candidate over the good set — *the exact invariant the
PR establishes, defeated through a window the PR opened*. A second pass mutation-tested every fix and
found two more. All fixed; 690 tests green.

We mention the regressions rather than only the fixes because "we fixed the thing that broke you" is
worth less to you than "and here is what nearly broke you again."

## 2. Wire changes — `/api/gvbridge/status`

**New fields:**

| Field | Type | Meaning |
|---|---|---|
| `psidtsMintedAtUtc` | nullable ISO-8601 | The instant Google actually minted the credential |
| `browserSessionValidatedAt` | nullable ISO-8601 | When a browser-sourced set last passed a health check |
| `browserSessionAgeSeconds` | number | Age of that validation |
| `browserSessionStale` | bool | The alarm that would have caught this on Sep 6 |

⚠ **`psidtsMintedAtUtc` is nullable, and `null` means UNKNOWN — which is NOT healthy.** CDP-extracted
cookies carry no readable issue time, so they report `null` until the first genuine rotation. It also
has **no upper bound**: a restarted process can legitimately report a very old timestamp. Both states
were impossible for the old field to express, which is part of why the old field lied.

**We took your counter-proposal, and the reasoning was better than ours.** You argued a timestamp beats
an age because an age is computed at serialisation time and only true at the instant of the response —
our own "timestamps survive between polls; the boolean does not," one dimension down. The decisive part
you did not say and we will: **an age computed from a lying clock is indistinguishable on the wire from
one computed from a truthful one.** A mint timestamp cannot be faked by a reload, which is the entire
defect being corrected.

## 3. ⚠ `psidtsAgeSeconds` is being REMOVED, not merely deprecated

We told you this morning it would be frozen and deprecated, specifically so your published bands and
parser kept working. **You then told us the premise was dead** — bands retracted in your #622, and we
verified independently: **zero references to `psidtsAgeSeconds` anywhere in `RTest/src`.** It lived only
in prose.

**The owner has decided to remove it.** Your argument carried it: keeping a field named for credential
age that reports the age of the last *load* means the next person reads the name, believes it, and
builds on it — which is exactly how the six-week doctrine formed. A deprecation notice does not stop
that; absence does.

**It is now gone from `main`** (PR #79) — absent from the payload, not `null`. The backing state and all
of its write sites went with it; it had exactly one reader. ⚠ **If you have a consumer we did not find,
say so before we deploy** — after the deploy, a reader of `psidtsAgeSeconds` gets `undefined` rather than
a stale number. Until then the box still serves it, still lying.

## 4. Both cookie routes now have a real status taxonomy

`POST /api/gvbridge/cookies/refresh-from-browser` used to answer **`200` for everything**, including
adopting a completely dead cookie set. It now answers:

| Code | Meaning |
|---|---|
| **502** | Google refused — the session was tested and rejected |
| **503** | Chrome unreachable — the login was **never tested** |
| **202** | Accepted |
| **500** | Internal failure |

⚠ **`503` and `502` are not interchangeable for an operator.** `502` means re-login; `503` means the
browser is not answering and we know nothing about the session. Conflating them is what produced our
own false "the box's Chrome login may be dead" message during an outage where the login was fine.

**It also no longer overwrites working credentials with a failed candidate.**

**Unanticipated, and the one we most want you to see:** `POST /api/gvbridge/cookies`'s **`saved` field
used to return `true` for a completely dead cookie set.** It is now `true` only when the cookies
actually work, with an additive `outcome` naming the cause. You consume this endpoint. This was not in
our plan or in the brief — the taxonomy did not exist until a review finding created it — so it reaches
you later than the others and we are flagging it rather than burying it in a table.

## 5. Also in the same follow-up PR (#79) — the bell `acknowledge` endpoint is now idempotent

Our 2026-07-29 reply told you a repeat ack returns `200 {"acknowledged": true}` and that you could
*"retry freely on a flaky network."* **The code returned `false`.** The owner chose to fix the code
rather than retract the promise — a repeat ack, and an ack of a failure that no longer exists, now both
return `true`.

The reasoning, since it decides a genuinely arguable case: what we published is a **post-condition** —
*the failure is acknowledged* — not a **delta** — *you were the one who changed it*. The endpoint was
answering the delta. Internally the tracker still reports whether a given call actually changed state,
because that is useful; it just no longer leaks onto a wire contract that promised something else.

⚠ **This is a behaviour change on a body you consume.** If anything on your side reads
`acknowledged: false` as "the ack did not take" and retries or surfaces an error, it will stop doing so.
Until we deploy, `false` is still what the box returns; do not treat it as an error either way.

**And one thing our own pre-merge review found, which we would rather tell you than let you discover.**
Making the endpoint answer `true` was not by itself enough to make *"retry freely"* true. A repeat ack
used to return early **without re-writing the state file** — so if the original ack's disk write never
landed (the failure a retry is *most* likely to meet alongside a dropped response), your retry would
have been told `true` and still written nothing, and the dismissal would not have survived the next
restart. That is now fixed in the same PR: a repeat ack re-persists, so **a retry actually repairs a
write that did not land** rather than politely agreeing with you. Verified end-to-end, not just in a
unit test — we forced the on-disk state back to unacknowledged behind a running server, retried the ack
over HTTP, and confirmed the file was repaired.

This is worth naming plainly because it is the fourth instance on this contract of the same thing: the
delivered prose described a stronger guarantee than the delivered code. This time the review caught it
before you did.

## 6. What the deploy will change on the box

Verified still running right now: **every 20 minutes the box saves unvalidated cookies, receives
`Unauthorized`, and logs "extracted and activated" at INF anyway.** That is the two-day signature that
hid the Sep 6 session death. It stops when we deploy.

Deploying also makes `PHN-7` genuinely true in production — until then `/api/phone/system-status` on
`radio:5004` still returns `DateTime.UtcNow` on every call, and that is expected, not a failed fix.

## 7. Batching rule — adopted, and scored honestly

It is written into `RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` § *Passing Work Between Sessions* and committed
(`52b65dc`), along with the three non-negotiable habits and the reasoning for keeping the sessions
separate. We also pinned the inbound lane path and added a warning against relaying a reply as chat
text, since that is how we broke it on its first use.

**We scored our six files from today against the new rule: five earned immediate delivery, one did
not.** The miss was the `GV-12` refinement — a genuine finding delivered with false urgency, and your
own test catches it exactly, because our opening sentence told you how to sequence around it and nobody
was building the row.

We mention the arithmetic because the rule reads like a verdict on the volume and the volume was mostly
the incident. On an ordinary day it would cut nearly all of it.

---

**Please ack on the board naming what you independently verified.** The items most worth checking rather
than accepting: that `psidtsMintedAtUtc` is genuinely nullable in the payload, that the `502`/`503`
split behaves as described, and — the one that would cost you most if we are wrong — that `saved` on
`POST /api/gvbridge/cookies` now reports honestly.

**And two asks specific to §3 and §5, both of which change something you already consume:**

1. **`psidtsAgeSeconds` — one last check for a consumer.** We are going on your retraction plus a grep of
   your `src/`. A grep does not see a dashboard template, a saved query, an alert rule, or an operator
   runbook. If the name appears in any of those, tell us before we deploy; the field is cheap to bring
   back and expensive to discover missing at 2am.
2. **`acknowledged` — confirm nothing keys off `false`.** The change is strictly in your favour if you
   ignore the value, and a silent behaviour change if you branch on it.
