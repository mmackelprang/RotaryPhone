# Plan — GV session alarm: transport an existing, already-correct signal to a human

**Spec:** [`../superpowers/specs/2026-09-09-gv-session-alarm-design.md`](../superpowers/specs/2026-09-09-gv-session-alarm-design.md) —
read it first. Its §3 "Verified foundations" table and its §2 non-goals are settled and are **not** re-opened here.
**Date:** 2026-09-09. **Status:** planned, not started.
**Branch:** `feat/gv-session-alarm`.

> **There is no work queue in this repo.** `docs/BUILDER_QUEUE.md` and `docs/ROADMAP.md` do not exist and
> are not being created. This plan file **is** the handoff artefact: whoever builds this executes the tasks
> below in order. Nothing needs to be added anywhere else.

⛔ **Phase 1 is TRANSPORT, not detection.** Two correct detectors already exist — the `[ERR] GVApi: REJECTED`
log line (`GVApiAdapter.cs:899-902`) and `browserSessionStale` (`:255`). No task below invents a third, and no
task below writes new alert copy: the alarm **quotes the service's own strings verbatim**, and Task 12 is a
test that fails if the two copies ever drift apart.

---

## 0. What changed between the spec and this plan

Seven findings. **Five of them contradict the spec, this plan's own dispatch, or a mid-flight correction**;
two confirm something worth having checked. They are stated first because the phase structure below is
shaped by them, and because two of them move work out of this plan entirely.

### 0.1 ✅ CONFIRMED INDEPENDENTLY — the atomic-install work has **not** landed

Measured here before the correction arrived, by asking git rather than by reading the spec:

```
$ git merge-base --is-ancestor e6f8018 main
NO — not on main

$ git branch -a --contains e6f8018
  fix/deploy-honest-status
  remotes/origin/fix/deploy-honest-status
```

`e6f8018 fix(deploy): install gv-bridge tooling atomically and gate the session-killing flag` is on the
**open PR #84 branch**, not on `main`. `main`'s `deploy/setup-gvbridge.sh:89` still reads
`install -m "$mode" "$src" "$dest"` — the non-atomic form. The `install_atomic()` helper does not exist on
`main` at all.

⭐ **Keep the reason this matters, not just the fact.** The false claim was written *inside the spec whose
§6 catalogues this exact failure*, from memory of work done the same morning in the same repo. It is
instance 8 of "verified that a mechanism ran, inferred that it worked" — and it is the one-line argument
for the rule every acceptance criterion below obeys: **read the installed artefact. Not a repo file, not a
branch name, and not an author's memory of this morning.**

### 0.2 ⛔ THE SPEC CONTRADICTS ITSELF — §7 and §8 cannot both be satisfied, so §7 takes its second option

This is the most consequential finding and it changes Phase 0's shape.

- **§7** says resolve the install path, and offers two options: *"have the deploy invoke
  `setup-gvbridge.sh`"* **or** *"install the alarm through a separate, explicitly-deployed unit"*.
- **§8** says: *"Deploying the shipped script would add `flock`, introducing a **third** state — someone
  else holds the lock — that also arrives as exit 0. … **Fixing the exit code is therefore a prerequisite
  for deploying the shipped `gv-bridge-ensure.sh` at all.**"*
- **The dispatch for this plan** says the exit-code change is **flag-only, do not implement** — it is
  cross-boundary and must be announced in the boundary doc's Change Log first.

`setup-gvbridge.sh:131` installs `gv-bridge-ensure.sh`. So **§7 option one ships the very file §8 forbids
shipping**, using a fix §8 requires and this plan is forbidden to make. Verified rather than assumed —
`flock` is present in the shipped copy and absent from the installed one:

```
$ grep -n "flock\|exit 0" deploy/gv-bridge-ensure.sh        # SHIPPED, on main
42:LOCK="${GV_BRIDGE_LOCK:-${PROFILE}.lock}"
48:if command -v flock >/dev/null 2>&1 && : >>"${LOCK}" 2>/dev/null; then
50:  flock -n 9 || exit 0          <-- the third outcome, arriving as exit 0
55:  exit 0
```

**Decision taken: §7 option two — a separate, narrow, explicitly-deployed installer.**
`deploy/install-gv-session-alarm.sh` installs the alarm's script and units and **nothing else**. Four
reasons, in order of weight:

1. It is the only option that does not require the out-of-scope, cross-boundary exit-code change first.
2. It does not depend on PR #84. Option one needs `install_atomic()`, which is on #84 (§0.1), so option one
   is **blocked on an unmerged PR**; option two carries its own four-line atomic install and is blocked on
   nothing.
3. Blast radius. `setup-gvbridge.sh` also re-applies the autostart entry, the desktop shortcut and
   `systemctl --user enable --now gv-bridge-watchdog.timer`. Re-applying all of that to install one alarm
   is a wide surface for a narrow change — the concern the deploy plan's own scope doc raises at
   `deploy-tooling-honest-deploy.md:86-92`.
4. It leaves §7's *general* problem exactly where it already lives: PR #84 Task 10, unstarted, with the box
   as its lane. This plan does not adopt that work; Task 3 makes it **visible on every deploy** instead.

⚠ **What this decision gives up, stated so it is not lost:** `~/bin/gv-bridge-ensure.sh` stays three weeks
stale after this arc ships. That is unchanged by this plan and is not this plan's defect to fix — but it is
now *reported* rather than silent, which is the whole of Task 3.

### 0.3 ⛔ THE DISPATCH'S TIMER-STOP INSTRUCTION DOES NOT APPLY — and the lesson underneath it does

The dispatch says: *"`setup-gvbridge.sh` rewrites scripts the 2-minute watchdog may be mid-execution on …
the timer should be stopped across the install and restarted after."*

**That is correct for `setup-gvbridge.sh` and irrelevant to the chosen path.** The narrow installer touches
no file `gv-bridge-watchdog.timer` executes, so there is nothing to stop. Adding a stop/start around it
would be ceremony that buys nothing and introduces a real failure — a deploy that aborts between the stop
and the start leaves the bridge with **no** watchdog.

⭐ **The transferable half is still live, against a different timer.** `gv-session-alarm.timer` fires every
5 minutes on `~/bin/gv-session-alarm.sh`, so the alarm's own installer has exactly the race
`deploy-tooling-honest-deploy-plan.md:757-830` measured: `install -m` unlinks the destination and creates a
new inode, and **between the unlink and the end of the copy the path does not exist**. A timer firing in
that window gets `ENOENT`; one firing at the tail could exec a partial file at mode 0600 — which does not
crash, it **stops early**, and every line after the cut silently does not exist. Task 2 therefore uses
install-then-`mv`, and says so at the call site.

### 0.4 ⛔ ADDING THE DTO FIELD BREAKS A DELIBERATE TEST — this is a two-file change, not a one-file change

`GVBridgeControllerTests.cs:110-160` pins the **exact field names and their order** as a cross-repo contract:

```csharp
Assert.Equal(
  new[] { "available", "activeMode", …, "browserSessionAgeSeconds", "browserSessionStale", },
  names);
```

Appending `browserRefreshOutcome` fails this test until the array is updated in the same commit. That is
the pin doing its job — it is the reason a field cannot be added to this payload by accident. Task 4 updates
both, and the ordering is not free: the new field goes **last**, after `browserSessionStale`, so no existing
name changes position.

### 0.5 ⚠ THE ALERT COPY WILL LIVE IN TWO LANGUAGES — so it gets a drift guard

"Quote the service's own wording" means the exact sentences at `GVApiAdapter.cs:899-902`, `:1226-1230`,
`:1234-1240` and `:1255-1260` are reproduced in a **shell** script. That is a second copy of a string, in a
different language, with nothing connecting them — the classic way a quotation silently stops being a
quotation. Task 12 adds a test that greps the C# for each sentence the shell quotes and fails on a
mismatch, so the divergence is caught at `dotnet test` rather than in a delivered message that
misattributes the service.

### 0.6 ✅ OPEN DECISION #5 IS ALREADY WRITTEN — do not write it twice

Spec §11 decision 5 assigns the `KNOWN-ISSUES.md` cron correction to *"this arc."* It is **already done**,
on PR #84: commit `2c6797b docs(known-issues): DO NOT retire the cron — it is load-bearing and no longer
the hazard`, +32 lines, additive annotation. Writing a second correction would produce two annotations of
the same entry saying the same thing. Task 19 is therefore a **verification after #84 merges**, not an
authoring task.

### 0.7 ⚠ THE "Cannot unlink" LINE — it does reproduce on `main`, and it is already fixed elsewhere

The mid-flight correction said this claim *"does NOT cleanly match main's code: on main this path would
FAIL rather than warn."* **Checked, and it is the other way round.** `Deploy-ToLinux.ps1:124-130` on `main`:

```powershell
"set -e -o pipefail`n" +
"tar -C '$publishMsys' --exclude=./.playwright -czf - . | ssh '$SshTarget' '" +
  "cp -f $TargetPath/appsettings.Production.json /tmp/rp-prod.bak 2>/dev/null || true; " +
  "tar -xzf - --unlink-first -C $TargetPath; " +
  "[ -f /tmp/rp-prod.bak ] && mv -f /tmp/rp-prod.bak $TargetPath/appsettings.Production.json || true; " +
  "chmod +x $TargetPath/RotaryPhoneController.Server'`n"
