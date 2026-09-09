# GV session alarm — design

**Date:** 2026-09-09
**Status:** approved in shape; implementation not started
**Author:** RotaryPhone session `rotaryphone-50`, with measurements corroborated by the
Radio Console session on the shared box.

---

## 1. The problem, stated precisely

On 2026-09-09 the box's Chrome Google Voice session signed out at **16:40:01Z** and was
restored by an owner re-login at **18:50:14Z**. **Two hours and ten minutes**, during which:

- the service reported `available:true`, `cookiesValid:true`, `sipRegistered:true`
- it re-minted its own `__Secure-1PSIDTS` every 8 minutes, staying healthy on a lineage
  it regenerates from itself
- the 20-minute cron harvested cookies from the signed-out Chrome on schedule, and Google
  **rejected every one** — six `[ERR]` lines between 13:00 and 14:40
- nobody noticed

⭐ **The service composed a correct, complete alert six times:**

```
[ERR] GVApi: REJECTED a cookie set from refresh-from-browser — Google refused it.
The working on-disk set was NOT overwritten. If the source is the box's Chrome,
that session is dead: ACTION: re-login at voice.google.com.
```

Correct severity vocabulary. The exact remedy. And the reassurance an operator most needs
before panicking — that the working set survived. It reached a journal nobody was reading.

⛔ **So this is not a detection problem.** Detection exists twice over: this log line, and
`browserSessionStale`. **The gap is transport.** Phase 1 builds a path from an existing,
already-correct signal to a human, and nothing else.

### 1.1 Why it matters that the service looked healthy

`docs/handoffs/2026-09-08-rotaryphone-auth-lineage-fixes.md:89-93`: *"The service mints its
own PSIDTS every 8 minutes and can look perfectly healthy on a lineage it regenerates from
itself. Recovery has no floor below a working browser session."*

The phone worked the whole time. It was working on a credential it could renew but could not
*re-derive*. When that chain finally breaks there is nothing underneath it — which is the
shape of the 2026-09-06 two-day masking that preceded an 83-minute outage.

---

## 2. Non-goals

| Not doing | Why |
|---|---|
| Automating the Google login | Adversarially defended. Storing a Google password + TOTP seed on a box shared with Radio Console is a worse posture than a 2-minute manual re-login, and it fails unpredictably. |
| New detection logic | Two correct detectors already exist. Adding a third is the mistake this document exists to avoid. |
| Changing Chrome lifecycle ownership | Shell + systemd own the process; the C# service observes. That boundary is deliberate and stays. |
| Touching BT/audio, `hci0`/`hci1`, WirePlumber, the kiosk profile | Governed by `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`. |
| Retiring the 20-minute cron | Measured load-bearing (§3). `KNOWN-ISSUES.md:484-497` says retire it; that recommendation is the stale side of a contradiction now settled by measurement. |

---

## 3. Verified foundations — measured 2026-09-09, do not rebuild

Everything in this table was checked on the box. Several were assumptions that turned out
already-solved; rebuilding them would be waste.

| Claim | Verdict | Evidence |
|---|---|---|
| Chrome profile persists across restarts | ✅ **Solved** | Profile birth `2026-03-21`, 170M, **47** logged relaunches. `--collect` reaps the transient *unit*, not the profile. |
| Bridge navigates to the right page at launch | ✅ **Solved** | `https://voice.google.com` is the trailing positional arg — verified in the **installed** copy, not just the repo. |
| Singleton lock recovery after `kill -9` | ✅ **Solved** | `rm -f "$PROFILE"/Singleton*` before every launch attempt. |
| Watchdog keeps Chrome alive | ✅ **Active** | `gv-bridge-watchdog.timer` active, 2 min. |
| Nightly recycle | ⚠ Installed but **disabled** | `systemctl --user is-enabled gv-bridge-restart.timer` → `disabled`. |
| 20-minute cookie cron | ✅ **Alive and load-bearing** | Fires on cadence; each successful firing is what keeps the browser lineage validated. Corroborated by Radio Console. |
| Chat gateway reachable from `radio` | ✅ **Verified** | `192.168.86.47:8085`, TCP open, HTTP answering in 7ms. |
| Chrome window placement | ⚠ **Behind the console, not off-screen** | `--window-position=10000,10000` is a no-op under Wayland. `gv-bridge-ensure.sh` says it plainly: *"Off-screen placement is not what keeps this window out of the way — stacking order is."* |

