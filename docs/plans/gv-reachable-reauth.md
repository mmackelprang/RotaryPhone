# Plan — GV reachable re-auth: make the human's sign-in fast to reach and certain to have worked

**Spec:** [`../superpowers/specs/2026-09-25-gv-reachable-reauth-design.md`](../superpowers/specs/2026-09-25-gv-reachable-reauth-design.md).
Read it first. §1 (no password, ever), §5 (the helper) and §6 (reachability options) are what this plan builds.
**Supersedes:** [`gv-auto-relogin.md`](gv-auto-relogin.md) (abandoned by owner decision 2026-09-25) and draft PR #88.
**Date:** 2026-09-25. **Status:** planned, not started. Awaiting owner review of the spec's §10 decisions.

> There is no work queue in this repo. This plan file is the handoff artefact. Whoever builds this executes the
> tasks below in order.

---

## 1. How anything gets verified

**The rule, inherited from `gv-session-alarm.md` §1 and the boundary doc:** every acceptance check reads the
**installed** artefact or an **outcome**, never a repo file and never "the component ran". Unit tests and local
harnesses are the exception, because their subject really is the repo source, and they are marked that way.

**And every check must be able to fail.** Each one below either has a negative control, or is written so that the
**unsafe** behaviour is what the assertion catches. A check that has only ever seen the passing case is not
evidence.

### 1.1 Lanes

| Lane | Where | Proves | Box? | Owner? |
|---|---|---|---|---|
| **O** owner | a decision, written in the PR body | — | no | yes |
| **L** local | WSL/Linux, `deploy/tests/`, a throwaway headless Chrome plus stub HTTP servers | the helper's state machine, `gv-cdp.py`'s surface, the alarm's new track and copy | no | no |
| **U** unit | `dotnet test` (**Windows SDK**; WSL carries no net10.0 SDK, per `gv-session-alarm.md` §7.7) | the helper↔alarm contract drift guard | no | no |
| **D** deploy | `Deploy-ToLinux.ps1` to `radio` | installed = shipped = repo | yes | runs or approves it |
| **B** box read-only | `ssh radio`, bounded, non-streaming (`--since` **and** `-n`; never `-f`) | installed state, the helper doing nothing on a healthy session | yes | no |
| **A** box attended | `radio`, **owner present at the box or on the remote path being measured** | reachability, the real sign-in, the Chat thread | yes | **yes** |

### 1.2 Owner gates

