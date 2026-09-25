# GV reachable re-auth — design

**Date:** 2026-09-25
**Status:** proposed; awaiting owner review. Nothing is implemented.
**Supersedes:** `2026-09-09-gv-auto-relogin-design.md` (abandoned by owner decision 2026-09-25) and draft PR #88.
**Designs:** Phase 2 of `2026-09-09-gv-session-alarm-design.md` §10 ("remote re-auth, scoped, not designed").
**Plan:** `docs/plans/gv-reachable-reauth.md`.

---

## 1. The decision this design starts from

On 2026-09-25 the owner abandoned automated sign-in. **Nothing in this design stores, reads, types, relays or
submits a Google password.** A human signs in; the machine's job is to make that sign-in fast to reach and
certain to have worked.

That removes the whole hazard class the auto-relogin design existed to contain. There is no credential file, no
circuit breaker, no rejection classifier, and no need to recognise a Google challenge: if Google challenges, a
person is already looking at the page and answers it.

What remains is the two frictions the alarm spec §10 measured on 2026-09-09, which turned a two-minute fix into
two hours and ten minutes:

1. **Reaching the window.** The bridge Chrome sits behind Radio Console's fullscreen kiosk on a shared Wayland
   display. `--window-position` does nothing under Wayland; stacking order decides what is visible.
2. **Knowing it worked.** After signing in, nothing confirms it. The owner either waits up to 20 minutes for the
   cookie cron or knows to POST `refresh-from-browser`. On 2026-09-09 the visible indicators still read broken
   after a login that had already worked (alarm spec §6).

This design addresses both, plus a third that the first two expose:

3. **The alarm does not say what to do.** Today's `browser_stale` action is *"re-login at voice.google.com in the
   box's Chrome (CDP 9224)"* (`deploy/gv-session-alarm.sh:403`). It does not say where the window is, how to get
   to it, or that confirmation will follow.

---

## 2. Non-goals

| Not doing | Why |
|---|---|
| Any form of automated sign-in, including keystroke relay | Owner decision 2026-09-25. See §6.5 for the one reachability option this rules out. |
| New detection | The service already classifies the session (`browserRefreshOutcome`) and the alarm already transports it. The helper in §5 acts on the service's classification; it does not produce one of its own. |
| Changing Radio Console's kiosk, launcher, or dialogs | Theirs. §7 drafts the requests; nothing here assumes they are granted. |
| Raising the bridge window over the kiosk automatically | It would put a Google sign-in page over a guest-facing screen with nobody present. At most, a raise happens when a human asks for it (§6). |
| Changing `gv-bridge-ensure.sh`, the watchdog, the nightly recycle, or the Chrome profile | The exit-code question (alarm spec §8, §11 decision 4) is still open and cross-boundary. This work does not touch it. |
| Retiring the 20-minute cookie cron | Load-bearing (alarm spec §3). |

---

## 3. What was measured on the box, 2026-09-25 ~15:35Z, read-only

Every row was read over `ssh radio` today. Nothing was changed. The rows are what the options in §6 rest on.

