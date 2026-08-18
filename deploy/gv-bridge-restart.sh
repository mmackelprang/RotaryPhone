#!/usr/bin/env bash
# =============================================================================
# gv-bridge-restart.sh — nightly recycle of the GV bridge browser.
#
# Invoked by gv-bridge-restart.timer at 04:00 to shed leaked renderer heap.
# Kills the running bridge, then delegates the relaunch to gv-bridge-ensure.sh
# so the Chrome command line exists in exactly one place.
#
# That delegation is deliberate. The box's hand-written copy of this script
# carried its own duplicate of the launch line, and when the CDP flags were
# added to ensure.sh on 2026-08-18 this copy did not get them — so the nightly
# recycle would have relaunched the bridge WITHOUT --remote-debugging-port and
# silently broken cookie refresh until someone noticed empty SMS lists. One
# launch line, one place to fix.
#
# Killing by profile marker is safe from self-inflicted kills: this script's own
# command line is its path, which does not contain the marker, and neither does
# ensure.sh's.
# =============================================================================
set -u

PROFILE="${GV_BRIDGE_PROFILE:-${HOME}/.config/gv-bridge-chrome}"
LOG="${GV_BRIDGE_LOG:-${HOME}/.local/state/gv-bridge-restart.log}"
MARKER="user-data-dir=${PROFILE}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENSURE="${SCRIPT_DIR}/gv-bridge-ensure.sh"

ts() { date '+%Y-%m-%d %H:%M:%S'; }

mkdir -p "$(dirname "${LOG}")" 2>/dev/null || true

if pgrep -f "${MARKER}" >/dev/null 2>&1; then
  pkill -f "${MARKER}"
  sleep 3
  pkill -9 -f "${MARKER}" 2>/dev/null || true
  sleep 1
  echo "$(ts) restart: killed existing" >> "${LOG}"
fi

if [ ! -x "${ENSURE}" ]; then
  echo "$(ts) restart: FAILED - ${ENSURE} missing or not executable" >> "${LOG}"
  exit 1
fi

# ensure.sh clears the stale Singleton* locks the kill above leaves behind, then
# relaunches. It no-ops if the kill did not actually take, which is the right
# outcome: a bridge that is still up beats one that is down.
"${ENSURE}"
echo "$(ts) restart: relaunched via ensure" >> "${LOG}"
