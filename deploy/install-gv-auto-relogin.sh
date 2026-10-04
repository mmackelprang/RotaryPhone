#!/usr/bin/env bash
# =============================================================================
# Install ONLY GV auto-relogin: the actuator, its breaker, the CDP helper, the owner's
# sign-in driver (if shipped), and two systemd user units. Run from the deploy, and safe
# to run by hand. docs/plans/gv-auto-relogin.md Task 15.
#
#   bash /opt/rotary-phone/deploy/install-gv-auto-relogin.sh
#   bash /opt/rotary-phone/deploy/install-gv-auto-relogin.sh --enable
#
# WHY A NARROW INSTALLER, NOT A LINE IN setup-gvbridge.sh — the same two reasons
# install-gv-session-alarm.sh gives, unchanged: setup-gvbridge.sh also installs
# gv-bridge-ensure.sh, whose shipped copy changes an exit-code contract Radio Console
# reads (cross-boundary, not this arc's); and it re-applies autostart, a desktop
# shortcut and `enable --now` on the watchdog, none of which should happen because
# auto-relogin needed installing. This script touches ONLY the files listed below.
#
# ⛔ THE TIMER IS NOT ENABLED BY DEFAULT, AND FOR A STRONGER REASON THAN THE ALARM'S.
# The alarm defers enabling because a missing token would make it fail 288 times a day.
# This one defers because ENABLING IT ARMS AN AUTOMATION THAT SUBMITS A PASSWORD TO
# GOOGLE. That is the owner's act (gate G2, spec §3). --enable is separate and deliberate,
# and it REFUSES, non-zero and saying which, on each of:
#   1. no gv-account.conf                -> the actuator would trip on its first Stale
#   2. gv-account.conf not mode 600, or not owned by this user
#                                        -> a credential readable by Radio Console (same uid)
#                                           or by whoever owns the file
#   3. no ~/bin/gv-session-alarm.sh      -> ⛔ THE ESCALATION PATH IS ABSENT (plan §0.1).
#                                           A breaker that trips into silence is worse
#                                           than no automation.
#   4. no ~/bin/gv-relogin-signin.py     -> nothing to run: the owner's driver is not
#                                           installed, and enabling would only log
#                                           "not installed" every 5 minutes
#
# ⛔ AND THE BREAKER IS NEVER ARMED HERE. An absent state file reads as TRIPPED (fail
# closed), so a fresh install attempts nothing until a human runs
# `gv-auto-relogin.sh --reset`. Arming an automation that submits a password is a human
# act with a name on it, not a side effect of a deploy.
# =============================================================================
set -euo pipefail

ENABLE_TIMER=0
for arg in "$@"; do
    case "$arg" in
        --enable) ENABLE_TIMER=1 ;;
        -h|--help) sed -n '2,39p' "$0"; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/bin"
SYSTEMD_USER_DIR="${HOME}/.config/systemd/user"
STATE_DIR="${HOME}/.local/state"
ACCOUNT_FILE="${GV_RELOGIN_ACCOUNT_FILE:-/opt/rotary-phone/gv-account.conf}"

log()  { echo "[gv-auto-relogin] $1"; }
fail() { echo "[gv-auto-relogin] ERROR: $1" >&2; exit 1; }

# Atomic replace by rename — the rationale is install-gv-session-alarm.sh's install_atomic,
# duplicated for the reason given there (sourcing a sibling installer to borrow four lines
# would execute it). The timer fires ~/bin/gv-auto-relogin.sh every 5 minutes once enabled;
# there is no quiet window to install in.
install_atomic() {
    local src="$1" dest="$2" mode="$3"
    if ! install -m "$mode" "$src" "${dest}.new"; then rm -f "${dest}.new"; return 1; fi
    if ! mv -f "${dest}.new" "$dest";            then rm -f "${dest}.new"; return 1; fi
}
backup_if_changed() {
    local src="$1" dest="$2"
    if [ -f "$dest" ] && ! cmp -s "$src" "$dest"; then
        cp -p "$dest" "${dest}.bak"
        log "Backed up existing $(basename "$dest") -> $(basename "$dest").bak"
    fi
}
install_one() {
    local src="$1" dest="$2" mode="$3"
    [ -f "$src" ] || fail "missing ${src} — the deploy did not ship it. NOT installing a partial auto-relogin."
    backup_if_changed "$src" "$dest"
    install_atomic "$src" "$dest" "$mode" || fail "could not install ${dest}"
    log "installed ${dest} (mode ${mode})"
}

