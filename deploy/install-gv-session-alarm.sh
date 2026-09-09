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
        -h|--help) sed -n '2,32p' "$0"; exit 0 ;;
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
# ⚠ This duplicates install_atomic() from deploy/setup-gvbridge.sh, which HAS now
# landed on main (e6f8018, PR #84 — the plan predates that merge and says the
# opposite). It stays duplicated anyway, and for a better reason than the plan's:
# sourcing setup-gvbridge.sh to borrow one four-line helper would execute a
# 13KB script whose whole point is that this installer must not run it. Four
# duplicated lines are cheaper than that coupling. Converge only if the helper
# is ever extracted into a file that does nothing but define helpers.
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
install_one "${DEPLOY_DIR}/systemd/gv-session-alarm.service"      "${SYSTEMD_USER_DIR}/gv-session-alarm.service"     644
install_one "${DEPLOY_DIR}/systemd/gv-session-alarm.timer"        "${SYSTEMD_USER_DIR}/gv-session-alarm.timer"       644

# ⚠ NOT fatal, and the `|| :` is load-bearing rather than lazy. A machine with no
# user D-Bus — a CI container, a box reached over a non-session ssh — has no
# `systemctl --user` to reload, and under `set -e` that would abort the install
# AFTER the three files had already been written. The files landing is what this
# script asserts; the reload is what makes systemd notice, and it happens anyway
# the next time the user session starts.
if systemctl --user daemon-reload 2>/dev/null; then
    log "systemd user daemon reloaded"
else
    log "WARNING: 'systemctl --user daemon-reload' failed (no user bus?). The unit"
    log "         FILES are installed; systemd will pick them up on the next user"
    log "         session. --enable below will fail until a user bus exists."
fi

# ⚠ LINGERING IS A SILENT-DEATH MODE FOR A USER TIMER, so it is REPORTED rather than
# assumed. Without it the user manager stops when the last session ends, and
# gv-session-alarm.timer simply does not fire — while every file check above passes and
# the deploy prints success. Measured on `radio` 2026-09-09: Linger=no. It works there
# today only because the box holds a graphical session; that is a circumstance, not a
# guarantee, and the gateway dead-man is what actually covers it.
#
# ⛔ NOT enabled automatically. `loginctl enable-linger` changes the login behaviour of a
# box shared with Radio Console; that is the owner's call, not an installer's.
LINGER="$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null || echo unknown)"
if [ "$LINGER" = "yes" ]; then
    log "lingering is ON — the timer will fire with nobody logged in."
else
    log "⚠ lingering is ${LINGER}. This is a USER timer: with no session open the user"
    log "  manager stops and the alarm SILENTLY DOES NOT RUN, while every file check"
    log "  here still passes. It works while the box holds a graphical session."
    log "  The gateway dead-man is what covers the gap. To close it properly:"
    log "      sudo loginctl enable-linger $(id -un)"
fi

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