⛔ **The installed script is three weeks older than the repo copy.** `~/bin/gv-bridge-ensure.sh`
is 1044 bytes (Aug 18); `/opt/rotary-phone/deploy/gv-bridge-ensure.sh` is 4981 bytes (Sep 9).
The deploy copies the new file to `/opt` and **never runs `setup-gvbridge.sh`**, so the executed
copy has not moved since August. See §7 — this is not a footnote, it is a blocker.

---

## 4. Phase 1 — transport

### 4.1 Additive DTO field

Add `browserRefreshOutcome` (the enum as a string) to `GET /api/gvbridge/status`.

**Rationale.** `BrowserSessionStale` is a derived read of one enum (`GVApiAdapter.cs:255`):

```csharp
public bool BrowserSessionStale => _lastBrowserRefreshOutcome == BrowserRefreshOutcome.Stale;
```

over `{ NotAttempted, Unreachable, Stale, Succeeded, TornDown }`. **If Chrome is dead the
outcome is `Unreachable`, so the boolean reads `false`.** A consumer keyed on the boolean
alone shows a green light on the worst state.

⚠ **The boolean stays.** Radio Console consumes it per boundary doc `:194`. This is additive
only; removing or repurposing it is a contract breach.

### 4.2 The alarm

`~/bin/gv-session-alarm.sh`, driven by a systemd **user** timer every 5 minutes.

- polls `GET /api/gvbridge/status`
- keeps last posted state in `~/.local/state/gv-session-alarm.state`
- posts **only on transition** — the owner's chat policy forbids reposting an unchanged state
- **quotes the service's own alert copy** rather than inventing new wording

⭐ Five minutes is generous: the underlying condition persists for hours and only changes when
the 20-minute cron or the recovery ladder attempts a refresh. We are not catching a transient.

### 4.3 Outcome → severity

| Status | Policy severity | Gateway severity | Meaning |
|---|---|---|---|
| `Succeeded` after an open alert | `RESOLVED` | `info` | closes the incident thread |
| `Stale` | `ACTION` | `alert` | signed out — re-login needed |
| `Unreachable` | `ACTION` | `alert` | **Chrome is gone — the case the boolean reports as `false`** |
| `NotAttempted`, persistent | `WARN` | `warning` | no extractor/store wired |
| `Succeeded` but age > threshold | `WARN` | `warning` | aging — act before it dies |
| service not answering at all | `ACTION` | `alert` | the case in-process detection structurally cannot report |
| `TornDown` | — | — | service teardown, not a fault; ignore |

The owner's four-value policy vocabulary (`ACTION`/`WARN`/`INFO`/`RESOLVED`) maps cleanly onto
the gateway's three (`alert`/`warning`/`info`), and the routing agrees: `ACTION`+`WARN` notify;
`INFO`+`RESOLVED` are quiet. `RESOLVED` is quiet **only because it threads under the alert** —
if threading breaks, a silent resolve becomes invisible and the owner is left believing an
incident is still open.

### 4.4 Gateway contract — measured, not assumed

`POST /v1/notify`, bearer token, JSON: `source`, `severity` (`alert|warning|info`), `title`,
`body` (markdown, optional), `action` (optional), `dedupe_key` (optional), `thread_key`
(optional), `timestamp`.

⛔ **Measured traps, from `aitrader/docs/chat-gateway-requirements.md`. Every one of these cost
a real alarm on the other project.**