```

The remote chain is `;`-separated and **ends in `chmod`**, so `ssh` reports `chmod`'s status — `0` — while
the remote `tar` has exited 2. `set -e -o pipefail` is in the *local* script and cannot see it. So the run
prints `tar: .: Cannot unlink: Invalid argument` **and then succeeds**. That is not inference: PR #84's plan
recorded it from a live deploy on the box (`deploy-tooling-honest-deploy-plan.md:52-69`), and the three
facts there — sha unchanged, mtime moved, backup consumed — prove the sequence rather than assuming it.

**Two qualifiers that shrink it, both of which stand:** it only reaches this path when `rsync` is missing
(it was missing from the owner's PowerShell `PATH`, which is why every deploy took it, and the owner is
installing rsync), and **the fix is already built on PR #84** — the files-only archive (`find … -type f`)
has no `./` member for `--unlink-first` to fail on.

⛔ **No task is scoped for it here.** It reproduces, it is real, and the remedy is merging #84. Writing a
second fix for it in this plan would be duplicate work on an in-flight PR. Recorded, not adopted.

### 0.8 ⛔ A TIMER INSTALLED BEFORE THE TOKEN EXISTS FAILS EVERY 5 MINUTES

Spec §5.1 is emphatic that a missing token must be a **loud, non-zero** failure — *"silence must not be a
valid state."* Correct, and it has a scheduling consequence the spec does not draw: owner decision #1 (the
`rotaryphone`-scoped gateway token) is **not yet done**, so a timer enabled at install time would fail
loudly 288 times a day until it is.

An alarm that cries wolf before it has ever worked is the failure mode §5.2 warns about — *"the alarm gets
muted by the human, which is the worst outcome of all"* — arrived at from the install side instead of the
grace side.

**Therefore: the installer installs the units and does `daemon-reload`, but does NOT enable the timer.**
Enabling is an explicit, separate step (Task 16) taken once `~/.rotaryphone-env` exists. This also splits
the acceptance check in two, and both halves are honest:

| Check | Command | Phase |
|---|---|---|
| **installed** | `systemctl --user list-unit-files 'gv-session-alarm.*'` and `list-timers --all` | Task 5, no token |
| **enabled and firing** | `systemctl --user list-timers 'gv-session-alarm.*'` (no `--all`) | Task 16, token-gated |

⚠ Spec acceptance 8 says "verified via `~/bin` and `systemctl --user list-timers`". Plain `list-timers`
lists only *active* timers, so it would report nothing for an installed-but-disabled unit and read as a
failure. `--all` is the correct instrument for the installed check. This is a refinement of the spec's
acceptance, not a relaxation of it — both halves are still observed on the box.

### 0.9 ⛔ `main` HAS A LIVE SILENT-STALE-DEPLOY PATH — which changes what Task 3's check may compare

Measured on `main`, and it is the same mechanism as §0.7 seen from the other end:

| Line | What it does | What it reports |
|---|---|---|
| `:125` | `set -e -o pipefail` — in the **local** script | governs the local pipeline only |
| `:126-130` | remote chain, `;`-separated, **no `set -e`**, ends in `chmod` | `ssh` returns **chmod's** status |
| `:135` | `$syncExit = $LASTEXITCODE` | reads `0` |
| `:137` | `throw "… aborting (service NOT restarted…)"` | **never fires** |

A remote `tar` failure — including the `./` + `--unlink-first` exit-2 form measured in this repo — is
invisible. `chmod` succeeds regardless, `ssh` returns 0, `pipefail` sees success, the throw at `:137` does
not fire, and step `[4/4]` restarts the service on a tree that may not have been updated.

⚠ And `:113-114` claims the opposite, in a comment: *"`$LASTEXITCODE` is checked so a failed sync ABORTS
the deploy instead of restarting the service on the OLD binary (the silent-stale-deploy bug this
replaces)."* The check is real, it runs, and it is **structurally incapable of detecting what it was
written to detect.** PR #84 runs the remote chain under its own `set -e`, which is the actual fix.

⛔ **This lands directly on Task 3, and it is the reason Task 3 is designed the way it is.** This arc's
whole §7 problem is *"the deploy reports success while `~/bin` stays stale."* Finding §0.9 says the deploy
can **also** report success while `/opt/rotary-phone/deploy` stays stale. A staleness banner that compares
the **installed** copy to the **shipped** copy would then be comparing two stale things and printing
reassurance — a check that runs, passes, and answers the wrong question.

**Therefore Task 3 checks the whole three-link chain, not one link of it**, and the repo end of the chain
is computed **on the deploying machine** and carried to the box as an expected digest. The links are
exactly the boundary doc's own: *merged ≠ deployed ≠ INSTALLED*
(`docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md:619`).

⚠ **Assumption stated out loud, as required:** the narrow installer in Task 2 assumes the deploy actually
transferred `deploy/*.sh` and `deploy/systemd/*` to the box. On `main` that assumption is **not** reliably
true. Its precondition is **PR #84**. Task 3 exists so that when the assumption fails, the deploy says so
rather than the alarm quietly never being installed — which would be this arc reproducing, in its own
installer, the exact failure it was built to detect.

### 0.10 📌 Recorded, not scoped — the transport is selected by a check that answers a different question

`Deploy-ToLinux.ps1:84` chooses the sync path with `Get-Command rsync`. rsync has apparently never been on
this workstation's `PATH`, so **the tar path is the one that has always run**; if rsync ever appears by any
route, the deploy silently switches to a branch that has never executed here. Ours is safer than the
sibling project's — the Windows→msys path conversion is done (`:87`) and an rsync failure falls back to tar
(`:96-100`) rather than aborting — so this is a latent hazard, not a live defect. **No task. Recorded so it
is not unremarked.**

⭐ **The framing worth keeping, because three of the findings above are instances of it.** Alongside
"verified that a mechanism ran, inferred that it worked" there is a fourth neighbour: **a check that ran,
passed, and answered a different question than the one being asked.**

| The check | What it truthfully reported | What it was read as |
|---|---|---|
| `Get-Command rsync` | rsync **exists** | "rsync works here" |
| `$LASTEXITCODE` at `:135` | **chmod** succeeded | "the sync succeeded" |
| a shipped-vs-installed hash compare | the two copies **match** | "the box has the current file" |

The third row is the one this plan could have written itself. Task 3 is built to avoid it.

---

## 1. How anything gets verified

⛔ **The single rule, from spec §5.3 and §6:** *every acceptance check reads the **installed** artefact —
`~/bin/…`, `systemctl --user …`, a delivered message. **A task that asserts against a repo file is a
defect, not a task.*** The three exceptions are unit tests, whose subject genuinely *is* the repo source
(Tasks 4, 12, 14), and they are marked as such.

And from §6: **no criterion may be satisfied by observing that a component ran.** Each one below names an
*outcome* and the observation that confirms it.

| Lane | Where | What it can prove | Needs the token? |
|---|---|---|---|
| **L** — local, no box | WSL / any Linux, `deploy/tests/` | the alarm's whole decision tree, the gateway traps, truncation, transition-only posting, heartbeat suppression — against a **stub gateway** | no |
| **U** — unit tests | `dotnet test` | the DTO shape and order pin, the copy-drift guard, the profile-scoped kill predicate | no |
| **B** — box, no token | `radio` over `ssh-mcp` | installed-vs-shipped state, `systemctl --user list-unit-files`, `gv-login` against a live bridge, the age baseline | no |
| **T** — box **and** token | `radio`, after owner decision #1 | **delivered messages**, thread continuity, the live 422, the dead-man firing | **yes** |

### 1.1 Token gating — what is blocked and what is not

Owner decision #1 (a `rotaryphone`-scoped gateway token in `~/.rotaryphone-env` on the box) is **not yet
done**. It blocks live end-to-end delivery and nothing else. The sequencing below is built so that
**16 of 19 tasks complete and verify without it.**

| Token-gated (lane T) | Not gated |
|---|---|
| **Task 16** — install the env file, enable the timer | Tasks 1–15, 17–19 |
| **Task 17** — the six forced-failure acceptance runs | |

⭐ **Everything the gateway does is exercised first in lane L against a stub** that reproduces the measured
traps of §4.4 — the 200-character `action` cap, the `info` drop, case-sensitive `grace`. So Task 17 is
confirming behaviour that has already been proven, on the real gateway, rather than discovering it there.
The stub is not a substitute for Task 17; it is what makes Task 17 short.

### 1.2 Branching

Per repo policy, implementation happens on `feat/gv-session-alarm` and merges via PR. **This plan and the
spec are preparatory and live on `main`.** No task below commits anything until the branch exists.

---

## 2. Task list

Dependency order. **Phases 0–3 are entirely box-free except Tasks 1 and 3b.** Phase 5 is the only
token-gated phase.

### Phase 0 — the install path, and making its failure visible

---

#### Task 1 — Baseline the install path on the box, before changing anything · lane **B**

**No code.** Read-only. Its output is the "before" half of every later comparison, and it settles whether
§0.1's repo-side finding is also true of the box.

Run over `ssh-mcp` (`mmack@radio`) and record the output verbatim in the PR body:

```bash
# 1. What is INSTALLED, and how old is it?
ls -l --time-style=long-iso ~/bin/gv-bridge-ensure.sh ~/bin/gv-bridge-restart.sh
sha256sum ~/bin/gv-bridge-ensure.sh

# 2. What was SHIPPED to /opt — the middle link of the chain (§0.9)
ls -l --time-style=long-iso /opt/rotary-phone/deploy/
sha256sum /opt/rotary-phone/deploy/gv-bridge-ensure.sh

# 3. Do the alarm's names collide with anything already there?
ls -l ~/bin/gv-session-alarm.sh 2>&1
ls -l ~/.config/systemd/user/gv-session-alarm.* 2>&1
ls -l ~/.rotaryphone-env 2>&1

# 4. Timer state — the watchdog we must NOT disturb, and the absence of ours
systemctl --user list-timers --all 'gv-*'

# 5. Will a user timer run when nobody is logged in? (a silent-death mode for the alarm)
loginctl show-user "$(id -un)" -p Linger

# 6. Does the running build already serve the field we are about to add?
curl -s http://localhost:5004/api/gvbridge/status | python3 -m json.tool
```

**Acceptance** — this task passes when all six answers are *recorded*, and it **fails** if any command
cannot be run, rather than being skipped:

- The two sha256 values in steps 1 and 2 are written down. ⚠ If they **differ**, §7's staleness is confirmed
  live and Task 3's warning path has a real subject to demonstrate against. If they **match**, that is a
  finding too — it means someone has run `setup-gvbridge.sh` by hand since Aug 18, and Task 3's warning
  must then be demonstrated by a deliberately-injected difference rather than by the ambient one.
- Step 3 returns "No such file" for all three. If `~/.rotaryphone-env` already exists, **stop and ask the
  owner** — decision #1 may have been taken without this plan knowing, which changes the token gating.
- Step 5: if `Linger=no`, record it. It is not a blocker (the box runs a graphical session), but it is a
  mode in which the alarm dies silently, and the dead-man in Task 11 is what covers it.
- Step 6: record whether `browserRefreshOutcome` is present. It must be **absent** — its presence would
  mean the box is running something other than `main`.

⛔ **Do not run `setup-gvbridge.sh`, do not deploy, do not restart anything in this task.**

---

#### Task 2 — A narrow, alarm-only installer · lane **L**

**Depends on:** nothing. **Decision:** spec §7 option two — see §0.2 for why option one is unavailable.

Create `deploy/install-gv-session-alarm.sh`, mode 755:

```bash
#!/usr/bin/env bash
# =============================================================================
# Install ONLY the GV session alarm: ~/bin/gv-session-alarm.sh and its two
# systemd user units. Run from the deploy, and safe to run by hand.
#
#   bash /opt/rotary-phone/deploy/install-gv-session-alarm.sh
#   bash /opt/rotary-phone/deploy/install-gv-session-alarm.sh --enable
#
# WHY THIS EXISTS RATHER THAN A LINE IN setup-gvbridge.sh
# ------------------------------------------------------
# setup-gvbridge.sh also installs gv-bridge-ensure.sh. The shipped copy of that
# script adds `flock` with `flock -n 9 || exit 0`, which introduces a THIRD
# outcome that arrives as exit 0. Radio Console's KIOSK-2 launcher invokes it
# and reads that exit code to tell "already up" from "just launched" — a
# contract that already cannot express two states must not silently acquire a
# third. Fixing that exit code is cross-boundary, must be announced in the
# boundary doc's Change Log first, and is NOT in this arc. So the alarm does not
# travel on that script's install path.
# See docs/superpowers/specs/2026-09-09-gv-session-alarm-design.md §7 and §8.
#
# It is also narrow on purpose: setup-gvbridge.sh re-applies the autostart
# entry, the desktop shortcut and `enable --now` on the watchdog timer. None of
# that should happen because an alarm needed installing.
#
# WHY THE TIMER IS NOT ENABLED BY DEFAULT
# ---------------------------------------
# The alarm FAILS LOUDLY and non-zero when ~/.rotaryphone-env is missing — that
# is deliberate (spec §5.1: silence must not be a valid state). Enabling the
# timer before the token exists would therefore fail 288 times a day until it
# does, and an alarm that cries wolf before it has ever worked is an alarm that
# gets muted. --enable is a separate, deliberate step.
# =============================================================================
set -euo pipefail

ENABLE_TIMER=0
for arg in "$@"; do
    case "$arg" in
        --enable) ENABLE_TIMER=1 ;;
        -h|--help) sed -n '2,36p' "$0"; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/bin"
SYSTEMD_USER_DIR="${HOME}/.config/systemd/user"
STATE_DIR="${HOME}/.local/state"

log()  { echo "[gv-session-alarm] $1"; }
fail() { echo "[gv-session-alarm] ERROR: $1" >&2; exit 1; }

# Replace a destination by ATOMIC RENAME. Never write to the live path.
#
# `install -m` gets the MODE right (this box runs umask 0002) but NOT the
# replacement: strace shows it doing unlink(dest) then open(dest, O_CREAT|O_EXCL,
# 0600), so between the unlink and the end of the copy THE PATH DOES NOT EXIST,
# and at the tail of that window it exists but is partial at mode 0600.
#
# gv-session-alarm.timer fires on ~/bin/gv-session-alarm.sh every 5 minutes.
# There is no quiet window to install in. A firing inside that window gets
# ENOENT; one at the tail could exec a truncated script — which does not crash,
# it STOPS EARLY, and every line after the cut silently does not exist.
#
# rename(2) is atomic within a filesystem. "${dest}.new" is a SIBLING of
# "${dest}" on purpose, so the rename can never degrade into a cross-device copy.
#
# NOTE: do not "verify" a replacement by comparing inode numbers. Measured
# 2026-09-09 in this repo: the freed inode was immediately REUSED.
#
# ⚠ This duplicates install_atomic() from deploy/setup-gvbridge.sh as it exists
# on PR #84 (commit e6f8018). It is duplicated rather than shared because #84 is
# UNMERGED — sourcing a helper that is not on main would make this installer
# depend on an open PR. Converge the two once #84 lands.
install_atomic() {
    local src="$1" dest="$2" mode="$3"
    if ! install -m "$mode" "$src" "${dest}.new"; then rm -f "${dest}.new"; return 1; fi
    if ! mv -f "${dest}.new" "$dest";            then rm -f "${dest}.new"; return 1; fi
}

# One rolling backup, not one per run — the deploy runs this every time, and
# accrued .bak-<stamp> files in ~/bin are litter, not protection.
backup_if_changed() {
    local src="$1" dest="$2"
    if [ -f "$dest" ] && ! cmp -s "$src" "$dest"; then
        cp -p "$dest" "${dest}.bak"
        log "Backed up existing $(basename "$dest") -> $(basename "$dest").bak"
    fi
}

install_one() {
    local src="$1" dest="$2" mode="$3"
    [ -f "$src" ] || fail "missing ${src} — the deploy did not ship it. NOT installing a partial alarm."
    backup_if_changed "$src" "$dest"
    install_atomic "$src" "$dest" "$mode" || fail "could not install ${dest}"
    log "installed ${dest} (mode ${mode})"
}

mkdir -p "$BIN_DIR" "$SYSTEMD_USER_DIR" "$STATE_DIR"

install_one "${DEPLOY_DIR}/gv-session-alarm.sh"                  "${BIN_DIR}/gv-session-alarm.sh"                  755
install_one "${DEPLOY_DIR}/systemd/gv-session-alarm.service"     "${SYSTEMD_USER_DIR}/gv-session-alarm.service"     644
install_one "${DEPLOY_DIR}/systemd/gv-session-alarm.timer"       "${SYSTEMD_USER_DIR}/gv-session-alarm.timer"       644

systemctl --user daemon-reload

if [ "$ENABLE_TIMER" -eq 1 ]; then
    if [ ! -r "${HOME}/.rotaryphone-env" ]; then
        fail "refusing --enable: ${HOME}/.rotaryphone-env is missing. The alarm exits non-zero without it by design, so enabling now would fail every 5 minutes. Create the env file first."
    fi
    systemctl --user enable --now gv-session-alarm.timer
    log "timer ENABLED — next run:"
    systemctl --user list-timers 'gv-session-alarm.*' --no-pager
else
    log "timer INSTALLED but NOT enabled (no token yet — see the header)."
    log "Enable it with: bash ${DEPLOY_DIR}/install-gv-session-alarm.sh --enable"
fi

# Self-report, so the deploy's gate can ask the INSTALLED thing what it is
# rather than checking what a file contains. A checksum cannot catch a bad mode,
# a partial copy, or the right name over the wrong file.
log "installed state:"
"${BIN_DIR}/gv-session-alarm.sh" --print-config
```

**Acceptance** — run entirely locally with a fake `HOME`, no box:

```bash
export HOME="$(mktemp -d)"; mkdir -p "$HOME"
bash deploy/install-gv-session-alarm.sh                      # from a checkout with deploy/ populated
test -x "$HOME/bin/gv-session-alarm.sh"                      # installed, executable
test -f "$HOME/.config/systemd/user/gv-session-alarm.timer"
! systemctl --user is-enabled gv-session-alarm.timer         # NOT enabled
bash deploy/install-gv-session-alarm.sh --enable; echo "exit=$?"   # expect exit=1, refusing
```

- The three files exist at the stated modes (`755`, `644`, `644`).
- `--enable` **refuses with a non-zero exit** while `~/.rotaryphone-env` is absent, and says why.
- Running the installer twice leaves exactly one `.bak` per changed file, never a `.bak-<stamp>` series.
- ⛔ **No `${dest}.new` file survives any run**, including a run made to fail by pointing `BIN_DIR` at a
  read-only directory. Debris at mode 0600 in `~/bin` is the failure this pattern exists to avoid.
- `systemctl --user daemon-reload` is tolerated failing on a machine with no user bus (a CI container);
  the file installation is what this task asserts.

---

#### Task 3 — The staleness trigger: put a tier-1 fact where it fires · lane **L** + **B**

**Depends on:** Task 2 (for the alarm's file names). **Motivating principle, and it is the point of the
task:**

> A document is **read** when someone opens it. A rule is **consumed** when someone **acts**.
> Anything that must fire at action-time cannot live only in a document.
>
> | When the fact must fire | Where it belongs |
> |---|---|
> | when someone **runs** something | in the thing they run — a printed line, a failing check |
> | when someone **edits** something | a comment at the edit site |
> | when someone **reasons** | a document, and accept it will sometimes be missed |

Spec §7 — *the deploy never runs `setup-gvbridge.sh`, so `~/bin` stays stale while the deploy reports
success* — **was already documented**, in `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` under
*"Merged ≠ deployed ≠ INSTALLED"* (`:619`), with a table and citations. Both sessions rediscovered it today
by expensive independent measurement. It was a **tier-1 fact filed in tier 3**. That is instance 7 in the
spec's §6 list, and this task is the correction: the deploy carries the trigger itself.

⛔ **HARD CONSTRAINT — tier 1 has a failure mode worse than tier 3, and this repo demonstrates it.** A
warning that always fires is not a warning; it is noise with an alarming shape, and it trains the operator
to scroll past the one run where it means something. §0.7's `Cannot unlink` line is that, in this file, on
this box. **The new line must be printed only when the state genuinely is drifted or genuinely cannot be
determined.**

⛔ **AND IT MUST COMPARE THE WHOLE CHAIN (§0.9).** Comparing *shipped* to *installed* is the third row of
§0.10's table — two stale things matching, reported as reassurance. The repo digest is therefore computed
on the deploying machine and carried to the box.

**3a — `deploy/check-installed-drift.sh`, mode 755 · lane L**

```bash
#!/usr/bin/env bash
# =============================================================================
# Report the INSTALLED state of the box's user-level tooling — conditionally.
#
#   check-installed-drift.sh --group alarm  [--manifest FILE] [--ship-dir DIR]
#   check-installed-drift.sh --group bridge [--manifest FILE] [--ship-dir DIR]
#
# Exit: 0 = every file in the group matches end to end
#       1 = DRIFT — at least one link of the chain differs
#       2 = CANNOT DETERMINE — a file or the manifest is missing/unreadable
#
# THREE LINKS, NOT TWO. The chain is:
#
#     repo (expected digest, computed on the deploying machine)
#       -> shipped   (/opt/rotary-phone/deploy/...)
#         -> installed (~/bin/..., ~/.config/systemd/user/...)
#
# Comparing only shipped-vs-installed is a check that RUNS, PASSES, and answers
# a different question: on main, Deploy-ToLinux.ps1's remote chain is
# ;-separated with no `set -e` and ends in chmod, so ssh returns chmod's status
# and a failed tar is invisible (see docs/plans/gv-session-alarm.md §0.9). /opt
# can therefore be stale too, and two stale copies MATCH.
#
# ⚠ ABSENCE IS NOT SUCCESS. Every "cannot read" path exits 2 and says so. A
# check that goes quiet when its subject is missing is the defect this repo
# corrected in the deploy gate on 2026-09-09: test for PRESENCE, not absence.
#
# ⚠ THE SILENT PATH IS DELIBERATELY QUIET BUT NOT SILENT. One short line on
# success, so "the check ran and found nothing" is distinguishable from "the
# check did not run". The ⚠ marker and the ACTION text appear ONLY on drift.
# =============================================================================
set -uo pipefail

GROUP=""
SHIP_DIR="/opt/rotary-phone/deploy"
MANIFEST=""

while [ $# -gt 0 ]; do
    case "$1" in
        --group)    GROUP="${2:-}"; shift 2 ;;
        --ship-dir) SHIP_DIR="${2:-}"; shift 2 ;;
        --manifest) MANIFEST="${2:-}"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

: "${MANIFEST:=${SHIP_DIR}/.shipped-manifest.sha256}"

# group -> "shipped-relative-path|installed-absolute-path" pairs
case "$GROUP" in
  alarm)
    PAIRS=(
      "gv-session-alarm.sh|${HOME}/bin/gv-session-alarm.sh"
      "systemd/gv-session-alarm.service|${HOME}/.config/systemd/user/gv-session-alarm.service"
      "systemd/gv-session-alarm.timer|${HOME}/.config/systemd/user/gv-session-alarm.timer"
    ) ;;
  bridge)
    PAIRS=(
      "gv-bridge-ensure.sh|${HOME}/bin/gv-bridge-ensure.sh"
      "gv-bridge-restart.sh|${HOME}/bin/gv-bridge-restart.sh"
    ) ;;
  *) echo "--group must be 'alarm' or 'bridge'" >&2; exit 2 ;;
esac

if [ ! -r "$MANIFEST" ]; then
    echo "⚠ [drift-check] ${GROUP}: CANNOT DETERMINE — no shipped manifest at ${MANIFEST}."
    echo "    The deploy did not write one, or did not reach the box. Nothing here can be"
    echo "    stated about what is installed. ACTION: re-run the deploy and read its output."
    exit 2
fi

expected_of() { awk -v f="$1" '$2 == f { print $1 }' "$MANIFEST" | head -n1; }
digest_of()   { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }
stamp_of()    { date -r "$1" '+%Y-%m-%d %H:%M' 2>/dev/null || echo "unknown"; }

rc=0
matched=0
total=${#PAIRS[@]}

for pair in "${PAIRS[@]}"; do
    rel="${pair%%|*}"
    installed="${pair##*|}"
    shipped="${SHIP_DIR}/${rel}"

    want="$(expected_of "$rel")"
    have_ship="$(digest_of "$shipped")"
    have_inst="$(digest_of "$installed")"

    if [ -z "$want" ]; then
        echo "⚠ [drift-check] ${GROUP}: CANNOT DETERMINE — ${rel} is not in the manifest."
        echo "    ACTION: the deploy shipped a file it did not record, or recorded none. Re-deploy."
        rc=2; continue
    fi
    if [ -z "$have_ship" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${shipped} is MISSING or unreadable on the box."
        echo "    The deploy reported success but the file is not here. ACTION: re-run the deploy;"
        echo "    on main a failed remote tar is invisible to it (see plan §0.9)."
        rc=2; continue
    fi
    if [ "$have_ship" != "$want" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${rel} SHIPPED COPY IS STALE — /opt does not match the repo."
        echo "    repo    sha256 ${want}"
        echo "    shipped sha256 ${have_ship}   mtime $(stamp_of "$shipped")"
        echo "    The transfer did not land. ACTION: re-run the deploy and check the sync step."
        rc=1; continue
    fi
    if [ -z "$have_inst" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${installed} is NOT INSTALLED."
        echo "    shipped sha256 ${have_ship}   mtime $(stamp_of "$shipped")"
        if [ "$GROUP" = "bridge" ]; then
            echo "    setup-gvbridge.sh installs this and THE DEPLOY DOES NOT RUN IT."
            echo "    ACTION: bash ${SHIP_DIR}/setup-gvbridge.sh  (see plan §0.2 before you do)."
        else
            echo "    ACTION: bash ${SHIP_DIR}/install-gv-session-alarm.sh"
        fi
        rc=1; continue
    fi
    if [ "$have_inst" != "$have_ship" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${installed} DIFFERS from the shipped copy."
        echo "    installed sha256 ${have_inst}   mtime $(stamp_of "$installed")"
        echo "    shipped   sha256 ${have_ship}   mtime $(stamp_of "$shipped")"
        echo "    The box is executing an older file than the one this deploy shipped."
        if [ "$GROUP" = "bridge" ]; then
            echo "    ACTION: bash ${SHIP_DIR}/setup-gvbridge.sh  (see plan §0.2 before you do)."
        else
            echo "    ACTION: bash ${SHIP_DIR}/install-gv-session-alarm.sh"
        fi
        rc=1; continue
    fi
    matched=$((matched + 1))
done

if [ "$rc" -eq 0 ]; then
    echo "[drift-check] ${GROUP}: ${matched}/${total} installed files match repo → shipped → installed."
fi
exit "$rc"
```

**3b — write the manifest and call the check, from `Deploy-ToLinux.ps1` · lane L to write, B to see**

Two additions. **Keep them to as few lines as possible: PR #84 rewrites 275 lines of this file, and every
line added here is a rebase conflict.**

After the block that copies `deploy/*.sh` and `deploy/systemd/*` (`:179-198` on `main`), add:

```powershell
# Record what the REPO holds for every file we just shipped, and carry it to the box.
# check-installed-drift.sh needs the repo end of the chain: comparing only shipped-vs-installed
# would compare two stale copies and print reassurance (see docs/plans/gv-session-alarm.md §0.9).
$manifestPath = Join-Path ([System.IO.Path]::GetTempPath()) "rp-shipped-manifest.sha256"
$manifestLines = foreach ($f in ($shellScripts + $unitFiles)) {
  $rel = if ($f.Directory.Name -eq "systemd") { "systemd/$($f.Name)" } else { $f.Name }
  "$((Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower())  $rel"
}
[System.IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"),
                               (New-Object System.Text.UTF8Encoding($false)))
scp $manifestPath "${SshTarget}:${TargetPath}/deploy/.shipped-manifest.sha256"
if ($LASTEXITCODE -ne 0) { throw "failed to ship the drift manifest (exit $LASTEXITCODE) -- the post-deploy state check would silently have nothing to compare against" }
Remove-Item $manifestPath -ErrorAction SilentlyContinue
```

Then, after the service restart in step `[4/4]`, add:

```powershell
# --- Post-deploy: install the alarm, and report the installed state of both groups ---
# The alarm's installer is narrow and safe to run every deploy; it does NOT touch
# gv-bridge-ensure.sh (see docs/plans/gv-session-alarm.md §0.2 for why that matters).
ssh $SshTarget "bash ${TargetPath}/deploy/install-gv-session-alarm.sh"
if ($LASTEXITCODE -ne 0) { throw "the GV session alarm installer failed (exit $LASTEXITCODE) -- the alarm is NOT installed" }

# Conditional by construction: these print one quiet line when everything matches, and a
# ⚠ block naming the action only when it does not. Never an unconditional banner.
ssh $SshTarget "bash ${TargetPath}/deploy/check-installed-drift.sh --group alarm"
if ($LASTEXITCODE -ne 0) { throw "the alarm's installed state does not match what was shipped (exit $LASTEXITCODE) -- see the drift report above" }

ssh $SshTarget "bash ${TargetPath}/deploy/check-installed-drift.sh --group bridge"
# NOT a throw. gv-bridge-ensure.sh's staleness is real and is PR #84 Task 10's to fix, not this
# deploy's. Aborting here would block every deploy on an unrelated open PR. It must be LOUD and
# it must not be fatal.
if ($LASTEXITCODE -ne 0) { Write-Host "  (bridge tooling is not in sync -- see above. Not fatal; tracked as PR #84 Task 10.)" -ForegroundColor Yellow }
```

**Acceptance** — lane **L** first, with a fabricated tree and fake `HOME`; then lane **B**:

| Case | Setup | Required output | Exit |
|---|---|---|---|
| **all in sync** | manifest, shipped and installed all identical | exactly one line, `[drift-check] alarm: 3/3 …`, **no `⚠`** | 0 |
| **not installed** | remove `~/bin/gv-session-alarm.sh` | `⚠ … NOT INSTALLED` + the `ACTION:` line naming the installer | 1 |
| **installed differs** | edit the installed copy | `⚠ … DIFFERS` + both sha256s + both mtimes | 1 |
| **shipped stale** | edit the shipped copy so it no longer matches the manifest | `⚠ … SHIPPED COPY IS STALE` — **the case §0.9 requires and a two-link check cannot see** | 1 |
| **no manifest** | delete `.shipped-manifest.sha256` | `⚠ … CANNOT DETERMINE` — never a silent pass | 2 |
| **shipped missing** | delete the shipped file, keep the manifest | `⚠ … MISSING or unreadable` | 2 |

⛔ **The load-bearing criterion is the first row.** Run the in-sync case **ten times** and confirm the
output is ten identical single lines with no `⚠`. A warning that fires on a healthy deploy is a regression,
not a fix, and would become an eighth instance of §6's class by a different route.

⭐ **And the fourth row is what separates this from the check it replaces.** A shipped-vs-installed
comparison passes that case while the box runs the wrong code.

---

### Phase 1 — the additive DTO field

---

#### Task 4 — Expose `browserRefreshOutcome` as a string, additively · lane **U**

**Depends on:** nothing. **Four files, one commit** — the order pin (§0.4) makes them inseparable.

**4a — the adapter property.** In `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs`, directly
after `BrowserSessionStale` (`:255`):

```csharp
    /// <summary>
    /// Why the last attempt to pull cookies from the box's Chrome ended the way it did, as a string:
    /// one of <c>NotAttempted</c>, <c>Unreachable</c>, <c>Stale</c>, <c>Succeeded</c>, <c>TornDown</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ ADDITIVE. <see cref="BrowserSessionStale"/> is unchanged and stays — Radio Console consumes the
    /// boolean under the cross-repo contract (boundary doc, "GV auth lineage" table). Removing or
    /// repurposing it is a contract breach.
    /// <para>
    /// It exists because the boolean is a derived read of ONE enum value. When Chrome is DEAD the outcome
    /// is <c>Unreachable</c>, so <c>browserSessionStale</c> reads FALSE — a consumer keyed on the boolean
    /// alone shows a green light on the worst state. That is the 2h10m gap of 2026-09-09 expressed as a
    /// type. This property reports the whole enum so "signed out" and "gone" are distinguishable.
    /// </para>
    /// <para>
    /// ⚠ The NAME is <c>BrowserRefreshOutcomeName</c>, not <c>BrowserRefreshOutcome</c>, and that is not a
    /// stylistic choice: a property may not share its name with a nested type in the same class (CS0102),
    /// and <see cref="BrowserRefreshOutcome"/> is the enum declared at the bottom of this file. Do not
    /// "tidy" it. The JSON name is <c>browserRefreshOutcome</c> and that is what consumers see.
    /// </para>
    /// <para>
    /// A STRING rather than the enum, for two reasons. The enum is <c>internal</c>, so a public property
    /// returning it does not compile. And a name on the wire means a future member arrives at a consumer
    /// as an unrecognised STRING it can log, rather than as a silently-renumbered integer it will
    /// mis-decode.
    /// </para>
    /// </remarks>
    public string BrowserRefreshOutcomeName => _lastBrowserRefreshOutcome.ToString();
```

**4b — the DTO.** In `src/RotaryPhoneController.GVBridge/Api/GvBridgeDtos.cs`, change the final line of
`GvBridgeStatusDto` from a terminator to a continuation and append:

```csharp
  [property: JsonPropertyName("browserSessionStale")] bool BrowserSessionStale = false,
  // Added by the 2026-09-09 GV session alarm work. browserSessionStale is a derived read of ONE value
  // of this enum: when Chrome is DEAD the outcome is Unreachable, so the BOOLEAN READS FALSE on the
  // worst state. The boolean stays — Radio Console consumes it — and this reports the whole enum
  // beside it. One of: NotAttempted, Unreachable, Stale, Succeeded, TornDown.
  //
  // ⚠ APPENDED LAST, deliberately. GVBridgeControllerTests pins this payload's field names AND their
  // order as a cross-repo contract; appending is what keeps every existing name in its existing
  // position. Do not insert it next to browserSessionStale because they read well together.
  [property: JsonPropertyName("browserRefreshOutcome")] string BrowserRefreshOutcome = "NotAttempted");
```

**4c — the controller.** `GVBridgeController.cs:61`:

```csharp
            BrowserSessionStale: _adapter.BrowserSessionStale,
            BrowserRefreshOutcome: _adapter.BrowserRefreshOutcomeName));
```

**4d — the tests.** In `src/RotaryPhoneController.GVBridge.Tests/Api/GVBridgeControllerTests.cs`, append
`"browserRefreshOutcome"` as the **last** element of the order-pin array at `:138-159`, and add:

```csharp
  [Fact]
  public void GetStatus_ExposesBrowserRefreshOutcome_AndTheBooleanIsUnchangedBesideIt()
  {
    // ⛔ The point of this test is the PAIR, not the new field. browserSessionStale is a derived read of
    // one enum value: on Unreachable — Chrome gone, the worst state — the boolean is FALSE. A consumer
    // keyed on the boolean alone shows green. Asserting both together is what pins that the string
    // carries information the boolean structurally cannot.
    var controller = CreateController();

    var result = controller.GetStatus();

    var okResult = Assert.IsType<OkObjectResult>(result);
    var json = JsonSerializer.Serialize(okResult.Value);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Assert.True(root.TryGetProperty("browserRefreshOutcome", out var outcome));
    Assert.Equal(JsonValueKind.String, outcome.ValueKind);

    // An inactive adapter has attempted nothing. NOT "Succeeded", and not absent: a consumer must be
    // able to tell "never tried" from "tried and worked".
    Assert.Equal("NotAttempted", outcome.GetString());

    // The existing boolean is untouched beside it — same name, same type, same value.
    Assert.True(root.TryGetProperty("browserSessionStale", out var stale));
    Assert.Equal(JsonValueKind.False, stale.ValueKind);
  }
```

And in `src/RotaryPhoneController.GVBridge.Tests/Adapters/GVApiAdapterCookieLineageTests.cs`, extend the
existing `BrowserSessionStale_DistinguishesADeadLoginFromAnUnreachableChrome` test (`:665`) — it already
builds both adapters, so the assertion is two lines:

```csharp
        Assert.True(stale.BrowserSessionStale);
        Assert.Equal("Stale", stale.BrowserRefreshOutcomeName);

        Assert.False(unreachable.BrowserSessionStale);   // NOT stale — nothing tested the login
        // ⭐ …and THIS is the whole reason the string field exists. The boolean above reads false on a
        // DEAD Chrome, identically to a healthy one. The string does not.
        Assert.Equal("Unreachable", unreachable.BrowserRefreshOutcomeName);
```

**Acceptance:**

- `dotnet test` is green, including `GetStatus_PsidtsAgeSeconds_IsGone_AndTheRemainingFieldsKeepTheirNamesAndOrder`.
  ⚠ **Run that test on the unmodified array first and watch it FAIL**, then update the array. A pin you
  never saw fail is a pin you have not confirmed is wired up.
- The new field is the **last** key in the serialised payload; every pre-existing key keeps its index.
- `Assert.Equal("Unreachable", unreachable.BrowserRefreshOutcomeName)` passes **on the same adapter
  instance** whose `BrowserSessionStale` is `false`. That single pair is spec acceptance 1's real content.
- ⛔ Nothing in `GVApiAdapter` that *sets* `_lastBrowserRefreshOutcome` is modified. This task adds a
  reader. Detection is not touched.

---

#### Task 5 — Ship the alarm's files, and prove they install · lane **L** + **B**

**Depends on:** Tasks 2, 3, and the alarm script itself (Task 13 installs the units; the script is Task 8).
**Sequenced here as the gate, executed after Phase 2.** Listed in Phase 1 because it is the *blocker* the
spec's §7 names, and Builder should see it early.

No new code. This is the on-box observation that §7 demands, and it is **not token-gated**:

```bash
# On the deploying machine
pwsh deploy/Deploy-ToLinux.ps1        # a normal deploy, no special flags

# On the box — the INSTALLED artefacts, never the repo
ls -l --time-style=long-iso ~/bin/gv-session-alarm.sh
sha256sum ~/bin/gv-session-alarm.sh /opt/rotary-phone/deploy/gv-session-alarm.sh
systemctl --user list-unit-files 'gv-session-alarm.*'
systemctl --user list-timers --all 'gv-session-alarm.*'
~/bin/gv-session-alarm.sh --print-config
```

**Acceptance** — spec acceptance 8, split per §0.8:

- `~/bin/gv-session-alarm.sh` exists, mode `755`, and its sha256 **equals** the shipped copy's.
- `list-unit-files` shows both units as `disabled` — installed, not enabled. Enabled is Task 16.
- `list-timers --all` lists `gv-session-alarm.timer`.
- `--print-config` runs from the **installed** path and prints its resolved configuration. This is the
  check a checksum cannot make: it proves the installed thing *executes*, catching a bad mode, a partial
  copy, or the right name over the wrong file.
- The deploy printed `[drift-check] alarm: 3/3 …` with **no `⚠`**.
- ⛔ `~/bin/gv-bridge-ensure.sh` is **byte-identical to before the deploy** — confirmed against Task 1's
  recorded sha256. This arc must not have shipped `gv-bridge-ensure.sh` (§0.2, §8).
- ⛔ `gv-bridge-watchdog.timer` is still `active` and its NEXT is within 2 minutes. The alarm's install
  must not have disturbed it.

⚠ **If the deploy silently changed nothing** (§0.9), the sha256 comparison and `--print-config` are what
catch it — not the deploy's own success message.

---

#### Task 6 — Announce the additive field across the boundary · lane **L**

**Depends on:** Task 4. **Additive annotation, not a rewrite.**

In `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`, add one row to the "GV auth lineage" table (`:190`,
after `browserSessionStale`):

```markdown
| `browserRefreshOutcome` | ⭐ String, added 2026-09-09. The **whole** enum behind `browserSessionStale`: `NotAttempted`, `Unreachable`, `Stale`, `Succeeded`, `TornDown`. |
```

And immediately below the table:

```markdown
⛔ **`browserSessionStale` is FALSE when Chrome is DEAD, and that is not a bug in it.** It is a derived read
of one value — `outcome == Stale` — which means "Chrome was reachable, handed us cookies, and Google
rejected them". When Chrome is **gone** the outcome is `Unreachable`, so the boolean reads `false`,
identically to a perfectly healthy session. **A consumer keyed on the boolean alone shows a green light on
the worst available state.** That is what let the 2026-09-09 sign-out run 2h10m unnoticed.

⚠️ **The boolean is NOT deprecated and is NOT changing.** This row is additive; `browserSessionStale` keeps
its name, position, type and meaning. Bind a *stale-login* indicator to the boolean as today if you wish —
but bind anything that means *"the browser session is not usable"* to `browserRefreshOutcome`, and treat an
**unrecognised** string as a fault rather than as healthy: a future enum member must not read as green.
```

Then append one row to the Change Log (`:826`), matching the existing `| Date | Changed by | What changed |`
shape and the house convention of opening with the BT/audio disclaimer:

```markdown
| 2026-09-09 | RotaryPhone session | **API only — one ADDITIVE field. No BT/audio change; hci0/hci1 ownership, profiles and WirePlumber configs untouched.** `GET /api/gvbridge/status` gains **`browserRefreshOutcome`** (string, appended last, so no existing field changes name, type or position): the full enum behind `browserSessionStale` — `NotAttempted` \| `Unreachable` \| `Stale` \| `Succeeded` \| `TornDown`. ⛔ **Read the note under the GV auth lineage table before binding anything to it:** `browserSessionStale` is `false` when Chrome is DEAD, because it tests only the `Stale` value; a consumer keyed on the boolean alone shows green on the worst state. **`browserSessionStale` is unchanged and is not deprecated** — this is additive only. Motivated by the 2026-09-09 sign-out that ran **2h10m** with `available:true`, `cookiesValid:true`, `sipRegistered:true` while the service composed a correct alert six times into a journal nobody read. ⚠ **Merged ≠ deployed ≠ INSTALLED:** this row describes `main`. Confirm against `radio:5004/api/gvbridge/status` before binding a UI to it. |
```

**Acceptance:**

- The existing table rows and the existing `browserSessionStale` description are **byte-unchanged**.
- The Change Log row states the BT/audio no-op, marks the change additive, and carries the
  merged≠deployed≠installed caveat — the three things every prior row in that table carries.
- ⛔ **No row is added for the exit-code change.** That is Task 18, and it is flag-only.

---

### Phase 2 — the alarm

Every task in this phase is verified in lane **L**, against a stub gateway, with no box and no token.

---

#### Task 7 — A stub gateway that reproduces the measured traps · lane **L**

**Depends on:** nothing. **Written first, because every later task's acceptance runs against it.**

⭐ **Why a stub and not "test it against the real thing later".** Spec §4.4's traps each *"cost a real alarm
on the other project"*, and they are all silent: a 422 delivers nothing and prints to stderr. Discovering
them on the real gateway means discovering them by not being notified. The stub makes the failure visible
in a test rather than in an outage — and it means the token-gated Task 17 confirms known behaviour rather
than exploring.

Create `deploy/tests/gv-alarm-gateway-stub.py`:

```python
#!/usr/bin/env python3
"""Stub of the chat gateway, reproducing ONLY its MEASURED behaviour.

Every rule below is from the spec's §4.4 table, which is itself from
aitrader/docs/chat-gateway-requirements.md. Each cost a real alarm.

  * `action` is capped at 200 CHARACTERS; over-long is 422 and DELIVERS NOTHING.
  * `action` and `timestamp` are SILENTLY DROPPED on severity=info.
  * `grace` is CASE-SENSITIVE; "30M" is a 422.
  * dedupe_key ignores severity, title and thread_key.

⚠ This stub is deliberately STRICTER than the real gateway in one way and
weaker in another, and both are on purpose:
  - stricter: it requires a bearer token, so a missing-credential bug fails
    here rather than being masked by a permissive endpoint.
  - weaker: it does not implement delivery, routing, or the in-memory queue.
    It cannot prove a message REACHED a human. Only Task 17 can do that.

Usage:
    python3 gv-alarm-gateway-stub.py --port 8099 --log /tmp/gw.jsonl
    python3 gv-alarm-gateway-stub.py --port 8099 --log /tmp/gw.jsonl --fail-notify 500
"""
import argparse, json, sys, time
from http.server import BaseHTTPRequestHandler, HTTPServer

ACTION_MAX_CHARS = 200
VALID_SEVERITY = {"alert", "warning", "info"}
TOKEN = "stub-token"
ARGS = None
CHECKS = {}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        payload = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _record(self, kind, code, body):
        with open(ARGS.log, "a") as fh:
            fh.write(json.dumps({
                "t": time.time(), "kind": kind, "status": code, "body": body,
            }) + "\n")

    def _authed(self):
        return self.headers.get("Authorization") == f"Bearer {TOKEN}"

    def _read(self):
        n = int(self.headers.get("Content-Length") or 0)
        try:
            return json.loads(self.rfile.read(n) or b"{}")
        except json.JSONDecodeError:
            return None

    def do_POST(self):
        body = self._read()
        if body is None:
            return self._send(400, {"error": "malformed json"})
        if not self._authed():
            self._record(self.path, 401, body)
            return self._send(401, {"error": "bad or missing bearer token"})

        if self.path == "/v1/notify":
            return self._notify(body)
        if self.path == "/v1/heartbeat":
            return self._heartbeat(body)
        self._send(404, {"error": "no such route"})

    def do_GET(self):
        if self.path.startswith("/v1/heartbeat/"):
            src = self.path.rsplit("/", 1)[-1]
            if src not in CHECKS:
                return self._send(404, {"error": "no such check"})
            return self._send(200, CHECKS[src])
        self._send(404, {"error": "no such route"})

    def _notify(self, body):
        if ARGS.fail_notify:
            self._record("notify", ARGS.fail_notify, body)
            return self._send(ARGS.fail_notify, {"error": "forced failure (--fail-notify)"})

        sev = body.get("severity")
        if sev not in VALID_SEVERITY:
            self._record("notify", 422, body)
            return self._send(422, {"error": f"severity must be one of {sorted(VALID_SEVERITY)}"})

        action = body.get("action")
        # THE MEASURED TRAP: characters, not bytes, and the whole message is refused.
        if action is not None and len(action) > ACTION_MAX_CHARS:
            self._record("notify", 422, body)
            return self._send(422, {
                "error": "action too long",
                "limit": ACTION_MAX_CHARS,
                "got": len(action),
                "note": "nothing was delivered",
            })

        stored = dict(body)
        if sev == "info":
            # Silently dropped. No error, no warning — which is what makes it a trap.
            stored.pop("action", None)
            stored.pop("timestamp", None)
        self._record("notify", 202, stored)
        self._send(202, {"queued": True})

    def _heartbeat(self, body):
        grace = body.get("grace", "")
        schedule = body.get("schedule", "")
        for name, val in (("grace", grace), ("schedule", schedule)):
            if not isinstance(val, str) or not val:
                self._record("heartbeat", 422, body)
                return self._send(422, {"error": f"{name} is required"})
            # THE MEASURED TRAP: case-sensitive. "30M" is a 422.
            if val != val.lower():
                self._record("heartbeat", 422, body)
                return self._send(422, {"error": f"{name} must be lower-case", "got": val})
        src = body.get("source")
        if not src:
            self._record("heartbeat", 422, body)
            return self._send(422, {"error": "source is required"})
        CHECKS[src] = {
            "source": src,
            "check_id": body.get("check_id"),
            "schedule": schedule,
            "grace": grace,
            "last_seen": time.time(),
            "refresh_count": CHECKS.get(src, {}).get("refresh_count", 0) + 1,
        }
        self._record("heartbeat", 200, body)
        self._send(200, CHECKS[src])

    def log_message(self, *a):
        pass  # stdout belongs to the harness


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8099)
    ap.add_argument("--log", default="/tmp/gv-alarm-gateway-stub.jsonl")
    ap.add_argument("--fail-notify", type=int, default=0,
                    help="return this status for every /v1/notify")
    ARGS = ap.parse_args()
    open(ARGS.log, "w").close()
    print(f"stub gateway on 127.0.0.1:{ARGS.port}, log {ARGS.log}", file=sys.stderr)
    HTTPServer(("127.0.0.1", ARGS.port), Handler).serve_forever()
```

**Acceptance** — the stub is tested before anything is tested against it:

```bash
python3 deploy/tests/gv-alarm-gateway-stub.py --port 8099 --log /tmp/gw.jsonl &
sleep 1
H='Authorization: Bearer stub-token'
# 1. a normal alert is accepted
curl -s -o /dev/null -w '%{http_code}\n' -X POST -H "$H" -H 'Content-Type: application/json' \
  -d '{"source":"x","severity":"alert","title":"t","action":"short"}' localhost:8099/v1/notify
# 2. a 201-character action is REFUSED
curl -s -w '\n%{http_code}\n' -X POST -H "$H" -H 'Content-Type: application/json' \
  -d "{\"source\":\"x\",\"severity\":\"alert\",\"title\":\"t\",\"action\":\"$(printf 'a%.0s' $(seq 201))\"}" \
  localhost:8099/v1/notify
# 3. an upper-case grace is REFUSED
curl -s -w '\n%{http_code}\n' -X POST -H "$H" -H 'Content-Type: application/json' \
  -d '{"source":"x","check_id":"c","schedule":"5m","grace":"30M"}' localhost:8099/v1/heartbeat
# 4. no token is REFUSED
curl -s -o /dev/null -w '%{http_code}\n' -X POST -H 'Content-Type: application/json' \
  -d '{"source":"x","severity":"info","title":"t"}' localhost:8099/v1/notify
```

Expected, in order: `202`, `422` with `"limit": 200` and `"got": 201`, `422` naming `grace`, `401`.
Then: a message posted at `severity:"info"` with an `action` is recorded in the log **without** the
`action` key — the silent drop, made visible.

---

#### Task 8 — `gv-session-alarm.sh`: environment, poll, classify, state · lane **L**

**Depends on:** Task 7. Creates `deploy/gv-session-alarm.sh`. **This task's script posts nothing** — it
classifies and prints. Posting is Task 9. Splitting it this way means the decision tree is provable before
any network call is involved in the proof.

```bash
#!/usr/bin/env bash
# =============================================================================
# GV session alarm — TRANSPORT for a signal the service already produces.
#
# ⛔ THIS SCRIPT DETECTS NOTHING. Two correct detectors already exist: the
# service's own [ERR] GVApi log lines, and browserRefreshOutcome on
# GET /api/gvbridge/status. On 2026-09-09 the service composed a correct,
# complete, correctly-worded alert SIX TIMES over 2h10m and it reached a journal
# nobody was reading. The gap was never detection. Do not add a third detector
# here; if this script ever seems to need one, the fix belongs in the service.
#
# Run by gv-session-alarm.timer every 5 minutes. Five minutes is generous on
# purpose: the underlying condition persists for hours and only changes when the
# 20-minute cron or the recovery ladder attempts a refresh. This is not chasing
# a transient.
#
# EXIT CODES — they are the systemd-visible half of the contract:
#   0  the cycle completed: a conclusion was reached and everything that had to
#      be delivered was delivered. Includes "the service is down and we said so."
#   1  the cycle did NOT complete: config missing, a notify failed, state could
#      not be persisted, or the heartbeat refresh failed.
# =============================================================================

# ⚠ NOT `set -e`. A script whose job is to report failures must not die on the
# first one — an early exit is exactly the silence this alarm exists to prevent.
# Every failure below is checked explicitly and turned into a journal line and
# an exit code.
set -uo pipefail

VERSION="1"
SOURCE_NAME="rotaryphone"

now_utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
log()  { printf '%s gv-session-alarm[%s]: %s\n' "$(now_utc)" "$$" "$*" >&2; }
die()  { log "FATAL: $*"; exit 1; }

# --- Configuration -----------------------------------------------------------
# ⛔ A systemd USER timer inherits NO login-shell environment. This script
# SOURCES its configuration explicitly and never relies on inheritance.
#
# Measured, aitrader 2026-08-14: a refusal journaled correctly and notified
# NOBODY, because the credentials lived only in ~/.aitrader-env which only the
# cron wrapper sourced. The gateway, the bearer, the call site and the routing
# were all proved healthy the same hour by a positive control.
#
# ⛔ And aitrader's "unset => no-op, not error" rule is CORRECT for an optional
# third store on a trading bot and EXACTLY WRONG for the only alarm on a phone.
# Silence is not a valid state here. Missing configuration is a hard, non-zero,
# journaled failure — nothing below is best-effort.
ENV_FILE="${GV_ALARM_ENV_FILE:-${HOME}/.rotaryphone-env}"
STATE_FILE="${GV_ALARM_STATE_FILE:-${HOME}/.local/state/gv-session-alarm.state}"

if [ "${1:-}" = "--print-config" ]; then
    # Side-effect free, and it runs BEFORE the env file is required so the
    # deploy's post-install gate can call it on a box that has no token yet.
    printf 'gv-session-alarm version=%s\n' "$VERSION"
    printf '  script     %s\n' "$0"
    printf '  env_file   %s (%s)\n' "$ENV_FILE" \
        "$([ -r "$ENV_FILE" ] && echo readable || echo MISSING)"
    printf '  state_file %s (%s)\n' "$STATE_FILE" \
        "$([ -r "$STATE_FILE" ] && echo present || echo absent)"
    printf '  status_url %s\n' "${GV_ALARM_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"
    exit 0
fi

[ -r "$ENV_FILE" ] || die "${ENV_FILE} is missing or unreadable. This alarm has no credentials and cannot notify anyone. Exiting NON-ZERO so the unit FAILS and systemd records it — a silent no-op here would reproduce the exact condition this alarm exists to detect."

# shellcheck disable=SC1090
. "$ENV_FILE" || die "could not source ${ENV_FILE}"

for required in ROTARYPHONE_GATEWAY_URL ROTARYPHONE_GATEWAY_TOKEN; do
    if [ -z "${!required:-}" ]; then
        die "${required} is unset or empty after sourcing ${ENV_FILE}. Refusing to run half-configured."
    fi
done

STATUS_URL="${GV_ALARM_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"
GATEWAY_URL="${ROTARYPHONE_GATEWAY_URL%/}"

for tool in curl jq; do
    command -v "$tool" >/dev/null 2>&1 || die "${tool} is not on PATH. Refusing to run: without it this script cannot tell a healthy session from a dead one, and would report the wrong answer rather than none."
done

# --- State -------------------------------------------------------------------
# Persisted so a transition survives a reboot, a service restart and a redeploy.
# In particular INCIDENT_THREAD_KEY must survive, or the all-clear opens a NEW
# thread instead of closing the one the owner was notified about — and a
# RESOLVED that does not thread under its alert is invisible, because RESOLVED
# is deliberately routed to the quiet lane.
LAST_POSTED_CONDITION=""
INCIDENT_THREAD_KEY=""
INCIDENT_OPENED_AT=""
PENDING_CONDITION=""
PENDING_POLLS=0

if [ -r "$STATE_FILE" ]; then
    # shellcheck disable=SC1090
    . "$STATE_FILE" || log "WARNING: ${STATE_FILE} exists but could not be sourced; treating as empty"
fi

write_state() {
    local dir; dir="$(dirname "$STATE_FILE")"
    mkdir -p "$dir" 2>/dev/null || { log "could not create ${dir}"; return 1; }
    # Atomic: a reader (or the next run) sees the whole old file or the whole new
    # one, never a half-written one. Same reasoning as the installer's
    # install_atomic, against the same 5-minute timer.
    {
        printf '# written %s by gv-session-alarm v%s\n' "$(now_utc)" "$VERSION"
        printf 'LAST_POSTED_CONDITION=%q\n' "$LAST_POSTED_CONDITION"
        printf 'INCIDENT_THREAD_KEY=%q\n'   "$INCIDENT_THREAD_KEY"
        printf 'INCIDENT_OPENED_AT=%q\n'    "$INCIDENT_OPENED_AT"
        printf 'PENDING_CONDITION=%q\n'     "$PENDING_CONDITION"
        printf 'PENDING_POLLS=%q\n'         "$PENDING_POLLS"
    } > "${STATE_FILE}.new" || { rm -f "${STATE_FILE}.new"; log "could not write ${STATE_FILE}.new"; return 1; }
    mv -f "${STATE_FILE}.new" "$STATE_FILE" || { rm -f "${STATE_FILE}.new"; log "could not replace ${STATE_FILE}"; return 1; }
    return 0
}

# --- Poll --------------------------------------------------------------------
poll_ok=0
status_body=""
status_http=""

resp="$(curl -sS --max-time 10 -w $'\n%{http_code}' "$STATUS_URL" 2>/dev/null)"
curl_rc=$?
if [ "$curl_rc" -eq 0 ]; then
    status_http="${resp##*$'\n'}"
    status_body="${resp%$'\n'*}"
    [ "$status_http" = "200" ] && poll_ok=1
fi

# --- Classify ----------------------------------------------------------------
# ⛔ The mapping is per CONDITION, not per message. dedupe_key ignores severity,
# title and thread_key, so a key chosen per message collapses two different
# alerts into one.
outcome="UNPOLLED"
age_seconds=""
if [ "$poll_ok" -eq 1 ]; then
    outcome="$(printf '%s' "$status_body" | jq -r '.browserRefreshOutcome // "FIELD_MISSING"' 2>/dev/null)" \
        || outcome="UNPARSEABLE"
    age_seconds="$(printf '%s' "$status_body" | jq -r '.browserSessionAgeSeconds // ""' 2>/dev/null)"
fi

case "$outcome" in
    UNPOLLED)      condition="service_unreachable" ;;
    Stale)         condition="browser_stale" ;;
    Unreachable)   condition="browser_unreachable" ;;
    NotAttempted)  condition="not_attempted" ;;
    Succeeded)     condition="ok" ;;
    TornDown)      condition="ignore" ;;
    FIELD_MISSING) condition="field_missing" ;;
    *)             condition="unknown_outcome" ;;
esac

# ⭐ field_missing and unknown_outcome are NOT in the spec's §4.3 table, and both
# must exist rather than folding into "ok".
#
#   field_missing  — the box is running a build without browserRefreshOutcome.
#                    That is not hypothetical: merged != deployed != INSTALLED,
#                    and this arc's own §0.9 found a deploy path that reports
#                    success while changing nothing. Reading a missing field as
#                    healthy would make the alarm mute in precisely the state
#                    where the deploy has already failed once.
#   unknown_outcome — a future enum member. It must not read as green.
#
# Both are WARN: something is wrong with the instrument, not (yet) with the
# session, and the operator action is to look at the deploy rather than at
# Google.

# NotAttempted is only worth waking someone for if it PERSISTS — a single tick
# during startup is normal. Three consecutive polls is 15 minutes.
MIN_POLLS_TO_POST=1
[ "$condition" = "not_attempted" ] && MIN_POLLS_TO_POST=3

if [ "$condition" = "$PENDING_CONDITION" ]; then
    PENDING_POLLS=$((PENDING_POLLS + 1))
else
    PENDING_CONDITION="$condition"
    PENDING_POLLS=1
fi

log "outcome=${outcome} condition=${condition} age=${age_seconds:-none} polls=${PENDING_POLLS} last_posted=${LAST_POSTED_CONDITION:-none}"
```

**Acceptance** — `deploy/tests/repro-gv-session-alarm.sh` (created in Task 12) drives all of these with a
stub status endpoint. Each is an *outcome*, observed:

| Case | Status endpoint serves | `condition` must be |
|---|---|---|
| healthy | `{"browserRefreshOutcome":"Succeeded", …}` | `ok` |
| signed out | `"Stale"` | `browser_stale` |
| **Chrome gone** | `"Unreachable"` **with `browserSessionStale:false`** | `browser_unreachable` |
| not wired | `"NotAttempted"` | `not_attempted`, and **not posted before the 3rd consecutive poll** |
| teardown | `"TornDown"` | `ignore` |
| **old build** | a payload with **no** `browserRefreshOutcome` key | `field_missing` — **never `ok`** |
| **future value** | `"Hibernating"` | `unknown_outcome` — **never `ok`** |
| service down | connection refused | `service_unreachable` |
| service sick | HTTP 500 | `service_unreachable` |

- ⛔ **The third and sixth rows are the load-bearing ones.** Row 3 is the case `browserSessionStale`
  reports as `false` — the whole reason Task 4 exists. Row 6 is the case a naive `// false` default would
  turn green.
- With `~/.rotaryphone-env` absent: exit status is **1**, and stderr contains `FATAL` and the file's path.
  ⛔ Assert the exit code, not the message alone — spec acceptance 5 is *"it does not exit 0."*
- With the env file present but `ROTARYPHONE_GATEWAY_TOKEN` empty: exit **1**, naming that variable.
- `--print-config` exits **0** with no env file present, and reports `env_file … (MISSING)`. The deploy's
  gate must be able to interrogate an unconfigured install.
- After any run, `${STATE_FILE}.new` does not exist.

---

#### Task 9 — The POST: check the status, and truncate `action` · lane **L**

**Depends on:** Tasks 7, 8. Appends to `deploy/gv-session-alarm.sh`.

⛔ **The rule this task exists for.** On 2026-08-31 an over-long `action` returned 422, the notification was
never delivered, and the failure printed to stderr into a log nobody reads — *"the control installed to make
a scan failure loud was itself mute, in exactly the situation it exists for."* **A non-2xx is a first-class
failure, never something to print and move past.**

```bash
# --- Gateway ------------------------------------------------------------------
# MEASURED CAP: 200 CHARACTERS on `action`. Over-long is a 422 that DELIVERS
# NOTHING, silently. Our natural action fits comfortably; the cap exists because
# a future edit will not, and because the body must never be spliced into it.
ACTION_MAX="${GV_ALARM_ACTION_MAX:-200}"

NOTIFY_ATTEMPTED=0
NOTIFY_FAILED=0

# Truncate to (ACTION_MAX - 3) and append ASCII "...", NOT a one-character "…".
# The measured cap is in characters, but we do not control what the gateway
# counts on the far side of a proxy, and "…" is 1 character / 3 bytes. An
# ASCII-only marker is <= the cap under BOTH readings. Getting this wrong turns
# a truncation that was supposed to prevent a 422 into one that causes it.
truncate_action() {
    local s="$1"
    if [ "${#s}" -le "$ACTION_MAX" ]; then printf '%s' "$s"; return 0; fi
    printf '%s...' "${s:0:$((ACTION_MAX - 3))}"
    return 0
}

# post_notify SEVERITY TITLE BODY ACTION DEDUPE_KEY THREAD_KEY
post_notify() {
    local severity="$1" title="$2" body="$3" action="$4" dedupe="$5" thread="$6"
    local payload resp rc http out

    # MEASURED: `action` and `timestamp` are SILENTLY DROPPED on severity=info.
    # So anything whose action matters goes on `warning`, never `info` — and we
    # do not SEND an action on info, rather than sending one that vanishes and
    # believing it arrived.
    if [ "$severity" = "info" ] && [ -n "$action" ]; then
        log "dropping action on an info message by design (the gateway would drop it silently): ${action}"
        action=""
    fi

    payload="$(jq -nc \
        --arg source     "$SOURCE_NAME" \
        --arg severity   "$severity" \
        --arg title      "$title" \
        --arg body       "$body" \
        --arg action     "$(truncate_action "$action")" \
        --arg dedupe_key "$dedupe" \
        --arg thread_key "$thread" \
        --arg timestamp  "$(now_utc)" \
        '{source:$source, severity:$severity, title:$title, body:$body,
          dedupe_key:$dedupe_key, thread_key:$thread_key, timestamp:$timestamp}
         + (if $action == "" then {} else {action:$action} end)')" \
        || { log "NOTIFY FAILED: could not build the payload for ${dedupe}"; NOTIFY_ATTEMPTED=$((NOTIFY_ATTEMPTED+1)); NOTIFY_FAILED=$((NOTIFY_FAILED+1)); return 1; }

    NOTIFY_ATTEMPTED=$((NOTIFY_ATTEMPTED + 1))

    resp="$(curl -sS --max-time 15 -X POST \
        -H "Authorization: Bearer ${ROTARYPHONE_GATEWAY_TOKEN}" \
        -H 'Content-Type: application/json' \
        -w $'\n%{http_code}' \
        --data-binary "$payload" \
        "${GATEWAY_URL}/v1/notify" 2>&1)"
    rc=$?
    http="${resp##*$'\n'}"
    out="${resp%$'\n'*}"

    if [ "$rc" -ne 0 ]; then
        NOTIFY_FAILED=$((NOTIFY_FAILED + 1))
        log "NOTIFY FAILED: curl exit ${rc} for severity=${severity} dedupe=${dedupe}. Transport error, nothing delivered. Detail: ${out}"
        return 1
    fi

    case "$http" in
        2*)
            log "notify DELIVERED http=${http} severity=${severity} dedupe=${dedupe} thread=${thread}"
            return 0
            ;;
        *)
            NOTIFY_FAILED=$((NOTIFY_FAILED + 1))
            log "NOTIFY FAILED: http=${http} severity=${severity} dedupe=${dedupe}. NOTHING WAS DELIVERED."
            log "gateway said: ${out}"
            # ⛔ Deliberately NOT retried with a reshaped message. A 422 names its
            # own limit in the body above, which is why the body is journaled
            # verbatim — but a script that rewrites its own message on refusal
            # delivers something nobody tested, and hides the defect that caused
            # the refusal. The heartbeat refresh is suppressed instead (Task 11),
            # so the gateway raises the missing-check alert on our behalf.
            return 1
            ;;
    esac
}
```

⚠ **Deviation from the spec, stated rather than smuggled.** §4.4 says *"read the limit off the 422 rather
than hardcoding."* This implements the *first* half — the 422 body, which carries the limit, is journaled
verbatim — and refuses the second: it does not adapt and re-send. A message that is reshaped by a failure
handler and then delivered is a message no test covered, and the reshaping hides the defect. The cap stays
a constant, overridable by `GV_ALARM_ACTION_MAX` for testing. **Flagged for the owner as decision Q2 in §4.**

**Acceptance** — lane **L**, against the stub:

- A `warning` with a 60-character action is accepted `202`, and the stub's log records the action **intact**.
- A `warning` with a **250-character** action is accepted `202`, and the stub's log records an action of
  **exactly 200 characters ending in `...`**. ⛔ Assert the recorded length, not that the call succeeded —
  "it returned 202" is satisfied by a message that was truncated wrongly and delivered anyway.
- ⛔ **Forced 422.** With `GV_ALARM_ACTION_MAX=9999`, a 250-character action reaches the stub un-truncated
  and is refused `422`. Required observations, all three:
  1. exit status is **1**;
  2. the journal contains `NOTHING WAS DELIVERED` **and** the gateway's body including `"limit": 200`;
  3. the stub's log shows **no delivered message** for that dedupe key.
- An `info` message given an action is sent **without** the `action` key, and the journal says so. Confirm
  against the stub's log, not against our own intent.
- With `--fail-notify 500`: exit **1**, `NOTIFY FAILED: http=500`, and `NOTIFY_FAILED` non-zero.

---

#### Task 10 — Threading, and the service's own words · lane **L**

**Depends on:** Task 9. Appends to `deploy/gv-session-alarm.sh`.

⛔ **No new alert copy is written in this task.** Each body quotes the service verbatim. The line numbers
are recorded so Task 12's guard can find them, and so a reader can check the quotation is one.

```bash
# --- Copy ---------------------------------------------------------------------
# ⛔ EVERY BLOCK-QUOTED LINE BELOW IS THE SERVICE'S OWN WORDING, COPIED VERBATIM.
# Do not improve it, re-punctuate it, or shorten it. It is quoted precisely
# because the service already gets this right: on 2026-09-09 it composed a
# correct, complete, correctly-worded alert six times — correct severity
# vocabulary, the exact remedy, and the reassurance an operator most needs
# before panicking, that the working set survived. What failed was transport.
#
# ⚠ These strings are ALSO in C#. deploy/tests/check-alarm-copy-drift.sh (Task 12)
# fails the build if the two ever diverge. If you edit one, edit both.
#   browser_stale       <- GVApiAdapter.cs:899-902
#   browser_unreachable <- GVApiAdapter.cs:1234-1240
#   not_attempted       <- GVApiAdapter.cs:1255-1260

body_for() {
    case "$1" in
      browser_stale)
        cat <<'QUOTE'
The service reported this, in its own words:

> GVApi: REJECTED a cookie set from refresh-from-browser — Google refused it. The working on-disk set was NOT overwritten. If the source is the box's Chrome, that session is dead: ACTION: re-login at voice.google.com.

**The phone still works.** It is running on a credential it can renew but cannot re-derive, and there is
nothing underneath that. Recovery has no floor below a working browser session.
QUOTE
        ;;
      browser_unreachable)
        cat <<'QUOTE'
Chrome could not be reached at all, so the Google login was **never tested**. The service's own words:

> GVApi: all cookie-recovery rungs failed and CHROME WAS UNREACHABLE on CDP port 9224 — our own rotation chain lapsed and the browser fallback could not be tried, so the Google login was never tested. ACTION: confirm Chrome is running (pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome") BEFORE touching the Google login; the session may be perfectly fine.

⚠ `browserSessionStale` reads **false** in this state, identically to a healthy one. That is why this
alarm reads `browserRefreshOutcome` instead.
QUOTE
        ;;
      not_attempted)
        cat <<'QUOTE'
The browser was never consulted, for 15 minutes or more. The service's own words:

> GVApi: all cookie-recovery rungs failed and the browser was NEVER CONSULTED (no CDP extractor wired, or no cookie store). Our rotation chain lapsed and nothing tested the Google login. ACTION: check the service's CDP wiring and that Chrome is up on port 9224; do NOT assume the login is dead.
QUOTE
        ;;
      service_unreachable)
        cat <<QUOTE
The alarm could not reach \`${STATUS_URL}\`. No status was obtained, so nothing is known about the Google
Voice session — **this is not a report that the session is fine.**

This is the case in-process detection structurally cannot cover: a service that is not running cannot
report that it is not running.
QUOTE
        ;;
      field_missing)
        cat <<QUOTE
\`${STATUS_URL}\` answered, but its payload has **no \`browserRefreshOutcome\` field**. The box is running a
build that predates it.

⚠ This alarm cannot tell a healthy session from a dead one against this build, and it is reporting that
rather than defaulting to green. **Merged ≠ deployed ≠ INSTALLED.**
ACTION: check what is actually deployed on the box.
QUOTE
        ;;
      unknown_outcome)
        cat <<QUOTE
\`${STATUS_URL}\` returned \`browserRefreshOutcome\` = **${outcome}**, which this alarm does not recognise.

Treated as a fault rather than as healthy, deliberately: a new enum member must not read as green.
QUOTE
        ;;
    esac
}

title_for() {
    # ⚠ NO SEVERITY IN THE TITLE. The gateway prepends its own severity_prefix();
    # a title carrying its own renders it twice, with the two vocabularies free
    # to disagree. Measured on the sibling project: "ℹ️ [INFO] [pmtrader] ℹ️ INFO · …".
    case "$1" in
      browser_stale)       echo "[${SOURCE_NAME}] GV session — signed out, re-login needed" ;;
      browser_unreachable) echo "[${SOURCE_NAME}] GV session — Chrome is gone, login untested" ;;
      not_attempted)       echo "[${SOURCE_NAME}] GV session — browser never consulted" ;;
      service_unreachable) echo "[${SOURCE_NAME}] GV session — the service is not answering" ;;
      field_missing)       echo "[${SOURCE_NAME}] GV session — the box is running an older build" ;;
      unknown_outcome)     echo "[${SOURCE_NAME}] GV session — unrecognised outcome" ;;
      ok)                  echo "[${SOURCE_NAME}] GV session — recovered" ;;
    esac
}

action_for() {
    # Every one of these is well inside 200 characters. truncate_action is the
    # guard for the edit that changes that, not for these.
    case "$1" in
      browser_stale)       echo "re-login at voice.google.com in the box's Chrome (CDP 9224)" ;;
      browser_unreachable) echo 'pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"; if absent: ~/bin/gv-bridge-ensure.sh' ;;
      not_attempted)       echo "check the CDP wiring and that Chrome answers on port 9224" ;;
      service_unreachable) echo "systemctl status rotary-phone on radio" ;;
      field_missing)       echo "check what build is deployed: sha256sum /opt/rotary-phone/RotaryPhoneController.Server" ;;
      unknown_outcome)     echo "read the journal: journalctl --user -u gv-session-alarm -n 50" ;;
      *)                   echo "" ;;
    esac
}

severity_for() {
    case "$1" in
      browser_stale|browser_unreachable|service_unreachable) echo "alert" ;;
      not_attempted|field_missing|unknown_outcome)           echo "warning" ;;
      ok)                                                    echo "info" ;;
    esac
}

# --- Incident threading -------------------------------------------------------
# ⛔ The thread key names the INCIDENT and is stable across its whole life — never
# a timestamp of the message, never a status, never the condition. One sign-out
# is ONE thread from detection to all-clear, and the RESOLVED replies INTO it.
#
# ⚠ This is load-bearing, not cosmetic. RESOLVED is routed to the quiet lane, and
# it is only safe to be quiet BECAUSE it threads under an alert the owner was
# already notified about. A RESOLVED that opens a new thread is invisible, and
# the owner is left believing an incident is still open.
open_incident_thread() {
    INCIDENT_THREAD_KEY="${SOURCE_NAME}-gv-session-$(date -u +%Y%m%dT%H%M%SZ)"
    INCIDENT_OPENED_AT="$(now_utc)"
    post_notify "info" \
        "[${SOURCE_NAME}] 🧵 GV session — browser session incident" \
        "Subject: the box's Chrome Google Voice session (profile \`~/.config/gv-bridge-chrome\`, CDP 9224) on \`radio\`.
Closes when: \`browserRefreshOutcome\` returns \`Succeeded\` after an owner re-login.
Identifiers: thread \`${INCIDENT_THREAD_KEY}\`, status \`${STATUS_URL}\`, opened ${INCIDENT_OPENED_AT}." \
        "" \
        "${SOURCE_NAME}-gv-session-thread-${INCIDENT_THREAD_KEY}" \
        "$INCIDENT_THREAD_KEY"
}

# --- Decide and post ----------------------------------------------------------
# POST ONLY ON TRANSITION. The owner's chat policy forbids reposting an unchanged
# state: three "nothing to report" titles in a row means the threshold is wrong,
# not that three things happened. The underlying condition here persists for
# HOURS, so an every-tick alarm would be almost entirely repetition.
if [ "$condition" = "ignore" ]; then
    log "outcome=TornDown — service teardown, not a fault. Nothing posted."
elif [ "$condition" = "$LAST_POSTED_CONDITION" ]; then
    log "condition unchanged since the last post (${condition}); nothing posted."
elif [ "$PENDING_POLLS" -lt "$MIN_POLLS_TO_POST" ]; then
    log "condition=${condition} seen ${PENDING_POLLS}/${MIN_POLLS_TO_POST} consecutive polls; not posting yet."
elif [ "$condition" = "ok" ]; then
    if [ -n "$INCIDENT_THREAD_KEY" ]; then
        # RESOLVED — quiet, and it MUST reply into the open thread.
        post_notify "info" \
            "$(title_for ok)" \
            "$(now_utc) · RESOLVED
\`browserRefreshOutcome\` is **Succeeded**: cookies pulled from the box's Chrome passed a live probe against
Google. The session opened at ${INCIDENT_OPENED_AT} is closed.
Action: none." \
            "" \
            "${SOURCE_NAME}-gv-session-resolved-${INCIDENT_THREAD_KEY}" \
            "$INCIDENT_THREAD_KEY"
        if [ "$NOTIFY_FAILED" -eq 0 ]; then
            LAST_POSTED_CONDITION="ok"
            INCIDENT_THREAD_KEY=""
            INCIDENT_OPENED_AT=""
        fi
    else
        # Healthy, and no incident was ever open. Post nothing at all.
        log "healthy, no open incident; nothing to post."
        LAST_POSTED_CONDITION="ok"
    fi
else
    [ -z "$INCIDENT_THREAD_KEY" ] && open_incident_thread
    post_notify \
        "$(severity_for "$condition")" \
        "$(title_for "$condition")" \
        "$(now_utc) · ${condition}
$(body_for "$condition")" \
        "$(action_for "$condition")" \
        "${SOURCE_NAME}-gv-session-${condition}" \
        "$INCIDENT_THREAD_KEY"
    [ "$NOTIFY_FAILED" -eq 0 ] && LAST_POSTED_CONDITION="$condition"
fi
```

**Acceptance** — lane **L**, against the stub; every assertion reads the **stub's log**, never our own state
file (spec acceptance 4 is explicit about this):

- **Transition only.** Serve `Stale` for five consecutive runs: the stub log holds **exactly two** messages
  — the 🧵 thread root and one `alert` — not ten.
- **`ok` from a cold start posts nothing.** With no state file, serve `Succeeded`: the stub log is **empty**.
  A healthy alarm on a healthy phone is silent.
- ⛔ **`Stale` → `Succeeded` closes in the same thread.** Serve `Stale`, then `Succeeded`. In the stub log:
  the `alert` and the later `info` RESOLVED carry an **identical `thread_key`**, and their `dedupe_key`s
  **differ**. Read both from the log; do not read the state file to confirm it.
- **The thread key survives a restart.** Serve `Stale`; delete nothing but restart the shell; serve
  `Succeeded`. Same `thread_key`. Then delete the state file mid-incident and confirm the RESOLVED opens a
  *new* thread — the failure mode, demonstrated once so its shape is known.
- **`ok` after a resolved incident is silent.** A third run serving `Succeeded` adds nothing to the log.
- **No severity in any title.** `grep -c '\[ALERT\]\|\[WARN\]\|ACTION:' ` over the recorded `title` fields
  is `0`.
- **No `action` on any `info`.** Every `info` record in the stub log lacks the key.
- **A flapping condition threads under one incident.** `Stale` → `Unreachable` → `Stale`: three condition
  messages, **one** `thread_key`, three distinct `dedupe_key`s.

---

#### Task 11 — The dead-man, and a correction to the spec's refresh rule · lane **L**

**Depends on:** Tasks 9, 10. Appends to `deploy/gv-session-alarm.sh`.

⭐ **The idea, which is the best thing in the spec.** The gateway raises an alert when a registered check
stops being refreshed. So the alarm can alarm **about itself, through a different code path than the one
that might be broken** — closing the loop the 2026-08-31 incident left open.

⛔ **But the spec states the refresh rule in a form that over-fires, and it must not ship as written.**

> §5.2: *"Refresh the heartbeat only after a healthy poll-and-notify cycle. **If the status poll fails**, or
> a required notify returns non-2xx, do not refresh the heartbeat."*

Take the case that rule is aimed at and the case it catches by accident:

| Situation | Spec's rule | What the owner is told | Is that true? |
|---|---|---|---|
| notify path broken, poll fine | suppress | "the alarm is dead" | ✅ **yes** — this is the whole point |
| **service down, alarm reports it perfectly** | **suppress** | **"the alarm is dead"** | ⛔ **no — the alarm is working** |

The second row is a **false alarm about the alarm**, raised in exactly the situation where the owner is
already dealing with a real one. And §5.2's own warning says why that is the worst outcome available: a
false alarm *"gets the alarm muted by the human."* Suppressing on a failed **poll** confuses *"the thing I
watch is broken"* with *"I am broken."*

**Corrected rule, implemented below:**

> **Refresh the heartbeat when the cycle COMPLETED — a conclusion was reached and everything that had to be
> delivered was delivered. Suppress it when a required notify failed, or state could not be persisted. Do
> NOT suppress merely because the service was unreachable: that is a condition this alarm exists to report,
> and reporting it successfully IS the alarm working.**

⭐ **The broken-notify case is still covered twice over, which is what makes the correction safe.** If the
gateway or the token is broken, the heartbeat POST goes through **the same transport and the same bearer**
and fails too, so the script exits non-zero and the check is not refreshed. If the gateway accepts
heartbeats but refuses notifies — the 422 case — `NOTIFY_FAILED` catches it. There is no gap between them.
**Flagged for the owner as decision Q1 in §4.**

```bash
# --- Dead-man -----------------------------------------------------------------
# POST /v1/heartbeat registers/refreshes a check; the gateway raises an alert on
# our route if it is not refreshed within `grace`.
#
# ⚠ GRACE MUST EXCEED THE TIMER INTERVAL WITH REAL MARGIN. The timer is 5m; grace
# is 30m — six intervals. Ordinary jitter, a slow poll, a boot, or one missed run
# must not raise "the alarm is dead", because a dead-man that cries wolf is a
# dead-man that gets muted, and then nothing is watching anything.
#
# ⚠ LOWER-CASE ONLY. `grace` is case-sensitive: "30M" is a 422, and a 422 here
# means THE CHECK IS NEVER REGISTERED — no dead-man at all, silently.
HEARTBEAT_CHECK_ID="${GV_ALARM_HEARTBEAT_CHECK_ID:-gv-session-alarm}"
HEARTBEAT_SCHEDULE="${GV_ALARM_HEARTBEAT_SCHEDULE:-5m}"
HEARTBEAT_GRACE="${GV_ALARM_HEARTBEAT_GRACE:-30m}"

refresh_heartbeat() {
    local resp rc http out payload
    payload="$(jq -nc \
        --arg source   "$SOURCE_NAME" \
        --arg check_id "$HEARTBEAT_CHECK_ID" \
        --arg schedule "$HEARTBEAT_SCHEDULE" \
        --arg grace    "$HEARTBEAT_GRACE" \
        '{source:$source, check_id:$check_id, schedule:$schedule, grace:$grace}')" || return 1

    resp="$(curl -sS --max-time 15 -X POST \
        -H "Authorization: Bearer ${ROTARYPHONE_GATEWAY_TOKEN}" \
        -H 'Content-Type: application/json' \
        -w $'\n%{http_code}' \
        --data-binary "$payload" \
        "${GATEWAY_URL}/v1/heartbeat" 2>&1)"
    rc=$?
    http="${resp##*$'\n'}"
    out="${resp%$'\n'*}"

    if [ "$rc" -ne 0 ]; then
        log "HEARTBEAT FAILED: curl exit ${rc}. Detail: ${out}"
        return 1
    fi
    case "$http" in
        2*) log "heartbeat refreshed http=${http} schedule=${HEARTBEAT_SCHEDULE} grace=${HEARTBEAT_GRACE}"; return 0 ;;
        *)  log "HEARTBEAT FAILED: http=${http}. THERE IS NO DEAD-MAN until this succeeds — check grace/schedule case (lower-case only). Gateway said: ${out}"
            return 1 ;;
    esac
}

# --- Persist, then decide whether we have earned the heartbeat ----------------
state_ok=1
write_state || state_ok=0

if [ "$NOTIFY_FAILED" -gt 0 ]; then
    log "NOT refreshing the heartbeat: ${NOTIFY_FAILED}/${NOTIFY_ATTEMPTED} notifications this cycle were NOT delivered. The gateway will raise the missing-check alert on our behalf, through a path that is not the one that just broke."
    exit 1
fi
if [ "$state_ok" -eq 0 ]; then
    log "NOT refreshing the heartbeat: state could not be persisted, so the next run cannot tell a transition from a repeat and would either spam or go silent."
    exit 1
fi

# ⚠ A FAILED POLL DOES NOT SUPPRESS THE HEARTBEAT — see the plan's Task 11.
# "The service is down" is a condition this alarm exists to REPORT. Reporting it
# successfully is the alarm working, and raising "the alarm is dead" on top of a
# real outage is how an alarm gets muted.
if [ "$poll_ok" -ne 1 ]; then
    log "the status poll failed and was reported; the cycle still COMPLETED, so the heartbeat is refreshed."
fi

refresh_heartbeat || exit 1
exit 0
```

**Acceptance** — lane **L**, against the stub. ⛔ **Every one reads the check state back through
`GET /v1/heartbeat/rotaryphone`, not the POST's return code.** Verifying that the refresh *ran* and
inferring that the check *moved* is the §6 failure applied to the dead-man itself.

| Case | Setup | `refresh_count` / `last_seen` | exit |
|---|---|---|---|
| healthy, nothing to say | `Succeeded`, no incident | **increments** | 0 |
| a real alert delivered | `Stale`, stub accepts | **increments** | 0 |
| **service down, reported** | connection refused, stub accepts | ⛔ **increments** — the corrected rule | 0 |
| **notify refused** | `--fail-notify 500` | ⛔ **does NOT move** | 1 |
| **forced 422** | `GV_ALARM_ACTION_MAX=9999`, 250-char action | ⛔ **does NOT move** | 1 |
| state unwritable | `STATE_FILE` under a read-only dir | **does NOT move** | 1 |
| **bad grace** | `GV_ALARM_HEARTBEAT_GRACE=30M` | 422; check **never registered**; journal names the case rule | 1 |

- ⛔ **Row 5 is spec acceptance 6 in full** — an over-long action forced through, journaled as a failure,
  **and** the heartbeat suppressed. All three observations, or the row does not pass.
- ⛔ **Row 7 is the trap that would leave the dead-man silently absent.** Assert `GET /v1/heartbeat/rotaryphone`
  returns **404**, not that the POST returned 422. The distinction is the whole point.
- The default `grace` is `30m` and the default `schedule` is `5m`, both lower-case, and the harness asserts
  the literal strings that reached the stub.

⚠ **Field-shape caveat, and it must not be skipped.** `schedule`/`grace`/`check_id` come from spec §4.4,
whose own source is `aitrader/docs/chat-gateway-requirements.md` — **not readable from this repo**. The stub
encodes the spec's description, so a *mis-transcribed field name* would pass every lane-L test and fail
silently on the real gateway, leaving no dead-man. **Task 17 must therefore read the real check back with
`GET /v1/heartbeat/rotaryphone` before this is considered done.**

---

#### Task 12 — Two guards: the harness, and the copy-drift check · lane **L** + **U**

**Depends on:** Tasks 7–11.

**12a — `deploy/tests/repro-gv-session-alarm.sh`.** The single entry point that runs every lane-L case
above. Structure, with the two fixtures that make it hermetic:

```bash
#!/usr/bin/env bash
# End-to-end harness for gv-session-alarm.sh. No box, no token, no network
# beyond loopback. Runs the stub gateway and a stub status endpoint, drives the
# alarm through every condition, and asserts against WHAT THE STUBS RECORDED —
# never against the alarm's own state file, and never against "it ran".
set -uo pipefail

WORK="$(mktemp -d)"; trap 'chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"; kill %1 %2 2>/dev/null' EXIT
export HOME="$WORK/home"; mkdir -p "$HOME/.local/state"

cat > "$HOME/.rotaryphone-env" <<'EOF'
ROTARYPHONE_GATEWAY_URL=http://127.0.0.1:8099
ROTARYPHONE_GATEWAY_TOKEN=stub-token
EOF

# A status endpoint whose body is whatever is in $WORK/status.json, and whose
# HTTP code is whatever is in $WORK/status.code. Both are rewritten between
# cases, so one server covers every scenario including "service down" (code 000
# = refuse the connection by stopping the server).
python3 "$(dirname "$0")/gv-alarm-status-stub.py" --port 8098 --dir "$WORK" &
python3 "$(dirname "$0")/gv-alarm-gateway-stub.py" --port 8099 --log "$WORK/gw.jsonl" &
sleep 1

export GV_ALARM_STATUS_URL=http://127.0.0.1:8098/api/gvbridge/status
export GV_ALARM_STATE_FILE="$HOME/.local/state/gv-session-alarm.state"

serve()  { printf '%s' "$1" > "$WORK/status.json"; printf '%s' "${2:-200}" > "$WORK/status.code"; }
run()    { bash "$(dirname "$0")/../gv-session-alarm.sh"; echo "$?"; }
gwlog()  { jq -c 'select(.kind=="notify" and .status==202) | .body' "$WORK/gw.jsonl"; }
reset()  { : > "$WORK/gw.jsonl"; rm -f "$GV_ALARM_STATE_FILE"; }

fail=0
check() { # check NAME EXPECTED ACTUAL
    if [ "$2" = "$3" ]; then echo "  PASS $1"
    else echo "  FAIL $1: expected [$2] got [$3]"; fail=1; fi
}

# --- every case from Tasks 8, 9, 10 and 11 goes here, one `reset` apart ---
# (one worked example; the rest follow the same shape)
echo "case: Stale -> Succeeded closes in the SAME thread"
reset
serve '{"browserRefreshOutcome":"Stale","browserSessionStale":true}'
run >/dev/null
serve '{"browserRefreshOutcome":"Succeeded","browserSessionStale":false}'
run >/dev/null
alert_thread="$(gwlog | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
resolved_thread="$(gwlog | jq -r 'select(.severity=="info" and (.title|test("recovered"))) | .thread_key' | head -1)"
check "same thread_key"  "$alert_thread" "$resolved_thread"
check "thread_key is set" "yes" "$([ -n "$alert_thread" ] && echo yes || echo no)"
alert_dedupe="$(gwlog | jq -r 'select(.severity=="alert") | .dedupe_key' | head -1)"
resolved_dedupe="$(gwlog | jq -r 'select(.severity=="info" and (.title|test("recovered"))) | .dedupe_key' | head -1)"
check "dedupe_keys differ" "different" \
      "$([ "$alert_dedupe" != "$resolved_dedupe" ] && echo different || echo same)"

exit "$fail"
```

Plus `deploy/tests/gv-alarm-status-stub.py` — a ~30-line server that serves `$dir/status.json` with the
code in `$dir/status.code`, so a case can be changed without restarting anything.

**12b — `deploy/tests/check-alarm-copy-drift.sh`, wired into `dotnet test` · lane U.** §0.5's guard:

```bash
#!/usr/bin/env bash
# The alarm QUOTES the service. A quotation that has silently stopped matching
# its source is worse than a paraphrase: it attributes words to the service that
# the service does not say, in a message an operator will act on.
#
# Each sentence below must appear in BOTH deploy/gv-session-alarm.sh and the C#
# that emits it. Compared after collapsing whitespace, because the C# is split
# across string-concatenation lines and the shell is not.
set -uo pipefail
cd "$(dirname "$0")/../.."

SH="deploy/gv-session-alarm.sh"
CS="src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs"
flat() { tr -d '\n' < "$1" | tr -s ' '; }
SH_FLAT="$(flat "$SH")"
# Strip C# string-concatenation seams: `" + "` and `"\n + "` become nothing.
CS_FLAT="$(tr -d '\n' < "$CS" | tr -s ' ' | sed 's/" *+ *"//g')"

fail=0
while IFS= read -r q; do
    [ -z "$q" ] && continue
    case "$SH_FLAT" in *"$q"*) ;; *) echo "MISSING FROM $SH: $q"; fail=1 ;; esac
    case "$CS_FLAT" in *"$q"*) ;; *) echo "MISSING FROM $CS: $q"; fail=1 ;; esac
done <<'QUOTES'
Google refused it. The working on-disk set was NOT overwritten.
ACTION: re-login at voice.google.com.
CHROME WAS UNREACHABLE on CDP port
so the Google login was never tested.
the browser was NEVER CONSULTED
QUOTES

[ "$fail" -eq 0 ] && echo "alarm copy matches the service's own wording"
exit "$fail"
```

Wire it into the build so it cannot be forgotten — in
`src/RotaryPhoneController.GVBridge.Tests/RotaryPhoneController.GVBridge.Tests.csproj`:

```xml
  <!-- The alarm script quotes GVApiAdapter's own alert wording verbatim. Nothing else connects the
       two copies, so this runs on every test build. Skipped off Linux, where the shell is absent;
       CI and the box both run Linux, and the guard is worthless if it silently passes there. -->
  <Target Name="CheckAlarmCopyDrift" BeforeTargets="Build" Condition="'$([MSBuild]::IsOSPlatform(Linux))' == 'true'">
    <Exec Command="bash $(MSBuildProjectDirectory)/../../deploy/tests/check-alarm-copy-drift.sh" />
  </Target>
```

**Acceptance:**

- `bash deploy/tests/repro-gv-session-alarm.sh` exits **0** and prints a `PASS` line for every case in
  Tasks 8–11. Its exit code is the gate; a harness that prints `FAIL` and exits 0 is not a harness.
- ⛔ **The drift guard is proven by breaking it.** Change one word in the shell quote, run
  `dotnet build`, and watch it **fail** naming the sentence. Restore it. A guard nobody has seen fail is a
  guard nobody knows is wired up — the same rule as Task 4's order pin.
- The harness leaves nothing behind: no listener on 8098/8099, no temp dir.

---

#### Task 13 — The systemd user units · lane **L**

**Depends on:** Task 8 (for `--print-config`). Two files under `deploy/systemd/`; they ship automatically,
because `Deploy-ToLinux.ps1:180` globs the whole directory.

`deploy/systemd/gv-session-alarm.service`:

```ini
[Unit]
Description=GV session alarm — transport an existing signal to a human
Documentation=file:///opt/rotary-phone/docs/plans/gv-session-alarm.md

[Service]
Type=oneshot
ExecStart=%h/bin/gv-session-alarm.sh

# ⛔ DELIBERATELY NO EnvironmentFile=. The script SOURCES ~/.rotaryphone-env
# itself and exits NON-ZERO when it is missing. `EnvironmentFile=-%h/...` would
# make a missing file a silent no-op — which is the aitrader 2026-08-14 failure
# exactly: the alarm ran, notified nobody, and reported success. A user timer
# inherits no login-shell environment, so the sourcing must be explicit and its
# absence must be loud.

# A failed run must be VISIBLE in `systemctl --user status`, so no Restart=.
# Retrying would blur the signal the exit code carries, and the 5-minute timer
# is the retry.
```

`deploy/systemd/gv-session-alarm.timer`:

```ini
[Unit]
Description=Run the GV session alarm every 5 minutes

[Timer]
# 5 minutes is generous on purpose: the condition persists for HOURS and changes
# only when the 20-minute cron or the recovery ladder attempts a refresh. We are
# not catching a transient.
OnBootSec=3min
OnUnitActiveSec=5min
AccuracySec=30s

# ⚠ Persistent=false. A missed window must not fire a burst of catch-up runs at
# boot — each would be a fresh poll of the same unchanged state. The gateway's
# 30-minute grace is what covers a gap, not a replay.
Persistent=false

Unit=gv-session-alarm.service

[Install]
WantedBy=timers.target
```

**Acceptance** — lane **L** where a user bus exists, otherwise deferred to Task 5's on-box run:

- `systemd-analyze verify deploy/systemd/gv-session-alarm.service deploy/systemd/gv-session-alarm.timer`
  reports no errors.
- ⛔ **No `EnvironmentFile=` in either unit.** `grep -c EnvironmentFile deploy/systemd/gv-session-alarm.*`
  is `0`. This is asserted rather than trusted because it is the one line whose *presence* would quietly
  undo spec §5.1.
- `OnUnitActiveSec=5min` and the heartbeat's default `grace=30m` are consistent: grace is **6×** the
  interval. If either is ever changed, the other must be re-derived — recorded in §4 as a standing
  invariant, not just a comment.
- The timer's `[Install]` section exists, so `--enable` in Task 2 has something to enable.

---

### Phase 3 — the `gv-login` hazard pointing at Radio Console

---

#### Task 14 — `gv-login` must connect to the bridge, and must not be able to kill the kiosk · lane **U**

**Depends on:** nothing. Spec §8.1. **Latent today** — `~/.local/share/RotaryPhone` does not exist on the
box, so this has never run there, and it cannot clobber good cookies (it returns `false` at `:152`/`:175`
before any save). **It is fixed now because it is the command an operator reaches for when the login
breaks — the worst possible moment to discover it takes out the other service's kiosk.**

The chain: `CookieRetriever.cs:15` hardcodes CDP port **9222**; the bridge listens on **9224**. So the
"connect to existing Chrome" branch at `:36-38` can **never** succeed, `gv-login` always falls through to
`:47-61`, and that kills Chrome **by process name** (`chrome`, `chromium`, `chromium-browser`) **with no
profile filter**. `~/.config/radio-kiosk-chrome` is present on the box.

**14a — the port.** In `src/RotaryPhoneController.GVBridge/Auth/CookieRetriever.cs`, delete `:15` and take
the port as a parameter:

```csharp
    // ⛔ The port used to be a private const 9222 here while the bridge listens on
    // GVBridgeConfig.ChromeCdpPort (9224). The "connect to an existing Chrome" branch below could
    // therefore NEVER succeed, so gv-login ALWAYS fell through to the launch branch — which killed
    // Chrome by process name. A wrong constant turned an optional fallback into the only path.
    // It is a parameter now precisely so it cannot drift back out of step with the config.
    public static async Task<bool> RetrieveAndSaveAsync(
        string cookiePath, string keyPath, int cdpPort,
        Action<string>? log = null, CancellationToken ct = default)
```

Replace every `DebugPort` with `cdpPort` (`:37`, `:38`, `:74`, `:94`).

**14b — the kill, scoped to our own profile.** Replace `:51-61`:

```csharp
            // Kill existing Chrome/Chromium to free the debug port.
            //
            // ⛔ SCOPED TO OUR OWN PROFILE, and that is not defensive style — it is a cross-service
            // boundary. This used to kill EVERY process named chrome/chromium/chromium-browser with no
            // filter at all. `radio` also runs Radio Console's kiosk Chrome on
            // ~/.config/radio-kiosk-chrome, and the GV bridge's own browser on
            // ~/.config/gv-bridge-chrome. Either would have been killed by an operator running
            // `gv-login` to fix a broken login — see docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md.
            KillOwnDebugProfileChrome(debugProfilePath, cdpPort, log);
```

…moving the `debugProfilePath` computation above it, and adding:

```csharp
    /// <summary>
    /// True when <paramref name="commandLine"/> is a browser started against
    /// <paramref name="profileDir"/>. Pure, so the boundary it enforces is unit-testable without
    /// starting a process.
    /// </summary>
    /// <remarks>
    /// ⚠ Matches on <c>--user-data-dir=</c> and NOT on the process name. A name match is what made the
    /// old code able to reach Radio Console's kiosk. The trailing-separator and quoted forms are both
    /// accepted because Chrome is invoked both ways on this box; a bare prefix match is NOT used, or
    /// <c>~/.config/gv-bridge-chrome</c> would match <c>~/.config/gv-bridge-chrome-backup</c>.
    /// </remarks>
    internal static bool IsOurDebugProfileProcess(string? commandLine, string? profileDir)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(profileDir))
            return false;

        var dir = profileDir.TrimEnd('/', '\\');
        foreach (var form in new[] { $"--user-data-dir={dir}", $"--user-data-dir=\"{dir}\"" })
        {
            var idx = commandLine.IndexOf(form, StringComparison.Ordinal);
            if (idx < 0) continue;
            // The next character must end the value, or "…/gv-bridge-chrome" would match
            // "…/gv-bridge-chrome-backup".
            var after = idx + form.Length;
            if (after >= commandLine.Length) return true;
            var c = commandLine[after];
            if (c is ' ' or '\0' or '/' or '"' or '\'') return true;
        }
        return false;
    }

    private static void KillOwnDebugProfileChrome(string profileDir, int cdpPort, Action<string> log)
    {
        if (!OperatingSystem.IsLinux())
        {
            // ⛔ Refuse rather than fall back to a name match. There is no safe way to identify our own
            // browser here, and the failure mode of guessing is killing someone else's.
            log($"Not Linux: refusing to kill any browser by process name. If CDP port {cdpPort} is busy, "
                + $"close the browser using {profileDir} by hand and re-run.");
            return;
        }

        var killed = 0;
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out var pid)) continue;

            string cmdline;
            try { cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' '); }
#pragma warning disable CA1031
            catch { continue; }   // the process exited, or is not ours to read
#pragma warning restore CA1031

            if (!IsOurDebugProfileProcess(cmdline, profileDir)) continue;

            try { Process.GetProcessById(pid).Kill(); killed++; }
#pragma warning disable CA1031
            catch { /* best effort */ }
