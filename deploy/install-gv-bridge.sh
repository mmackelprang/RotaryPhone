#!/usr/bin/env bash
# =============================================================================
# install-gv-bridge.sh — install the GV bridge launch scripts into ~/bin.
#
# Installs, from the shipped deploy directory:
#   gv-bridge-ensure.sh   -> ~/bin  (watchdog timer, login autostart, nightly restart)
#   gv-bridge-restart.sh  -> ~/bin  (nightly restart timer)
#   gv-keyring-unlock.py  -> ~/bin  (unlocks the login keyring before Chrome starts)
#
# Run by Deploy-ToLinux.ps1 on every deploy, so the box runs the repo's scripts instead of
# drifting hand-installed copies. Narrow on purpose: setup-gvbridge.sh also does one-time setup
# (including the SUPERSEDED Chromium unit) that must not re-run on each deploy.
#
# SAFETY GATE: before installing anything, the new ensure script's --print-config Chrome flags
# must match the command line of the bridge Chrome running now. A mismatch means the new script
# would launch Chrome differently, so the install is refused and ~/bin is left untouched.
# If no bridge Chrome is running there is nothing to compare against; the install is refused
# unless --skip-flag-check is given.
#
# Every replaced file is backed up as <name>.bak-<timestamp>. Rollback: copy those back.
#
# Usage: install-gv-bridge.sh [--skip-flag-check]
# =============================================================================
set -euo pipefail

SKIP_FLAG_CHECK=0
for arg in "$@"; do
    case "$arg" in
        --skip-flag-check) SKIP_FLAG_CHECK=1 ;;
        -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/bin"
STAMP="$(date '+%Y%m%d-%H%M%S')"
FILES=(gv-bridge-ensure.sh gv-bridge-restart.sh gv-keyring-unlock.py)

log()  { echo "[gv-bridge] $1"; }
fail() { echo "[gv-bridge] ERROR: $1" >&2; exit 1; }

for f in "${FILES[@]}"; do
    [ -f "${DEPLOY_DIR}/${f}" ] || fail "missing ${DEPLOY_DIR}/${f} — the deploy did not ship it. Nothing installed."
done
bash -n "${DEPLOY_DIR}/gv-bridge-ensure.sh"  || fail "gv-bridge-ensure.sh has a syntax error. Nothing installed."
bash -n "${DEPLOY_DIR}/gv-bridge-restart.sh" || fail "gv-bridge-restart.sh has a syntax error. Nothing installed."

# --- Safety gate: same Chrome flags as the running bridge -------------------
want="$(bash "${DEPLOY_DIR}/gv-bridge-ensure.sh" --print-config | sed -n 's/^chrome_arg=//p' | sort)"
profile="$(bash "${DEPLOY_DIR}/gv-bridge-ensure.sh" --print-config | sed -n 's/^profile=//p')"
# The browser process carries the profile marker and no --type= (renderers/helpers do).
pid=""
for p in $(pgrep -f "user-data-dir=${profile}" || true); do
    if [ -r "/proc/${p}/cmdline" ] && ! tr '\0' '\n' < "/proc/${p}/cmdline" | grep -q '^--type='; then
        pid="$p"; break
    fi
done
if [ -n "$pid" ]; then
    have="$(tr '\0' '\n' < "/proc/${pid}/cmdline" | tail -n +2 | sed '/^$/d' | sort)"
    if [ "$want" != "$have" ]; then
        echo "[gv-bridge] new script's Chrome flags differ from the running bridge (pid ${pid}):" >&2
        diff <(echo "$have") <(echo "$want") | sed 's/^/[gv-bridge]   /' >&2 || true
        fail "REFUSING to install: the new ensure script would launch Chrome differently. ~/bin untouched."
    fi
    log "flag check: new ensure script launches Chrome with the running bridge's exact flags"
elif [ "$SKIP_FLAG_CHECK" -eq 1 ]; then
    log "flag check SKIPPED (--skip-flag-check): no running bridge to compare against"
else
    fail "no running bridge Chrome to compare flags against. Start it, or rerun with --skip-flag-check. ~/bin untouched."
fi

# --- Install ----------------------------------------------------------------
mkdir -p "$BIN_DIR"
for f in "${FILES[@]}"; do
    src="${DEPLOY_DIR}/${f}"; dest="${BIN_DIR}/${f}"
    if [ -f "$dest" ] && cmp -s "$src" "$dest"; then
        log "${f} already current"
        continue
    fi
    if [ -f "$dest" ]; then
        cp -p "$dest" "${dest}.bak-${STAMP}"
        log "backed up ${f} -> ${f}.bak-${STAMP}"
    fi
    install -m 755 "$src" "${dest}.new" && mv -f "${dest}.new" "$dest" || { rm -f "${dest}.new"; fail "could not install ${dest}"; }
    log "installed ${dest}"
done

# --- Post-install: the INSTALLED ensure script reports the shipped config ----
if [ "$(bash "${BIN_DIR}/gv-bridge-ensure.sh" --print-config)" != "$(bash "${DEPLOY_DIR}/gv-bridge-ensure.sh" --print-config)" ]; then
    fail "installed ${BIN_DIR}/gv-bridge-ensure.sh does not report the shipped config. Backups: *.bak-${STAMP}"
fi
log "post-install check: installed ensure script reports the shipped config"