| Fact | Value | How it was read |
|---|---|---|
| Desktop | **GNOME Shell 46.0**, Ubuntu 24.04.5, Wayland session on `seat0`/`tty2`; `Xwayland :0 -rootless` also running | `gnome-shell --version`, `loginctl`, `ps` |
| Bridge Chrome | PID 3131, `--ozone-platform=wayland`, `--user-data-dir=~/.config/gv-bridge-chrome`, CDP `127.0.0.1:9224`, `--window-size=800,600 --window-position=10000,10000` | `/proc/<pid>/cmdline` |
| Kiosk Chrome | PID 553778, **`--kiosk`**, `--ozone-platform=wayland`, `--user-data-dir=~/.config/radio-kiosk-chrome`, CDP `127.0.0.1:9223` | `/proc/<pid>/cmdline` |
| `org.gnome.Shell.Eval` | **disabled**: returns `(false, '')` | `gdbus call … Eval "1+1"` (evaluates nothing when disabled) |
| `org.gnome.Shell.Introspect.GetWindows` | **AccessDenied on GNOME 46**. Radio Console's measurement, recorded in their installed `/usr/local/bin/radio-console-open:933-936`. Not re-measured here | cited |
| X11 window tools | `xdotool` present, `wmctrl` absent. Both Chromes are native Wayland clients, so an X11 tool cannot see either window | `which`; inferred from the ozone flag |
| Shell extensions enabled | `ding`, `tiling-assistant`, `ubuntu-appindicators`, `ubuntu-dock`. None exposes window control over D-Bus | `gnome-extensions list --enabled` |
| Window-manager settings | `focus-new-windows='smart'`; `dynamic-workspaces=false`, 4 workspaces | `gsettings` |
| Input devices | **`Logitech K400 Plus`** (wireless keyboard with touchpad) and a HID pointer `222a:0335` (identity not confirmed; possibly the touchscreen) | `/proc/bus/input/devices` |
| GNOME Remote Desktop | User-mode RDP is **configured** (`grdctl status`: enabled, port 3389, not view-only), but `gnome-remote-desktop.service` (user) is **inactive and disabled**, and **nothing listens on 3389**. The system daemon runs; `grdctl --system status` needs a polkit prompt and was not read | `grdctl status`, `systemctl --user`, `ss -ltn` |
| Kiosk exit | Radio Console ships **`Exit to Desktop`** (`~/Desktop/radio-exit-browser.desktop` → `/usr/local/bin/radio-kiosk-exit`), which stops only the kiosk profile and leaves the bridge running. **`Radio Console`** (`radio-console-open`) relaunches it | file reads |
| Kiosk sign-in affordance | `radio-console-open` already contains a **dormant** `Show the sign-in` button: `GV_RAISE_SUPPORTED=0` (`:694`), and `raise_gv_bridge()` launches `google-chrome --user-data-dir=<bridge profile> https://voice.google.com` (`:702`). Their comment: *"raising the bridge Chrome's window under Wayland is unmeasured on this box … a button which does nothing must not ship"* | file read |
| Alarm | `~/bin/gv-session-alarm.sh` installed (Sep 10 12:10), `gv-session-alarm.timer` active, 5-minute cadence; `Linger=yes` | `ls`, `list-timers`, `loginctl` |
| Session now | `browserRefreshOutcome=Succeeded`, `validatedAt=2026-09-25T15:33:38Z`; **one** page target, `voice.google.com/u/0/voicemail` | `GET /api/gvbridge/status`, CDP `/json/list` |
| CDP client runtime | `python3` + `websocket-client 1.9.0` present. No node, no Playwright | `python3 -c` |
| Installed ensure script | `~/bin/gv-bridge-ensure.sh` is still the **1044-byte Aug 18** copy | `ls -l` |

**Consequences for the design:**

- No mechanism **measured** on this box can raise one native-Wayland window above another fullscreen one
  programmatically. Every programmatic raise in §6 is marked *unmeasured* and has an attended measurement task.
- The box **has a keyboard.** The Radio Console design note that re-login "is not achievable with finger-only
  input" (`RTest/docs/design-handoffs/HANDOFF-kiosk-desktop-launcher.md:454-456`) is correct, and a K400 is
  attached today. Whether it is always attached is an owner fact and is **unknown** (§10, O4).
- **RDP is not reachable today.** If the owner uses it, they start it on demand; that is unknown (§10, O3).

---

## 4. Evidence from the 2026-09-25 attended spike

`docs/spikes/2026-09-09-gv-signin-cdp-recording.md` (on PR #88's branch; carried by this work, §9). The facts this
design uses:

| Fact | Used by |
|---|---|
| A signed-out forced navigation to `voice.google.com/u/0/voicemail` lands on `workspace.google.com/products/voice/` | §5.3 prepare-and-probe |
| `accounts.google.com/ServiceLogin?continue=https://voice.google.com/` redirects to `/v3/signin/accountchooser?…`. The profile remembers the account, so the first screen is **a click on the account, not an email field** | §5.3; the alarm's instruction text |
| Chooser → `/v3/signin/challenge/pwd` → settled on `voice.google.com/u/0/voicemail` | §5.4 sign-in detection |
| **No challenge** appeared on any of three sign-ins | reassurance only. A challenge would now be answered by the human anyway |
| `refresh-from-browser` POST → status updated in under a second, `refreshed:true` | §5.4 confirmation |
| Something refreshed the session **on its own** within seconds of the sign-in, before the spike's own POST | §5.4: confirmation must not assume it is the only refresher |
| ⛔ While the only page target was on `accounts.google.com/v3/signin/challenge/pwd`, the service reported **`Unreachable`**, and the alarm labelled a signed-out session *"Chrome is gone"* | §5.2 new-tab rule; §8 dependency on the parallel label fix |
| ⛔ The alarm filed the 09-25 alert under a five-day-old incident thread | §8 dependency on the parallel thread-key fix |
| Signed-out windows shorter than the alarm's debounce produced no alert | acceptable: nothing needed doing |

Machine timings from the spike: navigations settle in 2–5 s. The human took ~1 minute from chooser to password
page and ≤71 s from password page to settled voicemail.

---

## 5. The design

### 5.1 Shape

```
service ──(browserRefreshOutcome)──► gv-reauth-assist (new, box, 1-min timer)
                                          │  prepare: open the sign-in tab
                                          │  watch:   read that tab's own location
                                          │  confirm: forced navigation → POST refresh → validatedAt moved
                                          ▼
                                     assist state file (words for a human)
                                          │
gv-session-alarm (existing, 5-min timer) ─┘ quotes those words into the incident thread
                                            (and is started early by the assist on confirmation)
```

Three rules carry over unchanged from the alarm and the abandoned relogin design, because they were right:

- **Shell and systemd own the browser; the C# service observes.** Navigating the bridge tab is a browser action,
  so the helper is a box-side script, not service code. That is the placement the relogin spec §5 argued for, and
  it keeps Radio Console's KIOSK-2 assumptions (the bridge's lifecycle is shell-owned) intact.