#pragma warning restore CA1031
        }
        log(killed > 0
            ? $"Killed {killed} browser process(es) using {profileDir}."
            : $"No browser is using {profileDir}; killed nothing.");
    }
```

**14c — the call site.** `src/RotaryPhoneController.Server/Program.cs:29-39`:

```csharp
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        // ⚠ ADDED: the box's authoritative settings live in appsettings.Production.json — it is the file
        // the deploy deliberately does NOT overwrite. Reading only appsettings.json meant gv-login could
        // resolve a different CDP port (and different cookie paths) than the running service uses, which
        // is the same class of defect as the hardcoded 9222 it is being fixed alongside.
        .AddJsonFile("appsettings.Production.json", optional: true)
        .Build();
    var gvConfig = config.GetSection("GVBridge");

    var cookiePath = gvConfig["CookieFilePath"] ?? "data/gv-cookies.enc";
    var keyPath = gvConfig["CookieKeyFilePath"] ?? "data/gv-key.bin";
    // Default 9224 — GVBridgeConfig.ChromeCdpPort's own default, and the port the bridge listens on.
    var cdpPort = int.TryParse(gvConfig["ChromeCdpPort"], out var configuredPort) ? configuredPort : 9224;

    var result = await RotaryPhoneController.GVBridge.Auth.CookieRetriever.RetrieveAndSaveAsync(
        cookiePath, keyPath, cdpPort,
        msg => logger.LogInformation("{Message}", msg));
