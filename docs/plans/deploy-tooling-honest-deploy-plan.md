# Plan — `Deploy-ToLinux.ps1`: stop reporting success without doing the job

**Scope doc:** [`deploy-tooling-honest-deploy.md`](deploy-tooling-honest-deploy.md) — read it first; the two
design decisions in it are settled and are not re-opened here.
**Date:** 2026-09-09. **Status:** planned, not started.
**Branch:** `fix/deploy-honest-tar-and-gvbridge-install`.

⚠ **Sequencing:** not on the coordinated-deploy critical path. Radio Console's `KIOSK-3` install and the
#78/#79 deploy come first. This fixes the tooling that runs *next* time, and every task below is written
so it can sit on a branch until that lands.

> **There is no work queue in this repo.** `docs/BUILDER_QUEUE.md` and `docs/ROADMAP.md` do not exist and
> are not being created. This plan file **is** the handoff artefact: whoever builds this executes the
> tasks below in order. Nothing needs to be added anywhere else.

---

## 0. What changed since the scope doc was written

The scope doc told the planner to re-derive the clobber mechanism rather than build on the recorded one.
That was done twice over — **locally with no box**, and then confirmed by a **live deploy on `radio`**
while this plan was being written. **Eight findings came back. Seven of them contradict a document, a
code comment, or an instruction given to this plan**; the eighth confirms something worth having checked.
They are stated first because most of the task list is shaped by them.

### 0.1 ⛔ The recorded mechanism is FALSIFIED — the restore does run, and it works

`docs/KNOWN-ISSUES.md:126-128` states:

> Reproducing that tar-pipe **on Linux**, `--unlink-first` errors on directories, so `tar` exits **2**.
> With `set -e -o pipefail` the chain aborts **before** the restore `mv` runs — leaving the box on the
> repo's template config.

**The first sentence is true. The second is false.** `set -e -o pipefail` is set in the *local*
PowerShell-invoked script (`Deploy-ToLinux.ps1:125`); backup → extract → restore is a `;`-separated
string executed by the *remote* shell, which does not inherit it. `pipefail` can only abort the **local**
script, and by then the remote work has already finished.

**Local reproduction** (exact chain shape, plain `sh -c`):

```
tar: .: Cannot unlink: Invalid argument
tar: ./sub: Cannot unlink: Directory not empty
tar: Exiting with failure status due to previous errors

CHAIN_EXIT=0
dst/appsettings.Production.json:BOX-AUTHORITATIVE   <-- restored
dst/RotaryPhoneController.Server:NEW-BINARY         <-- extracted anyway
dst/sub/lib.so:NEW-LIB                              <-- extracted anyway
```

**Live confirmation, a real deploy on the box**, same conclusion by three independent facts:

```
rsync not found, using tar-pipe over ssh (bash)...
tar: .: Cannot unlink: Invalid argument
tar: ./wwwroot: Cannot unlink: Directory not empty
tar: ./wwwroot/assets: Cannot unlink: Directory not empty
tar: Exiting with failure status due to previous errors

baseline sha256[16]   b3d0c6972722fb3f
after deploy          b3d0c6972722fb3f    IDENTICAL      <-- content survived
mtime                 moved to 11:28                     <-- but it WAS overwritten
/tmp/rp-prod.bak      GONE                               <-- and the restore consumed the backup
BluetoothAdapter      hci1                intact
```

sha unchanged **plus** mtime moved **plus** backup consumed proves the sequence rather than assuming it:
tar overwrote the file, and the restore `mv` put it back.

### 0.2 ✅ The surviving mechanism — both ends of the dance are best-effort

⛔ **Re-scope, do not de-scope.** The defect is real; it is simply not "the restore never runs". Both ends
are `|| true`:

```sh
cp -f $TargetPath/appsettings.Production.json /tmp/rp-prod.bak 2>/dev/null || true
...
[ -f /tmp/rp-prod.bak ] && mv -f /tmp/rp-prod.bak $TargetPath/appsettings.Production.json || true
```

If the **backup** step fails for any reason — first deploy with no file yet, `/tmp` not writable, disk
full, a `/tmp/rp-prod.bak` another uid owns in a sticky `/tmp` — the `2>/dev/null || true` swallows it,
tar overwrites the config, the `[ -f ]` guard is false or points at a **stale** file, and **no restore
happens.** The box silently keeps the repo template, and nothing in the deploy says so.

**Reproduced locally** (read-only parent directory holding a stale backup — the cheapest faithful stand-in
for a `/tmp` this uid cannot write):

```
config_now=TEMPLATE-FROM-REPO     <-- clobbered
backup_survives=yes               <-- exactly the state PR #72 UAT found
```

Two further points that keep this a live hazard rather than a solved one:

- The live observation is **one run, not a proof it always holds.** Two deploys racing, or `/tmp` cleaned
  between the `cp` and the `mv`, break it.
- ⭐ **The fix therefore removes the state rather than protecting it.** Excluding the file at archive
  creation is strictly better than making a best-effort restore more reliable: the file is never
  overwritten, so nothing needs restoring, and the property holds regardless of which failure mode is in
  play. That is why Task 3 deletes the backup/restore dance instead of hardening it.

⚠ **Consequence for the scope doc's acceptance criterion.** *"A deliberately-failed rsync followed by the
tar path leaves the box's `appsettings.Production.json` byte-identical"* **passes against the unfixed
code** in the plain case — the live deploy above is exactly that test, and it passed. It is not a test of
the fix. Task 3 carries a criterion the current code fails.

### 0.3 ⛔ Priority correction — the tar path is not a fallback, it is the only path that runs today

The scope doc says the tar path is *"reachable on any rsync FAILURE, not only rsync's absence"*. True, and
now understated: **`rsync` is absent from the deploying machine's PowerShell `PATH`**, so
`Get-Command rsync` returns nothing and **every deploy from that machine takes the hazardous path**. The
live run above opens with `rsync not found, using tar-pipe over ssh (bash)...`.

The owner is installing `rsync`, which changes the default — **it does not fix the fallback**, and the
fallback is still reached on any transient rsync failure. This raises the priority of Tasks 3 and 4 from
"harden a rare path" to "fix the path in use".

### 0.4 ⛔ NEW defect the scope doc does not have — the tar path always reports success

`Deploy-ToLinux.ps1:113-114` carries this comment:

> `$LASTEXITCODE` is checked so a failed sync ABORTS the deploy instead of restarting the service on the
> OLD binary (the silent-stale-deploy bug this replaces).

**Measured false.** The remote chain's exit status is `chmod`'s, not `tar`'s — `CHAIN_EXIT=0` above, with
tar having exited 2. The check at `:137` exists and is structurally blind: `ssh` reports 0, the local
pipeline is clean, `pipefail` sees nothing, and the deploy proceeds to restart the service.

And it is not an edge case. **Every** archive built with `tar -C … -czf - .` carries a `./` member, and
`--unlink-first` calls `unlink(".")` on it, which cannot succeed:

```
--unlink-first                          tar: .: Cannot unlink: Invalid argument            EXIT=2
--unlink-first --no-overwrite-dir       tar: '--no-overwrite-dir' cannot be used with '--unlink-first'
--unlink-first --keep-directory-symlink tar: .: Cannot unlink: Invalid argument            EXIT=2
```

So **on the tar path, `tar` fails on every single run and the deploy has never once been able to notice.**
This is the scope doc's own theme — *stop reporting success without doing the job* — sitting inside the
code written to fix it.

⭐ **There is a second cost, and it is the one that bites people rather than machines.** A deploy that
prints four `tar: … Cannot unlink` lines and then `=== Deploy Complete ===` on **every** run trains the
operator to read a failing deploy as normal. When the extract genuinely fails, the diagnostic that should
raise the alarm is the one already being scrolled past. This becomes **Defect 4**, handled by Task 4.

**And it is not confined to the fallback.** Auditing every native call in the script (`grep` output in
Task 4b) found **11 of 20 with no exit check at all** — including `scp` of the initial
`appsettings.Production.json` (`:147`), `chmod 755` on the deploy scripts that Task 10 is about to
execute (`:204`), `chmod +x` on the server binary (`:208`), the `scp`+install of `rotary-phone.service`
(`:216-217`), and `systemctl restart` itself (`:221`). A failure in any of them is silent and the script
still prints `=== Deploy Complete ===`. Split out as **Task 4b** because its blast radius is the whole
script rather than the tar path.

⚠ **The obvious repair does not work**, and the constraint is written into Task 4:
`$ErrorActionPreference = "Stop"` does **not** cover native commands. The script's own comment at
`:172-176` already says so correctly; the fix is a per-call `$LASTEXITCODE` capture, not a preference
variable.

### 0.5 ⛔ The coordinator's `install` constraint — right conclusion, wrong mechanism, twice

A mid-flight message asserted that `install -m MODE src dst` *"opens and truncates the destination in
place"*, so *"a concurrently executing invocation can read a partially written file."* It flagged its own
provenance as reasoned-from-semantics rather than measured. It was measured here, and **the mechanism is
wrong in two specific ways while the recommendation is right**:

```
$ strace -e trace=unlink,openat,rename install -m 755 new dest
unlink("dest")                                          = 0
openat(AT_FDCWD, "dest", O_WRONLY|O_CREAT|O_EXCL, 0600) = 4
```

- **`install` does not truncate in place.** It unlinks and creates a fresh inode (`O_EXCL`). That is
  precisely why it can install over a busy executable without `ETXTBSY`.
- **A process already executing the old file is therefore NOT at risk.** It holds the unlinked inode and
  runs the whole old file to completion. There is no half-file for a running invocation to read.

**But there is a real race, and the recommended fix is exactly right for it.** The exposure is the window
between the `unlink` and the end of the copy, during which the destination **path does not exist** —
measured directly:

```
$ ( while :; do [ -e target.bin ] || { echo "OBSERVED: path MISSING during install"; break; }; done ) &
$ install -m 755 big.bin target.bin        # 60 MB
OBSERVED: path MISSING during install
final mode=755 size=60000000
```