mkdir -p "$BIN_DIR" "$SYSTEMD_USER_DIR" "$STATE_DIR"

# The breaker FIRST: the actuator sources it and refuses to run without it, so the
# window in which a new actuator could meet an old breaker is closed from this side.
install_one "${DEPLOY_DIR}/gv-auto-relogin-breaker.sh"      "${BIN_DIR}/gv-auto-relogin-breaker.sh"      755
install_one "${DEPLOY_DIR}/gv-cdp.py"                       "${BIN_DIR}/gv-cdp.py"                       755
install_one "${DEPLOY_DIR}/gv-auto-relogin.sh"              "${BIN_DIR}/gv-auto-relogin.sh"              755
install_one "${DEPLOY_DIR}/systemd/gv-auto-relogin.service" "${SYSTEMD_USER_DIR}/gv-auto-relogin.service" 644
install_one "${DEPLOY_DIR}/systemd/gv-auto-relogin.timer"   "${SYSTEMD_USER_DIR}/gv-auto-relogin.timer"   644

# ⚠ The owner's driver is OPTIONAL here. Shipped -> installed. Not shipped -> nothing is
# installed AND NOTHING IS REMOVED: an installed driver the repo does not carry is
# reported by the drift check (--group relogin), not silently deleted by an installer.
if [ -f "${DEPLOY_DIR}/gv-relogin-signin.py" ]; then
    install_one "${DEPLOY_DIR}/gv-relogin-signin.py" "${BIN_DIR}/gv-relogin-signin.py" 755
elif [ -f "${BIN_DIR}/gv-relogin-signin.py" ]; then
    log "⚠ ${BIN_DIR}/gv-relogin-signin.py is installed but this deploy did not ship one. Left in place; the drift check reports it."
else
    log "no sign-in driver shipped (deploy/gv-relogin-signin.py is the owner's to write). Auto-relogin is inert until it is."
fi

if systemctl --user daemon-reload 2>/dev/null; then
    log "systemd user daemon reloaded"
else
    log "WARNING: 'systemctl --user daemon-reload' failed (no user bus?). The unit FILES are"
    log "         installed; systemd picks them up on the next user session."
fi

if [ "$ENABLE_TIMER" -eq 1 ]; then
    [ -e "$ACCOUNT_FILE" ] \
        || fail "refusing --enable: ${ACCOUNT_FILE} is missing. The owner populates it on the box by hand (docs/gv-relogin-driver-contract.md); no deploy creates it."
    mode="$(stat -c %a "$ACCOUNT_FILE")"
    [ "$mode" = "600" ] \
        || fail "refusing --enable: ${ACCOUNT_FILE} is mode ${mode}, not 600. This box is shared with Radio Console under the same uid."
    [ "$(stat -c %u "$ACCOUNT_FILE")" = "$(id -u)" ] \
        || fail "refusing --enable: ${ACCOUNT_FILE} is not owned by $(id -un)."
    [ -x "${BIN_DIR}/gv-session-alarm.sh" ] \
        || fail "refusing --enable: the GV session alarm is not installed at ${BIN_DIR}/gv-session-alarm.sh. It is the ONLY escalation path this actuator has — a breaker that trips with no alarm installed stops silently, which is worse than no automation. Install and prove the alarm first."
    [ -f "${BIN_DIR}/gv-relogin-signin.py" ] \
        || fail "refusing --enable: no sign-in driver at ${BIN_DIR}/gv-relogin-signin.py. Enabling would only log 'not installed' every 5 minutes. Ship the owner's driver first."
    systemctl --user enable --now gv-auto-relogin.timer
    log "timer ENABLED. The breaker is NOT armed by this; see its state below."
else
    log "timer INSTALLED but NOT enabled. Enabling it arms an automation that submits a"
    log "password to Google — the owner's decision. When ready:"
    log "    bash ${DEPLOY_DIR}/install-gv-auto-relogin.sh --enable"
fi

# Self-report from the INSTALLED path, so the deploy's gate asks the installed thing what
# it is rather than checking what a file contains.
log "installed state:"
"${BIN_DIR}/gv-auto-relogin.sh" --print-config
log "breaker: $("${BIN_DIR}/gv-auto-relogin.sh" --status | head -1)"
log "The breaker starts TRIPPED on a fresh install. A human arms it with: gv-auto-relogin.sh --reset"
