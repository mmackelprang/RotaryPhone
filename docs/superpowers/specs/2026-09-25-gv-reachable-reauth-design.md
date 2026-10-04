# GV reachable re-auth: the human fallback — design

**Date:** 2026-09-25 (revised the same day after owner decisions)
**Status:** proposed; O1 and O2 decided by the owner, other decisions open (§9). Nothing is implemented.
**Designs:** Phase 2 of `2026-09-09-gv-session-alarm-design.md` §10 ("remote re-auth, scoped, not designed"),
as the **human fallback** for auto-relogin.
**Works beside:** `2026-09-09-gv-auto-relogin-design.md` and PR #88 (`feat/gv-auto-relogin`), resumed 2026-09-25
under an **owner-written sign-in driver** (`docs/gv-relogin-driver-contract.md`, being written on #88).
**Builds on:** PR #90 (merged, `54d77ca`): `BrowserRefreshOutcome.SignedOut` and the alarm condition
`browser_signed_out`.
**Plan:** `docs/plans/gv-reachable-reauth.md`.

---

## 1. Where this fits

There are now two ways back from a signed-out bridge session.

| | Who signs in | Owned by | Primary? |
|---|---|---|---|
| **Auto-relogin** (PR #88) | the owner-written driver, under a circuit breaker | #88 | **yes**, whenever it is in play (§5.1) |
| **Reachable re-auth** (this design) | a human, at the box or remotely | this design | the **fallback**, when auto-relogin cannot or will not recover |

This design builds the fallback. It prepares the Google sign-in page in the bridge window, makes that window
reachable, detects that the human has finished, verifies the result with Google, and closes the loop in Chat.

⛔ **The assist never signs in.** It does not store, read, type, relay or submit a password, and it shares no
code path that does. The password-handling code is the owner-written driver's, on #88, and nothing here calls it.

The fallback exists because auto-relogin will sometimes stop, by design:

- **The breaker is TRIPPED.** Reasons include `credential_rejected`, `challenged`, `unclassified`,
  `verification_failed`, `driver_failed`, `account_file_*` and `state_corrupt` (#88 plan Tasks 5, 9 and 10). The
  breaker never re-arms itself; a human must act.
- **The actuator is absent.** It is not installed, or its timer is disabled. That is today's state, and it stays
  the state until #88's gate G2 is met.
- **The actuator declined.** It is armed but has not recovered the session within a handover window (§5.1), for
  example because its hourly or daily budget is spent.

In every one of those states, what is left is the manual path the alarm spec §10 measured on 2026-09-09. Two
frictions turned a two-minute fix into two hours and ten minutes:

1. **Reaching the window.** The bridge Chrome sits behind Radio Console's fullscreen kiosk on a shared Wayland
   display. `--window-position` does nothing under Wayland; stacking order decides what is visible.
2. **Knowing it worked.** After signing in, nothing confirms it. The owner waits up to 20 minutes for the cookie
   cron, or has to know to POST `refresh-from-browser`.

There is a third friction as well: the alert does not say where the window is or how to reach it.

---

## 2. Non-goals

| Not doing | Why |
|---|---|
| Signing in, in any form | That is #88's driver. The assist is the path for when the driver is not in play |
| Racing the actuator | §5.1 defines precedence and the mechanism that enforces it |
| New detection | The service classifies (`browserRefreshOutcome`, including `SignedOut` since PR #90); the breaker records its own state. The assist reads both as data |
| A keystroke relay through RotaryPhone | It would route the password through our service (§6.6) |
| Changing Radio Console's kiosk, launcher or dialogs | Theirs. §7 drafts the requests |
| Raising the bridge window over the kiosk unattended | It would put a Google sign-in page over a guest-facing screen with nobody present |
| Changing `gv-bridge-ensure.sh`, the watchdog, the nightly recycle, or the profile | The exit-code question (alarm spec §8) is still open with Radio Console |
| Retiring the 20-minute cookie cron | Load-bearing (alarm spec §3) |

---

## 3. What was measured on the box, 2026-09-25 ~15:35Z, read-only

Every row was read over `ssh radio` today. Nothing was changed.

| Fact | Value | How it was read |
|---|---|---|
| Desktop | **GNOME Shell 46.0**, Ubuntu 24.04.5, Wayland session on `seat0`/`tty2`; `Xwayland :0 -rootless` also running | `gnome-shell --version`, `loginctl`, `ps` |
| Bridge Chrome | `--ozone-platform=wayland`, `--user-data-dir=~/.config/gv-bridge-chrome`, CDP `127.0.0.1:9224`, `--window-size=800,600` | `/proc/<pid>/cmdline` |
| Kiosk Chrome | **`--kiosk`**, `--ozone-platform=wayland`, `--user-data-dir=~/.config/radio-kiosk-chrome`, CDP `127.0.0.1:9223` | `/proc/<pid>/cmdline` |
| `org.gnome.Shell.Eval` | **disabled**: returns `(false, '')` | `gdbus call … Eval "1+1"` |
| `org.gnome.Shell.Introspect.GetWindows` | AccessDenied on GNOME 46. Radio Console's measurement (`/usr/local/bin/radio-console-open:933-936`); not re-measured | cited |
| X11 window tools | `xdotool` present, `wmctrl` absent. Both Chromes are native Wayland clients, so an X11 tool cannot see either window | `which`; inferred from the ozone flag |
| Shell extensions | `ding`, `tiling-assistant`, `ubuntu-appindicators`, `ubuntu-dock`. None exposes window control | `gnome-extensions list --enabled` |
| Window-manager settings | `focus-new-windows='smart'`; 4 static workspaces | `gsettings` |
| Input devices | **`Logitech K400 Plus`** (keyboard with touchpad) and a HID pointer `222a:0335` (identity unconfirmed) | `/proc/bus/input/devices` |
| GNOME Remote Desktop | User-mode RDP is **configured** (enabled, port 3389, not view-only), but the user service is **inactive and disabled**, and **nothing listens on 3389** | `grdctl status`, `systemctl --user`, `ss -ltn` |
| Kiosk exit | Radio Console ships **`Exit to Desktop`** (`radio-kiosk-exit`, which stops only the kiosk profile) and **`Radio Console`** (`radio-console-open`, which relaunches it) | file reads |
| Kiosk sign-in affordance | a dormant `Show the sign-in` in `radio-console-open`: `GV_RAISE_SUPPORTED=0` (`:694`); `raise_gv_bridge()` opens `https://voice.google.com` in the bridge profile (`:702`) | file read |
| Alarm | installed, timer active on a 5-minute cadence; `Linger=yes` | `ls`, `list-timers`, `loginctl` |
| Auto-relogin on the box | **not installed**: no `gv-auto-relogin*` in `~/bin`, no unit | `ls ~/bin` |
| CDP client runtime | `python3` + `websocket-client 1.9.0`; no node, no Playwright | `python3 -c` |

**Consequences:**

- No mechanism **measured** on this box can programmatically raise one native-Wayland window above another
  fullscreen one. Every programmatic raise in §6 is marked unmeasured and has an attended task.
- The box has a keyboard today. Whether it always does is **unknown** (O4).
- RDP is not reachable today (O3).

---

## 4. Evidence this design uses

**From the attended spike** (`docs/spikes/2026-09-09-gv-signin-cdp-recording.md`, on #88):

| Fact | Used by |
|---|---|
| A signed-out forced navigation to `voice.google.com/u/0/voicemail` lands on `workspace.google.com/products/voice/` | §5.4 gate |
| `accounts.google.com/ServiceLogin?continue=…voice…` redirects to the **account chooser**. The human's first action is a click, not typing an email | §5.3; the alarm's instruction text |
| Chooser → `/v3/signin/challenge/pwd` → `voice.google.com/u/0/voicemail` | §5.4 sign-in detection |
| `refresh-from-browser` → status updated in under 1 s | §5.4 |
| Something else refreshed within seconds of the sign-in | §5.4: confirmation must not assume it is the only refresher |

**From PR #90** (merged): the service now reports **`SignedOut`** when Chrome answered but holds no Google
session. It is decided by the cookie jar (`MissingRequiredCookies`/`NoCookies`), or by a tab on a known
signed-out page when no Voice tab matches (`GVApiAdapter.ClassifyFailedExtraction`, `IsSignedOutPage`, which
parses the host and never matches a substring). The alarm maps it to `browser_signed_out`, severity `alert`,
action *"a human must sign in at voice.google.com in the box's Chrome (CDP 9224). Chrome is up; do not restart
it."* **This design uses `SignedOut` as its trigger** and drops the earlier `Unreachable`-with-Chrome-alive
workaround.

---

## 5. The design

### 5.1 Precedence: who acts when

**The rule: auto-relogin first while it is in play. The assist prepares the page only when auto-relogin is out
of play, has had its chance, or has already handed over to a human.**

The assist decides from three readings, each taken as **data**:

| Reading | Source | How |
|---|---|---|
| session | `GET /api/gvbridge/status` → `browserRefreshOutcome` | JSON |
| actuator installed and enabled | `systemctl --user is-enabled gv-auto-relogin.timer` and `-x ~/bin/gv-auto-relogin.sh` | exit codes |
| breaker | `~/.local/state/gv-auto-relogin.state` | parsed line by line against a whitelist (`BREAKER_STATE`, `BREAKER_REASON`, `BREAKER_REASON_TEXT`, `BREAKER_TRIPPED_AT`, `BREAKER_LAST_ATTEMPT_AT`); `printf %q` escapes decoded by a small, eval-free decoder; **never sourced**. A sourced state file can redefine `printf` (#88 `c992d57`) |

| Session | Actuator | Breaker | Assist |
|---|---|---|---|
| not `SignedOut`/`Stale` | — | — | nothing (and tidy up, §5.6) |
| `SignedOut` or `Stale` | **not installed, or timer not enabled** | — | **prepare** (fallback mode `actuator-absent`) |
| `SignedOut` or `Stale` | enabled | **`TRIPPED`**, or the file is missing or unreadable | **prepare** (`breaker-tripped`). A missing or unreadable file is what the breaker itself treats as TRIPPED |
| `SignedOut` or `Stale` | enabled | `ARMED` | **wait**, unless the session has been failed for longer than the **handover window H** (O8). Then **prepare** (`actuator-declined`) |

⭐ **Handover means handover.** Once the assist is `PREPARED` (a human has been told to sign in), **the actuator
must stand down** until the assist returns to `IDLE`. Otherwise the driver could navigate the page away from a
human who is mid-password. That requires one addition on #88: the actuator reads
`~/.local/state/gv-reauth-assist.state` as data, and refuses an attempt while `STATE` is `PREPARED` or
`SIGNED_IN_UNCONFIRMED`, logging *"a human sign-in is in progress"*. This is not a trip, and it spends no budget.
It is the one ask this design makes of #88 (§8). **Until #88 has it, the `actuator-declined` row is disabled**:
the assist then prepares only when the actuator is absent or TRIPPED, the two cases in which the actuator cannot
act at all.

**The lock.** The assist takes the **actuator's own lock file** (`~/.local/state/gv-auto-relogin.lock`, #88's
`breaker_lock`) with `flock -n` on its own descriptor, only for the seconds a tick spends on CDP. If the lock is
held, a sign-in attempt is running, so the assist does nothing that tick. This guarantees the two never drive the
browser at the same moment. It is a guarantee about moments, not about ownership of the page; the stand-down
above covers the rest.

- **Lock order:** assist lock, then actuator lock. The actuator never takes the assist's lock, so there is no
  cycle.
- The breaker's `--reset` waits on that lock for up to 60 s (`GV_RELOGIN_LOCK_WAIT`). The assist holds it for
  seconds, so a reset is never starved.

| Situation | What happens |
|---|---|
| Actuator enabled and armed; session signs out | the actuator tries. On success, `Succeeded`: the assist never acted. On a trip, the assist prepares on its next tick, and **adopts** the sign-in tab the driver left behind (§5.3) |
| Actuator enabled and armed, but out of budget | the assist waits for H, then prepares. The actuator stands down while a human is on it |
| Actuator not installed (today) | the assist is the only recovery, and prepares at once |
| The human signs in while the breaker is TRIPPED | the session recovers. **The breaker stays TRIPPED**; only a human `--reset` re-arms it. The assist's confirmation says so and quotes `BREAKER_REASON_TEXT` (§5.5) |

### 5.2 The helper: `gv-reauth-assist.sh`

A box-side script on its own systemd **user** timer, every 60 s, holding its own `flock`. It uses the CDP tool
from #88, `gv-cdp.py` (see §8 for where it lives), against the bridge's Chrome on `:9224`.

| May | May not |
|---|---|
| list page targets; open one new tab; activate a tab; navigate a tab it opened or adopted; read **that tab's own** `window.location.href`; close a tab it opened | type, click, or send any `Input.*` event; call any `Runtime.evaluate` other than `window.location.href` |
| POST `refresh-from-browser`; GET status; read the breaker's state file | read form fields, the DOM, cookies or storage; **write** the breaker's state file; invoke the actuator or its driver |
| write its own state file; `systemctl --user start gv-session-alarm.service` | start or kill a browser, touch the profile, touch the kiosk or port 9223 |

These are enforced as **static checks on the assist script**: no `Input.`, no `eval`/`dump`/`shot` subcommand
invocations, no reference to the driver or to `gv-account.conf`, and no password vocabulary.

**A new tab, not the service's tab.** PR #90 made the service's classification independent of which tab it
finds, because it decides on the cookie jar. The new-tab rule still stands for two reasons: it leaves whatever
the driver or the service was looking at untouched, and it gives the assist a target it owns and can close.

### 5.3 Prepare, and probe in the same step

When §5.1 says **prepare**:

1. **Adopt first.** If a page target's **own** `window.location.href` is already on an `accounts.google.com`
   sign-in path (a human is already there, or the driver tripped part-way and left the page up), adopt it. Do not
   open a second tab.
2. Otherwise open a new tab at
   `https://accounts.google.com/ServiceLogin?continue=https://voice.google.com/u/0/voicemail`. Wait for its load
   event (15 s: 3–5× the spike's worst), activate it, and read **its own** location:

| Lands on | Meaning | State |
|---|---|---|
| `accounts.google.com/…` | the chooser, or a challenge page, is up | **`PREPARED`** |
| `voice.google.com/…` | Chrome is **signed in**. With `SignedOut` that contradicts the cookie jar; with `Stale` it means Google refused cookies from a live session | **`NOT_SIGNED_OUT`**; close the tab; tell the owner a re-login will not fix it |
| anything else, or no load event | unrecognised | **`PREPARE_FAILED`**, with the URL recorded; close the tab |

⚠ **The `voice.google.com` row is inferred.** The spike only exercised `ServiceLogin` while signed out. Plan
Task 12 measures it.

The state file records `FALLBACK_MODE` (`actuator-absent` / `breaker-tripped` / `actuator-declined`), so the
owner is told **why** a human is needed.

### 5.4 Watch, then confirm

Each tick while `PREPARED`, read the prepared tab's own `window.location.href`, never `/json/list`'s cached `.url`
(`KNOWN-ISSUES.md:16-22`). When it is on `voice.google.com`:

1. **Gate: forced navigation** to `voice.google.com/u/0/voicemail`, then read where it lands. `workspace.google.com`
   means the sign-in did not take: go back to `PREPARED`.
2. **Authority: Google.** Record `browserSessionValidatedAt` as `before`, then POST `refresh-from-browser`.
3. **`CONFIRMED`** only on all three:
   - the POST returned **200**;
   - `browserRefreshOutcome == Succeeded`;
   - `browserSessionValidatedAt` is later than `before`.
4. A `502` gives **`CONFIRM_REFUSED`**, with the service's wording recorded. Do not loop.
5. A `503`/`404`/transport failure retries each tick up to 5 times, then gives **`CONFIRM_FAILED`**.

The actuator lock is held for the POST as well, so an actuator that re-arms mid-confirmation cannot overlap it.

Target: confirmation in Chat **within about 2 minutes of the sign-in**, against up to 25 minutes today.

### 5.5 Closing the loop in Chat

The alarm already posts RESOLVED in the incident thread when the outcome returns to `Succeeded`, including the
rule added by PR #90 that a RESOLVED never roots its own thread. So on `CONFIRMED` the assist runs
`systemctl --user start gv-session-alarm.service`. The unit is `Type=oneshot`, so this is serialised with a timer
firing. RESOLVED arrives within seconds, carrying the assist's `CONFIRMED_TEXT`.

- `CONFIRMED_TEXT` states what was verified: landing URL, POST 200, and `validatedAt` before → after.
- When the breaker is TRIPPED it adds one more sentence: *"Auto-relogin is still stopped (`<BREAKER_REASON>`); run
  `gv-auto-relogin.sh --reset` once the cause is fixed."* The session is fixed; the automation is not, and both
  facts belong in the thread.

### 5.6 The alarm: a second track, and copy

The alarm reads the assist's state as data (whitelisted keys, one per line, no quoting, capped values; **never
sourced**) on a **track of its own**. It is never a value of `LAST_POSTED_CONDITION`, and it is independent of
#88's `relogin_unavailable` track. Folding either into the single condition string mutes the alarm (#88 plan
§0.9).

| Moment | Message | Severity | Action (written by the assist, ≤200 characters) |
|---|---|---|---|
| `browser_signed_out` posted while the assist is already `PREPARED` | the existing alert plus the assist's sentence | `alert` | e.g. *"Sign-in page is open in the GV bridge window on radio. At the box: tap Exit to Desktop, click the account, type the password. Confirmation follows here."* |
| the assist becomes `PREPARED` after the alert | reply `[rotaryphone] GV session — sign-in page ready (<why>)` | `warning` (not `info`: the gateway drops `action` on `info`) | the same, with `<why>` from `FALLBACK_MODE` |
| `NOT_SIGNED_OUT` | reply: *"Chrome is signed in; re-login will not fix this"* | `warning` | *"do not re-login; read the service journal"* |
| `CONFIRM_REFUSED` / `CONFIRM_FAILED` / `PREPARE_FAILED` | reply naming which, with the evidence | `warning` | the assist's text |
| `CONFIRMED` → `Succeeded` | the existing RESOLVED plus `CONFIRMED_TEXT` | `info` (quiet; threaded under the alert) | none |

Dedupe keys for this track embed the incident thread key, so they are per event, not per condition.

⛔ **The action names only reachability paths that an attended measurement has shown to work** (§6, plan Task 11).
Until then it says only: *"Sign-in page is open in the GV bridge window on radio (behind the kiosk)."*

⚠ **Messages while the actuator is still in play are #88's to shape.** Today `browser_signed_out` posts an ACTION
immediately, even when an armed actuator will fix it within the hour. Whether that alert should be deferred or
reworded while the actuator is armed is recorded as an open item for #88 (§8), not designed here.

**Tidying up.** When the session reports `Succeeded` and the assist did not confirm it (the actuator, the cron or
the ladder did), the assist closes the tab it opened and returns to `IDLE`. RESOLVED then carries no
`CONFIRMED_TEXT`, because nobody verified a human sign-in.

### 5.7 Tab hygiene

The assist closes only tabs it opened, identified by the target id it recorded. After `CONFIRMED` it keeps the
tab (now the good Voice tab). It never closes a tab it adopted from a human or from the driver.

---

## 6. Reachability

**Decided by the owner, 2026-09-25 (O1):** *Exit to Desktop* and the *SSH tunnel* now; offer Radio Console the
*Show the sign-in* button later. The other options are recorded so the reasons are not lost.

### 6.1 A. At the box: Exit to Desktop (decided; exists today)

The owner taps `Exit to Desktop`, signs in in the bridge window on the K400, and taps `Radio Console` after
confirmation arrives.

- **Boundary:** uses their affordance as designed; FYI only (§7, item 1).
- **Unmeasured:** that the bridge window is visible once the kiosk closes; how long the kiosk takes to return;
  whether music continues meanwhile. Their copy says the button "leaves everything else running".

### 6.2 E. Remote: DevTools over an SSH tunnel (decided)

`ssh -L 9224:127.0.0.1:9224 radio`, then `chrome://inspect` on the owner's laptop. Inspect the prepared tab and
sign in through DevTools' screencast.

- **Boundary:** none. The password goes from the laptop keyboard, through DevTools and the SSH tunnel, into the
  bridge Chrome; no RotaryPhone code is on that path.
- **Unmeasured:** that screencast input works against this Chrome (152) and that Google accepts it. It is the same
  browser, profile and IP, but that is reasoning, not a measurement.
- ⚠ **Mutual exclusion with the actuator** comes from §5.1's stand-down. A DevTools session does not take the
  lock.

### 6.3 B. Radio Console's `Show the sign-in` (decided: offer later)

Their dormant button, pointed at a RotaryPhone-owned `~/bin/gv-reauth-show.sh` that activates the prepared tab.
It has defined exit codes, unlike `gv-bridge-ensure.sh`. **This crosses the boundary: their code, their call.**
Unmeasured: whether activation from a `zenity` click raises the window on GNOME 46.

### 6.4 C. RotaryPhone raises its window over the running kiosk: not planned

Via CDP on our port. It covers Radio Console's screen, so it crosses the boundary in effect. It is likely
defeated by focus-stealing prevention: `Shell.Eval` is disabled, `GetWindows` is denied, and X11 tools are blind
to these windows. It is measured once, attended, only if Radio Console does not object (plan Task 11 M5), so the
question gets closed.

### 6.5 D. GNOME Remote Desktop: open (O3)

Configured but not running. Starting it is a shared-system change that exposes the kiosk to remote input.

### 6.6 F. Rejected: a sign-in page served by RotaryPhone

A CDP screencast relay would route the password's keystrokes through our service over plain LAN HTTP. That
contradicts §1's rule that the assist never handles the password, and it adds an unauthenticated
credential-entry surface.

---

## 7. Cross-boundary: what Radio Console is asked for

Drafted in `docs/prompts/2026-09-25-rotaryphone-reauth-window-request.md`. **Not delivered** until O7.

1. **FYI:** our alarm copy will name `Exit to Desktop` and `Radio Console`.
2. **Request, later, their decision:** enable `Show the sign-in`, calling `~/bin/gv-reauth-show.sh`.
3. **FYI, conditional:** GNOME RDP, if the owner turns it on.
4. **Question:** does anything of theirs react to an extra tab in the bridge profile?

A Change Log row in the boundary doc goes in before anything named in items 1–2 ships.

---

## 8. Relationship to PR #88, and dependencies

PR #88 is **kept** and is the primary path. The assist is designed so that it works **with #88 absent**, which is
today's state, and so that it never races #88 once it is present.

| Item | Where | State |
|---|---|---|
| PR #90: `SignedOut`, `browser_signed_out`, thread-key retirement | `main` (`54d77ca`) | ✅ merged. The assist triggers on `SignedOut` |
| **Ask to #88: the actuator stands down while the assist is `PREPARED`/`SIGNED_IN_UNCONFIRMED`** (§5.1) | #88 actuator (#88 plan Task 9) | not yet requested. Until it lands, the `actuator-declined` row stays off |
| **Ask to #88: the actuator triggers on `SignedOut`** (and `Stale`), not `Stale` alone | #88 actuator | #88's spec §5 step 1 predates PR #90 |
| Contract pins: breaker state path and field names, lock path | a drift guard in this work (plan Task 8) | reads #88's files once merged |
| `gv-cdp.py`: one home, `deploy/gv-cdp.py`, shipped and installed | whichever of #88 and this work merges first moves it from `deploy/tools/`; the second rebases | coordination |
| The assist's subcommands `new`, `activate`, `close`, `href` | added to that one file | additive; they do not widen the driver's surface |
| Open for #88: the wording and timing of `browser_signed_out` while an armed actuator is about to act | #88's alarm track | recorded, not designed here |
| The driver contract `docs/gv-relogin-driver-contract.md` | #88 | being written. The assist depends only on the breaker file, the lock and the actuator unit names, not on the driver |

---

## 9. Open decisions

| # | Decision | State |
|---|---|---|
| O1 | Reachability paths | ✅ **Decided 2026-09-25:** A (Exit to Desktop) + E (SSH tunnel) now; offer B to Radio Console later |
| O2 | Prepare automatically or on demand | ✅ **Decided 2026-09-25: automatic** (within §5.1's precedence) |
| O3 | GNOME RDP: is it used? Measured off | **open, unknown** |
| O4 | Is the K400 permanently attached? | **open, unknown owner fact** |
| O5 | End-to-end test: deliberate attended sign-out, or wait for a natural one | **open**. Recommended: deliberate, and with the actuator absent or disabled for that run, so it tests the fallback rather than #88 |
| O7 | When to send the Radio Console request | **open**. Recommended: after the attended measurement (plan Task 11) |
| O8 | **Handover window H**: how long `SignedOut` may persist with the actuator armed before the assist hands the page to a human | **open, new**. Options: never (a human only after a trip or when the actuator is absent); a fixed H; H tied to #88's rate limit. Recommended: **never, until #88's attempt frequency is measured.** Cost: an armed actuator that is out of budget leaves the owner waiting up to an hour. A fixed H would guess a number the #88 spec says has not been measured |

---

## 10. Acceptance criteria

Every criterion names an **outcome** and must be able to fail. Box checks read the **installed** artefact.

1. **Prepare in the fallback cases.** Each of these, in the local harness, yields `PREPARED` and an active sign-in
   tab whose own location is on the sign-in host:
   - `SignedOut` + actuator not installed;
   - `SignedOut` + actuator timer disabled;
   - `SignedOut` + breaker `TRIPPED`;
   - `SignedOut` + breaker file unreadable.
2. ⛔ **No race.** `SignedOut` + actuator enabled + breaker `ARMED` gives **zero** CDP connections from the
   assist (with H = never). With the actuator lock held by another process, the assist makes zero CDP connections
   that tick, whatever the state.
3. **Adoption.** With a target already on a sign-in path (the driver's leftovers, for instance), no new tab is
   opened.
4. **Breaker read as data.** A breaker file carrying `BREAKER_REASON_TEXT=$(touch /tmp/x)` and a `printf()`
   redefinition creates no file, and the text surfaces literally in `CONFIRMED_TEXT`.
5. **Confirmation needs Google.** Three negative controls each prevent `CONFIRMED`:
   - the gate lands on workspace;
   - the POST returns 502;
   - the POST returns 200 but `validatedAt` does not move.
6. **Closed loop, end to end** (attended, O5). RESOLVED with `CONFIRMED_TEXT` appears in the thread **within 3
   minutes** of the sign-in, verified by the owner reading the thread. If the breaker was TRIPPED, the thread
   also says it still is.
7. **Stand-down honoured** (once #88 has the ask). With the assist `PREPARED`, the actuator's journal shows the
   refusal, and its budget counters are unchanged.
8. **No password path.** Static checks over the assist script find no `Input.`, no evaluate other than `href`, no
   reference to the driver or `gv-account.conf`, and no password vocabulary. Each check has a negative control.
9. **Reachability measured** for A and E with a neutral `data:` page before either is named in the alarm copy.
10. **Installed, not merely shipped** (`check-installed-drift.sh --group reauth`), and the alarm's existing cases
    pass unchanged when the assist's state file is absent.
