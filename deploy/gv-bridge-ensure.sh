#!/usr/bin/env bash
# =============================================================================
# gv-bridge-ensure.sh — start the GV bridge browser if it is not already up.
#
# Invoked by:
#   - gv-bridge-watchdog.timer                      every 2 minutes (liveness)
#   - ~/.config/autostart/gv-bridge-chrome.desktop  at GNOME login
#   - gv-bridge-restart.sh                          after the nightly kill
#   - NOT WIRED YET: the deploy's post-install gate --print-config, side-effect free
#     (the flag works; nothing calls it today -- see the Self-report note below)
#
# Idempotent by contract: the watchdog runs this every 2 minutes, so an
# invocation made while the bridge is already up does nothing and exits 0.
#
# Liveness is tested by profile marker rather than by a systemd unit because
# Chrome self-reparents out of the transient systemd-run scope into its own app
# scope. No other browser on this box uses this --user-data-dir, so a process
# carrying it is the bridge.
#
# What the bridge is FOR: holding one authenticated Google Voice session that
# RotaryPhone's API can scrape cookies from over CDP. It no longer carries call
# audio or drives answer/hangup — see the --load-extension note below.
# =============================================================================
set -u

# Paths are overridable so the script can be exercised outside the radio box;
# the defaults are the values the box actually runs with.
PROFILE="${GV_BRIDGE_PROFILE:-${HOME}/.config/gv-bridge-chrome}"
EXTENSION_DIR="${GV_BRIDGE_EXTENSION_DIR:-/opt/rotary-phone/ChromeExtension}"
CDP_PORT="${GV_BRIDGE_CDP_PORT:-9224}"
LOG="${GV_BRIDGE_LOG:-${HOME}/.local/state/gv-bridge-restart.log}"
BRIDGE_URL="${GV_BRIDGE_URL:-https://voice.google.com}"
MARKER="user-data-dir=${PROFILE}"

ts() { date '+%Y-%m-%d %H:%M:%S'; }

# --- The launch command line -------------------------------------------------
# Built HERE, before the lock and before any mkdir, so --print-config below can
# report it without taking the lock or touching the filesystem. The construction
# is pure — a [ -d ] test and array appends — so nothing changes by it happening
# earlier than it used to.
CHROME_ARGS=(
  # Keeps Google Voice's own ringer and call audio out of the console speakers.
  --mute-audio
)

# Chrome has ignored --load-extension since v137; this box runs Chrome 151, and
# the live profile's Preferences file lists only Chrome's built-in extensions —
# the GV Bridge extension is NOT loaded (verified 2026-08-18). Calls work anyway
# because audio moved to the SIPSorcery DTLS-SRTP path in the .NET service and
# answer/hangup go over SIP, not DOM clicks. The flag is passed only to keep this
# command line identical to the process the box is running today; nothing
# depends on it. Do not add code that assumes the extension is present.
if [ -d "${EXTENSION_DIR}" ]; then
  CHROME_ARGS+=( "--load-extension=${EXTENSION_DIR}" )
fi

CHROME_ARGS+=(
  "--user-data-dir=${PROFILE}"
  --no-first-run
  --disable-default-apps
  --disable-background-timer-throttling
  --disable-renderer-backgrounding
  "--window-size=800,600"
  # A no-op under Wayland (the compositor places the window); retained because
  # the running process carries it. Off-screen placement is not what keeps this
  # window out of the way — stacking order is.
  "--window-position=10000,10000"
  --ozone-platform=wayland
  # LOAD-BEARING, not a debug aid. RotaryPhone's API pulls the authenticated
  # Google session cookies out of this browser over CDP
  # (POST /api/gvbridge/cookies/refresh-from-browser, driven every 20 minutes by
  # cron). Drop these two flags and that call cannot reach the browser: the API
  # reports "authenticated client unavailable" and the SMS and voicemail lists
  # come back empty. Verified end-to-end 2026-08-18.
  "--remote-debugging-port=${CDP_PORT}"
  "--remote-allow-origins=*"
  "${BRIDGE_URL}"
)

# --- Self-report -------------------------------------------------------------
# NOT CALLED BY THE DEPLOY TODAY. --print-config exists and is genuinely
# side-effect free, but nothing invokes it: the string "--print-config" appears
# nowhere in Deploy-ToLinux.ps1, which says as much itself ("Today setup-gvbridge.sh
# is shipped but never executed by the deploy"). Wiring it is plan Task 10, not
# started. Do not read the paragraph below as a description of current behaviour.
#
# THE INTENT, once Task 10 lands: the deploy calls this on the INSTALLED copy after
# setup-gvbridge.sh runs, so the gate tests what the installed thing DOES rather
# than what a file contains -- a checksum cannot catch a bad mode, a partial copy,
# or the wrong file under the right name.
#
# Deliberately NOT a --version constant. A hand-maintained version string is a
# second source of truth that goes stale silently — which is the whole disease
# the deploy PR this arrived with exists to treat. This reports the real,
# resolved command line, so it cannot drift from the code it lives in.
#
# Must run BEFORE the lock and BEFORE any mkdir: it has to be side-effect free so
# the watchdog's 2-minute cadence cannot be disturbed by a deploy asking a
# question. It launches no Chrome, creates no lock file and writes no log.
#
# ${1:-} rather than $1 because of `set -u` above: the no-argument case is the
# normal one and must stay safe.
if [ "${1:-}" = "--print-config" ]; then
  printf 'script=gv-bridge-ensure.sh\n'
  printf 'profile=%s\n'    "${PROFILE}"
  printf 'cdp_port=%s\n'   "${CDP_PORT}"
  printf 'url=%s\n'        "${BRIDGE_URL}"
  printf 'chrome_arg=%s\n' "${CHROME_ARGS[@]}"
  exit 0
fi

# Serialize against the other launcher. The watchdog fires every 2 minutes and
# the nightly recycle kills-then-relaunches, so without this the recycle's
# `pkill -9` can land on a Chrome the watchdog started a moment earlier and
# leave a half-initialised profile behind. Both scripts take the same lock.
#
# Failing to take it is not an error here: whoever holds it is already bringing
# the bridge up or recycling it, which is exactly the outcome this script wants.
LOCK="${GV_BRIDGE_LOCK:-${PROFILE}.lock}"

# Only lock if the lock is actually obtainable. If flock is missing or the file
# cannot be opened, carry on WITHOUT it: an unserialized launch risks a rare
# double-start, but treating an unopenable lock as "someone else has it" would
# mean never launching the bridge at all, which is far worse.
if command -v flock >/dev/null 2>&1 && : >>"${LOCK}" 2>/dev/null; then
  exec 9>>"${LOCK}"
  flock -n 9 || exit 0
fi

# Checked under the lock, so the answer cannot go stale between here and launch.
if pgrep -f "${MARKER}" >/dev/null 2>&1; then
  exit 0
fi

mkdir -p "$(dirname "${LOG}")" 2>/dev/null || true

# Chrome refuses to open a profile whose Singleton* lock files survive a crash
# or a kill -9, so clear them before every launch attempt.
rm -f "${PROFILE}"/Singleton* 2>/dev/null || true

# --collect reaps the transient unit once Chrome reparents itself away from it,
# so repeated launches do not accumulate failed scopes.
systemd-run --user --collect google-chrome "${CHROME_ARGS[@]}" >> "${LOG}" 2>&1
echo "$(ts) ensure: bridge was down -> launched" >> "${LOG}"
