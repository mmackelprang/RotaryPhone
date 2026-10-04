#!/usr/bin/env bash
# =============================================================================
# gv-bridge-ensure.sh — start the GV bridge browser if it is not already up.
#
# Invoked by:
#   - gv-bridge-watchdog.timer                      every 2 minutes (liveness)
#   - ~/.config/autostart/gv-bridge-chrome.desktop  at GNOME login
#   - gv-bridge-restart.sh                          after the nightly kill
#   - install-gv-bridge.sh (every deploy): --print-config, side-effect free, as the
#     flag-match safety gate before it installs this script into ~/bin
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
# Called by install-gv-bridge.sh, which Deploy-ToLinux.ps1 runs on every deploy:
# BEFORE install, on the shipped copy, its chrome_arg lines must match the running
# bridge Chrome's command line or nothing is installed; AFTER install, on the
# INSTALLED copy, its output must match the shipped copy's. The second call is what
# tests what the installed thing DOES rather
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
# ⚠ And per spec §8, fixing this is a PREREQUISITE for deploying this file at
# all: the shipped copy has flock, the installed copy (Aug 18) does not, so any
# install of this script is what makes the third state real on the box.
# Tracked: docs/plans/gv-session-alarm.md Task 18; spec §8 and §11 decision 4.

# Serialize against the other launcher. The watchdog fires every 2 minutes and
# the nightly recycle kills-then-relaunches, so without this the recycle's
# `pkill -9` can land on a Chrome the watchdog started a moment earlier and
# leave a half-initialised profile behind. Both scripts take the same lock.
#
# A held lock means someone else is bringing the bridge up or recycling it, so WAIT
# for them (up to 60s) and then fall through to the normal liveness check below.
# Deliberately NOT `flock -n || exit 0`: Radio Console's KIOSK-2 launcher reads this
# script's exit code, and "someone else holds the lock" would be a THIRD outcome
# arriving as 0 (session-alarm spec §8, decision 4, owner ruling 2026-10-04). Waiting
# keeps exactly today's two: already up (0) and launched (0). On a 60s timeout the
# script carries on unlocked, the same as the no-flock path below.
LOCK="${GV_BRIDGE_LOCK:-${PROFILE}.lock}"

# Only lock if the lock is actually obtainable. If flock is missing or the file
# cannot be opened, carry on WITHOUT it: an unserialized launch risks a rare
# double-start, but treating an unopenable lock as "someone else has it" would
# mean never launching the bridge at all, which is far worse.
if command -v flock >/dev/null 2>&1 && : >>"${LOCK}" 2>/dev/null; then
  exec 9>>"${LOCK}"
  flock -w 60 9 || echo "$(ts) ensure: lock still held after 60s -> continuing unlocked" >> "${LOG}"
fi

# Checked under the lock, so the answer cannot go stale between here and launch.
if pgrep -f "${MARKER}" >/dev/null 2>&1; then
  exit 0
fi

mkdir -p "$(dirname "${LOG}")" 2>/dev/null || true

# Chrome refuses to open a profile whose Singleton* lock files survive a crash
# or a kill -9, so clear them before every launch attempt.
rm -f "${PROFILE}"/Singleton* 2>/dev/null || true

# Auto-login never types a password, so the login keyring (which holds Chrome's cookie key) stays
# locked and Chrome blocks on an unlock prompt. Unlock it from the TPM-bound credential
# /etc/credstore.encrypted/radio-keyring.cred (systemd-creds, root-only to decrypt). Best effort:
# any failure leaves the keyring as it was and Chrome prompts exactly as before.
KEYRING_CRED="/etc/credstore.encrypted/radio-keyring.cred"
KEYRING_UNLOCK="${KEYRING_UNLOCK:-${HOME}/bin/gv-keyring-unlock.py}"
if [ -r "${KEYRING_UNLOCK}" ] && sudo -n test -f "${KEYRING_CRED}" 2>/dev/null; then
  sudo -n systemd-creds decrypt --name=radio-keyring "${KEYRING_CRED}" - 2>>"${LOG}" \
    | timeout 20 python3 "${KEYRING_UNLOCK}" >> "${LOG}" 2>&1 \
    || echo "$(ts) ensure: keyring unlock failed (Chrome may prompt)" >> "${LOG}"
fi

# --collect reaps the transient unit once Chrome reparents itself away from it,
# so repeated launches do not accumulate failed scopes.
systemd-run --user --collect google-chrome "${CHROME_ARGS[@]}" >> "${LOG}" 2>&1
echo "$(ts) ensure: bridge was down -> launched" >> "${LOG}"