```

**14d — the test that is the actual acceptance.** New
`src/RotaryPhoneController.GVBridge.Tests/Auth/CookieRetrieverProfileScopeTests.cs`:

```csharp
using RotaryPhoneController.GVBridge.Auth;

namespace RotaryPhoneController.GVBridge.Tests.Auth;

public class CookieRetrieverProfileScopeTests
{
    // The two command lines that actually run on `radio`, alongside each other.
    private const string RadioKioskChrome =
        "/usr/bin/google-chrome --user-data-dir=/home/mmack/.config/radio-kiosk-chrome --kiosk https://localhost:5173";
    private const string GvBridgeChrome =
        "/usr/bin/google-chrome --user-data-dir=/home/mmack/.config/gv-bridge-chrome --remote-debugging-port=9224 https://voice.google.com";

    [Fact]
    public void ItNeverMatchesRadioConsolesKioskChrome()
    {
        // ⛔ THIS IS THE WHOLE POINT OF THE CHANGE. The old code killed by PROCESS NAME, so this
        // command line was a match, and `gv-login` — the command an operator runs when the login is
        // already broken — would have taken out the other service's kiosk.
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            RadioKioskChrome, "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile"));

        // And it must not reach the GV bridge's own browser either: gv-login's launch branch owns a
        // DIFFERENT profile, and killing the bridge is how the session under repair gets destroyed.
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            GvBridgeChrome, "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile"));
    }

    [Fact]
    public void ItMatchesOurOwnProfile_InBothQuotedAndBareForms()
    {
        const string dir = "/home/mmack/.local/share/RotaryPhone/chrome-debug-profile";
        Assert.True(CookieRetriever.IsOurDebugProfileProcess(
            $"/usr/bin/google-chrome --remote-debugging-port=9224 --user-data-dir={dir} --no-first-run", dir));
        Assert.True(CookieRetriever.IsOurDebugProfileProcess(
            $"/usr/bin/google-chrome --user-data-dir=\"{dir}\" --no-first-run", dir));
        Assert.True(CookieRetriever.IsOurDebugProfileProcess($"chrome --user-data-dir={dir}", dir));
        Assert.True(CookieRetriever.IsOurDebugProfileProcess($"chrome --user-data-dir={dir}/", dir));
    }

    [Fact]
    public void ItDoesNotMatchAProfileThatMerelySharesAPrefix()
    {
        const string dir = "/home/mmack/.config/gv-bridge-chrome";
        Assert.False(CookieRetriever.IsOurDebugProfileProcess(
            $"chrome --user-data-dir={dir}-backup", dir));
    }

    [Theory]
    [InlineData(null, "/x")] [InlineData("chrome", null)]
    [InlineData("", "/x")]   [InlineData("chrome", "")]
    public void EmptyInputsMatchNothing(string? cmd, string? dir)
        => Assert.False(CookieRetriever.IsOurDebugProfileProcess(cmd, dir));
}
```

`InternalsVisibleTo` for the test assembly is already configured (the adapter tests reach
`BrowserRefreshOutcome`); confirm rather than assume, and add it to
`RotaryPhoneController.GVBridge.csproj` if not.

**Acceptance:**

- `dotnet test` green, with `ItNeverMatchesRadioConsolesKioskChrome` passing. ⛔ **Run it against the old
  name-matching logic first and watch it fail** — otherwise the test proves nothing about the change.
- `grep -c 9222 src/RotaryPhoneController.GVBridge/Auth/CookieRetriever.cs` is **0**.
- `grep -c "GetProcessesByName" src/RotaryPhoneController.GVBridge/Auth/CookieRetriever.cs` is **0**.
- Spec acceptance 9 (`gv-login` against a running bridge kills nothing) is a **box** check and is deferred
  to Task 17c. The unit tests above are what make that a confirmation rather than an experiment.

---

### Phase 4 — measure the threshold; do not choose it

---

#### Task 15 — Report the age distribution of a healthy session · lane **B**

**Depends on:** Task 4 deployed to the box (so the outcome is visible alongside the age).

⛔ **This task must NOT produce a number for the WARN threshold.** Spec §11 decision 3 says
*"Needs a measured baseline before a number is chosen — **do not guess one.**"* Today's session was ~2h old
at death; a healthy one runs for days. **A plausible-looking number here is worse than no number**, because
the alarm's whole credibility rests on it: too low and the owner mutes it, too high and it never fires.
**The threshold is chosen by the owner, after reading this task's report.**

Create `deploy/tools/sample-browser-session-age.sh` and run it **on the box** for at least **72 hours**:

```bash
#!/usr/bin/env bash
# Sample the box's browser-session age, so a WARN threshold can be CHOSEN rather
# than guessed. Writes one CSV row per sample. Read-only: it polls an endpoint.
#
#   nohup bash sample-browser-session-age.sh > /dev/null 2>&1 &
set -uo pipefail
OUT="${1:-$HOME/.local/state/gv-session-age-samples.csv}"
URL="${GV_ALARM_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"

