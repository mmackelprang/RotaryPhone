# ⚠ URGENT INBOUND from RotaryPhone — 2026-09-08 (fifth) — `psidtsAgeSeconds` is NOT the honest field

> Read this before any further work that reads `/api/gvbridge/status`. It contradicts a doctrine
> currently written into your `INTEGRATIONS.md` and into `PHN-2`.

## The claim we need to retract, on your side this time

`design/INTEGRATIONS.md:722`:

> *"`psidtsAgeSeconds` is the ONLY trustworthy field on `GET :5004/api/gvbridge/status` — and it is a
> live blackout clock. … Read it as: `< 660` healthy · `660–1200` blackout … resets at ~1200. The
> sibling fields **lie**."*

`design/plans/PHN-2-retire-the-audio-element.md:1992`:

> *"Read **`psidtsAgeSeconds`** and **ignore every other field in that payload**."*

**`psidtsAgeSeconds` is the field that lies.** It is not a PSIDTS clock. It is an age-of-last-*load*
clock.

## The defect

`GVApiAdapter.cs` sets `_psidtsRefreshedAt = DateTime.UtcNow` on **every reload**, not on every mint —
at `:728` (`ReloadCookiesAsync`) and at `:397` (`ActivateCoreAsync`, i.e. the restart path). So loading
a two-day-old cookie off disk resets the counter to zero and the field reports a credential minted
Sep 6 as seconds old.

## The proof, from today's outage — the one you saw as 502s

Captured live while the bridge was completely dead and returning you 502s on every call:

```json
{"available":false,"sipRegistered":false,"wsConnected":false,
 "cookiesValid":false,"degraded":false,"authBlackout":false,
 "psidtsAgeSeconds":608,
 "lastApiSuccessAt":null,"lastApiAuthFailureAt":null}
```

**`608` is inside your `< 660` "healthy" band.** A second capture minutes later read `656` — still
"healthy." Your rule would have reported the surface healthy through the entire 83-minute outage, and
`PHN-2`'s UAT step *"Pass: `psidtsAgeSeconds` under 660"* would have **passed against a dead bridge**.

## Why it looked honest for six weeks, and why that is the dangerous part

**In steady state it is accurate by coincidence.** A successful rotation mints a new PSIDTS and reloads
the cookie set in the same instant, so load-time and mint-time coincide and the field tracks the real
credential age. Your two confirmations — the 2026-07-31 root-cause pass and the GV-8 UAT — were both
taken during steady-state operation, so both were correct observations of a field that happens to be
right when nothing has gone wrong.

**It decorrelates on exactly the three occasions that matter:**

1. **After a restart** — the process stamps `UtcNow` over an inherited credential of unknown age. This
   is what caused today's outage, and it is why the field read `608` on a corpse.
2. **After a recovery** — a reload from disk or from Chrome resets it, including a reload of cookies
   that are already dead.
3. **After adopting a stale browser session** — the 20-minute CDP refresh reset it every 20 minutes for
   two days while the credentials it adopted were failing immediately.

So the field is trustworthy precisely when you do not need it, and false precisely when you do.

## What to use instead

This supersedes the guidance in our own §2 retraction earlier today only by adding to it — that
predicate stands and is still correct:

```
unhealthy = !cookiesValid || !available || degraded || authBlackout
            || lastApiSuccessAt is null or older than ~2 min
```

**`lastApiSuccessAt` is the field your doctrine should have named.** It cannot be faked by a reload: it
moves only when a real authenticated call to Google actually succeeded. During today's outage it was
`null`. During the two-day masked failure it advanced normally, correctly reflecting that service *was*
working — on regenerated credentials, which is the truth.

`cookiesValid` also held correctly through both of today's failure states, and it is cheaper to read.

## What we are changing, and it is a breaking change for you

Our fix makes `psidtsAgeSeconds` report the **true** age of the credential, persisted across restarts.
Two consequences you must handle:

- **A restarted process can report a genuinely large value** where it previously reported a small one.
  Under your current bands that reads as "blackout" — which will now be *correct*, but it will look
  like a regression if you are not expecting it.
- **Unknown mint time becomes `null`**, which was previously impossible. CDP-extracted cookies carry no
  readable issue time, so they will report `null` until the first real rotation mints one. **Your
  parser must tolerate `null`.**

**Tell us if you would rather we ship this behind a new field name** and leave `psidtsAgeSeconds`
frozen — we can add `psidtsAgeSecondsTrue` or similar and deprecate the old one on your schedule. Our
earlier reply told you the field *"stays exactly as it is,"* and we read that as a promise not to
promote it into health derivation rather than a promise never to fix its accuracy. If you read it the
other way, say so and we will do it your way; it is your integration and you have written doctrine
against it.

## One more thing, said plainly

We found this defect **five weeks ago and shipped nothing.** `docs/KNOWN-ISSUES.md` finding **L2**
records that `psidtsAgeSeconds` *"resets on activation regardless of the cookies' true issue time"* and
that it read `6` right after a restart whose on-disk PSIDTS was ~7 minutes old. We scored it **LOW**.

The same document's *"Proposed hardening (not implemented)"* section, dated 2026-08-01, proposes exactly
the persist-before-validate fix for the other defect that made today unrecoverable.

Both defects were observed, written down, correctly diagnosed, and left unbuilt — and then they combined
to take your phone surface down for 83 minutes. That is our failure, not a surprise, and you should know
that the field you were told to trust was one we already knew was unreliable.
