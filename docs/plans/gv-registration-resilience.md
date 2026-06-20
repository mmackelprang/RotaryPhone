# GV Registration Resilience — plan

**Branch:** `fix/gv-registration-resilience`
**Origin:** 2026-06-19 live incident — "rings but no audio." Root cause: GV auth decayed over a 6-day
uptime; cookie auto-rotation didn't restore it; `GvSipTransport` fell into a ~7/sec REGISTER→401 **storm**
(~600k/day) that likely got the account **`603 Declined`** by Google. Meanwhile `/api/gvbridge/status`
reported `sipRegistered:true` the whole time (the flag is never reset on failure), so the outage was
invisible. See memory `project_gv_registration_603_incident`.

## Goal

Make GV registration **self-healing and honest**, and detect/auto-fix the "no inbound audio" class
without a human noticing first. All recovery stays in RotaryPhone's lane (no PipeWire/cross-service).

## PRs (priority order)

### PR1 — Honest status + explicit 603 handling  *(this PR; smallest, safest, highest value)*
- `GvSipTransport.RegisterAsync` message handler: reset `_registered = false` in **every** REGISTER
  failure branch — post-Digest 401/407 (cseq≥2), 403, and the generic `else`.
- Add an explicit **`603 Declined`** branch: log it as a real registration decline, set
  `_registered = false`, `RaiseAuthenticationFailed("REGISTER 603")` so the recovery ladder runs, and
  `regTcs.TrySetResult(false)` so the attempt counts as failed and the backoff applies.
- Result: `IsRegistered` (`_registered && IsConnected`) tells the truth; `/api/gvbridge/status`
  `sipRegistered` stops lying.
- Tests: feed a 603 (and a post-Digest 401) through the channel → assert `IsRegistered==false` and that
  an `AuthenticationFailed` event fired.

### PR2 — Storm floor (defense-in-depth)
- Hard rate-floor on registration attempts so no code path can exceed ~1 attempt / N seconds even if a
  future bug bypasses the backoff loop. (The backoff in `ReconnectLoopAsync` is correct; this is a
  belt-and-suspenders cap measured at the REGISTER-send site.)
- Tests: simulate rapid failures → assert send rate is capped.

### PR3 — Auto cookie-recovery via refresh-from-browser
- On `AuthenticationFailed` escalation (incl. the new 603), automatically run the **refresh-from-browser**
  lever (the one that worked manually tonight) with a cooldown, before/alongside RotateCookies.
- After a successful cookie refresh, force a clean re-register (and, if the running transport won't adopt
  fresh creds in-loop, surface a clear "needs restart" status rather than silently failing).
- Tests: auth-failure → refresh invoked once within cooldown; success path re-registers.

### PR4 — Registration / audio watchdog + alerting  *(the user's original "auto-detect-and-fix")*
- Hosted watchdog: periodically assert `cookiesValid && IsRegistered`; during active calls, assert inbound
  RTP frames are flowing (`GVAudioBridgeService.Stats.InboundFramesSent` advancing). On failure: raise a
  visible alert (log + a `status` health field + SignalR push) and trigger PR3's auto-recovery.
- Expose a `degraded`/`lastHealthyAt` field on `/api/gvbridge/status` so the RTest dashboard can show it.
- Tests: watchdog flags unregistered/cookie-invalid/flat-inbound states and fires recovery.

## Out of scope / parallel
- The `603` may be a Google-side cooldown from the storm; clearing it may require time or a GV-account
  re-auth in the browser (user action). These PRs make the system *recover and report* correctly; they
  don't bypass a Google account block.
- `sipregisterinfo/get` credential expiry logs as "~15 years" — likely a misparse; investigate separately.