# Every 5 minutes — fine enough to see the 20-minute cron's sawtooth, which is
# the thing that matters. Sampling at 20 minutes could alias with the cron and
# report a flat line for a signal that is anything but.
[ -f "$OUT" ] || echo "utc,outcome,age_seconds,validated_at,stale,cookies_valid" > "$OUT"
while :; do
    body="$(curl -sS --max-time 10 "$URL" 2>/dev/null)"
    if [ -n "$body" ]; then
        printf '%s,%s,%s,%s,%s,%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
            "$(jq -r '.browserRefreshOutcome // "ABSENT"'   <<<"$body")" \
            "$(jq -r '.browserSessionAgeSeconds // ""'      <<<"$body")" \
            "$(jq -r '.browserSessionValidatedAt // ""'     <<<"$body")" \
            "$(jq -r '.browserSessionStale'                 <<<"$body")" \
            "$(jq -r '.cookiesValid'                        <<<"$body")" >> "$OUT"
    else
        printf '%s,UNREACHABLE,,,,\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$OUT"
    fi
    sleep 300
done
```

**The statistic that matters is not "age now".** `browserSessionAgeSeconds` resets every time the 20-minute
cron successfully revalidates, so its instantaneous value is mostly a measure of how long ago the last cron
tick was — not of session health. **The threshold must sit above the largest age reached during a period
the session was demonstrably healthy**, which is the *peak of the sawtooth*, not its mean.

Report exactly this, and nothing more:

| Statistic | Why it is the one that matters |
|---|---|
| **max age reached while `outcome == Succeeded` throughout** | the number a threshold must exceed, or it fires on a healthy phone |
| p50 / p95 / p99 of the same population | shows whether the max is a routine peak or a one-off |
| **the largest gap between two consecutive `Succeeded` samples** | a cron tick that is merely *late* must not read as a dead session |
| count of `Unreachable` / `NotAttempted` samples in a healthy window | the noise floor a WARN would have to sit above |
| the observation window, in hours, and any restarts inside it | a 72-hour window cannot bound a session that "runs for days" |

**Acceptance:**

- ≥ 72 hours of samples exist on the box, and the CSV is attached to the PR.
- The report presents the five statistics above **and no recommended value**. ⛔ If the report contains a
  proposed threshold, the task has failed its only real requirement.
- The report states plainly whether 72 hours was long enough to bound the maximum. Today's evidence says a
  healthy session "runs for days" — so if `max` is still climbing at the end of the window, **the honest
  answer is "the window did not bound it", not a number derived from a truncated sample.**
- ⚠ The `aging` → WARN row of spec §4.3 is **left unimplemented** by this plan. `severity_for()` has no
  `aging` case, on purpose. It is added in a follow-up once the owner has chosen the number.

---

### Phase 5 — token-gated: prove it delivers

⛔ **Both tasks below are blocked on owner decision #1.** Nothing else in this plan is.

---

#### Task 16 — The env file, and enabling the timer · lane **T**

**Depends on:** Tasks 5, 13, and **owner decision #1**.

⚠ **The token must be `rotaryphone`-scoped.** Reusing `AITRADER_GATEWAY_TOKEN` would mis-attribute the
source and collide in the gateway's routing config — the alarm would arrive labelled as another project, in
another project's lane, and the owner would have no way to route it.

On the box, as the service user:

```bash
umask 077
cat > ~/.rotaryphone-env <<'EOF'
# Credentials for gv-session-alarm.sh. A systemd USER timer inherits no login-shell
# environment, so this file is sourced explicitly by the script and its absence is a
# hard, non-zero, journaled failure. Do not add `-` semantics anywhere for this file.
ROTARYPHONE_GATEWAY_URL=http://192.168.86.47:8085
ROTARYPHONE_GATEWAY_TOKEN=<the rotaryphone-scoped token>
EOF
chmod 600 ~/.rotaryphone-env