A `gv-bridge-watchdog.timer` firing in that window gets `ENOENT` on
`ExecStart=%h/bin/gv-bridge-ensure.sh` — a failed unit, not a truncated script — and at the tail of the
window a *newly started* invocation could exec a partial file at mode 0600. `mv` after `install` removes
the window entirely, because `rename(2)` is atomic within a filesystem.

⚠ **One trap this measurement exposes, worth recording:** the inode number was **reused** immediately
after the unlink (1588070 → 1588070). "Check the inode did not change" is **not** a valid test for whether
a file was replaced.

The message's other two verified facts hold and are used as given: both `install -m` call sites
(`setup-gvbridge.sh:89`, `:105`), and `OnUnitActiveSec=2min` / `OnBootSec=2min` in
`deploy/systemd/gv-bridge-watchdog.timer` with `ExecStart=%h/bin/gv-bridge-ensure.sh`. And the framing is
correct: this PR is what converts a dormant hazard into a live one, the same class as the
`--password-store` constraint.

### 0.6 ⛔ `--password-store=basic` IS present in this repo — the guard must be narrow

The scope doc says both copies are clean. **`deploy/gv-bridge-ensure.sh` is clean** (`grep -c` → `0`,
re-verified). But a repo-wide search is not equivalent:

```
scripts/bin/Debug/net10.0/.playwright/package/lib/server/chromium/chromiumSwitches.js:81:  "--password-store=basic",
```

