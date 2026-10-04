# Plan — GV reachable re-auth: the human fallback when auto-relogin cannot recover

**Spec:** [`../superpowers/specs/2026-09-25-gv-reachable-reauth-design.md`](../superpowers/specs/2026-09-25-gv-reachable-reauth-design.md).
Read it first. §5.1 (precedence: who acts when) is the heart of this plan.
**Works beside:** [`gv-auto-relogin.md`](gv-auto-relogin.md) / PR #88, the primary path, resumed 2026-09-25 under an
owner-written sign-in driver. **Builds on:** PR #90 (merged, `54d77ca`): `SignedOut` / `browser_signed_out`.
**Date:** 2026-09-25 (revised the same day after owner decisions O1, O2 and O6). **Status:** planned, not started.

> There is no work queue in this repo. This plan file is the handoff artefact.

---

## 1. How anything gets verified

Every acceptance check reads the **installed** artefact or an **outcome**, never a repo file and never "the
component ran". Unit tests and local harnesses are the exception, marked as such. **Every check must be able to
fail:** it either has a negative control, or its assertion is written so that the unsafe behaviour is what it
catches.

### 1.1 Lanes

| Lane | Where | Proves | Box? | Owner? |
|---|---|---|---|---|
| **O** owner | a decision, written in the PR body | — | no | yes |
| **C** coordination | a request to the #88 builder, via the coordinator | an agreed contract | no | no |
| **L** local | WSL/Linux, `deploy/tests/`, a throwaway headless Chrome plus stubs | the assist's state machine and precedence, the alarm's new track and copy | no | no |
| **U** unit | `dotnet test` on the **Windows SDK** | contract drift guards | no | no |
| **D** deploy | `Deploy-ToLinux.ps1` to `radio` | installed = shipped = repo | yes | runs or approves |
| **B** box read-only | `ssh radio`, bounded (`--since` **and** `-n`, never `-f`) | installed state; the assist doing nothing when it should do nothing | yes | no |
| **A** box attended | `radio`, owner present | reachability, a real sign-in, the Chat thread | yes | **yes** |

### 1.2 Decisions and gates

| | Decision | State | Blocks |
|---|---|---|---|
| O1 | reachability: **A (Exit to Desktop) + E (SSH tunnel) now, offer B to Radio Console later** | ✅ decided 2026-09-25 | — |
| O2 | **automatic** prepare, within §5.1's precedence | ✅ decided 2026-09-25 | — |
| O6 | PR #88 **kept**; it is the primary path | ✅ decided 2026-09-25 | — |
| O3 | GNOME RDP in use? | open | nothing; D is not in scope unless O3 says so |
| O4 | K400 always attached? | open | Task 13 (final copy) |
| O5 | end-to-end on a deliberate or a natural sign-out | open | Task 12 |
| O7 | when to send the Radio Console request | open | Task 14 |
| O8 | handover window H while the actuator is armed | open; recommended **never** until #88's frequency is measured | Task 4's `actuator-declined` row (off by default) |
| **G88** | #88's actuator honours the stand-down (spec §5.1) **and** triggers on `SignedOut` | requested in Task 2 | the `actuator-declined` row; Task 16 |

### 1.3 Branching

Preparatory docs: `docs/gv-reachable-reauth`. Implementation: **`feat/gv-reachable-reauth`** from `main`. It does
**not** wait for #88 to merge: the assist is fully useful with the actuator absent, which is today's state. The
contract pins (Task 8) and the race acceptance (Task 16) are placed so that whichever of the two PRs merges
second completes them.

---

## 2. Task list

### Phase 0: record and coordinate

#### Task 1: Record decisions; ask the open ones · lane **O**

Write O1, O2 and O6 as decided in the implementation PR body. Ask O3, O4, O5, O7 and O8 as the spec §9 frames them.
O3 and O4 are open questions, not option lists.

**Acceptance:** each answer is recorded in the owner's words; unanswered items are listed as **open**, not
defaulted, except O8, whose recommended default ("never") is the safe one and is recorded as the default.

#### Task 2: The two asks to #88 · lane **C**

Through the coordinator, to the #88 builder. Neither is this work's to implement.