| Trap | Consequence | What we do |
|---|---|---|
| **`action` capped at 200 CHARACTERS** | over-long → HTTP 422 that **delivers nothing** | Truncate defensively; read the limit off the 422 rather than hardcoding. Our natural action — `re-login at voice.google.com in the box's Chrome (CDP 9224)` — is well inside it, but the body must not be spliced into it. |
| **`action` and `timestamp` silently dropped on `info`** | the "what to do" vanishes | Any message whose action matters goes on `warning`, never `info`. |
| **`dedupe_key` ignores severity, title and `thread_key`** | two different alerts collapse into one | Keys chosen **per condition**, never per message. |
| **`grace` is case-sensitive** | `2H` is a 422 | lower-case only. |
| **Gateway queue is in-memory; a restart drops undelivered jobs** | silent loss | The journal remains the authoritative record. A notification is never the only record of anything. |

⚠ **The 200-character lesson generalises past its own limit.** On 2026-08-31 the over-long
`action` returned 422, the notification was never delivered, and the failure printed to stderr
into a log nobody reads — *"the control installed to make a scan failure loud was itself mute,
in exactly the situation it exists for."* Our script must therefore **check the HTTP status of
its own POST and treat a non-2xx as a first-class failure**, never as something to print and
move past.

### 4.5 Threading

- `thread_key` names the **incident**, stable across its life — never a timestamp, a status,
  or a message. One sign-out is one thread from detection to all-clear.
- Thread title posted once, marked 🧵, stating what closes it:
  *"Closes when: `browserRefreshOutcome` returns `Succeeded` after an owner re-login."*
- Every update is a reply opening with its own UTC timestamp.
- The `RESOLVED` replies **into that thread**.
- The thread key is persisted in the state file so it survives reboots and service restarts.

### 4.6 Title

`[rotaryphone] GV session — signed out, re-login needed`

⚠ **No severity in the title.** The gateway prepends its own `severity_prefix()`; a title
carrying its own severity renders it twice, with the two vocabularies free to disagree.

---

## 5. The three hard constraints

These are not polish. Each one is a measured failure from a sibling system, and any of them
would render the alarm silently mute — reproducing the exact condition it exists to prevent.

### 5.1 Environment must be explicit, and absence must be loud

⚠ **Measured, aitrader 2026-08-14:** a refusal journaled correctly and **notified nobody**,
because the credentials lived only in `~/.aitrader-env` which only the cron wrapper sourced.
The gateway, the bearer, the call site and the routing were all proved healthy the same hour
by a positive control.

A systemd **user** timer inherits no login-shell environment. Therefore:

- the script **explicitly sources** `~/.rotaryphone-env`; it never relies on inheritance
- a missing URL or token **fails loudly** to the journal and is a non-zero exit
- ⛔ aitrader's *"unset ⇒ no-op, not error"* is correct for an optional third store on a
  trading bot. It is **exactly wrong** for the only alarm on a phone. Silence must not be a
  valid state.

### 5.2 The dead-man — and it is already built

`POST /v1/heartbeat {source, check_id, schedule, grace}` registers a check; the gateway raises
an `alert` on that source's route if the check is not refreshed in time.
`GET /v1/heartbeat/{source}` reads check state back.

⭐ **So the alarm can alarm about itself, through a different code path than the one that
might be broken.** The design that falls out of this:

> **Refresh the heartbeat only after a healthy poll-and-notify cycle.** If the status poll
> fails, or a required notify returns non-2xx, **do not refresh the heartbeat.** The gateway
> then raises the missing-check alert on our behalf.

That closes the loop the 2026-08-31 incident left open: a notification path that has broken
stops being able to hide, because the thing that reports its silence is not the thing that
broke.

⚠ `grace` must exceed the timer interval with margin — a 5-minute timer wants a grace of
`30m`, not `5m`, or ordinary jitter produces false alarms and the alarm gets muted by the
human, which is the worst outcome of all.

### 5.3 Verification against `~/bin`, never against the repo

⛔ Radio Console's catch, and it is the headline for any change to startup behaviour: **change
the repo, deploy, and the box keeps doing the old thing while the deploy reports success.**

Every acceptance check in §9 reads the *installed* artefact. No check in this work is allowed
to assert against a repo file.

---

## 6. What today's near-misses require of the test plan