That is Playwright's own bundled Chromium default. It is untracked build output (`git ls-files
scripts/bin` → 0 files) — but `Deploy-ToLinux.ps1:151-157` does `scp -r scripts/` with **no exclusion**,
so it ships to the box on every deploy from the owner's machine.

Two consequences, both handled below:

- The Task 8 guard is scoped to **`deploy/gv-bridge-ensure.sh` only**. A `grep -r` over `deploy/ scripts/`
  fails on a clean tree.
- 📌 **Note for the record, not a task:** any future on-box script that drives *Playwright's* Chromium
  against `~/.config/gv-bridge-chrome` inherits `--password-store=basic` from Playwright's defaults and
  would destroy the GV session by the same measured mechanism. Nothing does that today.

### 0.7 ✅ The scope doc's open question is answerable from the code — and points at the wrong race

> *"Does the deploy still stop and restart `rotary-phone` at the point the installer would run?"*

**No, and the two do not interact.** `setup-gvbridge.sh` writes to `~/bin`, `~/.config/systemd/user`,
`~/.config/autostart` and `~/Desktop`, and drives `systemctl --user`. `rotary-phone.service` is a
**system** unit, restarted at Step 4 (`:221`) after all file copying. The only mention of
`rotary-phone.service` inside the installer is `After=`/`Wants=` in the **opt-in legacy** block
(`:233-234`), off by default. There is no install-vs-restart race.

**The real race is the 2-minute watchdog timer**, which the scope doc does not mention. It is what §0.5
and Tasks 7 and 11 are about. The scope doc's open question is answered; new ones replace it in §6.

### 0.8 ✅ Line endings are already handled

`.gitattributes` pins `*.sh` and `deploy/systemd/*` to `eol=lf`, and every file is LF-only in the
checkout. A CRLF `gv-bridge-ensure.sh` would have failed with `bad interpreter: …^M` the moment this PR
starts executing it, so this was worth checking. **No task needed** — and the Task 10 sha256 gate would
catch a regression for free.

---

## 1. How anything gets verified

This is a PowerShell script running on Windows against a live Ubuntu box shared with another service.
"Write a unit test" is not available, and **"test manually after deploy" is not acceptable for the clobber
fix** — that is the defect being fixed, one level up. So every acceptance criterion below is classified,
and the split is deliberate: **the tar/`--unlink-first` behaviour, the `install` race, and the archive
contents are all provable with no box at all.**

| Lane | Where | What it can prove | Who runs it |
|---|---|---|---|
| **L — local, no box** | WSL / any Linux, in `deploy/tests/` | tar exit codes, archive membership, restore behaviour, the best-effort-backup clobber, `install` vs `mv` atomicity | Builder, in CI-less `bash` |
| **W — local, Windows only** | Owner's Git Bash / PowerShell | that a changed create-side `tar` invocation still works under msys | Owner, no box |
| **B — box** | `radio`, over ssh | `systemctl --user` reachability, the installed-artefact gate, the real end-to-end deploy | **Owner-run, output pasted back** |

⚠ **This session has no ssh access to `radio`** — the wildcard was removed and the MCP route needs a
restart. **No task below assumes an agent can reach the box.** Every **B** step is written as an exact
command with its expected output, for the owner to run and paste.

---

## 2. Task list

Dependency order. Phases 0–2 are entirely box-free.

### Phase 0 — establish the facts

---

#### Task 1 — Pin the clobber and its surviving mechanism in a repeatable test · lane **L**

The mechanism is now established (§0.1, §0.2). This task turns two ad-hoc reproductions into a script that
lives in the repo, so the next person does not have to re-derive it a third time.

Create `deploy/tests/repro-tar-clobber.sh`, runnable as `bash deploy/tests/repro-tar-clobber.sh`:

```bash
#!/usr/bin/env bash
# Reproduces the tar-pipe fallback's handling of appsettings.Production.json.
#
# Why this exists: docs/KNOWN-ISSUES.md attributed the clobber to `set -e` aborting
# the chain before the restore. That `set -e` is in the LOCAL script; the chain runs
# in the REMOTE shell, which does not inherit it. Case A is the falsification;
# Case B is the mechanism that actually produces the clobber.
#
# Run as a NORMAL USER. Case B relies on not being able to write a read-only
# directory, which root ignores.
set -u
[ "$(id -u)" -eq 0 ] && { echo "run as a non-root user; case B is meaningless as root"; exit 2; }

WORK="$(mktemp -d)"
trap 'chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT

build_fixture() {
    rm -rf "$WORK/src" "$WORK/dst"
    mkdir -p "$WORK/src/wwwroot/assets" "$WORK/dst/wwwroot/assets"
    echo 'TEMPLATE-FROM-REPO'  > "$WORK/src/appsettings.Production.json"
    echo 'NEW-BINARY'          > "$WORK/src/RotaryPhoneController.Server"
    echo 'NEW-ASSET'           > "$WORK/src/wwwroot/assets/app.js"
    echo 'BOX-AUTHORITATIVE'   > "$WORK/dst/appsettings.Production.json"
    echo 'OLD-BINARY'          > "$WORK/dst/RotaryPhoneController.Server"
    echo 'OLD-ASSET'           > "$WORK/dst/wwwroot/assets/app.js"
    tar -C "$WORK/src" -czf "$WORK/payload.tgz" .
}

# --- Case A: the chain exactly as Deploy-ToLinux.ps1:126-130 builds it -------------
# Expected: tar exits 2 on the directory members, every regular file is extracted
# anyway, the restore mv RUNS and succeeds, and the chain exits 0. The chain exit of
# 0 is Defect 4: it is chmod's status, not tar's.
build_fixture
sh -c "
  cp -f '$WORK/dst/appsettings.Production.json' '$WORK/rp-prod.bak' 2>/dev/null || true
  tar -xzf '$WORK/payload.tgz' --unlink-first -C '$WORK/dst'
  [ -f '$WORK/rp-prod.bak' ] && mv -f '$WORK/rp-prod.bak' '$WORK/dst/appsettings.Production.json' || true
  chmod +x '$WORK/dst/RotaryPhoneController.Server'
"
a_exit=$?
a_cfg="$(cat "$WORK/dst/appsettings.Production.json")"
a_bin="$(cat "$WORK/dst/RotaryPhoneController.Server")"
a_bak="$([ -f "$WORK/rp-prod.bak" ] && echo present || echo consumed)"
echo "A: chain_exit=$a_exit  config=$a_cfg  binary=$a_bin  backup=$a_bak"
[ "$a_exit" -eq 0 ] && [ "$a_cfg" = "BOX-AUTHORITATIVE" ] && [ "$a_bin" = "NEW-BINARY" ] \
  && echo "A: PASS (restore ran; chain hid tar's failure)" || echo "A: UNEXPECTED"

# --- Case B: the surviving mechanism -- a best-effort backup that fails silently ---
# A read-only parent directory holding a stale backup stands in for the real
# conditions (/tmp not writable, disk full, a rp-prod.bak owned by another uid in a
# sticky /tmp). cp -f fails and is swallowed; `[ -f ]` then sees the STALE file; the
# mv also fails and is swallowed. Net: the template stays on the box AND the backup
# survives in /tmp -- exactly the state PR #72 UAT found.
build_fixture
mkdir -p "$WORK/ro"
echo 'STALE-FROM-ANOTHER-RUN' > "$WORK/ro/rp-prod.bak"
chmod 0555 "$WORK/ro"
sh -c "
  cp -f '$WORK/dst/appsettings.Production.json' '$WORK/ro/rp-prod.bak' 2>/dev/null || true
  tar -xzf '$WORK/payload.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null
  [ -f '$WORK/ro/rp-prod.bak' ] && mv -f '$WORK/ro/rp-prod.bak' '$WORK/dst/appsettings.Production.json' 2>/dev/null || true
"
b_cfg="$(cat "$WORK/dst/appsettings.Production.json")"
b_bak="$([ -f "$WORK/ro/rp-prod.bak" ] && echo survives || echo consumed)"
chmod 0755 "$WORK/ro"
echo "B: config=$b_cfg  backup=$b_bak"
[ "$b_cfg" = "TEMPLATE-FROM-REPO" ] && [ "$b_bak" = "survives" ] \
  && echo "B: PASS (clobbered, silently)" || echo "B: UNEXPECTED"

# --- Case C: the fix -- the config is never a member of the archive ----------------
# The backup/restore dance is REMOVED. The box's file is untouched because nothing
# ever writes to it, whichever way the backup step would have failed.
build_fixture
before="$(sha256sum "$WORK/dst/appsettings.Production.json" | cut -d' ' -f1)"
tar -C "$WORK/src" --exclude=./appsettings.Production.json --exclude=./.playwright \
    -czf "$WORK/fixed.tgz" .
sh -c "tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null; chmod +x '$WORK/dst/RotaryPhoneController.Server'"
after="$(sha256sum "$WORK/dst/appsettings.Production.json" | cut -d' ' -f1)"
c_bin="$(cat "$WORK/dst/RotaryPhoneController.Server")"
echo "C: sha_before=$before"
echo "C: sha_after =$after  binary=$c_bin"
tar -tzf "$WORK/fixed.tgz" | grep -q appsettings.Production.json && { echo "C: FAIL (config is in the archive)"; exit 1; }
[ "$before" = "$after" ] && [ "$c_bin" = "NEW-BINARY" ] && echo "C: PASS" || echo "C: FAIL"
```

**Acceptance:**
- **A: PASS** — `chain_exit=0`, `config=BOX-AUTHORITATIVE`, `binary=NEW-BINARY`, with tar's `Cannot
  unlink` lines on stderr. This is the falsification of the recorded mechanism *and* of the `:113-114`
  comment, in one run.
- **B: PASS** — `config=TEMPLATE-FROM-REPO`, `backup=survives`. **This is the criterion the current code
  fails**, and the one the scope doc's original acceptance line could not express.
- **C: PASS** — the archive has no `appsettings.Production.json` member, the destination sha256 is
  unchanged, and the new binary still landed.

> ### ⛔ Correction 2026-09-09, made while building this task — case B's stated mechanism is wrong
>
> All three cases behave as written above. **Case B's explanation does not.** The comment in the script
> block says *"cp -f fails and is swallowed; `[ -f ]` then sees the STALE file"*. Measured against that
> exact fixture:
>
> ```
> cp -f src ro/rp-prod.bak     -> exit 0, backup content becomes BOX-AUTHORITATIVE
> mv -f ro/rp-prod.bak dst/…   -> mv: cannot move …: Permission denied, exit 1
> ```
>
> Writing to an **already-existing, writable** file needs no write permission on the containing
> directory — only *unlinking* it does. So in that fixture the backup **succeeds** and it is the
> **restore** that fails. The assertions (`config=TEMPLATE-FROM-REPO`, `backup=survives`) pass either
> way, which is precisely why this was worth catching: a green check whose stated mechanism is wrong is
> the defect this PR exists to fix, one level up.
>
> **The shipped script therefore splits case B in two**, both measured, neither hypothetical:
>
> | Case | Fixture | What fails | Result |
> |---|---|---|---|
> | **B1** | first deploy (no config on the box) + a stale `/tmp/rp-prod.bak` | the **backup** `cp -f` (source absent) | `[ -f ]` is true and the restore installs **stale content from an earlier run**. ⛔ Worse than the template — arbitrary config, chain exits 0 |
> | **B2** | this plan's original read-only-directory fixture, kept and relabelled | the **restore** `mv` | the repo **template** stays on the box and the backup is stranded in `/tmp` — exactly the state PR #72 UAT found |
>
> **Nothing downstream is invalidated.** Task 3's fix is mechanism-independent by design (§0.2: *"removes
> the state rather than protecting it"*), so it closes both. The script adds **C-B1** and **C-B2** —
> both fixtures re-run against the fixed chain — which is what Task 3's strengthened acceptance
> criterion actually asks for and which the block above did not implement.

---

#### Task 2 — Correct `docs/KNOWN-ISSUES.md` by annotation · lane **L**

**Depends on:** Task 1. **By annotation, not silent rewrite**, and ⛔ **the entry stays `🔴 OPEN`** — the
defect is real and unfixed until this PR lands. *"The restore never runs"* is precisely the claim that
would make a future reader rip out a mechanism that is working.

Leave the existing numbered mechanism 1–3 intact. Append immediately after it:

```markdown
> ### ⛔ Correction 2026-09-09 — step 3's mechanism is wrong; the defect is not. Entry stays OPEN.
>
> **Falsified twice: locally** (`deploy/tests/repro-tar-clobber.sh` case A) **and by a live deploy on the
> box.** Step 3 above says `set -e -o pipefail` aborts the chain before the restore `mv` runs. That
> `set -e` is in the **local** PowerShell-invoked script (`Deploy-ToLinux.ps1:125`). Backup → extract →
> restore is a `;`-separated string executed by the **remote** shell, which does not inherit it, and
> `pipefail` can only abort the local script after the remote work has finished. **The restore runs.**
>
> The live deploy proved the sequence with three facts rather than assuming it: the config's sha256 was
> **unchanged**, its mtime **moved**, and `/tmp/rp-prod.bak` was **gone**. tar overwrote the file and the
> restore put it back.
>
> What step 3 got right: `--unlink-first` really does fail on the archive's directory members and tar
> really does exit 2 — on **every** run, because `tar -czf - .` always carries a `./` member.
>
> **The mechanism that actually clobbers** (case B): both ends of the dance are best-effort. If
> `cp -f … /tmp/rp-prod.bak` fails — no file yet on a first deploy, `/tmp` unwritable, disk full, or a
> `rp-prod.bak` another uid owns in a sticky `/tmp` — the `2>/dev/null || true` swallows it, tar
> overwrites the config, and the `[ -f ]` guard is either false or pointing at a **stale** backup. No
> restore happens and nothing reports it.
>
> **A second, worse thing came out of the same measurement.** The remote chain's exit status is
> `chmod`'s, so it reports **0** while tar has failed. The comment at `Deploy-ToLinux.ps1:113-114`
> claiming the exit-code check prevents a silent stale deploy is therefore **false on the tar path** —
> the check is real but structurally blind. Tracked as Defect 4 in
> [`docs/plans/deploy-tooling-honest-deploy-plan.md`](plans/deploy-tooling-honest-deploy-plan.md).
>
> ⚠ **And this is not a rare path.** `rsync` is absent from the deploying machine's PowerShell `PATH`, so
> `Get-Command rsync` finds nothing and **every deploy from that machine takes the tar path.** Installing
> rsync changes the default; it does not fix the fallback.
>
> ⭐ **The lesson, which is the part worth keeping.** The recorded explanation was written after the
> defect was correctly observed, and it was wrong. It stayed plausible for five weeks because it named a
> real flag (`set -e`) doing a real thing (aborting a chain) in the wrong shell. Fixing what it described
> — making the restore unconditional, or wrapping it in a `trap` — would have changed nothing and looked
> like a fix. The chosen fix instead removes the file from the tar stream, so the property holds
> whichever way the backup step fails.
```

Also annotate the **Proposed fix** block at `:134-145`: mark the *"make the restore unconditional (run it
in a `trap`/`||` …)"* bullet as **superseded — the restore already runs; see the correction above**, and
mark the **Primary** and **Belt and braces** bullets as **adopted** (Tasks 3 and 5).

**Acceptance:** the original text is unmodified; the correction is additive; the superseded bullet is
marked in place rather than deleted; the entry's status line still reads `🔴 OPEN`.

---

### Phase 1 — fix the tar path

---

#### Task 3 — Never put `appsettings.Production.json` in the tar stream · lane **L** + **W**

**Depends on:** Task 1. **This is the mechanism-independent fix and the highest-value change in the PR.**
Deliberately the *simplest* form: one added `--exclude`, and the removal of the dance it makes redundant.
No restructuring of how the archive is built — that is Task 4, which is separately revertable.

In `Deploy-ToLinux.ps1`, replace the `$syncScript` construction at `:124-130`:

```powershell
  $syncScript =
    "set -e -o pipefail`n" +
    # --exclude=./appsettings.Production.json is the load-bearing line, and it mirrors
    # what the rsync path at :89 has always done. The box's copy is authoritative
    # (docs/HT801-ADDRESS.md) and carries BluetoothAdapter: hci1, which crosses the
    # Radio Console audio boundary. The publish output ships the repo TEMPLATE (the
    # SDK's appsettings*.json Content glob), so while it was in the stream the file
    # was overwritten on every run and depended on a restore to put it back.
    #
    # The backup/restore dance is GONE rather than repaired, and that is the point.
    # Both of its ends were best-effort (`2>/dev/null || true`), so a failed backup
    # silently became a clobber -- reproduced in deploy/tests/repro-tar-clobber.sh
    # case B. Excluding the member removes the state instead of protecting it: there
    # is nothing to restore because nothing is overwritten.
    "tar -C '$publishMsys' --exclude=./appsettings.Production.json --exclude=./.playwright -czf - . |" +
      " ssh '$SshTarget' '" +
      "tar -xzf - --unlink-first -C $TargetPath; " +
      "chmod +x $TargetPath/RotaryPhoneController.Server'`n"
```

Keep `--unlink-first` on the extract: it is what avoids `ETXTBSY` on the running binary and the mapped
`.so` files.

**Acceptance:**
- Task 1 case C prints `PASS`.
- ⚠ **The strengthened criterion, replacing the scope doc's:** Task 1 **case B**, re-run against the new
  chain, must leave the destination config **unchanged**. The old chain fails case B; the new one cannot
  reach the file at all. The scope doc's original criterion passes against unfixed code (§0.2) and is
  superseded.
- Archive membership, verified in this planning session against a fixture with the exact flag order:
  ```
  $ tar -C src --exclude=./appsettings.Production.json --exclude=./.playwright -czf - .   # exit 0
  ./
  ./wwwroot/
  ./wwwroot/assets/
  ./wwwroot/assets/app.js
  ./RotaryPhoneController.Server          <-- no appsettings.Production.json, no .playwright
  ```
  ⚠ **Flag order matters** and the existing line already has it right: GNU tar warns
  `--exclude … has no effect` if an `--exclude` follows a non-option operand. Keep both `--exclude`s
  before `-czf`.
- **Lane W:** the owner runs one deploy from Windows and confirms the sync still completes. The create
  side is unchanged apart from one added flag, so this is a low-risk confirmation rather than a real test.

---

#### Task 4 — Make the tar path's exit status honest · lane **L** + **W**

**Depends on:** Task 3. ⚠ **Separately revertable, and deliberately so.** Task 3 fixes the clobber with a
one-flag change; this task changes how the archive is *built*, which carries msys-on-Windows risk that
Task 3 does not. Given §0.3 — the tar path is the only path running today — that risk is production risk.
If the pipeline misbehaves under Git Bash, **revert this task alone**, keep Task 3, and record Defect 4 as
still open.

**Why the archive has to change at all:** `set -e` in the remote chain is not enough on its own. With a
`./` member present, tar exits 2 on every run, so an honest chain would make **every** deploy fail. The
directory members must go first.

```powershell
  $syncScript =
    "set -e -o pipefail`n" +
    "cd '$publishMsys'`n" +
    # Files-only member list, and no './' member. GNU tar creates missing parent
    # directories on extract, so directory members buy nothing here -- and
    # --unlink-first calls unlink() on every one of them, which cannot succeed.
    # `tar -czf - .` therefore made tar exit 2 on EVERY run (measured 2026-09-09,
    # deploy/tests/repro-tar-clobber.sh case A; seen live on the box the same day).
    # Dropping the directory members is what lets the exit status below be honest
    # instead of decorative -- and stops four `Cannot unlink` lines printing on every
    # successful deploy, which is what trained us to scroll past them.
    "find . -mindepth 1 -path ./.playwright -prune -o \( -type f -o -type l \) -print0 |" +
      " tar --null --exclude=./appsettings.Production.json -czf - -T - |" +
      " ssh '$SshTarget' '" +
      "set -e; " +
      "tar -xzf - --unlink-first -C $TargetPath; " +
      "chmod +x $TargetPath/RotaryPhoneController.Server'`n"
```

Correct the false comment at `:113-114`:

```powershell
  #   * The remote chain now runs under its own `set -e`. It used to end in `chmod`,
  #     so the chain reported CHMOD's status -- 0 -- while tar had exited 2. The
  #     $LASTEXITCODE check below was real but structurally blind, and the deploy
  #     restarted the service on a half-extracted tree while printing success.
  #     Measured 2026-09-09; the old comment claiming this was already handled was
  #     wrong. See docs/plans/deploy-tooling-honest-deploy-plan.md Defect 4.
```

##### ⛔ Implementation constraint — `$ErrorActionPreference` does NOT cover native commands

**This is a constraint, not a suggestion.** Someone will otherwise try the intuitive repair first and
lose a day proving it does nothing.

`Deploy-ToLinux.ps1:40` sets `$ErrorActionPreference = "Stop"`. **That does not make a failing `ssh`,
`scp`, `tar`, `rsync` or `bash` throw.** Whether a native command's non-zero exit becomes a terminating
error is governed by a *different* setting, `$PSNativeCommandUseErrorActionPreference`. The script's own
comment at `:172-176` already says this in prose — *"`$ErrorActionPreference = "Stop"` does NOT turn a
non-zero exit from a native executable into a terminating error"* — and it is correct.

⚠ **And the setting that would work is not a local fix.** `$PSNativeCommandUseErrorActionPreference =
$true` makes **every** native call in the file throw instead of falling through to its own check,
changing control flow throughout the script at once — including the rsync path at `:88-100`, whose
fallback depends on rsync being *allowed* to fail. Do not reach for it as a one-liner.

⭐ **The required pattern: capture the exit code on the line immediately after each call, one capture
per call.** Never one test at the end of a sequence.

```powershell
  bash $syncScriptPath
  $syncExit = $LASTEXITCODE          # immediately after the call, before anything else
```

**Step 0 of this task — confirm the version and the setting on the machine that actually deploys**
(lane **W**, owner-run, no box):

```powershell
$PSVersionTable.PSVersion
$PSNativeCommandUseErrorActionPreference    # may not exist at all
$ErrorActionPreference
```

⚠ **Do not build on a borrowed measurement.** `$PSNativeCommandUseErrorActionPreference = $false` was
measured by a **sibling repo on PowerShell 7.6.5**, not by us. Two things could differ here:

- If it reads `$true` on the owner's machine, the analysis above changes and the task must be re-planned.
- **More likely, it does not exist.** The setting arrived in PowerShell 7.3 (experimental) / 7.4. This
  script's own comment at `:122` works around *"PowerShell 5.1's unreliable native-arg quoting"*, which
  says the deploy has been run under **Windows PowerShell 5.1** — where the variable is absent entirely
  and `$ErrorActionPreference` has never covered native commands. **In that case the per-call capture is
  mandatory regardless**, and the borrowed measurement is not even applicable.

Either way the pattern below is correct; only the *reason* changes.

##### ✅ Step 0 MEASURED 2026-09-09 — the second hypothesis is the right one, and the shortcut is a no-op

Run on the deploying machine, both shells present on it:

| | Windows PowerShell | PowerShell 7 |
|---|---|---|
| `$PSVersionTable.PSVersion` | **5.1.26100.9343** | **7.6.5** |
| `$PSVersionTable.PSEdition` | Desktop | Core |
| `Test-Path variable:PSNativeCommandUseErrorActionPreference` | **False — the variable does not exist** | True |
| `$PSNativeCommandUseErrorActionPreference` | *(nothing)* | **False** |
| `$ErrorActionPreference` (default) | Continue | Continue |

The plan's *"more likely, it does not exist"* branch is the one that holds: under 5.1 — which
`:122`'s own workaround comment says this script runs under — the variable is **absent entirely**.

⛔ **And this makes the shortcut worse than merely wrong-scoped.** Under 5.1, assigning
`$PSNativeCommandUseErrorActionPreference = $true` is not an error and not a warning: PowerShell creates
a new variable, nothing ever reads it, and the deploy carries on exactly as before. **The "fix" would
look applied and do nothing** — the same disease as the `:113-114` comment this task exists to correct.
Under 7.6.5 it would work, and would change control flow for every native call in the file at once,
including the rsync branch at `:88-100` that depends on rsync being allowed to fail.

The sibling repo's borrowed `$false` on 7.6.5 turns out to be reproducible here — but it was never the
load-bearing fact. **The per-call `$LASTEXITCODE` capture is mandatory under either shell**, and it is
the only remedy that behaves identically in both.

##### 📌 Scope note — corroboration from a separate source, and where our shape differs

A sibling repo hit the same defect class independently and shipped a fix: their chain ran `ssh`, `scp`,
`ssh` in sequence and then tested `$LASTEXITCODE`, which by then belonged to the last `ssh` — whose
remote compound ended in `rm -rf`, returning 0 whether or not the directory existed. Two calls were
masked. **Ours ends in `chmod`; theirs ended in `rm -rf`.** Same shape: a chain whose last command always
succeeds, masking a failure earlier in it. Two repos, same tool gap, found independently — corroboration
from a genuinely separate source rather than agreement between checks that share a blind spot.

⛔ **But their scope note does NOT transfer, and adopting it would leave the worst call sites unfixed.**
They report the masking as fallback-only, because on their rsync path the rsync *is* the last native call
before the check. Verified against our script, that is **not** our situation:

| | Sibling repo | **This repo (verified 2026-09-09)** |
|---|---|---|
| Stale `$LASTEXITCODE` read after an intervening native call | Yes — the defect they fixed | **No. Every one of our six checks is already immediate** (`:68` after `:67`, `:96` after `:88`, `:135` after `:134`, `:185` after `:184`, `:189` after `:188`, `:198` after `:197`) |
| Masking inside a remote compound whose last command always succeeds | Yes (`rm -rf`) | **Yes** (`chmod`) — fallback path only, and that is Defect 4 |
| Native calls with **no exit check at all** | not reported | **11 of 20, spread across the whole script** — see Task 4b |

So on our side the fallback-only framing is **half right**: the *remote-chain* masking really is
fallback-only, and Task 4 fixes it there. The *unchecked-call* problem is not fallback-only at all — it
reaches Step 4, where the service unit is installed and the service is restarted. Task 4b is split out
for exactly that reason.

Then assert the box actually received the build, rather than trusting any exit code:

```powershell
  # Independent of the exit status: ask the box what it has. An exit code says what a
  # program claimed; this says what is on disk.
  $localBinSize = (Get-Item (Join-Path $PublishDir "RotaryPhoneController.Server")).Length
  $remoteBinSize = (ssh $SshTarget "stat -c %s ${TargetPath}/RotaryPhoneController.Server").Trim()
  if ($remoteBinSize -ne "$localBinSize") {
    throw "sync verification FAILED: ${TargetPath}/RotaryPhoneController.Server is $remoteBinSize bytes on ${TargetHost}, expected $localBinSize. The service has NOT been restarted."
  }
  Write-Host "  Sync verified: binary is $localBinSize bytes on the box" -ForegroundColor Green
```

**Acceptance:**
- **L:** a files-only archive extracts with `--unlink-first` at **exit 0**, and tar auto-creates missing
  directories. Verified in this planning session:
  ```
  members: ./a.txt  ./sub/b.txt  ./sub/deep/c.txt
  extract --unlink-first -> EXIT=0
  result:  dst/a.txt  dst/sub/b.txt  dst/sub/deep/c.txt     ('deep' did not exist beforehand)
  ```
- **L negative control:** point the extract's `-C` at a non-existent directory; the chain must exit
  **non-zero** and the PowerShell `throw` must fire. This is the criterion the current code fails.
- **W:** `find … -print0 | tar --null … -T -` must work under the owner's Git Bash. Run before deploying:
  ```
  cd /d/prj/RotaryPhone/publish/linux-x64
  find . -mindepth 1 -path ./.playwright -prune -o \( -type f -o -type l \) -print0 \
    | tar --null --exclude=./appsettings.Production.json -czf /tmp/t.tgz -T - && echo "create ok"
  tar -tzf /tmp/t.tgz | head -5
  tar -tzf /tmp/t.tgz | grep -c appsettings.Production.json    # expect 0
  ```
  ⛔ **If this fails under msys, stop and revert Task 4 only.** Task 3 stands alone.
- No `Cannot unlink` lines appear on a successful deploy.

---

#### Task 4b — Check the exit code of every native call, not just some of them · lane **L** + **W**

**Depends on:** Task 4's Step 0 (the version/setting confirmation). Split from Task 4 because its blast
radius is the **whole script**, not the tar path.

**Verified 2026-09-09: 11 of the script's 20 native calls have no exit check at all.** This is a
different defect from Defect 4 — not a masked status, an *unread* one — and it is the same disease:
the script prints `=== Deploy Complete ===` after work that may not have happened.

| Line | Call | What a silent failure costs |
|---|---|---|
| `:78` | `ssh … sudo mkdir -p …/{data,logs} && chown` | Every later copy lands somewhere unintended, or not at all |
| `:147` | `scp $prodConfig …appsettings.Production.json` | ⛔ **The box is left with no production config at all** — this is the first-deploy path for the very file the rest of this PR protects |
| `:155`, `:156` | `ssh mkdir` + `scp -r scripts` | Stale or absent HFP monitor scripts |
| `:163`, `:164`, `:166` | ChromeExtension copies | Stale extension; the snap copy is best-effort by design |
| `:204` | `ssh chmod 755 …/deploy/*.sh` | ⛔ **`setup-gvbridge.sh` not executable — Task 10 runs it next** |
| `:208` | `ssh chmod +x …Server` | ⛔ **The service cannot start**, and `:221` restarts it anyway |
| `:216`, `:217` | `scp rotary-phone.service` + `sudo mv … && daemon-reload && enable` | ⛔ **systemd is left on a stale unit**, then `:221` restarts into it |
| `:221` | `ssh sudo systemctl restart` | ⛔ **A failed restart is never noticed**; the script prints success |

Apply the per-call capture pattern from Task 4 to each. The four `⛔` rows are the ones that matter;
the rest are mechanical and should land in the same pass so the file has one consistent rule.

```powershell
# Native commands do not honour $ErrorActionPreference (see Task 4). Every native
# call captures its own status on the very next line -- one capture per call, never a
# single test after a sequence, because the value would then belong to whichever call
# ran last rather than to the one that failed.
ssh $SshTarget "chmod +x ${TargetPath}/RotaryPhoneController.Server && chmod +x ${TargetPath}/scripts/*.py 2>/dev/null"
$chmodExit = $LASTEXITCODE
if ($chmodExit -ne 0) { throw "failed to make ${TargetPath}/RotaryPhoneController.Server executable (exit $chmodExit) -- the service would fail to start; NOT restarting" }
```

📌 **While in there, correct one overstated comment.** `:172` reads *"Every scp is exit-code checked and
throws"*. That is true of the two `scp`s in **that block** (`:188`, `:197`) and false of the four
elsewhere (`:147`, `:156`, `:164`, `:216`). Scope the sentence to the block, or make it true by finishing
the job — Task 4b does the latter, at which point the comment can stay as written.

⚠ **Two calls must deliberately keep failing softly, and both need a comment saying so** — otherwise a
later reader "fixes" them and breaks the deploy:

- `:88` **rsync** — its failure is the branch condition for the tar fallback (`:96-100`). It must stay
  unchecked-by-throw.
- `:166` the **snap-profile extension copy** — already guarded by `if [ -d … ]` on the remote side and
  is genuinely optional.

**Acceptance:**
- Every native call is either followed by a captured status and a check, or carries a comment saying why
  it is allowed to fail. Verify by re-running the two greps used to build the table above:
  ```
  grep -nE '^\s*(ssh|scp|rsync|bash|dotnet|&) ' deploy/Deploy-ToLinux.ps1
  grep -n 'LASTEXITCODE' deploy/Deploy-ToLinux.ps1
  ```
  Every line in the first list is accounted for in the second, or by a comment.
- **W, negative control:** point `$TargetHost` at an unreachable host and run the deploy. It must throw
  at the **first** failing call and must **not** print `=== Deploy Complete ===`. Today it reaches the
  end. This is the criterion the current script fails, and it needs no box.

---

#### Task 5 — Belt and braces: keep the config out of the publish output · lane **L**

**Depends on:** nothing; can land with Task 3.

In `src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj`:

```xml
  <ItemGroup>
    <!-- The box's appsettings.Production.json is authoritative (docs/HT801-ADDRESS.md)
         and carries BluetoothAdapter: hci1, which crosses the Radio Console audio
         boundary. The SDK's default appsettings*.json Content glob would otherwise
         ship the repo TEMPLATE inside the publish artifact, so every sync path has to
         remember to exclude it. This makes no artifact able to carry it at all. -->
    <Content Update="appsettings.Production.json" CopyToPublishDirectory="Never" />
  </ItemGroup>
```

**Acceptance:** `dotnet publish … --output publish/linux-x64` and then
`test ! -f publish/linux-x64/appsettings.Production.json`. Fully local.

⚠ **This does not make Task 3 optional.** The rsync path's `--exclude` and Task 3's `--exclude` stay: a
developer publishing from an older checkout, or a stray copy left in the output directory by a previous
build, still reaches the stream. Two independent guards, deliberately.

---

#### Task 6 — Make a clobber loud · lane **L** to write, **B** to see

**Depends on:** Task 3.

After the restart in `Deploy-ToLinux.ps1`, print the values that cross the service boundary:

```powershell
# The values whose silent change crosses the Radio Console audio boundary
# (docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md). Printed on every deploy so a
# clobber is visible in the operator's own terminal rather than found weeks later by
# the other service losing audio.
Write-Host "  Post-deploy config (must read hci1):" -ForegroundColor Yellow
ssh $SshTarget "grep -E 'BluetoothAdapter|UseActualBluetoothHfp' ${TargetPath}/appsettings.Production.json || echo '  !! keys not found -- appsettings.Production.json may have been clobbered'"
```

**Acceptance:** the deploy prints a line containing `hci1`. Absence of the line is itself the alarm. Owner
sees this in Task 12.

---

### Phase 2 — make `setup-gvbridge.sh` safe to run on a 2-minute cadence

---

#### Task 7 — Replace files by atomic rename, not by unlink-and-recreate · lane **L**

**Depends on:** nothing. ⛔ **Prerequisite of Task 10, not a follow-up.** Running the installer from the
deploy without this is what creates the exposure (§0.5).

In `deploy/setup-gvbridge.sh`, add one helper and route both existing call sites through it:

```bash
# Replace a destination by ATOMIC RENAME. Never write to the live path.
#
# Two different problems are easy to confuse here, and `install -m` solves exactly
# one of them:
#
#   MODE CORRECTNESS  -- solved, and that is what write_mode's comment below is
#                        about: this box runs umask 0002, so `cat > f` would leave a
#                        group-writable .desktop file that GNOME silently refuses to
#                        launch. `install -m` never lets that version exist.
#
#   REPLACEMENT ATOMICITY -- NOT solved by install. strace shows install doing
#                        unlink(dest) then open(dest, O_CREAT|O_EXCL, 0600). A process
#                        already executing the old file is safe (it holds the unlinked
#                        inode), but between the unlink and the end of the copy the
#                        PATH DOES NOT EXIST -- measured directly with a polling loop
#                        -- and at the tail of that window it exists but is partial,
#                        at mode 0600.
#
# gv-bridge-watchdog.timer runs ExecStart=%h/bin/gv-bridge-ensure.sh every 2 minutes
# (OnUnitActiveSec=2min, AccuracySec=20s). There is no quiet window to install in. A
# watchdog firing inside that window gets ENOENT and the unit fails; a new invocation
# starting at the tail could exec a truncated script, which does not crash -- it STOPS
# EARLY, and every line after the cut silently does not exist.
#
# rename(2) is atomic within a filesystem: every observer sees the whole old file or
# the whole new one. "${dest}.new" is a sibling of "${dest}" on purpose, so the rename
# can never degrade into a cross-device copy.
#
# NOTE: do not "verify" a replacement by comparing inode numbers. Measured 2026-09-09:
# the freed inode was immediately REUSED by the new file.
install_atomic() {
    local src="$1" dest="$2" mode="$3"
    install -m "$mode" "$src" "${dest}.new"
    mv -f "${dest}.new" "$dest"
}
```

Then change the two call sites:

```bash
install_file() {
    local src="$1" dest="$2" mode="$3"
    if [ ! -f "$src" ]; then
        warn "Missing ${src} — deploy the RotaryPhone project first, then re-run."
        exit 1
    fi
    backup_if_changed "$src" "$dest"
    install_atomic "$src" "$dest" "$mode"      # was: install -m "$mode" "$src" "$dest"
}

write_mode() {
    local dest="$1" mode="$2" tmp
    tmp="$(mktemp)"
    cat > "$tmp"
    backup_if_changed "$tmp" "$dest"
    install_atomic "$tmp" "$dest" "$mode"      # was: install -m "$mode" "$tmp" "$dest"
    rm -f "$tmp"
}
```

⚠ `mktemp` puts `$tmp` in `/tmp`, which may be a different filesystem from `$HOME` — which is exactly why
`install_atomic` stages at `"${dest}.new"` rather than renaming `$tmp` directly.

**Acceptance** — add `deploy/tests/repro-install-atomicity.sh`, lane **L**:

```bash
#!/usr/bin/env bash
# Demonstrates the window `install -m` opens and that install-then-mv closes it.
set -u
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
head -c 60000000 /dev/urandom > "$W/new"
cp "$W/new" "$W/target"

watch_for_gap() {                       # prints yes if the path ever vanishes
    local seen=no deadline=$((SECONDS + 20))
    while [ $SECONDS -lt $deadline ]; do
        [ -e "$W/target" ] || { seen=yes; break; }
    done
    echo "$seen"
}

watch_for_gap > "$W/a.out" & sleep 0.05; install -m 755 "$W/new" "$W/target"; wait
echo "install -m   : path_missing_observed=$(cat "$W/a.out")   # expect yes"

cp "$W/new" "$W/target"
watch_for_gap > "$W/b.out" & sleep 0.05
install -m 755 "$W/new" "$W/target.new" && mv -f "$W/target.new" "$W/target"; wait
echo "install + mv : path_missing_observed=$(cat "$W/b.out")   # expect no"
```

- `install -m` → `path_missing_observed=yes` (reproduced in this planning session).
- `install + mv` → `path_missing_observed=no`.
- ⚠ **This is a timing test and can produce a false `no` on the first line** on a fast filesystem with a
  small file — hence the 60 MB payload. If the first line ever reads `no`, that is an inconclusive run,
  **not** a passing one. Re-run; if it stays `no`, record it and keep the `mv` anyway — the change is
  correct on `rename(2)` semantics regardless of whether this machine can catch the window.

---

#### Task 8 — Refuse to deploy a `gv-bridge-ensure.sh` carrying `--password-store` · lane **L**

**Depends on:** nothing. ⛔ **Must land before Task 10**, and must run **before** the `scp`, so a bad file
never reaches the box.

In `Deploy-ToLinux.ps1`, immediately before the `foreach ($script in $shellScripts)` loop:

```powershell
  # ⛔ Hard gate. Until this PR, setup-gvbridge.sh was never executed by the deploy, so
  # gv-bridge-ensure.sh sat on the box as an inert file and its contents did not
  # matter. This PR runs it -- converting a dormant file into an executed one, at which
  # point --password-store stops being inert. On a profile already holding v11 cookies
  # it makes the keyring-derived key unobtainable and Chrome DISCARDS them: measured
  # live at 45 v11 -> 16 v10, destroying the Google Voice session.
  # ~/.config/gv-bridge-chrome is exactly that profile.
  #
  # Scoped to this ONE file deliberately. A repo-wide search is NOT equivalent:
  # scripts/bin/Debug/net10.0/.playwright/.../chromiumSwitches.js carries
  # "--password-store=basic" as one of Playwright's own Chromium defaults, so a broad
  # grep fails on a clean tree (verified 2026-09-09).
  $ensureSrc = Join-Path $deployScripts "gv-bridge-ensure.sh"
  if (Test-Path $ensureSrc) {
    if (Select-String -Path $ensureSrc -Pattern 'password-store' -SimpleMatch -Quiet) {
      throw "REFUSING TO DEPLOY: deploy/gv-bridge-ensure.sh contains --password-store. On ~/.config/gv-bridge-chrome that discards the v11 cookies and destroys the Google Voice session. Remove the flag, then redeploy."
    }
  } else {
    throw "REFUSING TO DEPLOY: deploy/gv-bridge-ensure.sh is missing -- setup-gvbridge.sh would fail on the box after the binary sync had already landed."
  }
```

**Acceptance** (lane **L**, against a scratch copy):
- Against the current tree, the deploy proceeds (`grep -c password-store deploy/gv-bridge-ensure.sh` →
  `0`, re-verified 2026-09-09).
- Add `--password-store=basic` to a scratch copy → the deploy throws **before** any `scp` runs. Negative
  control; without it this task is untested.
- Delete the scratch `gv-bridge-ensure.sh` → the deploy throws.

---

#### Task 9 — Give `gv-bridge-ensure.sh` a `--print-config` self-report · lane **L**

**Depends on:** nothing. **Recommended, and the cost was checked rather than assumed.**

The scope doc asks for this "if it is cheap". It is: the script's argument-free contract has no options
today, its configuration is six `${VAR:-default}` assignments, and `CHROME_ARGS` is built by pure code
with no side effects. **~12 lines and one block move.**

⭐ **Prefer `--print-config` over `--version`.** A version constant is a second source of truth that goes
stale silently — the exact failure this whole PR is about. `--print-config` reports what the script would
actually *do*, so it cannot drift from the code it lives in, and it lets the post-install gate assert the
two properties that matter (`--remote-debugging-port` present, `--password-store` absent) against the
**installed, executed** artefact rather than against repo bytes.

Restructure the top of `deploy/gv-bridge-ensure.sh`: move the whole `CHROME_ARGS=( … )` construction
(currently `:64-101`) to sit **immediately after** the `MARKER=` assignment at `:31` and **before** the
lock block at `:42-51`. The construction is pure — a `[ -d ]` test and array appends — so nothing changes
by moving it. Then insert, still before the lock:

```bash
# Self-report. The deploy calls this on the INSTALLED copy after setup-gvbridge.sh
# runs, so the gate tests what the installed thing DOES rather than what a file
# contains: a checksum cannot catch a bad mode, a partial copy, or the wrong file
# under the right name.
#
# Deliberately NOT a --version constant. A hand-maintained version string is a second
# source of truth that goes stale silently -- which is the whole disease this deploy
# PR exists to treat. This reports the real, resolved command line.
#
# Must run before the lock and before any mkdir: it has to be side-effect free so the
# watchdog's 2-minute cadence cannot be disturbed by a deploy asking a question.
if [ "${1:-}" = "--print-config" ]; then
  printf 'script=gv-bridge-ensure.sh\n'
  printf 'profile=%s\n'   "${PROFILE}"
  printf 'cdp_port=%s\n'  "${CDP_PORT}"
  printf 'url=%s\n'       "${BRIDGE_URL}"
  printf 'chrome_arg=%s\n' "${CHROME_ARGS[@]}"
  exit 0
fi
```

⚠ **Chicken-and-egg, and it resolves itself.** The box's installed copy is the Aug 18 one and has no
`--print-config`. The Task 10 gate runs **after** `setup-gvbridge.sh` has installed the new copy, so the
first deploy that runs the installer is also the first that can call the flag. A gate failure on the
*first* run therefore means the install did not take — which is exactly what the gate is for.

**Acceptance** (lane **L**, no box):
```
$ GV_BRIDGE_PROFILE=/tmp/p GV_BRIDGE_CDP_PORT=9224 bash deploy/gv-bridge-ensure.sh --print-config
script=gv-bridge-ensure.sh
profile=/tmp/p
cdp_port=9224
url=https://voice.google.com
chrome_arg=--mute-audio
chrome_arg=--user-data-dir=/tmp/p
...
chrome_arg=--remote-debugging-port=9224
chrome_arg=--remote-allow-origins=*
chrome_arg=https://voice.google.com
```
- Exit 0, **no Chrome launched**, **no lock file created**, **no log file written**. Assert the last two
  explicitly — a `--print-config` with side effects would be run by the deploy every time against a live
  watchdog.
- `bash deploy/gv-bridge-ensure.sh --print-config | grep -c password-store` → `0`.
- With no arguments the script behaves exactly as before (`set -u` plus `${1:-}` makes the unset case
  safe).

---

### Phase 3 — run the installer, and prove it took

---

#### Task 10 — Run `setup-gvbridge.sh` from the deploy, then gate on the installed artefact · lane **B**

**Depends on:** Tasks 7, 8, 9. ⛔ **All three.** Task 7 stops the install racing the watchdog; Task 8 stops
a session-destroying flag reaching an executed file; Task 9 provides the gate's strongest signal.

The scope doc's framing is the right one: **ours is already shipped; this is "run the thing you already
ship."** Add after the `chmod 755 …/deploy/*.sh` line at `:204`:

```powershell
  # Run the installer we have been shipping and never running. Measured drift on
  # 2026-09-09: the installed ~/bin/gv-bridge-ensure.sh was 1044 B / Aug 18 against a
  # shipped 4981 B / Sep 8 -- the installed copy predated the entire GV auth arc.
  # Radio Console's KIOSK-2 consumes this script and could not have noticed: both
  # copies exit 0 on every path, and their contract is invoke-and-probe on the exit
  # code.
  #
  # XDG_RUNTIME_DIR / DBUS_SESSION_BUS_ADDRESS are set explicitly because this runs
  # over a NON-INTERACTIVE ssh session, where `systemctl --user` otherwise cannot reach
  # the user bus -- and setup-gvbridge.sh runs under `set -euo pipefail`, so its
  # `systemctl --user daemon-reload` failing would abort the installer AFTER the binary
  # sync had already landed. See open question Q1.
  Write-Host "  Installing GV bridge tooling..." -ForegroundColor Yellow
  ssh $SshTarget "XDG_RUNTIME_DIR=/run/user/`$(id -u) DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/`$(id -u)/bus bash ${TargetPath}/deploy/setup-gvbridge.sh"
  if ($LASTEXITCODE -ne 0) { throw "setup-gvbridge.sh failed (exit $LASTEXITCODE) -- the box may still be running a stale ~/bin/gv-bridge-ensure.sh" }

  # --- Post-install gate: two independent signals -------------------------------
  # (1) sha256 -- what the installed file CONTAINS.
  # ⛔ Scoped to the .sh files ONLY. The systemd units and .desktop entries are
  #    GENERATED from heredocs with ${BIN_DIR} expanded (setup-gvbridge.sh:155,:170),
  #    so no repo file exists to compare them against; a checksum gate on those would
  #    fail every single run.
  foreach ($name in @("gv-bridge-ensure.sh", "gv-bridge-restart.sh")) {
    $localHash  = (Get-FileHash (Join-Path $deployScripts $name) -Algorithm SHA256).Hash.ToLower()
    $remoteHash = (ssh $SshTarget "sha256sum ~/bin/$name | cut -d' ' -f1").Trim()
    if ($remoteHash -ne $localHash) {
      throw "INSTALL VERIFICATION FAILED: ~/bin/$name is $remoteHash on ${TargetHost}, expected $localHash. setup-gvbridge.sh reported success but the installed copy does not match what was shipped."
    }
  }

  # (2) --print-config -- what the installed file DOES. Catches a bad mode, a partial
  #     copy, or the right bytes under the wrong name, none of which a checksum sees.
  $cfg = (ssh $SshTarget "~/bin/gv-bridge-ensure.sh --print-config") -join "`n"
  if ($LASTEXITCODE -ne 0) { throw "INSTALL VERIFICATION FAILED: ~/bin/gv-bridge-ensure.sh --print-config exited $LASTEXITCODE -- the installed script is not executable or not runnable." }
  if ($cfg -notmatch 'remote-debugging-port=9224') {
    throw "INSTALL VERIFICATION FAILED: the installed gv-bridge-ensure.sh does not pass --remote-debugging-port=9224. CDP cookie refresh would silently stop working."
  }
  if ($cfg -match 'password-store') {
    throw "INSTALL VERIFICATION FAILED: the installed gv-bridge-ensure.sh passes --password-store. Stop the bridge before it next launches -- that flag discards the profile's v11 cookies."
  }
  Write-Host "  GV bridge tooling verified on the box" -ForegroundColor Green
```

**Acceptance** — lane **B**, owner-run, output pasted back:
- The deploy prints `GV bridge tooling verified on the box`.
- `ssh radio 'ls -l --time-style=long-iso ~/bin/gv-bridge-ensure.sh'` → size **4981** (plus Task 9's
  addition), today's date, mode `-rwxr-xr-x`. The drift measured on 2026-09-09 (1044 B / Aug 18) is gone.
- `ssh radio '~/bin/gv-bridge-ensure.sh --print-config'` includes
  `chrome_arg=--remote-debugging-port=9224` and no `password-store` line.
- **Negative control, and it is the one that matters** — point the gate at a decoy and confirm it
  **throws**:
  ```
  ssh radio 'printf "#!/usr/bin/env bash\nexit 0\n" > ~/bin/gv-bridge-ensure.sh.decoy; chmod 755 ~/bin/gv-bridge-ensure.sh.decoy'
  ```
  A gate never seen to fail is not known to work. Remove the decoy afterwards.

---

#### Task 11 — Decide the watchdog-timer question, and record the decision · lane **L** (decision) + **B** (evidence)

**Depends on:** Task 7. This is the *second, different* hazard — not truncation, but "which unit
definition did this invocation get". `setup-gvbridge.sh` rewrites four unit files then runs
`systemctl --user daemon-reload` and `enable --now gv-bridge-watchdog.timer`, on a timer with a 2-minute
period.

| Option | Cost | Failure mode |
|---|---|---|
| **A — stop the timer around the install, restart after** | One missed 2-minute liveness cycle | ⛔ If the deploy aborts between the stop and the start, **the watchdog stays stopped** and nothing brings the bridge back up. Silent, long-lived, and it ends in the outage class already at the top of `KNOWN-ISSUES.md` |
| **B — leave the timer running; make the unit installs atomic too (Task 7 already does, via `install_file`)** | A timer may fire during `daemon-reload` | An extra or a skipped liveness check. `gv-bridge-ensure.sh` is idempotent by contract, takes a `flock`, and no-ops when the bridge is up |

⭐ **Recommended: B.** The asymmetry decides it. Option B's worst case is one redundant idempotent
invocation of a script explicitly designed for a 2-minute cadence. Option A's worst case is the watchdog
left disabled after a failed deploy — the bridge stays down, the Chrome session rots, and the GV session
dies exactly as recorded in the ACTIVE OUTAGE entry. Trading a harmless, self-healing failure for a
silent, non-self-healing one is the wrong direction.

**If the owner prefers A anyway**, it is only acceptable with a restore that cannot be skipped — the
installer's `set -euo pipefail` and any `throw` in the deploy must both be covered:

```bash
# Only if option A is chosen. The trap is not optional: a deploy that aborts between
# the stop and the start leaves the bridge with no watchdog, which is worse than the
# race it avoids.
systemctl --user stop gv-bridge-watchdog.timer
trap 'systemctl --user start gv-bridge-watchdog.timer || true' EXIT
```

**Acceptance:** the plan records the owner's choice with its reasoning. If B, add a comment above the
`daemon-reload` in `setup-gvbridge.sh` saying the timer is deliberately left running and why. Evidence for
either: `ssh radio "systemctl --user list-timers 'gv-bridge-*'"` shows the watchdog **active** with a
`NEXT` within 2 minutes, both before and after a deploy.

---

#### Task 12 — Owner-run on-box UAT · lane **B**

**Depends on:** all of the above. **Not on the critical path** — run after the `KIOSK-3` / #78/#79 deploy.

```bash
# 1. BEFORE: capture the authoritative config and the drifted script.
ssh radio 'sha256sum /opt/rotary-phone/appsettings.Production.json; \
           grep -E "BluetoothAdapter|UseActualBluetoothHfp" /opt/rotary-phone/appsettings.Production.json; \
           ls -l --time-style=long-iso ~/bin/gv-bridge-ensure.sh; \
           ls -l /tmp/rp-prod.bak 2>&1'
#    Expect: BluetoothAdapter hci1; gv-bridge-ensure.sh 1044 bytes dated Aug 18.

# 2. Deploy on the tar path. It is currently the DEFAULT on that machine (rsync absent
#    from PATH). If rsync has since been installed, remove it from PATH for this run --
#    do NOT simulate the fallback by editing the script.
.\deploy\Deploy-ToLinux.ps1

# 3. AFTER: the config must be byte-identical, and no backup file should be created at
#    all -- the dance is gone, so /tmp/rp-prod.bak must be ABSENT, not merely consumed.
ssh radio 'sha256sum /opt/rotary-phone/appsettings.Production.json; \
           ls -l /tmp/rp-prod.bak 2>&1; \
           ls -l --time-style=long-iso ~/bin/gv-bridge-ensure.sh; \
           ~/bin/gv-bridge-ensure.sh --print-config'
#    Expect: SAME sha256 as step 1; "No such file" for rp-prod.bak; ensure.sh current;
#            chrome_arg=--remote-debugging-port=9224 present; no password-store line.

# 4. The watchdog survived the install.
ssh radio "systemctl --user list-timers 'gv-bridge-*'"
#    Expect: gv-bridge-watchdog.timer ACTIVE, NEXT within 2 minutes.

# 5. The bridge is still up and CDP still answers -- the property the whole GV auth
#    arc depends on.
ssh radio 'pgrep -af "user-data-dir=$HOME/.config/gv-bridge-chrome" | head -1; \
           curl -s http://localhost:9224/json/version | head -c 120'
#    Expect: one Chrome process; a JSON version blob.

# 6. The service came back on the NEW binary.
ssh radio 'systemctl is-active rotary-phone; curl -s localhost:5004/api/gvbridge/status'
#    Expect: active; sipRegistered:true, cookiesValid:true.
```

**Acceptance:** steps 1 and 3 report the **same** sha256 for `appsettings.Production.json`; `/tmp/rp-prod.bak`
is absent after step 3; step 3 shows the current script; steps 4–6 all pass. If Task 4 landed, the deploy
prints **no** `Cannot unlink` lines.

⚠ **Bounded reads only on this box** — never `journalctl -f` or `tail -f`. It is an N100 shared with Radio
Console and journald churn correlates with audible audio distortion there.

---

### Phase 4 — record

---

#### Task 13 — Backup accrual on a per-deploy cadence · lane **L**

**Depends on:** Task 10. A consequence of "run the installer every deploy" that the scope doc's
blast-radius note gestures at without naming.

`backup_if_changed` (`setup-gvbridge.sh:74-80`) writes a **timestamped** `.bak-${STAMP}` every time a
shipped file differs from the installed one. That was right for a script run by hand a few times a year.
Run on every deploy, it accrues backups in `~/bin`, `~/.config/systemd/user`, `~/.config/autostart` and —
**visibly, on a kiosk box** — `~/Desktop`.

```bash
# One rolling backup, not one per run. The protection this exists for is "an operator
# hand-tuned the watchdog interval and should not lose it silently", which one level
# satisfies. Unbounded history was harmless when this script ran by hand a few times a
# year; the deploy now runs it every time, and ~/Desktop is a kiosk screen the owner
# actually looks at.
backup_if_changed() {
    local src="$1" dest="$2"
    if [ -f "$dest" ] && ! cmp -s "$src" "$dest"; then
        cp -p "$dest" "${dest}.bak"
        log "Backed up existing $(basename "$dest") -> $(basename "$dest").bak"
    fi
}
```

`STAMP` becomes unused — remove the assignment at `:61` rather than leaving a dead variable under `set -u`.

**Acceptance:** run `bash deploy/setup-gvbridge.sh` twice against a scratch `HOME` with a modified
installed file; exactly one `.bak` exists per changed file after both runs. Lane **L** — set `HOME` to a
temp dir and stub `google-chrome` on `PATH`, or the Chrome check exits first.

---

#### Task 14 — Add Defect 4 and the priority correction to the scope doc, by annotation · lane **L**

**Depends on:** Tasks 1 and 4.

Append to `docs/plans/deploy-tooling-honest-deploy.md`, after Defect 3:

```markdown
## Defect 4 — the tar path has never been able to detect its own failure (added 2026-09-09)

Found while re-deriving Defect 1's mechanism, not part of the original scope.

The remote command string ends in `chmod`, so **the chain reports `chmod`'s exit status**. `tar` exiting 2
yields a chain exit of **0**. And tar exits 2 on *every* run: `tar -C … -czf - .` always carries a `./`
member and `--unlink-first` calls `unlink(".")` on it, which cannot succeed. So the comment at
`Deploy-ToLinux.ps1:113-114` — *"$LASTEXITCODE is checked so a failed sync ABORTS the deploy"* — has never
been true on this path. The check is real; it is structurally blind.

**Second cost, and it is the one that bites people:** four `tar: … Cannot unlink` lines print on every
successful deploy, which trains the operator to read a failing deploy as normal. The diagnostic that
should raise the alarm is the one already being scrolled past.

**Third, and wider than the tar path: 11 of the script's 20 native calls have no exit check at all** —
including `scp` of the initial `appsettings.Production.json` (`:147`), `chmod 755` on the deploy scripts
(`:204`), `chmod +x` on the server binary (`:208`), the `scp`+install of `rotary-phone.service`
(`:216-217`), and `systemctl restart` (`:221`). Each failure is silent and the script still prints
`=== Deploy Complete ===`.

⚠ **`$ErrorActionPreference = "Stop"` does not fix this** — it does not cover native commands, as the
script's own comment at `:172-176` already states. The remedy is a per-call `$LASTEXITCODE` capture.

Measured 2026-09-09 (`deploy/tests/repro-tar-clobber.sh` case A, plus a native-call audit) and seen live
on the box the same day. The same defect class was found independently in a sibling repo, whose remote
compound ended in `rm -rf` where ours ends in `chmod`.

**Acceptance:** a deliberately-failed extract makes the deploy throw and leaves the service unrestarted;
an unreachable target host aborts at the first failing call instead of printing `=== Deploy Complete ===`.
Handled by Tasks 4 and 4b.
```

Also annotate, in place:

- **Defect 1's bullet** *"reachable on any rsync FAILURE, not only rsync's absence"* → add: **⚠ Corrected
  2026-09-09 — understated. `rsync` is absent from the deploying machine's PowerShell `PATH`, so
  `Get-Command rsync` finds nothing and every deploy from that machine takes this path. It is not a
  fallback; it is the only path running today.**
- **Defect 1's Acceptance line (`:37-38`)** → **superseded — passes against the unfixed code (a live
  deploy on 2026-09-09 satisfied it while the defect was present). See the plan's §0.2 and Task 3.**
- **The Open question for the owner (`:116-121`)** → answered; see the plan's §0.7.

**Acceptance:** additive only; no existing line deleted.

---

## 3. Task dependency graph

```
T1 repro (L) ──┬─> T2 correct KNOWN-ISSUES, entry stays OPEN (L)
               └─> T14 annotate scope doc (L)

T3 --exclude + delete the dance (L/W) ──> T4 files-only archive + honest status (L/W)
                                              └─> T4b check every native call (L/W)
T5 csproj (L)                                            [independent]
T3 ──> T6 print BluetoothAdapter (L/B)

T7 atomic install (L) ──┐
T8 password-store gate (L) ──┼──> T10 run installer + gate (B) ──> T12 owner UAT (B)
T9 --print-config (L) ──┘                    │
                                              └──> T13 backup accrual (L)
T7 ──> T11 watchdog decision (L/B)
```

**Suggested commits**, four of them, each independently revertable — T4 is split out on purpose so the
msys risk in §0.3 can be reverted without losing the clobber fix:

1. `test(deploy): reproduce the tar-pipe clobber and correct the recorded mechanism` — T1, T2, T14
2. `fix(deploy): keep appsettings.Production.json out of the tar stream` — T3, T5, T6
3. `fix(deploy): let the tar path report its own failure` — T4
4. `fix(deploy): check the exit code of every native call` — T4b
5. `fix(deploy): install gv-bridge tooling atomically and run the installer we already ship` — T7, T8, T9,
   T10, T11, T13

Commit messages end with:

```
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01PJcw41E87SDxrugC3mKKLf
```

---

## 4. Out of scope

- Radio Console's `setup-kiosk.sh` gap — theirs, tracked on their side.
- The GV auth arc (#78/#79) and the `KIOSK-3` coordination — merged and parked.
- The *contents* of `appsettings.Production.json`; this PR only stops the deploy destroying it.
- Installing `rsync` on the deploying machine. The owner is doing that separately; it changes which path
  is the default and **does not fix the fallback**, which this PR does.
- `scp -r scripts/` shipping `scripts/bin/Debug/**` (including the whole Playwright package) to the box on
  every deploy. Real, wasteful, and adjacent to §0.6 — but a separate change with its own blast radius.

---

## 5. Docs impact

| File | Change |
|---|---|
| `docs/KNOWN-ISSUES.md` | T2 — correction block appended, **entry stays `🔴 OPEN`**; the superseded "make the restore unconditional" bullet marked in place |
| `docs/plans/deploy-tooling-honest-deploy.md` | T14 — Defect 4 appended; the rsync-absence priority correction, Defect 1's acceptance criterion and the open question annotated |
| `docs/plans/deploy-tooling-honest-deploy-plan.md` | this file |
| `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` | **No change.** Nothing here alters an adapter, a profile or a WirePlumber config. T6 only *prints* `BluetoothAdapter`; it never writes it |
| Cross-repo handoff | **Not required.** `~/bin/gv-bridge-ensure.sh` is consumed by Radio Console's `KIOSK-2` on an invoke-and-probe-the-exit-code contract, and that contract is unchanged — `--print-config` is additive and the argument-free path behaves identically. Worth a courtesy line in the next batched cross-repo message that the script they invoke stopped being three weeks stale |

---

## 6. Open questions for the owner

The scope doc's original open question is **answered** in §0.7 — there is no install-vs-restart race with
`rotary-phone`. Three replace it.

### Q1 ⛔ Does `systemctl --user` work over the deploy's non-interactive ssh session? — **needs the box**

**This is the single largest risk to Task 10 and it cannot be settled from the code.**
`setup-gvbridge.sh` runs under `set -euo pipefail` and calls `systemctl --user daemon-reload` (`:140`) and
`systemctl --user enable --now gv-bridge-watchdog.timer` (`:146`). Over `ssh host 'cmd'` there is no
session bus unless `XDG_RUNTIME_DIR` and `DBUS_SESSION_BUS_ADDRESS` are set or lingering is enabled. If
they are not, `daemon-reload` fails, the installer aborts — **after the binary sync has already landed** —
and the deploy throws in a half-done state.

Task 10 sets both variables defensively, but that is a guess until measured. **Please run and paste:**

```bash
ssh radio 'systemctl --user is-system-running; echo "rc=$?"'
ssh radio 'systemctl --user list-timers "gv-bridge-*" 2>&1 | head -3'
ssh radio 'XDG_RUNTIME_DIR=/run/user/$(id -u) DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus systemctl --user list-timers "gv-bridge-*" 2>&1 | head -3'
ssh radio 'loginctl show-user $(whoami) -p Linger'
```

If the bare call already works, the explicit variables in Task 10 are harmless belt. If only the second
form works, Task 10 is correct as written. If **neither** works, `Linger=no` is the reason and Task 10
needs `loginctl enable-linger` — a box-side change with its own rollback story, which would make this a
**blocker** rather than a detail.

#### ✅ ANSWERED 2026-09-09 (owner-run) — Task 10 is not blocked. The caveat is the interesting part.

```
ssh radio 'systemctl --user is-system-running; echo "rc=$?"'
  running
  rc=0

ssh radio 'systemctl --user list-timers "gv-bridge-*"'
  NEXT                        LEFT      LAST                        PASSED  UNIT
  Wed 2026-09-09 11:50:00 EDT 1min 45s  Wed 2026-09-09 11:48:00 EDT 14s ago gv-bridge-watchdog.timer

ssh radio 'XDG_RUNTIME_DIR=… DBUS_SESSION_BUS_ADDRESS=… systemctl --user list-timers "gv-bridge-*"'
  (identical output)

ssh radio 'loginctl show-user $(whoami) -p Linger'
  Linger=no
```

**The bare call already works over non-interactive ssh**, so by this section's own decision table the
explicit `XDG_RUNTIME_DIR` / `DBUS_SESSION_BUS_ADDRESS` in Task 10 are harmless belt, Task 10 is correct
as written, and no `loginctl enable-linger` is needed.

⚠ **But the measurement was taken in a state Task 10 will not run in, and that is the surviving risk.**
`Linger=no` means the per-user systemd manager exists **only while the user has an active session**. The
bare call worked because there **is** one right now — the box is idle in its normal state, kiosk up and
the watchdog firing, as that timer output shows.

The deploy stops services and the kiosk at Step 2, and Task 10 runs **after** that. If stopping the kiosk
ends the user's login session, the user manager goes away with it and `systemctl --user` fails at exactly
the moment Task 10 needs it — **after the binary sync has already landed**. Nothing was mid-deploy when
this was measured, so the measurement cannot speak to that state.

⭐ **This is the same shape as everything else in this plan: a check that passes in the state you can
easily observe, and says nothing about the state that matters.** "Q1 answered" must not be read as
"Task 10 is safe".

**How to settle it cheaply when Task 10 is built** — one command, in the right state, on a real deploy:
run `ssh radio 'systemctl --user is-system-running'` **after** the deploy's Step 2 has stopped the kiosk
and **before** the installer runs. That converts the assumption into a measurement.

**If the manager does go away**, Q2's `--scripts-only` contingency removes the `systemctl --user`
dependency from the deploy path entirely and the blocker evaporates. The contingency is already
designed — it simply has not been chosen.

### Q2 — Full installer, or a `--scripts-only` mode?

The decision to **run the installer** is settled and is not re-opened. This is only about *how much of it*
runs, which the scope doc explicitly leaves open ("consider whether a narrow single-file install path is
warranted").

Every deploy currently re-applies: two scripts, four systemd units, `daemon-reload`, `enable --now`, an
autostart entry, a desktop shortcut, and an extension check — to update one script.

- **Full run** (as planned): faithful to "run the thing you already ship", keeps one code path, and the
  units genuinely can drift too. Costs the blast radius above and inherits Q1's `systemctl --user`
  dependency on **every** deploy.
- **`--scripts-only`**: a flag that does steps 2 and 3 (directories and the two `.sh` files) and skips
  units, autostart, shortcut and `systemctl` entirely. Cuts the surface to the two files that actually
  drifted, and — usefully — **removes the `systemctl --user` dependency from the deploy path**, which
  would neutralise Q1 as a blocker. Costs a second code path, and unit drift would then need its own
  trigger.

⭐ **Recommendation: full run**, on the grounds that a narrow path is a second thing to keep correct and
the units are shipped to the box already. **But if Q1 comes back "neither form works", switch to
`--scripts-only`** — it turns a blocker into a non-issue, and it is a smaller change than enabling
lingering on a box shared with another service.

### Q3 — Option A or B for the watchdog timer? (Task 11)

Recommendation and reasoning in Task 11. **Recommended: B** — leave the timer running. Option A's failure
mode (watchdog silently left stopped after an aborted deploy) is strictly worse than the race it removes.
Confirm, and the decision gets recorded in the code.