1. **Stand-down.** The actuator reads `~/.local/state/gv-reauth-assist.state` **as data** (whitelisted `STATE`
   key, never sourced) and refuses an attempt while `STATE` is `PREPARED` or `SIGNED_IN_UNCONFIRMED`, journaling
   *"a human sign-in is in progress"*. This is not a trip, and no budget is spent. It needs a harness case in #88.
2. **Trigger.** The actuator acts on `SignedOut` (PR #90) as well as `Stale`.
3. **FYI:** the assist takes `~/.local/state/gv-auto-relogin.lock` with `flock -n`, for seconds, on its own
   descriptor. The assist reads (never writes) `gv-auto-relogin.state`. It relies on these names:
   `BREAKER_STATE`, `BREAKER_REASON`, `BREAKER_REASON_TEXT`, `BREAKER_TRIPPED_AT`, `BREAKER_LAST_ATTEMPT_AT`, the
   lock path, `~/bin/gv-auto-relogin.sh`, `gv-auto-relogin.timer`. Tell us before renaming any of them.
4. **Open for #88:** whether `browser_signed_out` should be deferred or reworded while an armed actuator is about
   to act (spec §5.6).

**Acceptance:** the #88 builder's reply, naming what they checked, is linked in the PR body. **G88** is met only
when items 1 and 2 are merged on `main` **and** a harness case in #88 exercises item 1. A reply saying "will do"
is not G88.

#### Task 3: One home for `gv-cdp.py` · lane **C** + **L**

The tool lives on #88 at `deploy/tools/gv-cdp.py` and does not ship. The assist needs it shipped, with four more
subcommands: `href` (the literal `window.location.href`), `new --url`, `activate --target`, `close --target`.

- If #88 has already moved it to `deploy/gv-cdp.py` and merged, add the subcommands there.
- Otherwise, this branch carries it to `deploy/gv-cdp.py` with provenance in the commit message
  (`carried from feat/gv-auto-relogin @ <sha>`), in an unchanged first commit, then adds the subcommands. #88
  rebases onto that. **One file, one home**; agree which PR moves it in Task 2's thread.
- The additions are **additive**: nothing removed that #88's driver may use.

**Acceptance (L), in `repro-gv-cdp.sh`:**

- `href` reports the target's **own** location, not `/json/list`'s cached URL. Case: an in-page
  `location.replace`. If a fast local Chrome cannot show the lag, the case is `SKIPPED-LOUDLY` and the run is
  non-zero.
- `new`/`close` change the page-target count +1/−1.
- `activate`: the harness's own CDP reading of `document.visibilityState` flips. If headless reports `visible`
  for both, the case is `SKIPPED-LOUDLY` and moves to Task 11.
- #88's existing static cases still pass.

---

### Phase 1: the assist

#### Task 4: `deploy/gv-reauth-assist.sh`, the state machine and the precedence · lane **L**

**Depends on:** Task 3.

Build per spec §5.1–§5.7.

**Mechanics:**
- `set -uo pipefail`; `LC_ALL=C.UTF-8`.
- Its own `flock -n` on `~/.local/state/gv-reauth-assist.lock`, taken **first**.
- Every URL, host, port, path and unit name is overridable by env, so the harness can point it at stubs.

**Precedence** (spec §5.1 table), as one function with one output line, `ACT <mode>` or `WAIT <why>`:
- **Actuator presence:** `[ -x ~/bin/gv-auto-relogin.sh ]` and `systemctl --user is-enabled gv-auto-relogin.timer`.
- **Breaker:** read line by line with a whitelist, decoding `printf %q` escapes **without** `eval`/`source`.
  Missing or unreadable reads as TRIPPED, which is the breaker's own rule.
- **`actuator-declined`:** gated on `GV_REAUTH_HANDOVER_MINUTES`, **unset by default**, so the row is off (O8), and
  only honoured once G88 is met. That is a config flag the installer sets, not a runtime guess.

**Actuator lock:** `flock -n` on `~/.local/state/gv-auto-relogin.lock` (a **different fd** from the assist's
lock) around every CDP action and the refresh POST. If it is held, log and skip the tick.

**States:** `IDLE`, `PREPARED`, `SIGNED_IN_UNCONFIRMED`, `CONFIRMED`, `NOT_SIGNED_OUT`, `PREPARE_FAILED`,
`CONFIRM_REFUSED`, `CONFIRM_FAILED`.

**State file:** `KEY=value`, no shell quoting, one key per line, values capped at 600 characters, written
atomically. Keys: `STATE`, `STATE_SINCE`, `FALLBACK_MODE`, `PREPARED_TARGET`, `PREPARED_OPENED_BY_US`,
`LANDED_URL`, `VALIDATED_BEFORE`, `VALIDATED_AFTER`, `REFRESH_HTTP`, `CONFIRM_TICKS`, `BREAKER_REASON_SEEN`,
`HUMAN_TEXT`, `HUMAN_ACTION`.

**Behaviour:**
- `HUMAN_ACTION` is ≤200 characters, checked before writing.
- On `CONFIRMED`, run `systemctl --user start gv-session-alarm.service`. A failure is logged loudly and does not
  undo `CONFIRMED`.
- `--status` (no CDP, no POST) and `--print-config` (no side effects).

**Harness `deploy/tests/repro-gv-reauth-assist.sh` (L).**

Fixtures:
- A headless throwaway Chrome.
- Stub hosts: *voice* (app, or a 302 to *workspace*), *signin* (chooser, or a 302 to *voice*), and status/refresh
  (extend `gv-alarm-status-stub.py`).
- A PATH shim for `systemctl` that records its argv and answers `is-enabled` per fixture.
- A fixture `~/bin/gv-auto-relogin.sh`.
- Fixture breaker state files.

The cases:

| Case | Setup | Must observe |
|---|---|---|
| P1 absent | `SignedOut`; no actuator | `PREPARED`, `FALLBACK_MODE=actuator-absent`; one new tab on *signin*, active |
| P2 disabled | `SignedOut`; actuator present, timer disabled | `PREPARED` (`actuator-absent`) |
| P3 tripped | `SignedOut`; enabled; breaker `TRIPPED credential_rejected` | `PREPARED` (`breaker-tripped`); `BREAKER_REASON_SEEN=credential_rejected` |
| P4 unreadable | `SignedOut`; enabled; breaker file garbage or missing | `PREPARED` (`breaker-tripped`) |
| P5 ⛔ **no race** | `SignedOut`; enabled; breaker `ARMED`; no handover set | **zero** connections recorded by a stub CDP listener over 3 ticks. Negative control: P1 on the same listener records ≥1 |
| P6 ⛔ **lock** | P1, but another process holds `gv-auto-relogin.lock` | zero CDP connections that tick; `PREPARED` on the tick after release |
| P7 declined | `ARMED`, `GV_REAUTH_HANDOVER_MINUTES=1`, `SignedOut` for 2 minutes | `PREPARED` (`actuator-declined`). The same without the variable: P5's result |
| P8 adopt | a target already on *signin* (the driver's leftovers) | no new tab; `PREPARED_OPENED_BY_US=0` |
| P9 ⛔ breaker injection | breaker file with `BREAKER_REASON_TEXT=$(touch $WORK/pwned)` and `printf() { :; }` | `$WORK/pwned` absent; the text surfaces literally |
| R1 not signed out | *signin* 302s to *voice* | `NOT_SIGNED_OUT`; tab closed; `HUMAN_TEXT` says do not re-login |
| R2 healthy | `Succeeded` | zero CDP connections (as P5) |
| R3 confirm | from P1, flip *signin* to *voice*; refresh 200; `validatedAt` advances | `CONFIRMED`; `systemctl start gv-session-alarm.service` recorded |
| R4 ⛔ gate | as R3, but the forced navigation lands on *workspace* | not `CONFIRMED` |
| R5 ⛔ refused | as R3, refresh 502 | `CONFIRM_REFUSED`; no `systemctl` |
| R6 ⛔ timestamp | as R3, `validatedAt` unchanged | not `CONFIRMED` |
| R7 transient | refresh 503 ×5 | `CONFIRM_FAILED` after 5 |
| R8 recovered elsewhere | `PREPARED`, then `Succeeded` with no sign-in by the assist | `IDLE`; our tab closed; an adopted tab **not** closed |
| R9 tripped note | R3 with the breaker `TRIPPED` | `HUMAN_TEXT` in `CONFIRMED` says auto-relogin is still stopped and quotes the reason |
| R10 action length | a planted 250-character action | refused and logged; previous value kept |
| R11 static | assist script: no `Input.`, no `eval`/`dump`/`shot` invocation, no `gv-account.conf`, no driver name, no password vocabulary; **no write** to the breaker file path | all pass; each negative control (the token planted in a temp copy) fails |

#### Task 5: `deploy/gv-reauth-show.sh` · lane **L**

Activates `PREPARED_TARGET`. Exit codes: 0 activated, 3 nothing prepared, 4 target gone, 5 CDP unreachable, 2
usage. It takes the actuator lock like the assist does.

**Acceptance (L):** one case per code, each asserting the code **and** its effect. With the assist's state file
absent it must return 3, not 0.

#### Task 6: Units, installer, deploy wiring, drift group · lane **L**

- `gv-reauth-assist.service` (`Type=oneshot`, no `Restart=`, no `EnvironmentFile=`) and `.timer` (60 s).
- `install-gv-reauth-assist.sh` on the pattern of `install-gv-session-alarm.sh`:
  - atomic installs into `~/bin` of the assist, the show script and `gv-cdp.py` (unless #88's installer already
    owns `gv-cdp.py`; agree in Task 3);
  - timer installed but **not enabled** without `--enable`;
  - `--handover-minutes N` writes the O8 setting into the unit's drop-in, only if given.
- `Deploy-ToLinux.ps1`: ship `deploy/*.py` (today only `*.sh` ships, `:770`); run the installer; add
  `check-installed-drift.sh --group reauth`.
- ⛔ Nothing is added to `setup-gvbridge.sh` (alarm spec §8).

**Acceptance (L):**
- `repro-installed-drift.sh` gains a `reauth` case; its negative control (a flipped byte) reports `DIFFERS`.
- The atomic-install pattern holds for the new installer.
- The script's built ship list contains `gv-cdp.py`, read from the list the script builds.

---

### Phase 2: the alarm

#### Task 7: The alarm's assist track and copy · lane **L**

**Depends on:** Task 4. PR #90 is merged, so `browser_signed_out` and the thread-key retirement are present on
`main`.

In `deploy/gv-session-alarm.sh`:

1. Read the assist state **as data** (whitelist, no source/eval). If the file is absent, the session track is
   byte-identical to today.
2. The `browser_signed_out` and `browser_stale` alerts gain the assist's `HUMAN_TEXT`, and `action` becomes
   `HUMAN_ACTION`, when the assist is `PREPARED` at post time.
3. The assist track, persisted as `LAST_POSTED_ASSIST_STATE` plus `STATE_SINCE`, posts `warning` replies on a
   transition into `PREPARED` (unless the alert already carried it), `NOT_SIGNED_OUT`, `PREPARE_FAILED`,
   `CONFIRM_REFUSED` and `CONFIRM_FAILED`.
   - Title: `[rotaryphone] GV session — <what changed>`.
   - Replies go under `INCIDENT_THREAD_KEY`, with the root re-attempted first if needed.
   - Dedupe key: `rotaryphone-gv-assist-<STATE>-<INCIDENT_THREAD_KEY>`.
4. RESOLVED gains `CONFIRMED_TEXT` when the assist was `CONFIRMED` within the open incident.
5. Independent of #88's `relogin_unavailable` track if that has merged. Neither track reads the other's variables.

**Acceptance (L), in `repro-gv-session-alarm.sh`:** each case reads the gateway stub's received payloads.

| Case | Must observe |
|---|---|
| A1 absent | every existing case (including PR #90's) passes unchanged |
| A2 prepared-before-alert | one `browser_signed_out` alert, `action` == the planted `HUMAN_ACTION` |
| A3 prepared-after-alert | alert, then one `warning` reply, same `thread_key` |
| A4 no repeat | no further posts over two more polls |
| A5 ⛔ injection | the `$(…)`/`printf()` state file creates nothing and is delivered literally |
| A6 resolved with proof | RESOLVED contains `CONFIRMED_TEXT`, in the incident thread |
| A7 ⛔ resolved without proof | assist `IDLE`, status `Succeeded`: RESOLVED has no `CONFIRMED_TEXT` |
| A8 ⛔ independence | assist stuck in `PREPARE_FAILED`; session transitions still post. If #88's track is present, a breaker trip still posts too |
| A9 422 | a refused assist reply means no heartbeat and exit 1 |


#### Task 8: Contract drift guards · lane **U** + **L**

Beside `AlarmCopyDriftTests.cs`:

- **assist ↔ alarm:** the state-file path and every whitelisted key appear in both scripts.
- **assist ↔ #88:** `BREAKER_*` field names, the breaker state path, the lock path, `gv-auto-relogin.sh` and
  `gv-auto-relogin.timer` appear in both the assist and #88's breaker/actuator.
  - ⛔ **This half is added by whichever PR merges second.** If this work merges first, the Task 2 request asks
    #88 to add it. If #88 merges first, it is added here. It must not be written against a file that is not on
    `main`: a test that passes because its subject is absent cannot fail.
- **#88 → assist:** #88's stand-down reads the assist state path and `STATE` values; pinned the same way.

**Acceptance (U):** passes on the Windows SDK. Negative control: renaming one field in a temp copy fails the test.

---

### Phase 3: on the box

#### Task 9: Deploy; installed, not enabled · lane **D** then **B**

```bash
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group reauth --ship-dir /opt/rotary-phone/deploy
bash /opt/rotary-phone/deploy/check-installed-drift.sh --group alarm  --ship-dir /opt/rotary-phone/deploy
systemctl --user list-unit-files 'gv-reauth-assist.*'
systemctl --user list-timers 'gv-reauth-assist.*'     # empty: not enabled yet
~/bin/gv-reauth-assist.sh --status
```

**Acceptance (B):**
- Both drift groups match.
- `--status` reports its precedence verdict. Today the actuator is not installed, so expect `ACT actuator-absent`
  if signed out and `nothing` if healthy; the verdict must match the live `browserRefreshOutcome`.
- The alarm journals `heartbeat refreshed` within 10 minutes (`--since -10min -n 20`).

#### Task 10: Enable; prove it leaves a healthy browser alone · lane **B**

`install-gv-reauth-assist.sh --enable`, then 30 minutes of observation.

**Acceptance (B):**
- The page-target count, sampled every 5 minutes, **never changes**.
- Every tick journals "nothing to do".
- Any tab opened on a healthy session is a failure.

#### Task 11: Attended measurements · lane **A** · owner present · no sign-out needed

Record the page-target baseline first; it must be the same at the end.

| # | Measure | Records |
|---|---|---|
| M1 | `ServiceLogin` while **signed in**: `new`, `href`, `close` | the landing URL. If it is not `voice.google.com/…`, spec §5.3's `NOT_SIGNED_OUT` row changes before Task 12 |
| M2 | with M1's tab open, POST `refresh-from-browser` | 200 and `validatedAt` moved. Otherwise the new-tab rule is unsafe: stop |
| M3 | **path A**: a neutral tab `data:text/html,<input autofocus>`, activated; the owner taps Exit to Desktop | window visible without further action? The owner types on the K400? Seconds for `Radio Console` to return? Did music continue? |
| M4 | **path E**: from a laptop, `ssh -L 9224:…`, `chrome://inspect`, type into the neutral tab | the text read back via a one-off CDP HTTP call, not via the shipped tool |
| M5 | `gv-reauth-show.sh` over the running kiosk (C, and B's mechanism) | ⛔ only if the owner OKs covering the kiosk for seconds and Radio Console has not objected. Result photographed by the owner |
| M6 | D (RDP), only if O3 says it is used | yes/no |

**Acceptance (A):** results in §4, each **observed by the owner**. A path marked "no" or "not measured" is not
named in the copy.

#### Task 12: End to end, fallback mode · lane **A** · O5

Run with the actuator **absent or its timer disabled** (O5), so the run tests the fallback and not #88.
Deliberate sign-out or a natural one, per O5.

**Acceptance (A), each observed by the owner:**

1. The assist is `PREPARED` within 2 minutes of `SignedOut`, and the tab's own `href` is on the sign-in host.
2. The thread shows the `browser_signed_out` alert (plus a `warning` if needed) whose action names only the
   measured paths.
3. The owner signs in; nothing of ours sees the password.
4. RESOLVED with `CONFIRMED_TEXT` appears **within 3 minutes**, and `validatedAt` is after the sign-in.
   ⛔ A later cron-driven `Succeeded` is not a pass.

#### Task 13: Final copy from the measurements · lane **L** · O4

`HUMAN_ACTION`/`HUMAN_TEXT` name exactly the paths with a Task 11 "yes". If O4 says the keyboard is not always
attached, add "you will need a keyboard".

**Acceptance (L):** a readable table in the harness maps each path to its Task 11 result. The shipped action is
≤200 characters and names no path whose result is not "yes".

---

### Phase 4: the boundary, the docs, and the race on the box

#### Task 14: Deliver the Radio Console request · lane **O** · O7

After Task 11. Finalise the draft with M3/M5 data, commit, push, and write it into
`D:\prj\RTest\RTest\docs\queue\inbound\`. Add the boundary-doc Change Log row **before** Task 13's copy naming
their buttons ships.

**Acceptance:** the lane copy's `sha256sum` matches the pushed file. Their acknowledgement, naming what they
checked, is recorded. An unacknowledged request is recorded as such.

#### Task 15: Runbook and doc corrections · lane **L**

- `SETUP-GVBridge.md`: "When the GV session signs out". Auto-relogin is tried first when it is installed. What
  the Chat thread says when a human is needed and why (`FALLBACK_MODE`). The measured paths.
  `gv-reauth-show.sh`. That a human sign-in does **not** re-arm the breaker.
- `SETUP-AND-TESTING.md:145`: replace `wmctrl -a Chrome`, which is absent and blind to Wayland windows.
- `KNOWN-ISSUES.md`: an entry for the flow and its limits.

**Acceptance:** `grep -n wmctrl docs/` returns only explanatory lines. Every runbook path has a Task 11 "yes".

#### Task 16: Race acceptance with the actuator installed · lane **B** + **A** · after G88 and #88's install

**Depends on:** G88; #88 deployed and enabled (#88 plan Task 16/17).

**Acceptance:**
1. **(B) Armed and healthy:** over 30 minutes, the page-target count is unchanged and the assist's `--status` says
   `WAIT` or nothing.
2. **(A) #88's forced-failure run** (#88 plan Task 17, wrong-credential or challenge case) leaves the breaker
   `TRIPPED`. Within 2 minutes the assist is `PREPARED` in `breaker-tripped` mode, having **adopted** the driver's
   sign-in tab if one was left up. The thread shows #88's `relogin_unavailable` alert **and** the assist's
   "sign-in page ready", in the same incident thread.
3. **(A) Stand-down:** with the assist `PREPARED`, the owner runs `gv-auto-relogin.sh --reset`. On the actuator's
   next tick its journal shows *"a human sign-in is in progress"*, and its budget counters are unchanged
   (`--status` before and after).
4. **(A)** The owner signs in. RESOLVED carries `CONFIRMED_TEXT` and states the breaker's current state.

---

## 3. Deliberately not in this plan

| Not here | Where |
|---|---|
| The sign-in driver, breaker, actuator, credential file, `relogin_unavailable` track | PR #88 |
| The rsync `--delete` hazard to `/opt/rotary-phone` files | recorded on #88 at the edit site (`c1749f5`) |
| `browser_signed_out` timing and wording while an armed actuator is about to act | open for #88 (Task 2, item 4) |
| `gv-bridge-ensure.sh` exit codes | alarm spec §8; open with Radio Console |
| Closing leftover tabs the assist did not open | deferred (spec §5.7) |

---

## 4. Measurement results (filled in by Task 11)

| # | Result | Observed by | Date |
|---|---|---|---|
| M1 | | | |
| M2 | | | |
| M3 | | | |
| M4 | | | |
| M5 | | | |
| M6 | | | |