- **The alarm stays transport.** `THIS SCRIPT DETECTS NOTHING` (`gv-session-alarm.sh:5`) stays true. The helper
  writes a sentence; the alarm quotes it, the same relationship the alarm already has with the service's log
  wording.
- **One Chat poster.** The helper does not hold the gateway token and does not post. A second notifier would
  duplicate threading logic that took three review rounds to get right. The helper gets its message out by
  asking the alarm to run (§5.5).

### 5.2 The helper: `gv-reauth-assist.sh`

A box-side script on its own systemd **user** timer, every 60 s, holding its own `flock`. It uses a small CDP
tool (`gv-cdp.py`, carried from PR #88 and cut down, §9) against the bridge's existing Chrome on `:9224`.

**What it may do, and nothing else:**

| May | May not |
|---|---|
| list page targets; open one new tab; activate a tab; navigate a tab it opened; read **that tab's own** `window.location.href`; close a tab it opened | type, click, or send any `Input.*` event |
| POST `refresh-from-browser`; GET status | read any form field, the DOM, cookies, or storage |
| write its own state file; `systemctl --user start gv-session-alarm.service` | start or kill a browser, touch the profile, touch the kiosk or port 9223 |

These are enforced as **static checks** on the shipped files (plan Task 3). `gv-cdp.py` loses its generic
`eval`, `dump` and `shot` commands from the spike: the only `Runtime.evaluate` it can send is the literal
`window.location.href`.

⛔ **The helper opens a NEW tab for sign-in; it never navigates the service's tab.** The spike showed that with
the only page target on an `accounts.google.com` page the service reported `Unreachable`, and the alarm posted
*"Chrome is gone"*. The service's extractor picks the first tab whose cached URL contains `voice.google.com`
(`CdpCookieExtractor.cs:88-89`) and returns 404 when none does (`GVBridgeController.cs:182`). Leaving the existing
tab alone keeps the service's reading exactly what it would have been without the helper. It is still wrong in
the ways §8 records; the helper does not add to that.

⚠ **Which label the service reports while a sign-in tab is open beside the service's tab is unmeasured.** Cookies
are per-browser, not per-tab, so the extraction should not care which matching tab it uses. That is reasoning,
not a measurement. Plan Task 12 measures it.

### 5.3 Prepare, and probe in the same step

**Trigger.** The helper acts on the service's classification. It never invents one.

| `browserRefreshOutcome` | Bridge process alive? | Helper |
|---|---|---|
| `Stale` | — | prepare |
| `Unreachable` | yes (`pgrep -f user-data-dir=…/gv-bridge-chrome`) **and** `:9224` answers | prepare. The spike showed a signed-out browser reported this way |
| `Unreachable` | no, or CDP silent | nothing. That is the watchdog's job and the alarm already says so |
| `Succeeded`, `NotAttempted`, `TornDown`, other | — | nothing, and clear any stale `PREPARED` state (§5.6) |

If the parallel branch (§8) introduces a signed-out outcome value, it joins the `prepare` row. The helper's
trigger table is the one place to add it.

**Prepare.** Open a new tab at
`https://accounts.google.com/ServiceLogin?continue=https://voice.google.com/u/0/voicemail`, wait for its load event
(15 s budget: 3–5× the spike's worst), activate it so it is the tab showing in the bridge window, then read **its
own** `window.location.href`. Where it lands decides the state:

| Lands on | Meaning | State |
|---|---|---|
| `accounts.google.com/…/signin/…` | signed out; the account chooser is up | **`PREPARED`** |
| `voice.google.com/…` | Chrome is **signed in**, yet the service reported a failure | **`NOT_SIGNED_OUT`**. Close the tab. A re-login will not fix this, and the owner must be told so |
| anything else, or no load event | unrecognised | **`PREPARE_FAILED`** with the URL recorded. Close the tab |

⚠ **The `voice.google.com` row is inferred, not measured.** The spike only exercised `ServiceLogin` while signed
out. That a signed-in profile is redirected straight to `continue` is the documented behaviour of that endpoint, not
something observed on this box. Plan Task 12 measures it. Until then, `NOT_SIGNED_OUT` is a claim the helper makes
with the landing URL quoted beside it, so a wrong inference is visible.

⭐ **If a page target is already on an `accounts.google.com` sign-in path, the helper adopts it** rather than
opening a second tab. A human is already signing in. Opening a new tab in front of them would pull the page out
from under their fingers.

### 5.4 Watch, then confirm

Each tick while `PREPARED`, the helper reads the prepared tab's own `window.location.href`. It **never** uses
`/json/list`'s cached `.url`. That field is the stale render `KNOWN-ISSUES.md:16-22` warns about, and on
2026-09-09 both repos misread it in opposite directions.

When the tab's own location is on `voice.google.com`:

1. **Gate, forced navigation.** Navigate that tab to `https://voice.google.com/u/0/voicemail` and read where it
   lands. `workspace.google.com` means the sign-in did not take: stay `PREPARED` and re-open the chooser on the
   next tick. `voice.google.com` passes the gate. This is alarm spec §5 step 6 / relogin plan §0.8: cheap, local,
   and only a gate.
2. **Authority, Google.** Record `browserSessionValidatedAt` (call it `before`), then
   `POST /api/gvbridge/cookies/refresh-from-browser`.
3. **Confirmed** only when all three hold:
   - the POST returned **200**;
   - a fresh `GET /status` shows `browserRefreshOutcome == Succeeded`;
   - `browserSessionValidatedAt` is **later than `before`**.

   A 200 alone is not enough: the spike saw another refresher move the timestamp within seconds of sign-in.
   Binding to "moved across our own POST" is what makes the claim "the session works now", not "something
   succeeded at some point".
4. On `502` (Google refused the harvested cookies after a sign-in the gate passed): **`CONFIRM_REFUSED`**, with the
   service's own 502 wording recorded. This is rare and serious, because a fresh sign-in was refused. Tell the owner;
   do not loop.
5. On `503`/`404`/transport failure: stay `SIGNED_IN_UNCONFIRMED` and retry next tick, at most 5 ticks, then
   **`CONFIRM_FAILED`** with the last status recorded.

Latency: the helper's 60 s tick, plus about 1 s of refresh, plus the alarm run it triggers. Target: the owner sees
confirmation **within about two minutes of signing in**, against up to 20 minutes plus 5 today.

### 5.5 Closing the loop in Chat

The existing alarm already posts **RESOLVED** in the incident thread when `browserRefreshOutcome` returns to
`Succeeded` (`gv-session-alarm.sh:486-503`). After the helper's POST, that is what the service reports. So the
loop closes with **no new posting path**:

- On `CONFIRMED`, the helper runs `systemctl --user start gv-session-alarm.service`. The unit is `Type=oneshot`,
  so systemd serialises this with a timer firing already in progress; it cannot run twice at once. The alarm then
  posts RESOLVED within seconds, not at its next 5-minute tick.
- The alarm's RESOLVED body gains one quoted paragraph, the helper's `CONFIRMED_TEXT`, stating what was verified,
  for example: *"Signed in and verified at 15:08:49Z: the tab landed on voice.google.com/u/0/voicemail,
  refresh-from-browser returned 200, and browserSessionValidatedAt moved 15:00:02Z → 15:08:49Z."* That puts the
  outcome in the thread, not merely the fact that a process ran.

### 5.6 The alarm's new copy, and a second track

The alarm reads the helper's state file **as data**. It never sources or evals it: the key/value lines are parsed
against a whitelist, one line per key, and text values are length-capped. The relogin branch learned this the hard
way: a sourced state file can redefine `printf` (PR #88 `c992d57`). The helper's state is a **second, independent
track**, alongside the session condition and never a value of `LAST_POSTED_CONDITION`. The relogin plan §0.9
showed that folding a second signal into the single condition string mutes the alarm in exactly the state it
exists for.

**What changes in the messages** (title format, threading and severities are unchanged, and follow the owner's
Chat policy):

| Moment | Message | Severity | Action (≤200 chars, written by the helper) |
|---|---|---|---|
| session goes `Stale` and the helper is already `PREPARED` | the existing alert, plus the helper's sentence | `alert` (ACTION) | e.g. *"Sign-in page is open in the GV bridge window on radio. At the box: tap Exit to Desktop, click the account, type the password. Confirmation follows here."* |
| the helper becomes `PREPARED` after the alert was already posted | reply: `[rotaryphone] GV session — sign-in page ready` | `warning` | the same action. **Not `info`**: the gateway silently drops `action` on `info` (alarm spec §4.4) |
| `NOT_SIGNED_OUT` | reply: `[rotaryphone] GV session — Chrome is signed in; re-login will not fix this` | `warning` | *"do not re-login. Read: journalctl -u rotary-phone --since -30min | grep GVApi"* |
| `CONFIRM_REFUSED` / `CONFIRM_FAILED` / `PREPARE_FAILED` | reply naming which, quoting the helper's recorded evidence | `warning` | the helper's text |
| `CONFIRMED` → service `Succeeded` | the existing RESOLVED, plus `CONFIRMED_TEXT` | `info` (RESOLVED, quiet: it threads under the alert) | none |

⛔ **The action text is written by the helper and quoted by the alarm, and it must be true when it is posted.**
The helper writes the reachability instruction only for a path the owner has chosen **and** that an attended
measurement has shown to work (§6, §10). Until then the instruction is the honest minimum: *"Sign-in page is open
in the GV bridge window on radio (behind the kiosk). Reach it at the box with the keyboard."* A path that has not
been shown to work is not named. That follows Radio Console's own rule: *"copy must not imply capabilities that
don't exist"*.

Dedupe keys for the new track embed the incident thread key. The existing per-condition alert key has a known
cross-incident hazard (`gv-session-alarm.sh:510-517`), and the new track does not inherit it.

**Clearing.** When the service reports `Succeeded` and the helper did not confirm it (the cron or the ladder
did), the helper closes the tab it opened and returns to `IDLE`. The alarm's RESOLVED then carries no
`CONFIRMED_TEXT`, which is honest: nobody verified a sign-in, because a refresh simply worked.

### 5.7 Tab hygiene

The helper closes **only tabs it opened**, identified by the target id it recorded. After `CONFIRMED` it keeps
the tab it opened (now the good `voice.google.com` tab) and leaves any others alone. Whether to also close a
leftover signed-out tab is **deferred**. Closing a tab the helper did not open is a browser-lifecycle action with
a rollback story, and nothing needs it yet. The cost is one extra tab per incident; incidents are measured in
single digits per month.

---

## 6. Reachability: the options

Each option below says what it needs, whether it crosses the Radio Console boundary, and what is measured versus
assumed. **The owner chooses** (§10, O1). The recommendation is at the end.

### 6.1 A. At the box: Exit to Desktop (exists today)

The owner taps Radio Console's `Exit to Desktop`. The kiosk closes, the bridge window is the only application
window left, and the prepared tab is its active tab. The owner clicks the account and types the password on the
K400. Once confirmation arrives, they tap `Radio Console` to bring the kiosk back.

- **Boundary:** uses a Radio Console affordance exactly as designed, and changes none of their code. The alarm's
  copy would **name their button**, so they get an FYI (§7, item 1) in case they rename it.
- **Measured:** the button exists and stops only the kiosk profile. **Unmeasured:** that the bridge window is then
  visible rather than minimised or on another workspace, and how long the kiosk takes to come back.
- **Cost:** the console is off-screen for the few minutes of the sign-in. Whether music continues while the kiosk
  is closed is **unknown**; their copy says the button "leaves everything else running".

### 6.2 B. At the box: Radio Console's dormant `Show the sign-in`

Already designed and coded on their side, behind `GV_RAISE_SUPPORTED=0`, waiting for exactly the measurement
this work can supply. Two adjustments make it useful:

1. Their `raise_gv_bridge()` opens `https://voice.google.com` in the bridge profile. With the helper in place that
   opens a **second** sign-in surface beside the prepared one, landing on the signed-out marketing page. The ask is
   for their button to call a **RotaryPhone-owned** `~/bin/gv-reauth-show.sh`, which activates the prepared tab
   and has meaningful exit codes from day one (not the exit-0-on-everything contract `gv-bridge-ensure.sh` is stuck
   with).
2. Their dialog runs from `radio-console-open`, meaning when the console is being **opened**, and the kiosk is not
   yet in front. That is why it may raise cleanly where a raise over a running kiosk might not.

- **Boundary:** **crosses**. It is their code and their call. §7 item 2.
- **Unmeasured:** whether a native-Wayland Chrome window activated from a `zenity` button click actually comes to
  the front on GNOME 46 (focus-stealing prevention).

### 6.3 C. RotaryPhone raises its own window over the running kiosk

Via CDP on our own port: `Target.activateTarget`, `Page.bringToFront`, `Browser.setWindowBounds` (`maximized` or
`fullscreen`).

- **Boundary:** our port and our window, **but its effect is to cover Radio Console's guest-facing screen.** That
  crosses the boundary in effect, and needs their agreement even though no file of theirs changes.
- **Unmeasured, and likely to fail:** GNOME's focus-stealing prevention normally answers an activation request
  that lacks an activation token with a *"… is ready"* notification, not a raise. Measured facts point the same
  way: `Shell.Eval` is disabled, `GetWindows` is denied, and X11 tools cannot see native-Wayland windows.
- **Hazard:** if it does work, it must never run unattended. §2 excludes that.

Recommendation: measure it (plan Task 12, one attended step) so the answer is recorded. Do not build on it
unless the measurement is positive **and** Radio Console agrees.

### 6.4 D. Remote: GNOME Remote Desktop

The owner connects over RDP to the live session, sees the physical screen (kiosk on top), switches to the
bridge window (Super → overview, or Alt+Tab), and signs in.

- **Boundary:** RDP is configured but **not running** (§3). Starting it is a **shared-system change**: it exposes
  the whole desktop, Radio Console's kiosk included, to a remote input device. That is an owner decision, with an
  FYI to Radio Console (§7, item 3).
- **Password path:** the owner's RDP client over RDP/TLS into GNOME. Nothing of RotaryPhone's sees it.
- **Unknown:** whether the owner already starts RDP on demand (the dispatch said they use it; the box shows it
  off at 15:35Z).

### 6.5 E. Remote: DevTools over an SSH tunnel (no install, no boundary)

`ssh -L 9224:127.0.0.1:9224 radio`, then `chrome://inspect` on the owner's laptop, **inspect** the prepared tab,
and interact with the page through DevTools' screencast.

- **Boundary:** none. Our port, bound to localhost, reached over the owner's existing SSH key. The kiosk is not
  touched and nothing is raised.
- **Password path:** laptop keyboard → DevTools → SSH tunnel → the bridge Chrome. **No RotaryPhone code is in
  that path**, which is why this is compatible with the owner decision where option F is not.
- **Unmeasured:** that DevTools screencast input works against this Chrome (152) from the owner's laptop Chrome,
  and that Google accepts the sign-in through it. It is the same browser, profile and IP, so there is no reason it
  should look different to Google, but that is reasoning. Plan Task 12.
- **Cost:** needs a laptop with SSH to `radio`; a phone will not do.

### 6.6 F. Rejected: a sign-in page served by RotaryPhone (CDP screencast relay)

RotaryPhone serves a web page that mirrors the bridge tab and forwards clicks and keystrokes to it. It would be
reachable from any browser on the LAN. It is **rejected** because the password's keystrokes would pass through
RotaryPhone's service, over plain HTTP on the LAN unless TLS and auth were added. That is "reading and submitting"
the password in all but name, which the 2026-09-25 decision rules out. It is recorded so the reason is not lost.

### 6.7 Recommendation

| | Ships in | Crosses boundary? | Measured? |
|---|---|---|---|
| **A** Exit to Desktop | Phase 1 | no (FYI only) | partly. Needs one attended check |
| **E** DevTools over SSH | Phase 1 | no | no. One attended check |
| **B** Show the sign-in | Phase 2 | **yes**. Radio Console's code | no |
| **D** RDP | on the owner's decision | **yes**. Shared system | no |
| **C** our raise | only if measured positive and agreed | **yes**, in effect | no |
| **F** relay | rejected | — | — |

**Recommended:** A for at-the-box, E for remote, both in Phase 1, because neither needs anyone's consent but the
owner's. Offer B to Radio Console as Phase 2: it is the best at-the-box experience, it is their design already,
and our half (`gv-reauth-show.sh`) is small. Measure C once so the question is closed. Leave D to the owner.

**What the recommendation gives up:** until B lands, the at-the-box path takes the console off-screen for a few
minutes. And E is only as good as the owner's access to a laptop.

---

## 7. Cross-boundary: what Radio Console is asked for

Drafted in `docs/prompts/2026-09-25-rotaryphone-reauth-window-request.md`. **Draft only.** It is not delivered to
their `docs/queue/inbound/` until the owner approves it (boundary doc: deliver from committed, pushed state, into
the recipient's lane).

1. **FYI, batch:** our alarm copy will name `Exit to Desktop` and `Radio Console`. Please tell us before renaming
   either.
2. **Request, their decision:** consider enabling `Show the sign-in`, with `raise_gv_bridge()` calling
   `~/bin/gv-reauth-show.sh` (RotaryPhone-owned, exit codes defined in the request) instead of opening
   `voice.google.com`. We supply the attended measurement of whether the raise works; they decide whether to ship.
3. **FYI, conditional:** if the owner enables GNOME RDP, the kiosk becomes remotely operable.
4. **Question:** is there anything in KIOSK-2 or `radio-console-open` that reacts to a *second tab* in the bridge
   profile? We found nothing: their liveness test is `pgrep` on the profile marker, and it is not affected.

A Change Log row in `RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` goes in **before** anything named in items 1–2 ships.

---

## 8. Dependencies

| Dependency | State (2026-09-25) | Why it blocks |
|---|---|---|
| `fix/alarm-thread-key-and-signedout-label`: retire an incident's thread key when it recovers undelivered | one commit (`4404fa1`), not merged | this design edits the same file and posts into the same threads. A reply filed under a five-day-old thread (spike finding 2) is worse with more replies |
| Same branch: the signed-out-as-`Unreachable` label | **not yet on the branch**, as far as its commits show | the helper's trigger row for `Unreachable`, and the alert copy the owner sees first, depend on what the service calls this state |
| Parallel owner of those fixes | another session | this design does not touch either fix; it rebases onto them |

---

## 9. PR #88: close it, and carry the survivors

**Recommendation: close draft PR #88 unmerged. Keep its branch, and carry the survivors into this work's
implementation branch with provenance noted in each commit.**

Why close rather than trim:

- About 1,300 of its 1,895 added lines are the breaker, its harness, the `relogin_unavailable` track, the plan, and
  the credential-file plumbing (`git diff origin/main...origin/feat/gv-auto-relogin --stat`). All of it is
  dropped. Trimming means reverting most of the branch under a title and branch name (`feat/gv-auto-relogin`)
  that would then describe the opposite of what merges.
- Every survivor needs changing anyway: `gv-cdp.py` must **ship** (it was deliberately kept out of the deploy) and
  lose three commands; the exclusion test must be retargeted; the second-track code must read a different file, as
  data.
- Its review threads are about the breaker. Carried into a trimmed PR they are noise, and so is their resolution
  history.
- Cost: the survivors are re-reviewed. That is proportionate, since each one changes.

| Survives | Changed how |
|---|---|
| `deploy/tools/gv-cdp.py` | moved to `deploy/gv-cdp.py` so it ships; **loses** `eval`, `dump` and `shot`; gains `new`, `activate`, `close`, `href`; keeps "never starts a browser, never clears a profile" and the no-secret-vocabulary rule |
| `deploy/tests/repro-gv-cdp.sh` | follows the tool. Static checks gain "no `Input.` domain, no `Runtime.evaluate` except `window.location.href`" |
| `docs/spikes/2026-09-09-gv-signin-cdp-recording.md` | carried verbatim, with a header noting the design it served was abandoned and this one uses rows 1, 3, 5, 8, 9 and the timings |
| The second-track **pattern** in `gv-session-alarm.sh` (independent track, persisted thread key, root re-attempt, dedupe keyed per event) | re-implemented against the helper's state file, **parsed as data, not sourced**. PR #88's alarm-side reader still sources in a subshell; the hardening in `c992d57` was applied to the breaker only |
| The rsync `--delete` finding and the exclusion test's method (read exclusions from the shipped `.ps1`, negative control) | **retargeted** at the files it listed that really are unprotected: `/opt/rotary-phone/refresh-gv-cookies.sh` (the load-bearing cron), `mute-gv-browser.py`, `scripts/`, and hand-made `*.bak*`. A separate small task; not a dependency of re-auth, carried so it is not lost |
| The drift-guard **pattern** (`AlarmCopyDriftTests.cs`) | a test pins the helper's state-file path and key names between the helper and the alarm |

| Dropped | Why |
|---|---|
| `deploy/gv-auto-relogin-breaker.sh`, `deploy/tests/repro-gv-relogin-breaker.sh` | no automated sign-in, so nothing to break |
| the `relogin_unavailable` alarm track and its `AlarmCopyDriftTests` additions | ditto |
| `gv-account.conf`: `SETUP-GVBridge.md` section, both deploy exclusions, test cases E/F | no credential file exists or may exist. **Keeping the exclusion would imply a sanctioned place for one** |
| `docs/plans/gv-auto-relogin.md` on the branch | superseded. The copy on `main` gets an ABANDONED banner in this docs PR |

---

## 10. Open decisions

Only the owner can settle these. "Unknown" means the option set itself is not known, not that it is empty.

| # | Decision | Options | Recommendation | What the others give up |
|---|---|---|---|---|
| O1 | Which reachability paths to support | A, B, C, D, E (§6); F rejected | **A + E now, offer B to Radio Console** | A-only: no remote path. Adding D: a shared-system exposure. Waiting for B: nothing ships until Radio Console acts |
| O2 | Prepare automatically, or only when asked | **auto** on the §5.3 trigger / on demand (the owner runs `gv-reauth-show.sh` or similar) | **auto** | On-demand adds a step to every incident. Auto's cost: a chooser page showing the account's email address sits in the bridge window until someone signs in, which is visible only if the kiosk is exited |
| O3 | GNOME RDP | leave off (today) / enable permanently / owner starts it on demand | **unknown**. The dispatch said the owner uses it; the box shows it off. Owner to say which is true | — |
| O4 | Is the K400 keyboard permanently attached? | — | **unknown owner fact**. If not, path A's copy must say "bring a keyboard", as Radio Console's copy already does | — |
| O5 | End-to-end acceptance on a real sign-out | wait for a natural one / a deliberate, attended sign-out (relogin plan §0.4 blast radius: the phone keeps working) | **deliberate and attended**, because a natural one is unbounded in time | natural: no cost, unbounded wait. Deliberate: minutes of a lost re-derivation floor, with the owner present |
| O6 | PR #88 | close + carry (§9) / trim in place | **close + carry** | trim: a branch name and title that contradict the merged content, plus breaker review noise |
| O7 | Delivering the Radio Console request (§7) | now, as a batch item / after Phase 1 merges | **after the Task 12 measurement**, so item 2 carries data rather than a guess | now: a request whose central fact is unmeasured |

---

## 11. Acceptance criteria

Every criterion names an **outcome** and the observation that confirms it, and each must be able to fail.
Criteria on the box read the **installed** artefact (`~/bin`, `systemctl --user`), never the repo (alarm spec
§5.3).

1. **Prepare.** With the service reporting `Stale` and the browser signed out, within 2 minutes the bridge has a
   tab whose **own** `window.location.href` is an `accounts.google.com` sign-in path, and it is the active tab.
   Read with `gv-cdp.py href`, not `/json/list`.
2. **No second surface.** With a page target already on a sign-in path, the helper opens no tab. Observed as an
   unchanged page-target count.
3. **Not-signed-out is told, not hidden.** A local harness where the sign-in URL lands on the voice stub yields
   `NOT_SIGNED_OUT`, a closed tab, and a `warning` in the thread that says *do not re-login*.
4. **Confirmation needs Google.** In the local harness, all three negative controls must prevent `CONFIRMED`:
   the gate lands on the workspace stub; refresh returns 502; refresh returns 200 but `validatedAt` does not move.
5. **Closed loop, end to end** (attended, O5). After the owner signs in, the incident thread shows RESOLVED with
   `CONFIRMED_TEXT` **within 3 minutes**, and `GET /status` shows `Succeeded` with `validatedAt` after the sign-in.
   Verified by the owner **reading the thread**, not from our state file.
6. **The alert says what to do.** The `browser_stale` alert delivered to the thread carries an action naming only a
   path that criterion 8 has shown to work. Verified by reading the delivered message.
7. **No password vocabulary, no input.** Static checks over the shipped helper and `gv-cdp.py`: zero matches for
   `password|passwd|credential|secret`, zero `Input.` CDP methods, and exactly one `Runtime.evaluate` expression
   (`window.location.href`). Each check has a negative control that plants the forbidden token and shows the check
   fails.
8. **Reachability, measured.** For each path the owner picks in O1, an attended run records that the owner could
   see the prepared tab and type into it, using a neutral local `data:` page, with no sign-out needed. A path
   without this record is not named in the alarm copy.
9. **Installed, not merely shipped.** After a normal deploy, `~/bin/gv-reauth-assist.sh`, `~/bin/gv-cdp.py` and
   the timer match the shipped copies (`check-installed-drift.sh --group reauth`), and `systemctl --user
   list-timers` shows the timer with a NEXT within 60 s.
10. **Alarm unaffected when the helper is absent.** With the helper's state file missing, the alarm's session track
    posts exactly what it posts today. The existing `repro-gv-session-alarm.sh` cases pass unchanged.