# Positive control BEFORE enabling anything: does this token work at all?
curl -sS -o /dev/null -w '%{http_code}\n' -X POST \
  -H "Authorization: Bearer $(. ~/.rotaryphone-env; echo "$ROTARYPHONE_GATEWAY_TOKEN")" \
  -H 'Content-Type: application/json' \
  -d '{"source":"rotaryphone","severity":"info","title":"[rotaryphone] alarm install — positive control","body":"If you are reading this, the token and the route work. No action."}' \
  http://192.168.86.47:8085/v1/notify

# Then a real run by hand, before any timer touches it
~/bin/gv-session-alarm.sh; echo "exit=$?"

bash /opt/rotary-phone/deploy/install-gv-session-alarm.sh --enable
systemctl --user list-timers 'gv-session-alarm.*'
```

**Acceptance:**

- ⛔ **The positive control is a DELIVERED MESSAGE the owner confirms seeing**, not a `202`. §6's whole
  lesson is that a mechanism reporting success is not the mechanism working. Do this before enabling the
  timer, so a token problem is found by one deliberate message rather than by 288 failing units a day.
- The hand-run exits **0** and the journal shows `heartbeat refreshed`.
- `systemctl --user list-timers 'gv-session-alarm.*'` — **plain, no `--all`** — lists the timer with a NEXT
  within 5 minutes. This is the half of spec acceptance 8 that Task 5 could not assert (§0.8).
- `~/.rotaryphone-env` is mode `600`. The token is **not** in the repo, not in `appsettings*.json`, and not
  in any file the deploy overwrites.
- `GET http://192.168.86.47:8085/v1/heartbeat/rotaryphone` returns a registered check whose `grace` reads
  **`30m`** — ⛔ **read it back from the gateway.** This is the field-shape caveat from Task 11: a
  mis-transcribed field name passes every lane-L test and leaves no dead-man.