| Gate | Blocks | Spec |
|---|---|---|
| **G1**: O1 (paths), O2 (auto-prepare), O6 (PR #88) decided | Task 3 onward | §10 |
| **G2**: O4 (keyboard attached?) and O3 (RDP) answered | Task 14 (the final alarm copy) | §10 |
| **G3**: O5 (deliberate or natural sign-out) decided, and the owner present | Task 13 | §10 |
| **G4**: O7 (when to send the Radio Console request) | Task 15 | §10 |

### 1.3 Branching

The spec and this plan are preparatory, on `docs/gv-reachable-reauth`. Implementation goes on
**`feat/gv-reachable-reauth`**, branched from `main` **after** Task 2's dependency has merged. Task 17 goes on its
own branch, `fix/deploy-rsync-delete-exclusions`: it is independent of re-auth, and bundling it would put a deploy
change inside a feature review.

---

## 2. Task list

### Phase 0: decisions and dependencies

#### Task 1: Record the owner's decisions · lane **O**

Present spec §10 O1–O7 to the owner in the shape the spec gives (options, recommendation, what each alternative
gives up). Where the spec says **unknown** (O3, O4), ask an open question. Do not offer invented options.

**Acceptance:** each decision is written in the implementation PR's body, with the owner's words. ⛔ If O1 selects
no path at all, stop. The helper still works, but the alarm would be telling the owner a page is ready in a window
they cannot reach.

#### Task 2: Dependency gate: the parallel alarm fixes are on `main` · lane **B** (git) + **L**

`fix/alarm-thread-key-and-signedout-label` owns two fixes this work builds on (spec §8). This plan does **not**
re-implement either.

```bash
git fetch origin
git merge-base --is-ancestor 4404fa1 origin/main && echo THREAD-KEY-FIX-MERGED || echo NOT-MERGED
git log origin/main --oneline -- deploy/gv-session-alarm.sh | head -5
```

**Acceptance:**

- `THREAD-KEY-FIX-MERGED` is printed. If the fix was squashed, the check is the presence of what it **adds**,
  not a commit id: `grep -c` for its retirement logic in `deploy/gv-session-alarm.sh` on `origin/main` is
  non-zero, and `repro-gv-session-alarm.sh` on `main` contains its new case.
- The signed-out label: record, in the PR body, **what the service now reports** for a signed-out browser whose
  only page is on `accounts.google.com`. Read it from the merged code or its tests, not from a branch name. If
  that fix has not merged, record `Unreachable` (the spike's measurement), and Task 5's trigger table keeps its
  `Unreachable + Chrome alive` row.
- ⛔ If the thread-key fix has not merged, Tasks 8 and 14 do not start. They edit the same file and post into the
  same threads.

---

### Phase 1: carry PR #88's survivors (spec §9)

#### Task 3: `gv-cdp.py` ships, with a smaller surface · lane **L**

**Depends on:** G1 (O6 = close + carry).

1. Close PR #88 with a comment linking this spec's §9. **Do not delete the branch.**
2. `git show origin/feat/gv-auto-relogin:deploy/tools/gv-cdp.py > deploy/gv-cdp.py`. Commit it unchanged first,
   with `Carried from PR #88 (feat/gv-auto-relogin @ 0221b63)` in the message, so the diff that follows is
   reviewable.
3. Then change it:
   - **remove** `eval`, `dump`, `shot`;
   - **add** `href --target` (the literal `window.location.href`, the tool's only `Runtime.evaluate`),
     `new --url` (`Target.createTarget`, prints the new target id), `activate --target`
     (`Target.activateTarget`), `close --target` (`Target.closeTarget`);
   - keep `targets`, `navigate`, the event buffer, and the `errorText` → exit 3 rule;
   - rewrite the docstring: it now ships, it is installed to `~/bin`, and the "lives in deploy/tools on purpose"
     paragraph is gone.
4. Carry `deploy/tests/repro-gv-cdp.sh` the same way (unchanged commit first), then extend it.

**Acceptance (L), each with a negative control:**

| Check | Passes when | Negative control that must FAIL it |
|---|---|---|
| no secret vocabulary | `grep -ciE 'password\|passwd\|credential\|secret'` = 0 | a temp copy with `# password` appended |
| no input | `grep -c "Input\."` = 0 | a temp copy with `Input.dispatchKeyEvent` appended |
| one evaluate expression | every `Runtime.evaluate` in the file has `expression="window.location.href"` | a temp copy with `expression="document.cookie"` |
| never launches a browser | `grep -cE 'Popen\|subprocess\|--user-data-dir\|launch'` = 0 | a temp copy importing `subprocess` |
| `href` reads the target's own location, not `/json/list` | against the harness Chrome, a target navigated by an in-page `location.replace` reports the **new** URL while `/json/list` still shows the old one | — (this case is itself the control: it fails if `href` reads the cache) |
| `new` / `activate` / `close` | the page-target count goes +1 then −1; after `activate`, the harness's **own** CDP call (not the tool's) reads `document.visibilityState` as `visible` on the activated target and `hidden` on the other | activating the *other* target flips both readings. If headless Chrome reports `visible` for both, the case is `SKIPPED-LOUDLY` and moves to Task 12 |

⚠ The `/json/list`-lag case may not reproduce on a fast local Chrome. If it cannot be made to show a difference,
the harness says `SKIPPED-LOUDLY` and exits non-zero. It does not pass silently (the repo's rule since
2026-09-09).

#### Task 4: Carry the spike record · lane **L**

`git show origin/feat/gv-auto-relogin:docs/spikes/2026-09-09-gv-signin-cdp-recording.md` → same path, unchanged
commit, then a follow-up commit adding a header: *the auto-relogin design this served was abandoned 2026-09-25;
`2026-09-25-gv-reachable-reauth-design.md` uses rows 1, 3, 5, 8, 9 and the timings.*

**Acceptance:** `git diff <carry-commit>^ <carry-commit> --stat` shows one file added and nothing else changed.
The header commit touches only the head of the file.

---

### Phase 2: the helper

#### Task 5: `deploy/gv-reauth-assist.sh`, the state machine · lane **L**

**Depends on:** Task 3.

Build the helper per spec §5.2–§5.4 and §5.7:

- `set -uo pipefail`, not `set -e`, and `LC_ALL=C.UTF-8`: the same reasons the alarm gives at `:24-39`.
- `flock -n` on `~/.local/state/gv-reauth-assist.lock`. If the lock is held, log and exit 0: another tick is
  mid-flight, which is the intended outcome.
- Every URL and host is overridable by env (`GV_REAUTH_SIGNIN_URL`, `GV_REAUTH_VOICE_URL`,
  `GV_REAUTH_VOICE_HOST`, `GV_REAUTH_SIGNIN_HOST`, `GV_REAUTH_STATUS_URL`, `GV_REAUTH_REFRESH_URL`,
  `GV_REAUTH_CDP_PORT`), so the local harness can point it at stubs. The defaults are the real values.
- States: `IDLE`, `PREPARED`, `SIGNED_IN_UNCONFIRMED`, `CONFIRMED`, `NOT_SIGNED_OUT`, `PREPARE_FAILED`,
  `CONFIRM_REFUSED`, `CONFIRM_FAILED`.
- State file `~/.local/state/gv-reauth-assist.state`: `KEY=value` lines, **no shell quoting**, one line per key,
  values stripped of newlines and capped at 600 characters. Written atomically (`.new` + `mv`, as `write_state`
  does in the alarm). Keys: `STATE`, `STATE_SINCE`, `PREPARED_TARGET`, `PREPARED_AT`, `LANDED_URL`,
  `VALIDATED_BEFORE`, `VALIDATED_AFTER`, `REFRESH_HTTP`, `CONFIRM_TICKS`, `HUMAN_TEXT`, `HUMAN_ACTION`.
- `HUMAN_TEXT` / `HUMAN_ACTION` are the only words the alarm will quote. `HUMAN_ACTION` is **≤200 characters by
  construction**, checked in the script before writing (the gateway's 422-delivers-nothing trap, alarm spec §4.4).
- On `CONFIRMED`: `systemctl --user start gv-session-alarm.service`, with its exit status logged. A failure to
  start is logged loudly and does **not** undo `CONFIRMED`: the alarm's own timer still runs within 5 minutes.
- `--status` prints the state file and what the next tick would do, **with no CDP call and no POST**. This is the
  lane-B instrument.
- `--print-config` has no side effects, like the alarm's.

**Harness `deploy/tests/repro-gv-reauth-assist.sh` (L):** a headless throwaway Chrome (own temp profile, own
port), plus a stub HTTP server with three hosts: *voice* serves an app page or 302s to *workspace*, depending on a
flag; *signin* serves a chooser page or 302s to *voice*; and a status/refresh stub (extend
`gv-alarm-status-stub.py`) with scriptable outcome, `validatedAt` and refresh HTTP code.

**Acceptance (L). Every row is an outcome, several are negative controls:**

| Case | Setup | Must observe |
|---|---|---|
| R1 prepare | status `Stale`, signin → chooser | `STATE=PREPARED`; one new page target; its `href` is on the *signin* host; it is the active target |
| R2 adopt | as R1, but a target is already on the *signin* host | no new target (count unchanged); `PREPARED_TARGET` is the existing id |
| R3 not signed out | status `Stale`, signin → 302 → *voice* | `STATE=NOT_SIGNED_OUT`; the opened tab **closed** (count back to baseline); `HUMAN_TEXT` contains "do not re-login" and the landed URL |
| R4 healthy | status `Succeeded`, and `GV_REAUTH_CDP_PORT` pointed at a stub listener that records every connection | **zero** connections recorded over 3 ticks: the helper must not touch a healthy browser. Negative control: the same stub with status `Stale` records ≥1, which shows the listener can see the helper |
| R5 Chrome dead | status `Unreachable`, no process with the profile marker | `STATE=IDLE`, no CDP attempt |
| R6 confirm | from R1, flip *signin* to 302 → *voice*; refresh 200; `validatedAt` advances | `CONFIRMED`; `VALIDATED_AFTER` > `VALIDATED_BEFORE`; `systemctl` invoked (stubbed by a PATH shim that records its argv) |
| R7 ⛔ gate negative | as R6, but the forced navigation to *voice* 302s to *workspace* | **not** `CONFIRMED`; back to `PREPARED` next tick |
| R8 ⛔ Google refuses | as R6, refresh 502 | `CONFIRM_REFUSED`; the 502 body quoted in `HUMAN_TEXT`; no `systemctl` call |
| R9 ⛔ timestamp does not move | as R6, refresh 200 but `validatedAt` unchanged | **not** `CONFIRMED` |
| R10 transient | as R6, refresh 503 ×5 | `SIGNED_IN_UNCONFIRMED` ×4 then `CONFIRM_FAILED` |
| R11 cleared by someone else | from `PREPARED`, status flips to `Succeeded` with no sign-in by the helper | `IDLE`; the helper's tab closed; **no** `CONFIRMED_TEXT` |
| R12 lock | two concurrent runs | exactly one does work; the other logs lock-held and exits 0 |
| R13 action length | a planted 250-char instruction | the script refuses to write it, and logs so; the state keeps its previous `HUMAN_ACTION` |
| R14 static | the Task 3 static checks, run over this script as well | all pass; each negative control fails them |

#### Task 6: `deploy/gv-reauth-show.sh` · lane **L**

The one-shot a human, or Radio Console's button (spec §6.2), runs to put the prepared tab in front. It activates
`PREPARED_TARGET` via `gv-cdp.py activate`. It does **not** attempt a compositor-level raise; whether activation
raises the window is exactly what Task 12 measures.

Exit codes are defined **before** anyone consumes them, the lesson of `gv-bridge-ensure.sh`:

| Code | Meaning |
|---|---|
| 0 | the prepared tab exists and was activated |
| 3 | nothing is prepared (the session is healthy, or the helper has not run) |
| 4 | the prepared target no longer exists |
| 5 | CDP unreachable |
| 2 | usage error |

**Acceptance (L):** one harness case per code, each asserting the code **and** the observable effect (the target
is active, or unchanged). A case that expects 0 with the helper's state file absent must get 3, not 0.

#### Task 7: Units, installer, deploy wiring, drift group · lane **L**

- `deploy/systemd/gv-reauth-assist.service` (`Type=oneshot`, no `Restart=`, no `EnvironmentFile=`) and
  `.timer` (`OnUnitActiveSec=60s`, `OnBootSec=2min`).
- `deploy/install-gv-reauth-assist.sh`, modelled on `install-gv-session-alarm.sh`: atomic install of
  `gv-reauth-assist.sh`, `gv-reauth-show.sh` and `gv-cdp.py` into `~/bin`, units into `~/.config/systemd/user`,
  **timer installed but not enabled unless `--enable`** (enabled in Task 13). Self-report via `--print-config`
  at the end.
- `Deploy-ToLinux.ps1`: ship `deploy/gv-cdp.py`. Today only `deploy/*.sh` ships (`:770`, no `-Recurse`, `.sh`
  filter). Add a `*.py` collection with the same exit-checked `scp` and a `chmod 755`, run the new installer after
  the alarm's (`:901`), and add `check-installed-drift.sh --group reauth` beside `:923`/`:928`.
- `check-installed-drift.sh`: add `reauth` to the group `case` (`:64-76`).
- ⛔ Do **not** add anything to `setup-gvbridge.sh`. It installs `gv-bridge-ensure.sh`, whose exit-code contract is
  frozen pending Radio Console (alarm spec §8). The same reasoning as the alarm's own installer header.

**Acceptance (L):**

- `repro-installed-drift.sh` gains a `reauth` case, plus a negative control: flip one byte of the installed copy,
  and the check reports `DIFFERS`.
- `repro-install-atomicity.sh` pattern applied to the new installer: no window in which `~/bin/gv-reauth-assist.sh`
  is absent or partial.
- A dry parse of `Deploy-ToLinux.ps1` shows `gv-cdp.py` in the shipped set. The check reads the list the script
  **builds**, not a restated copy, the same method as the carried tar-clobber cases.

---

### Phase 3: the alarm

#### Task 8: The alarm's second track and copy · lane **L**

**Depends on:** Task 2 (thread-key fix merged), Task 5.

In `deploy/gv-session-alarm.sh`:

1. **Read the helper's state as data.** `while IFS='=' read -r k v` over the file, whitelist of keys, first
   occurrence wins, unknown keys ignored, values capped. **No `source`, no `eval`, no subshell `.`.** The file
   missing means `absent`, which means the session track behaves byte-for-byte as today (spec criterion 10).
2. **Stale alert carries the helper's words** when `STATE=PREPARED` at post time: the body gains a quoted
   `HUMAN_TEXT` paragraph; `action` becomes `HUMAN_ACTION`. Otherwise today's action stands.
3. **Second track**, `LAST_POSTED_ASSIST_STATE` plus the assist's `STATE_SINCE`, persisted in the alarm state
   file. It posts on a transition into `PREPARED` (only if the alert did not already carry it), `NOT_SIGNED_OUT`,
   `PREPARE_FAILED`, `CONFIRM_REFUSED` or `CONFIRM_FAILED`. Severity `warning`; title
   `[rotaryphone] GV session — <what changed>`; reply into `INCIDENT_THREAD_KEY`, opening the thread root first if
   it was not delivered (the existing `open_incident_thread` retry rule). Dedupe key
   `rotaryphone-gv-assist-<STATE>-<INCIDENT_THREAD_KEY>`: per event, **not** per condition (spec §5.6).
4. **RESOLVED carries `CONFIRMED_TEXT`** when the assist state is `CONFIRMED` and its `STATE_SINCE` falls within
   the open incident. Otherwise RESOLVED is unchanged.
5. `THIS SCRIPT DETECTS NOTHING` stays true, and the header gains one line saying the assist track quotes the
   helper exactly as the session track quotes the service.

**Acceptance (L), in `repro-gv-session-alarm.sh`:**

| Case | Must observe (read from the gateway stub's received payloads, not the alarm's log) |
|---|---|
| A1 absent | every pre-existing case passes **unchanged**. Run the old file against the new script; zero diffs in received payloads |
| A2 prepared-before-alert | one alert; `action` == the planted `HUMAN_ACTION`; body contains `HUMAN_TEXT` |
| A3 prepared-after-alert | alert (old action), then **one** `warning` reply with the new action, same `thread_key` |
| A4 no repeat | A3 then two more polls: no further posts |
| A5 ⛔ injection | a state file with `HUMAN_TEXT=$(touch /tmp/pwned)` and a line `printf() { :; }`: the file `/tmp/pwned` is **not** created, and the text is delivered **literally** |
| A6 not-signed-out | `warning` reply whose action says not to re-login |
| A7 resolved with proof | `CONFIRMED` then status `Succeeded`: RESOLVED in the incident thread, body contains `CONFIRMED_TEXT` |
| A8 ⛔ resolved without proof | status `Succeeded`, assist `IDLE`: RESOLVED has **no** `CONFIRMED_TEXT` |
| A9 ⛔ track independence | assist stuck in `PREPARE_FAILED`, then the session goes `Stale` → `Unreachable`: **each** session transition still posts (relogin plan §0.9's failure, reproduced as a negative) |
| A10 422 | gateway stub answers 422 on the assist reply: heartbeat **not** refreshed, exit 1 |

#### Task 9: The helper↔alarm contract drift guard · lane **U**

In `src/RotaryPhoneController.GVBridge.Tests/Alarm/`, beside `AlarmCopyDriftTests.cs`: read both scripts from the
repo (as that test does) and assert that the state-file default path literal and every whitelisted key name
appear in both. Plus the mirror `deploy/tests/check-alarm-copy-drift.sh` case, so it also runs in lane L.

**Acceptance (U):** passes on the Windows SDK. Negative control: renaming one key in a temp copy of the helper
makes the test fail, which shows the test reads the helper rather than a restated list.

---

### Phase 4: on the box

#### Task 10: Deploy; installed, not enabled · lane **D** then **B**

**Depends on:** Tasks 3–9 merged to `main` (via the PR), and a normal deploy.

**Acceptance (B), each reading the installed state:**

```bash
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group reauth --ship-dir /opt/rotary-phone/deploy
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group alarm  --ship-dir /opt/rotary-phone/deploy
systemctl --user list-unit-files 'gv-reauth-assist.*'      # installed
systemctl --user list-timers 'gv-reauth-assist.*'          # EMPTY: not enabled yet, by design
~/bin/gv-reauth-assist.sh --print-config
~/bin/gv-reauth-assist.sh --status                          # IDLE on a healthy session
grep -c 'assist' ~/bin/gv-session-alarm.sh                  # non-zero: the NEW alarm is installed (presence of what the fix ADDS)
```

- Both drift groups report `N/N installed files match`.
- `--status` prints `STATE=IDLE` (or "no state file") and "next tick: nothing" while `browserRefreshOutcome` is
  `Succeeded`.
- ⛔ The alarm is still delivering: its next timer run journals `heartbeat refreshed`
  (`journalctl --user -u gv-session-alarm --since -10min -n 20`). A deploy that silences the alarm fails this task.

#### Task 11: Enable, and prove it leaves a healthy browser alone · lane **B**

`bash /opt/rotary-phone/deploy/install-gv-reauth-assist.sh --enable`, then observe for **30 minutes**:

**Acceptance (B):**

- `list-timers` shows NEXT within 60 s.
- The page-target count from `curl -s 127.0.0.1:9224/json/list | jq '[.[]|select(.type=="page")]|length'`,
  sampled every 5 minutes, **never changes**.
- The helper's journal (`--since -30min -n 60`) shows ticks, and every one ends in "nothing to do".
- ⛔ This is the outcome that matters most on a box shared with a guest-facing kiosk: **a healthy session produces
  zero browser actions.** Any tab opened in this window is a failure, not a curiosity.

#### Task 12: Attended measurements · lane **A** · owner present

**Depends on:** Task 11, G1. Everything here is reversible and needs **no sign-out**. Every page opened is closed
before the task ends, and the page-target count returns to its baseline (recorded first).

| # | Measure | How | Records |
|---|---|---|---|
| M1 | `ServiceLogin` landing while **signed in** (spec §5.3's inferred row) | `gv-cdp.py new --url <ServiceLogin…>`, then `href` after load, then `close` | the landed URL. If it is **not** `voice.google.com/…`, the helper's `NOT_SIGNED_OUT` row is wrong and Task 5 changes before Task 13 |
| M2 | an extra tab does not disturb the service | with M1's tab open, `POST refresh-from-browser` | HTTP code, and `validatedAt` moved. A non-200 here means the new-tab rule (spec §5.2) is unsafe; stop |
| M3 | **path A**: Exit to Desktop reveals the bridge window | open a neutral tab `data:text/html,<input autofocus placeholder=type-here>`, `activate` it; owner taps Exit to Desktop | is the bridge window visible without further action (yes / needs the overview / not found)? Can the owner type into the field on the K400? Seconds for `Radio Console` to bring the kiosk back |
| M4 | **path E**: DevTools over SSH | owner's laptop: `ssh -L 9224:127.0.0.1:9224 radio`, `chrome://inspect`, inspect the neutral tab, type into the field through the screencast | typed text readable back from the tab (the **one** permitted exception to "never read a field": the neutral page is ours and holds no secret. Read it with a one-off `curl` to the CDP HTTP endpoint, not by adding `eval` back to the shipped tool) |
| M5 | `gv-reauth-show.sh` over the running kiosk (**path C**, and the mechanism behind **B**) | ⛔ **only if the owner OKs covering the kiosk for a few seconds, and Radio Console has not objected** (see the handoff). Run it with the neutral tab prepared | does the bridge window come to the front (yes / a "ready" notification / nothing)? Screenshot the display with the owner's phone, not with a tool on the box |
| M6 | **path D**, only if O3 says RDP is in use | owner connects, reaches the neutral tab | yes/no; steps needed |

**Acceptance (A):** a results table appended to this plan (§4 below), one row per measurement, **each row
recorded by the owner's observation**, and the page-target count back at baseline afterwards. A path with "no" or
"not measured" is not named in the alarm copy (spec criterion 8).

#### Task 13: End to end on a real sign-out · lane **A** · G3

Per O5: either the owner signs out deliberately (relogin plan §0.4's blast radius, stated to the owner before
starting: the phone keeps working, and the re-derivation floor is lost until the sign-in), or this task waits for a
natural one.

**Acceptance (A), each observed by the owner or read from the service:**

1. Within 2 minutes of the service reporting the failure, `gv-cdp.py href --target <PREPARED_TARGET>` is on an
   `accounts.google.com` sign-in path (spec criterion 1).
2. The incident thread shows the alert, or alert plus `warning`, with the action text naming only the measured
   path(s) (criterion 6). **The owner reads it in Chat.**
3. The owner signs in using the named path, typing the password into Google's page. Nothing of ours sees it.
4. **Within 3 minutes of the sign-in settling**, the thread shows RESOLVED with `CONFIRMED_TEXT`. `GET /status`
   shows `Succeeded` with `browserSessionValidatedAt` after the sign-in time the owner noted (criterion 5).
5. Also record what the service reported **between** sign-out and sign-in, with the timestamps. This measures the
   label question spec §5.2/§8 left open, now with the new-tab rule in place.

⛔ If step 4 fails, **do not** treat a later cron-driven `Succeeded` as a pass. That is spec §6 of the alarm arc
(verifying that something ran and inferring that it worked), and it is the failure this whole design exists to
remove.

#### Task 14: Final alarm copy from the measurements · lane **L** · G2

**Depends on:** Task 12, O3, O4. Set the helper's `HUMAN_ACTION` / `HUMAN_TEXT` for `PREPARED` to name exactly the
paths Task 12 recorded as working, in the order the owner prefers. If O4 says the keyboard is not always present,
add Radio Console's truthful line: you will need a keyboard. Re-run R13 and A2.

**Acceptance (L):** the harness asserts the shipped `HUMAN_ACTION` string is ≤200 characters and contains no path
whose Task 12 row is not "yes". Implement this as a small table in the harness the owner can read, not as a
regex.

---

### Phase 5: across the boundary, and the docs

#### Task 15: Deliver the Radio Console request · lane **O** · G4

**Depends on:** Task 12 (so item 2 carries data). Finalise `docs/prompts/2026-09-25-rotaryphone-reauth-window-request.md`
with the M3/M5 results. Commit and push, then write it into `D:\prj\RTest\RTest\docs\queue\inbound\` (boundary
doc, "Cross-repo traffic": deliver into the recipient's lane from committed, pushed state, and name the files in
the message). Add the Change Log row to `RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` **before** Task 14's copy naming
`Exit to Desktop` ships.

**Acceptance:** the file in their lane is byte-identical to the pushed commit (`sha256sum` both). Their
acknowledgement, naming what they checked, is recorded in the Change Log row. An unacknowledged request is
recorded as **unacknowledged**, not as agreed.

#### Task 16: Runbook and doc corrections · lane **L**

- `docs/SETUP-GVBridge.md`: a "When the GV session signs out" section describing what the owner will see in Chat,
  the measured paths, and `gv-reauth-show.sh`; plus a note that `gv-account.conf` does **not** exist and must not
  be created.
- `docs/SETUP-AND-TESTING.md:145`: `ssh radio "wmctrl -a Chrome"` is wrong twice over: `wmctrl` is not installed,
  and both Chromes are native Wayland, which X11 tools cannot see (spec §3). Replace it with the measured path.
- `docs/KNOWN-ISSUES.md`: an entry recording the new flow and its known limits.

**Acceptance:** `grep -n "wmctrl" docs/` returns only lines that explain why it does not work. Each runbook step
names a path with a Task 12 "yes".

#### Task 17: Carried from PR #88: the rsync `--delete` hazard · lane **L** · separate branch, separate PR

Not a dependency of re-auth. Carried so PR #88's finding is not lost when it closes (spec §9).

1. **Re-derive the list; do not copy it.** On the box (lane B), list `/opt/rotary-phone` top level. Locally, list
   the publish output. Every box entry absent from the publish output is a deletion target under
   `rsync --delete`. PR #88 listed `refresh-gv-cookies.sh`, `mute-gv-browser.py`, `scripts/`,
   `ChromeExtension/` and `*.bak*`. Confirm each; in particular, check how `ChromeExtension/` gets there
   (`Deploy-ToLinux.ps1:757` reads from it).
2. Add rsync exclusions for the confirmed entries.
3. Carry PR #88's `repro-tar-clobber.sh` E/F **method** (exclusions read from the shipped `.ps1`, contents asserted
   after transfer, negative control with the exclusion removed), retargeted at `refresh-gv-cookies.sh`.

**Acceptance (L):** `F` passes (the cron script survives `rsync --delete` with the shipped exclusions), **and**
`F-neg` shows it deleted without them. Without `rsync` installed the harness says `SKIPPED-LOUDLY` and fails.

---

## 3. What is deliberately not in this plan

| Not here | Where it lives |
|---|---|
| The thread-key fix and the signed-out label | `fix/alarm-thread-key-and-signedout-label` (Task 2 waits for it) |
| `gv-bridge-ensure.sh` exit codes, and installing its newer copy | alarm spec §8, §11 decision 4; open with Radio Console |
| Any change on Radio Console's side | their decision, after Task 15 |
| Closing leftover signed-out tabs the helper did not open | deferred (spec §5.7) |
| A `WARN` threshold on `browserSessionAgeSeconds` | alarm spec §11 decision 3; still needs a measured baseline |

---

## 4. Measurement results (filled in by Task 12)

| # | Result | Observed by | Date |
|---|---|---|---|
| M1 | | | |
| M2 | | | |
| M3 | | | |
| M4 | | | |
| M5 | | | |
| M6 | | | |