Five instances on 2026-09-09, across both repos, of the same failure: **verifying that a
mechanism ran, and inferring that it worked.** In all five the mechanism genuinely was running.

1. Verified the cookie cron *fires* → inferred it *works*. Google was rejecting every harvest.
2. Verified the repo copy *contains* the right args → inferred *the box runs it*. It does not.
3. Verified `browserSessionStale` *exists* → inferred *someone sees it*. Nobody did, for 2h10m.
4. Read the rejection line through `cut -c1-170` → reported "Google refused it". The line
   continued: *"...ACTION: re-login at voice.google.com."* **The alert contained its own remedy
   and the instrument cut it off.**
5. Read the running Chrome argv through `cut -c1-260` → reported the command line "matches",
   a claim the truncated output could not support. It happened to be true.

⭐ And a sixth, in the opposite direction, during recovery: after the owner re-logged in, three
signals still read *not fixed* — the `workspace.google.com` tab, the unmoved `validatedAt`, and
`stale:true`. **All three were artefacts of not-yet-consumed, not of not-fixed.** The login had
already worked. The same gap produces false **reds** as well as false greens, and the red is
nastier because it invites you to go break something that is already fixed.

**Consequence for §9:** no acceptance criterion may be satisfied by observing that a component
ran. Each one names an *outcome* and the observation that confirms it.

---

## 7. ⛔ The install path is a blocker, not a footnote

`setup-gvbridge.sh` installs `~/bin` scripts and systemd units. **The deploy does not run it.**
An alarm delivered through that path would install, report success, and never execute — which
is precisely the failure mode in §5.1, arrived at from a different direction.

This must be resolved before the alarm can be trusted. Options, to be settled in planning:

- have the deploy invoke `setup-gvbridge.sh` (it is idempotent by design, but it currently
  rewrites scripts the 2-minute watchdog may be executing — the timer should be stopped across
  the install and restarted after)

  ⛔ **DEPENDENCY, corrected 2026-09-09 after this section was first written.** An earlier draft
  of this line claimed the atomic-install work "already landed." **It has not.** It is staged on
  the unmerged branch `fix/deploy-honest-status` (PR #84); `main` still archives `.` with
  `--unlink-first` in the rsync-fallback path and carries no files-only filter
  (`grep -c "type f" deploy/Deploy-ToLinux.ps1` → **0** on main). So this option is **blocked on
  PR #84 merging**, and any plan that assumes atomic install is available today is wrong.

  ⚠ The false claim was written from memory of the same day's work without checking merge state —
  which is §6's failure class, committed inside the document describing it. Recorded rather than
  quietly corrected, because the entry is worth less if its own author's instance is edited out.
- or install the alarm through a separate, explicitly-deployed unit
- **either way the acceptance check reads `~/bin` and `systemctl --user list-timers`**, never
  the repo

---

## 8. Cross-boundary — Radio Console

Coordinated with the live Radio Console session on 2026-09-09; full record at
`RTest/docs/queue/inbound/2026-09-09-rotaryphone-gv-session-stale-and-drift-verification.md`.

- **Exit code — now a prerequisite, not an improvement.** `gv-bridge-ensure.sh` exits 0 on
  every path. Their KIOSK-2 launcher invokes it and reads that code (`INTEGRATIONS.md:746`,
  "invoke-and-probe only"), so it cannot distinguish *already up* from *just launched*.
  ⛔ Deploying the shipped script would add `flock`, introducing a **third** state —
  *someone else holds the lock* — that also arrives as exit 0. In Radio Console's phrase, that
  is **"a contract that has run out of vocabulary."** A contract that cannot express its own
  outcomes must not acquire a third one first. Fixing the exit code is therefore a prerequisite
  for deploying the shipped `gv-bridge-ensure.sh` at all.
- **Announce before changing.** Any exit-code change goes in the boundary doc Change Log
  *before* it ships.
- **This work touches none of their surfaces** — no BT/audio, no `hci0`/`hci1`, no WirePlumber,
  no kiosk profile, no change to `gv-bridge-ensure.sh`, the watchdog, or the profile.

### 8.1 A hazard of ours pointing at them — fix in this arc

`CookieRetriever.cs:15` hardcodes CDP port **9222**; the bridge listens on **9224**. The
"connect to existing Chrome" branch can therefore *never* succeed, so `gv-login` always falls
through to `CookieRetriever.cs:52-61`, which kills Chrome **by process name** (`chrome`,
`chromium`, `chromium-browser`) **with no profile filter**. `~/.config/radio-kiosk-chrome` is
present on the box; this would take out Radio Console's kiosk.

Latent today (`~/.local/share/RotaryPhone` does not exist, so it has never run there) and it
cannot clobber good cookies (returns `false` at `:152`/`:175` before any save). But it is
exactly the command an operator reaches for when the login breaks — the worst possible moment.

**Fix:** correct the port to the configured `ChromeCdpPort`, and scope the kill to the profile
marker so it cannot reach any other profile.

---

## 9. Acceptance criteria

Each names an outcome and how it is observed. None is satisfied by "the component ran."

1. `GET /api/gvbridge/status` returns `browserRefreshOutcome` as a string, **and** the existing
   `browserSessionStale` boolean is byte-identical in shape to today's response.
2. With Chrome deliberately stopped, the alarm posts an `alert` — proving the `Unreachable`
   case that the boolean reports as `false`. **Observed as a delivered message, not a log line.**
3. With the service stopped, the alarm posts an `alert` — the case in-process detection cannot
   cover.
4. A simulated `Stale` → `Succeeded` transition produces a `RESOLVED` **in the same thread as
   its alert**, verified by reading the thread, not by reading our own state file.
5. With `~/.rotaryphone-env` absent, the timer unit **fails** and journals a distinguishable
   error. It does not exit 0.
6. An over-long `action` is truncated before send; a forced 422 is journaled as a failure **and
   suppresses the heartbeat refresh**.
7. Killing the alarm timer entirely results in a gateway missing-check alert within `grace`.
8. `~/bin/gv-session-alarm.sh` and its timer are present **on the box** after a normal deploy,
   verified via `~/bin` and `systemctl --user list-timers` — never against the repo.
9. `gv-login` against a running bridge connects to the existing Chrome on the configured port
   and kills nothing; `pgrep -f user-data-dir=.../radio-kiosk-chrome` is unchanged across the run.

---

## 10. Phase 2 — remote re-auth (scoped, not designed)

Today's outage measured two friction points that together turned a 2-minute fix into 2h10m:

1. **The bridge window sits behind the console window** (stacking order, not off-screen — the
   `--window-position` flag is a no-op under Wayland). Reaching it means raising a window on a
   display shared with Radio Console's kiosk.
2. **The operator gets no confirmation the re-login worked.** After logging in, nothing says it
   took; you either wait up to 20 minutes for the cron or know to POST `refresh-from-browser`
   yourself. Today the owner logged in and the visible indicators still read *broken* — see §6.

⭐ Phase 2 addresses those two, in that order. It is a separate design; the point of recording
it here is that phase 1's alarm is **not** the fix — it shortens the detection half of a
two-hour outage and leaves the recovery half untouched.

---

## 11. Open decisions

| # | Decision | Owner |
|---|---|---|
| 1 | A `rotaryphone`-scoped gateway token, in `~/.rotaryphone-env` on `radio`. Reusing `AITRADER_GATEWAY_TOKEN` would mis-attribute the source and collide in the gateway's routing config. | **Owner** |
| 2 | Resolve the §7 install path — deploy runs `setup-gvbridge.sh`, or a separate unit. | Planning |
| 3 | `WARN` threshold for `browserSessionAgeSeconds`. Today's session was ~2h old at death; a healthy one runs for days. Needs a measured baseline before a number is chosen — **do not guess one.** | Planning, after measurement |
| 4 | Exit-code semantics for `gv-bridge-ensure.sh` (§8). Cross-boundary; announce first. | Owner + Radio Console |
| 5 | `KNOWN-ISSUES.md:484-497` contradicts §3 on the cron. The doc should be corrected to record it as load-bearing. | This arc |