---

#### Task 17 — The six forced failures · lane **T**

**Depends on:** Task 16. Spec acceptance 2, 3, 4, 6, 7 and 9. ⛔ **Every one is observed as a delivered
message or a read-back gateway state. Not a log line, not an exit code, not our own state file.**

Announce to the owner before starting — several of these deliberately break the phone for a few minutes.

**17a — Chrome stopped → `alert` (spec acceptance 2).** The case `browserSessionStale` reports as `false`.

```bash
pkill -f 'user-data-dir=/home/mmack/.config/gv-bridge-chrome'
systemctl --user stop gv-bridge-watchdog.timer     # or it restarts Chrome within 2 minutes
curl -s localhost:5004/api/gvbridge/status | jq '{browserRefreshOutcome, browserSessionStale}'
systemctl --user start gv-session-alarm.service
# … observe the chat channel …
systemctl --user start gv-bridge-watchdog.timer    # ⚠ RESTORE THIS. Do not leave it stopped.
```

Required: the status shows `"browserRefreshOutcome": "Unreachable"` **with `"browserSessionStale": false`**,
and **a message arrives** at severity `alert` titled `[rotaryphone] GV session — Chrome is gone, login
untested`. ⭐ Screenshot both together — the false boolean beside the delivered alert is the single clearest
statement of why this arc exists.

**17b — service stopped → `alert` (acceptance 3).** The case in-process detection structurally cannot cover.

```bash
sudo systemctl stop rotary-phone
systemctl --user start gv-session-alarm.service
sudo systemctl start rotary-phone
```
Required: a delivered `alert`, *"the service is not answering"*. And per Task 11's corrected rule, the
heartbeat **did** refresh — confirm via `GET /v1/heartbeat/rotaryphone`.

**17c — `gv-login` kills nothing (acceptance 9).** ⛔ Capture the kiosk's PIDs **before and after**:

```bash
pgrep -af 'user-data-dir=.*radio-kiosk-chrome' | tee /tmp/kiosk-before
pgrep -af 'user-data-dir=.*gv-bridge-chrome'   | tee /tmp/bridge-before
cd /opt/rotary-phone && ./RotaryPhoneController.Server gv-login
pgrep -af 'user-data-dir=.*radio-kiosk-chrome' > /tmp/kiosk-after
pgrep -af 'user-data-dir=.*gv-bridge-chrome'   > /tmp/bridge-after
diff /tmp/kiosk-before /tmp/kiosk-after && echo "KIOSK UNCHANGED"
diff /tmp/bridge-before /tmp/bridge-after && echo "BRIDGE UNCHANGED"
```
Required: both `diff`s are empty, **and** the log says `Connected to existing Chrome on port 9224` — the
branch that could never be reached before. ⚠ If it says `Launching Chrome`, 14a did not take; stop.

**17d — `Stale` → `Succeeded` closes in the same thread (acceptance 4).** Requires a real sign-out and
re-login, so pair it with the next genuine one rather than manufacturing it. Required: **read the thread in
the chat client** and confirm the RESOLVED is a reply inside the alert's thread. ⛔ Not confirmed by our
state file — that is the spec's explicit instruction and it is right: the state file records what we
*intended* to thread under.

**17e — the live 422 suppresses the dead-man (acceptance 6).**

```bash
GV_ALARM_ACTION_MAX=9999 ~/bin/gv-session-alarm.sh; echo "exit=$?"
curl -sS -H "Authorization: Bearer $TOKEN" http://192.168.86.47:8085/v1/heartbeat/rotaryphone | jq
```
Run it while a condition is active so a message is actually attempted. Required, all three: exit **1**; the
journal carries the gateway's 422 body **including the limit it names**; and the check's last-seen
**has not moved**. ⭐ This is where the real gateway either confirms the stub's 200-character rule or
corrects it — record whichever happens.

**17f — killing the timer raises a missing-check alert (acceptance 7).**

```bash
systemctl --user stop gv-session-alarm.timer
date -u    # start the clock
# … wait out the 30-minute grace …
systemctl --user start gv-session-alarm.timer
```
Required: **a delivered alert from the gateway**, naming the missing check, arriving within `grace` of the
last refresh. ⭐ **This is the acceptance most worth doing patiently**, because it is the only one that
proves the alarm can report its own death. Record the elapsed time; if it is much shorter than 30 minutes,
`grace` is not being honoured as written and the false-alarm risk in §5.2 is live.

**Acceptance for the task as a whole:**

- Six results, each with the **delivered artefact** — a screenshot, a thread, or a gateway read-back.
- ⛔ `gv-bridge-watchdog.timer` is `active` at the end, and Chrome is up. 17a stops it; leaving it stopped
  would remove the bridge's liveness cover while this arc claims to have improved observability.
- Any deviation from the stub's modelled behaviour (§4.4's traps, the heartbeat field shape) is written back
  into this plan and into the spec's §4.4 table. The stub encodes a *transcription* of a doc in another
  repo; Task 17 is where that transcription is checked against the thing itself.

---

### Phase 6 — record, and hand off what is not ours

---

#### Task 18 — Flag the exit-code change; do NOT implement it · lane **L**

**Depends on:** nothing. Spec §8, spec §11 decision 4.

⛔ **No code.** `gv-bridge-ensure.sh` is not modified by this arc, and neither is the deploy's treatment of
it. This task records three things so the next session does not rediscover them.

**18a — a comment at the edit site (tier 2).** At the top of `deploy/gv-bridge-ensure.sh`, above the lock:

