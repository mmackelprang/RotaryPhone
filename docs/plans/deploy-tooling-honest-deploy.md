# Scope — `Deploy-ToLinux.ps1`: stop reporting success without doing the job

**Status:** scoped, not yet planned. **Date:** 2026-09-09.
**One theme, three defects:** every one is the deploy telling the operator it succeeded while the box is
not in the state the operator believes.

⚠ **Sequencing:** this PR is **not** on the coordinated-deploy critical path. Radio Console's `KIOSK-3`
install and the #78/#79 deploy come first. This fixes the tooling that will run *next* time.

---

## Defect 1 — 🔴 the tar-pipe fallback can clobber `appsettings.Production.json`

**Decision taken: fix the restore, keep the fallback.**

- The **rsync** path is safe: `--exclude 'appsettings.Production.json'` (`:89`).
- The **tar-pipe fallback** (`:124-129`) backs up to `/tmp/rp-prod.bak`, extracts, then restores.
- ⚠ **It is reachable on any rsync FAILURE, not only rsync's absence** (`:98`) — a transient network
  error drops a working machine onto this path.

**Why it matters beyond this service:** the clobbered template resets **`BluetoothAdapter: hci1`** and
`UseActualBluetoothHfp`. That **crosses the audio boundary into Radio Console** and nothing in the
deploy surfaces it. This is the single most dangerous item in the repo's KNOWN-ISSUES.

### ⛔ Re-derive the mechanism before fixing it

`KNOWN-ISSUES.md` states that `set -e -o pipefail` aborts the chain before the restore `mv`. **Treat
that as unverified.** The `set -e -o pipefail` is in the **local** script (`:123`), while
backup → extract → restore is a `;`-separated string executed by the **remote** shell — where a failed
`tar` would *not* stop the following commands. The bug is real (observed in PR #72 UAT, finding L3); the
recorded *explanation* may not be.

⭐ **Twice today a correct conclusion rested on a wrong mechanism** (a line-ending diagnosis, and an
absence-grep). A fix built on the wrong mechanism is the same trap: it may appear to work and fail
later for the original reason. **Reproduce the clobber first, then fix what actually causes it.**

**Acceptance:** a deliberately-failed rsync followed by the tar path leaves the box's
`appsettings.Production.json` byte-identical, proven by sha256 before and after.

---

## Defect 2 — the deploy ships an installer it never runs

**Decision taken: run `setup-gvbridge.sh`, then verify the installed artefact.**

`setup-gvbridge.sh` appears in `Deploy-ToLinux.ps1` only in **comments** (`:169`, `:170`, `:192`). The
`.sh` files are copied to `${TargetPath}/deploy/` (`:186`) and never installed.

**Measured drift, 2026-09-09:**

| Copy | Size | Date |
|---|---|---|
| **installed** `~/bin/gv-bridge-ensure.sh` (executed) | 1044 B, 13 lines | Aug 18 |
| **shipped** `/opt/rotary-phone/deploy/gv-bridge-ensure.sh` | 4981 B | Sep 8 |

Three weeks; the installed copy predates the whole GV auth arc. **Radio Console's `KIOSK-2` consumes it
and could not have noticed** — both copies exit 0 on every path, and their contract is invoke-and-probe
on the exit code.

⭐ **Our gap is narrower than Radio Console's, and their shape must not be copied onto it.** They must
*ship* the installer then run it — theirs is not on the box at all. **Ours is already shipped; the PR is
"run the thing you already ship."**

### Verification design — checked, not assumed

| Question | Answer | Consequence |
|---|---|---|
| Does `gv-bridge-ensure.sh` contain templating placeholders? | **No** — zero `@UPPER@` matches in it or `gv-bridge-restart.sh` | ✅ A file-level comparison **is** valid for these |
| Are the systemd units / `.desktop` files templated? | **Yes** — generated from heredocs with `${BIN_DIR}` expanded (`:155`, `:170`) | ⛔ **Never compare those to a repo file** |
| Is file mode handled correctly? | **Yes, already** — `install -m` (`:89`, `:105`), with the umask-0002 / 775-breaks-GNOME reasoning documented at `:94-98` | No change needed |

⚠ **This check exists because Radio Console's launcher ships `@ROTARY_UNIT@` and is substituted at
install time — a naive copy there would install a broken placeholder that looks correct.** Ours has no
placeholders, so the risk does not apply; but the *verification* had to be designed around the answer,
not around the assumption.

**Acceptance:** after deploy, `~/bin/gv-bridge-ensure.sh` matches `deploy/gv-bridge-ensure.sh` by
sha256, and the deploy **fails** if it does not.

⭐ **Better if cheap — make the artefact state its own identity.** A checksum tests what the file
*contains*; a self-report tests what the installed thing *does*, catching a bad mode, a truncated copy
or the wrong file. If `gv-bridge-ensure.sh` can grow a `--version` or `--print-config` flag, the
post-install gate should call **that** instead. This is the next step beyond "check presence, not
absence" — the rule the corrected `KIOSK-3` gate produced.

### ⚠ Blast radius — idempotent is not the same as narrow

`setup-gvbridge.sh` is idempotent, but it is idempotent across a **wide** surface: it installs the
scripts *and* systemd units *and* an autostart entry *and* a desktop shortcut *and* checks the
extension. **Running it on every deploy re-applies all of that** to update one script. Probably fine —
but decide it deliberately, and consider whether a narrow single-file install path is warranted, as
Radio Console did for `KIOSK-3`.

---

## Defect 3 — the constraint that only exists once Defect 2 is fixed

⛔ **The PR must assert `--password-store=basic` is absent from `gv-bridge-ensure.sh`.**

Currently both copies are clean (verified by Radio Console: `grep -c password-store` → `0` and `0`).
**Fixing Defect 2 converts a dormant file into an executed one**, at which point an inert flag stops
being inert. That flag, on a profile already holding v11 cookies, makes the keyring-derived key
unobtainable and **Chrome discards them** — measured live at 45 v11 → 16 v10, destroying the Google
Voice session. `~/.config/gv-bridge-chrome` is exactly that profile.

**Acceptance:** the deploy refuses to install a `gv-bridge-ensure.sh` containing that flag.

---

## Out of scope

- Radio Console's `setup-kiosk.sh` gap — theirs, tracked on their side.
- The GV auth arc (#78/#79) and the `KIOSK-3` coordination — already merged and parked.
- Any change to `appsettings.Production.json`'s *contents*; this PR only stops the deploy destroying it.

## Open question for the owner

**Does the deploy still stop and restart `rotary-phone` at the point the installer would run?** If so,
the ordering of install-vs-restart needs deciding, since `setup-gvbridge.sh` touches autostart and
timers. Radio Console explicitly refused to run their installer while a deploy held the tree; the same
race may exist here.
