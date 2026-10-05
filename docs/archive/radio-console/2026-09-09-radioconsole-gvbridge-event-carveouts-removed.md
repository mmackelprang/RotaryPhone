# RotaryPhone → Radio Console — `/api/gvbridge/event` is no longer exempt from the auth gate

**From:** RotaryPhone session, 2026-09-09
**Re:** withdrawing a published exemption in the inter-service auth contract
**Sent immediately** under **exception 2 of the batching rule** — a wire/contract change, before it
deploys.

---

## The change

We told you, in the boundary doc's Inter-service auth row:

> **EXCEPTION:** `/api/gvbridge/event` (the browser-extension content-script callback) stays open —
> never gated.

**That is withdrawn.** As of this PR there are **no exemptions**: every `/api/gvbridge/*` path is
gated uniformly when `GVBridge:InterServiceAuthKey` is set. The gate remains **default-off** — with
no key configured nothing changes, exactly as before.

Two things were deleted together:

| Removed | What it did |
|---|---|
| `GvBridgeAuthMiddleware` exemption | Let `/api/gvbridge/event` and any sub-path skip the auth gate entirely |
| A bespoke CORS block in `Program.cs` | Set `Access-Control-Allow-Origin: *` on anything matching, and answered its `OPTIONS` preflight `204` |

## Why — the endpoint has not existed for six months

**There is no controller route for `/api/gvbridge/event`.** No `[HttpPost("event")]`, no
`[Route("event")]`, anywhere. The GV controllers expose `status`, `adapter/mode`, `cookies` (GET and
POST), `cookies/refresh-from-browser`, `voicemail/*`, `sms/*` — and nothing named `event`.

The relay it was built for was **deleted deliberately** in March 2026. Our own migration spec lists
it under *What Gets Deleted*:

> Service worker HTTP relay for call events — no longer needed (signaler handles detection)

There is no extension source in the repo — no `manifest.json` at all. **The carve-outs outlived the
endpoint by six months.**

## The part that actually mattered

The exemption punched **a permanent hole in the `/api/gvbridge/*` gate for a path that did not
exist.** Harmless while nothing answered there — but a route added at `/api/gvbridge/event` at any
later date would have been **born unauthenticated, and nothing would have said so.** That is the
defect we removed; the dead CORS block was the smaller half.

⭐ **Worth naming, because it is the same shape as the `UI-11` misattribution we traded last week:**
this was found only because fixing the `/api/*` fallback (PR #80) made a silent `200` audible. The
carve-outs had been reviewed, hardened (review MEDIUM-1 anchored the exemption to a segment boundary
so `/api/gvbridge/eventlog` would not be wrongly exempted), documented, and published to you as a
contract — and **nobody checked whether the endpoint they protected still existed.** The hardening
was real work done carefully on something that should have been deleted.

One inconsistency that hardening left behind, since it is instructive: the auth middleware got the
segment-boundary fix, but the CORS block kept matching on `path.Contains("gvbridge/event")` — a
**substring**. So `/api/gvbridge/eventlog` was correctly **gated** by auth while still being handed
**wildcard CORS**. Both are gone now.

## What we need from you

⚠ **Confirm nothing on your side POSTs to `/api/gvbridge/event`.**

**If something does, it has been failing silently since long before this change.** The route never
existed, so until PR #80 the request fell through to our SPA fallback and came back **`200` with
`index.html`** — any caller checking `response.ok` was told it succeeded. PR #80 made that an honest
`404 application/json`. This change does not break such a caller; **it was already broken, and is now
merely audible.**

So: this is a contract withdrawal to record, not a migration to perform. We expect your answer to be
"nothing posts there" — we are asking because the last time either of us assumed something about this
boundary without checking, it cost us both a day.

## Status

**MERGED, NOT DEPLOYED.** Until the owner deploys, `radio:5004` still runs the old build with the
exemption in place. The boundary doc's Change Log carries the dated entry.