```bash
# ⛔ EVERY PATH IN THIS SCRIPT EXITS 0, AND THAT IS A CROSS-REPO CONTRACT PROBLEM.
#
# Radio Console's KIOSK-2 launcher invokes this script and READS ITS EXIT CODE
# ("invoke-and-probe only", their INTEGRATIONS.md:746). It already cannot tell
# "already up" from "just launched" — both are 0. The flock below adds a THIRD
# outcome, "someone else holds the lock", which is also 0. In Radio Console's
# phrase, that is "a contract that has run out of vocabulary."
#
# ⛔ Do not fix this unilaterally, and do not deploy a changed exit code before it
# is ANNOUNCED in docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md's Change Log.
# A launcher that reads a code we quietly redefine is a silent breakage in the
# other service.
#
# ⚠ And per that spec §8, fixing this is a PREREQUISITE for deploying this file at
# all: the shipped copy has flock, the installed copy (Aug 18) does not, so any
# install of this script is what makes the third state real on the box.
# Tracked: docs/plans/gv-session-alarm.md Task 18; spec §8 and §11 decision 4.
```

**18b — the sequencing consequence, in the plan that would trip over it.** This is why the GV session alarm
does **not** travel on `setup-gvbridge.sh` (§0.2), and it is also a constraint on **PR #84 Task 10**, which
proposes exactly that install. Add a note to `docs/plans/deploy-tooling-honest-deploy-plan.md` under Task 10:

```markdown
> ⛔ **Cross-repo prerequisite added 2026-09-09.** Running `setup-gvbridge.sh` from the deploy INSTALLS
> `gv-bridge-ensure.sh`, whose shipped copy adds `flock … || exit 0`. Radio Console's KIOSK-2 launcher reads
> that exit code, and it already cannot distinguish two states; this would add a third, also arriving as 0.
> Per `docs/superpowers/specs/2026-09-09-gv-session-alarm-design.md` §8, **fixing the exit code is a
> prerequisite for deploying the shipped `gv-bridge-ensure.sh` at all**, and any change to it must be
> announced in the boundary doc's Change Log first. Task 10 is therefore blocked on an owner + Radio Console
> decision (spec §11 decision 4), not merely on box access.
```

**18c — nothing in the boundary doc's Change Log.** ⛔ Deliberate. The Change Log records **changes that
shipped**. A row announcing a change nobody has made turns a ledger into a wish-list, and the next reader
cannot tell which rows describe the box. The announcement row is written **by whoever makes the change**,
**before** it ships. Task 6's row is the only one this arc adds.

**Acceptance:**

- `git diff` for this task touches exactly two files, both comment/prose, and **no behaviour changes**.
- `deploy/gv-bridge-ensure.sh` is otherwise byte-identical: `git diff --stat` shows additions only.
- The boundary doc's Change Log gains **no** row from this task.
- Spec §11 decision 4 remains open and is listed as such in §4 below.

---

#### Task 19 — Verify the cron correction rather than writing it twice · lane **L**

**Depends on:** PR #84 merging. Spec §11 decision 5.

The spec assigns the `KNOWN-ISSUES.md` cron correction to "this arc". **It is already written** — §0.6:
commit `2c6797b` on PR #84, an additive annotation stating that the 20-minute cron is load-bearing and must
not be retired, with the journal evidence and the attribution chain.

**So this task authors nothing.** After #84 merges:

```bash
grep -n "DO NOT retire\|load-bearing" docs/KNOWN-ISSUES.md
sed -n '478,530p' docs/KNOWN-ISSUES.md
```

**Acceptance:**

- The M1 follow-up entry carries the correction, and the correction is **additive** — the original
  recommendation is annotated as superseded, not deleted. *Why it stopped being correct is the point*, and a
  silent rewrite destroys it.
- ⛔ **There is exactly one such correction.** Two annotations of the same entry saying the same thing is the
  failure this task exists to avoid.
- If #84 has **not** merged by the time this arc is ready, ⚠ **do not write the correction here to unblock
  the checklist.** Leave decision 5 open, note it in the PR body, and let it land with #84. Duplicating it
  costs more than leaving it.

---

## 3. Dependency graph and suggested order

```
Phase 0   1 ─┐                    (box baseline — do this FIRST, it is the "before")
             ├─ 2 ── 3            (narrow installer; the conditional drift trigger)
             │
Phase 1   4 ─┼─ 6                 (DTO field ─ boundary doc row)
             │
Phase 2   7 ── 8 ── 9 ── 10 ── 11 (stub ─ classify ─ post ─ thread ─ dead-man)
                            └── 12 (harness + copy-drift guard)
             13                   (systemd units — parallel with 8-11)
             │
Phase 1   ┌──┴── 5                (DEPLOY + on-box install gate: needs 2,3,4,8,13)
          │
Phase 3  14                       (gv-login — independent of everything above)
Phase 4  15                       (age baseline — needs 4 deployed, i.e. after 5)
Phase 5  16 ── 17                 (⛔ TOKEN-GATED — needs decision #1)
Phase 6  18                       (independent; do it any time)
         19                       (needs PR #84 merged)
```

**Suggested order for a single build session, and why:**

1. **Task 1** first, always. It is the only "before" that can be captured, and Task 3's warning path needs a
   real subject.
2. **Tasks 4, 14, 18** next — three independent, box-free, token-free commits that can be reviewed while
   the rest is written. 14 in particular is a latent cross-service hazard and does not deserve to be
   sequenced behind an alarm.
3. **Tasks 7 → 13** as one continuous stretch. They are one artefact split for reviewability, and the
   harness is what makes each step provable.
4. **Tasks 2, 3, then 5.** The first deploy of this arc is the moment §7's blocker is either resolved or
   caught.
5. **Task 15** starts as soon as Task 5 lands, because it needs 72 hours of wall-clock and everything else
   can proceed while it runs.
6. **Tasks 16, 17** when the token exists. **Task 19** when #84 merges.

⚠ **Two things are wall-clock-bound and should be started early rather than optimally:** Task 15 (72 hours)
and Task 17f (a 30-minute grace window). Neither blocks anything else.

⛔ **The one ordering that is not negotiable: Task 5 before the alarm is called done.** An alarm that
installs, reports success, and never executes is the failure mode this whole document is about, arrived at
from the inside.

---

## 4. Open questions and decisions

| # | Question | Owner | State |
|---|---|---|---|
| **D1** | A `rotaryphone`-scoped gateway token in `~/.rotaryphone-env` on `radio`. | **Owner** | ⛔ **open — gates Tasks 16, 17 and nothing else** |
| **Q1** | **Correction to spec §5.2's heartbeat rule** (Task 11): refresh on a *completed cycle* rather than suppressing on a failed *poll*, so a reported service outage does not also raise "the alarm is dead". | Owner to ratify | ⚠ **planned as corrected**; say so if you disagree |
| **Q2** | **Deviation from spec §4.4** (Task 9): the 422's body is journaled verbatim so the limit is visible, but the script does **not** adapt and re-send. A message reshaped by a failure handler is one no test covered. | Owner to ratify | ⚠ **planned as a constant cap** |
| **Q3** | `WARN` threshold for `browserSessionAgeSeconds`. | **Owner, after Task 15** | ⛔ **open — deliberately no number in this plan** |
| **Q4** | Exit-code semantics for `gv-bridge-ensure.sh`. Cross-boundary; announce before shipping; also blocks PR #84 Task 10. | Owner + Radio Console | ⛔ **open — flagged, not implemented (Task 18)** |
| **Q5** | `KNOWN-ISSUES.md` cron correction. | — | ✅ **already written on PR #84** (Task 19 verifies) |
| **Q6** | **Does PR #84 merge before or after this arc?** It is not a hard dependency (§0.2), but `main` has a deploy path that reports success while changing nothing (§0.9). | Owner | ⚠ **recommend merging #84 first** |

⭐ **On Q6, the recommendation with its cost.** Merging #84 first means this arc deploys onto a deploy that
can report its own failures. Not merging first means Task 5's on-box gate is the *only* thing standing
between "the alarm was installed" and "the deploy said so" — which it is designed to be, but it is a thinner
margin than it needs to be. The cost of waiting is that this arc sits on a branch; the cost of not waiting
is that a silent transfer failure is caught by one check instead of two.

**Standing invariant, recorded because it spans two files:** `gv-session-alarm.timer`'s `OnUnitActiveSec`
and the script's `GV_ALARM_HEARTBEAT_GRACE` are coupled — grace must stay at least **6×** the interval.
Change one and re-derive the other. A grace that has drifted below the interval produces a false "the alarm
is dead" on ordinary jitter, and §5.2 is explicit that a false alarm here gets the alarm muted.

---

## 5. Out of scope

| Not doing | Why |
|---|---|
| **Automating the Google login** | Spec §2 — **permanently** out of scope. No credential storage, no TOTP, no Playwright-driven login, in any task, ever. Storing a Google password and TOTP seed on a box shared with Radio Console is a worse posture than a 2-minute manual re-login. |
| **New detection logic** | Two correct detectors exist. Adding a third is the mistake the spec was written to avoid. |
| **Phase 2 — remote re-auth** | Spec §10. The bridge window sits *behind* the console window (stacking order, not off-screen — `--window-position` is a no-op under Wayland), and the operator gets no confirmation a re-login took. ⭐ **Note the honest limit of this arc: it shortens the DETECTION half of a two-hour outage and leaves the RECOVERY half untouched.** A separate design. |
| **Retiring the 20-minute cron** | Measured load-bearing 2026-09-09. It stays. |
| **Removing or repurposing `browserSessionStale`** | Cross-repo contract. Task 4 is additive only. |
| **Changing `gv-bridge-ensure.sh`'s exit codes** | Task 18 — flagged, announced, not implemented. Cross-boundary. |
| **The `aging` → WARN row of spec §4.3** | Needs Q3's number. `severity_for()` has no `aging` case on purpose. |
| **Fixing the "Cannot unlink" line, the tar path, or `$LASTEXITCODE`** | §0.7, §0.9. Real, reproduced, and **already fixed on PR #84**. Duplicate work. |
| **The transport-selection hazard at `Deploy-ToLinux.ps1:84`** | §0.10 — recorded, not scoped. |
| **`~/bin/gv-bridge-ensure.sh`'s three-week staleness** | PR #84 Task 10, and blocked on Q4. This arc makes it **visible on every deploy** (Task 3) rather than fixing it. |
| **BT/audio, `hci0`/`hci1`, WirePlumber, the kiosk profile** | Governed by the boundary doc. ⛔ Nothing in this plan touches any of them — the one place it comes close is Task 14, which exists precisely to stop `gv-login` reaching Radio Console's kiosk. |

---

## 6. The risk that this plan is most likely to realise

⚠ **Not a technical risk — a procedural one, and it has happened three times in two days in this repo.**

Every task above can be performed, reported as complete, and leave the box in a state where the alarm does
not fire. The three ways, in the order they are most likely:

1. **Task 5 is skipped or softened** because the deploy printed success. §0.9 is the measured reason that is
   not enough. The gate is `sha256sum ~/bin/gv-session-alarm.sh` and `--print-config` on the installed path.
2. **Tasks 16–17 are deferred for the token and never returned to**, leaving units installed, disabled, and
   inert — which is §7's failure with a different filename. ⚠ **An installed, disabled alarm is worse than
   no alarm**, because the repo now says one exists.
3. **A number is written for Q3** because the table looked unfinished. Spec §11 refuses to guess one, and a
   plausible number is what makes an alarm either muted or mute.

⭐ **The single sentence to keep from all of it**, and it is earned rather than borrowed — instance 8 was
written *inside the spec that catalogues this exact failure*, from memory of work done that same morning in
this same repo:

> **Read the installed artefact. Not a repo file, not a branch name, and not your own memory of this
> morning.**


---

## 7. What the build found — corrections to this plan

*Appended 2026-09-09 by the build session. **Additive: nothing above is rewritten.** The plan's own §0
is the model — a claim that stopped being correct is worth more annotated than deleted, because the
reason it stopped being correct is the content.*

### 7.1 ⛔ PR #84 HAS MERGED — §0.1, §0.2, §0.7, §0.9, Q6 and Task 19 all move

Measured, by asking git rather than by reading this plan:

```
$ git merge-base --is-ancestor e6f8018 main   ->  YES
$ grep -c install_atomic deploy/setup-gvbridge.sh   ->  4
$ grep -c "type f" deploy/Deploy-ToLinux.ps1        ->  2
```

⭐ **The plan was right to check, and right about the method — it just checked at a moment that has
passed.** §0.1's whole point was "read the installed artefact, not your memory of this morning", and the
same discipline applied one day later reverses its finding. That is not a flaw in §0.1; it is §0.1
working.

| Claim above | Now |
|---|---|
| §0.1 "the atomic-install work has **not** landed" | ⛔ **false** — `e6f8018` is on `main` |
| §0.2 reason **2** ("option one is blocked on an unmerged PR") | ⛔ **void**. Reasons 1, 3 and 4 stand, and the narrow-installer decision is **unchanged** |
| §0.7 / §0.9 the silent-stale-deploy path | ✅ **fixed on main** — the remote chain runs under its own `set -e`, and the archive is files-only |
| §0.9's "⚠ Assumption … its precondition is PR #84" | ✅ **satisfied** |
| **Q6** "does #84 merge before or after this arc?" | ✅ **answered: before** |
| **Task 19** "depends on PR #84 merging" | ✅ **unblocked, and VERIFIED** — `docs/KNOWN-ISSUES.md` carries exactly one additive `⛔ SUPERSEDED 2026-09-09` block, original preserved. Nothing was written twice |

⚠ Task 2's `install_atomic()` comment ("duplicated because #84 is UNMERGED") was **corrected in the
shipped file**, not left to read falsely. It stays duplicated for a better reason: borrowing the helper
would mean sourcing a 13 KB script this installer exists to avoid running.

### 7.2 ⛔ `systemctl --user list-timers --all` does NOT list a disabled timer — §0.8 and Task 5

§0.8 reasons that plain `list-timers` would miss an installed-but-disabled unit and concludes `--all` is
"the correct instrument". **Measured on the box against a real installed-but-disabled timer:**

```
$ systemctl --user list-unit-files 'gv-bridge-restart.*'
  gv-bridge-restart.timer   disabled  enabled        <- present
$ systemctl --user list-timers --all 'gv-bridge-restart.*'
  0 timers listed.                                   <- ABSENT
```

`--all` adds *inactive but LOADED* timers. A disabled unit that has never started is not loaded, so it
appears in neither. **`list-unit-files` alone is the instrument for the installed check**, and Task 5's
bullet *"`list-timers --all` lists `gv-session-alarm.timer`"* would have **failed on a correct install**.

⭐ This is §0.10's own fourth neighbour, committed inside the section that names it: a check that runs,
answers truthfully, and answers a different question than the one being asked. §0.8 was derived by
reasoning about what `--all` ought to mean, and never run.

### 7.3 ⛔ Three acceptance checks contradict this plan's own literal code

Each is stated as `grep -c … is 0`, and each is unsatisfiable because the plan's own code block for that
task contains the string being forbidden. The **intent** is right in all three; only the instrument is.

| Task | Check as written | Why it cannot pass | The meaningful check, which does pass |
|---|---|---|---|
| **14** | `grep -c 9222 CookieRetriever.cs` is 0 | 14a's own comment says *"used to be a private const 9222"* | no **non-comment** occurrence — ✅ 0 |
| **13** | `grep -c EnvironmentFile gv-session-alarm.*` is 0 | the unit's own comment says *"DELIBERATELY NO `EnvironmentFile=`"* twice | no **active directive** — ✅ 0 |
| **10** | flapping gives *"three distinct `dedupe_key`s"* | `dedupe_key` is `…-${condition}` — **per condition, by this plan's own design** (spec §4.4: *"Keys chosen per condition, never per message"*) — and `Stale` recurs | 3 messages, **1** thread_key, **2** distinct dedupe_keys |

### 7.4 ⚠ Task 10 predicts the wrong failure shape for a lost state file

The plan says: *"delete the state file mid-incident and confirm the RESOLVED opens a **new** thread."*
**Measured: it does not. There is no all-clear at all.** On a cold state file the `ok` branch finds no
open incident and correctly stays silent, so the owner is left holding an alert that is never closed.

That is **worse** than the predicted shape and it is **not fixable** — a cold start and a lost state file
are indistinguishable without state. Recorded, asserted in the harness as the real behaviour, and the
state file's durability is what covers it.

### 7.5 ⛔ Task 12b's guard would have run NOWHERE

The MSBuild wiring is `Condition="'$([MSBuild]::IsOSPlatform(Linux))' == 'true'"`, reasoned as *"CI and
the box both run Linux."* Measured: **there is no CI** (no `.github` in this repo at all), the owner
builds on Windows, and the box runs a self-contained publish with no SDK. So the guard could not fire on
any machine that exists.

⭐ **A guard that cannot fail is the first of the boundary doc's four neighbours**, and it would have been
this arc shipping the failure class it was written to correct. The shell script is kept as the Linux
convenience copy; the **enforcement** is now `AlarmCopyDriftTests.cs`, which makes the same comparison as
a unit test and therefore runs wherever `dotnet test` runs. Both were proven by breaking the quote and
watching each fail naming the sentence.

### 7.6 ⚠ Two smaller corrections, both applied in the shipped code

- **Task 3b's `scp $manifestPath`** passes a raw Windows temp path. `scp` reads the leading `C:` as a
  **remote host**; every other `scp` in that file already does `-replace '\\', '/'`. Applied.
- **Task 2's installer aborts a deploy on a box with no user D-Bus.** `set -euo pipefail` plus a bare
  `systemctl --user daemon-reload` exits non-zero **after** the three files are written, and Task 3b's
  `throw` then fails the whole deploy. The plan's own Task 2 acceptance says the reload *"is tolerated
  failing"* — the code does not. Made non-fatal and loud. Reproduced locally.

### 7.7 📌 Line references above are stale, and lane U needs the Windows SDK

- Every `Deploy-ToLinux.ps1` line number in §0.7, §0.9 and Task 3b predates #84's 275-line rewrite. The
  deploy-scripts copy block is now ~`:367-435`; the post-deploy hooks land before `=== Deploy Complete ===`.
- **Lane U cannot run in WSL on this workstation:** the projects target `net10.0` and WSL carries only
  SDK 8.0.131 / 9.0.115. The Windows SDK (10.0.400) is what runs them — `dotnet.exe` from WSL works.
