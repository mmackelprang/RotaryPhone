#!/usr/bin/env bash
# =============================================================================
# GV Bridge Setup Script
#
# Provisions the launch + liveness tooling that keeps an authenticated Google
# Voice session alive on the Ubuntu radio box. Run once after deployment:
#
#   bash /opt/rotary-phone/deploy/setup-gvbridge.sh
#
# What the bridge browser is for
# ------------------------------
# It holds ONE logged-in Google Voice session. RotaryPhone's API scrapes that
# session's cookies over CDP and calls Google's HTTP API for SMS and voicemail.
# It does NOT carry call audio and does NOT drive answer/hangup — those run over
# SIP + DTLS-SRTP inside the .NET service (see the "superseded" note in step 7).
#
# What this does (default path — no sudo required):
#   1. Verifies Google Chrome is installed
#   2. Installs gv-bridge-ensure.sh and gv-bridge-restart.sh into ~/bin
#   3. Installs the watchdog + nightly-restart systemd user units
#   4. Enables the 2-minute watchdog timer (starts the bridge if it is down)
#   5. Installs the login autostart entry
#   6. Creates a desktop shortcut that runs the ensure script
#
# Optional legacy path (--with-legacy-extension-service): additionally
# provisions the superseded snap-Chromium + --load-extension configuration.
# Off by default; see step 7.
#
# After running:
#   - The watchdog brings the bridge up within 2 minutes, and keeps it up.
#   - FIRST TIME ONLY: log into Google Voice in that browser window.
# =============================================================================

set -euo pipefail

WITH_LEGACY_EXTENSION_SERVICE=0
for arg in "$@"; do
    case "$arg" in
        --with-legacy-extension-service) WITH_LEGACY_EXTENSION_SERVICE=1 ;;
        -h|--help)
            sed -n '2,31p' "$0"
            exit 0
            ;;
        *)
            echo "Unknown option: $arg" >&2
            exit 2
            ;;
    esac
done

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/opt/rotary-phone"
EXTENSION_DIR="${INSTALL_DIR}/ChromeExtension"
DATA_DIR="${INSTALL_DIR}/data"
BIN_DIR="${HOME}/bin"
PROFILE_DIR="${HOME}/.config/gv-bridge-chrome"
STATE_DIR="${HOME}/.local/state"
SYSTEMD_USER_DIR="${HOME}/.config/systemd/user"
AUTOSTART_DIR="${HOME}/.config/autostart"
DESKTOP_DIR="${HOME}/Desktop"
STAMP="$(date '+%Y%m%d-%H%M%S')"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[GVBridge]${NC} $1"; }
warn() { echo -e "${YELLOW}[GVBridge]${NC} $1"; }

# Never clobber a working hand-edited copy without leaving a way back. This
# applies to every file the script installs — scripts, units and .desktop entries
# alike — because an operator who hand-tunes, say, the watchdog interval should
# not lose it silently to the next provision.
backup_if_changed() {
    local src="$1" dest="$2"
    if [ -f "$dest" ] && ! cmp -s "$src" "$dest"; then
        cp -p "$dest" "${dest}.bak-${STAMP}"
        log "Backed up existing $(basename "$dest") -> $(basename "$dest").bak-${STAMP}"
    fi
}

install_file() {
    local src="$1" dest="$2" mode="$3"
    if [ ! -f "$src" ]; then
        warn "Missing ${src} — deploy the RotaryPhone project first, then re-run."
        exit 1
    fi
    backup_if_changed "$src" "$dest"
    install -m "$mode" "$src" "$dest"
}

# Write a file and give it an exact mode in one step.
#
# Why not `cat > f` followed by `chmod`: this box runs umask 0002, so `cat >`
# creates the file group-writable (664), and only the chmod that follows takes
# that away. GNOME silently refuses to launch a group-writable .desktop file —
# that is precisely why the shortcut this script used to write did nothing when
# clicked. Writing through a temp file and `install -m` never lets the
# group-writable version exist at the destination path at all.
write_mode() {
    local dest="$1" mode="$2" tmp
    tmp="$(mktemp)"
    cat > "$tmp"
    backup_if_changed "$tmp" "$dest"
    install -m "$mode" "$tmp" "$dest"
    rm -f "$tmp"
}

# --- Step 1: Verify Google Chrome ---
# Chrome, not Chromium: the profile at ${PROFILE_DIR} was created by Chrome and
# holds the live Google session. This script deliberately does NOT install a
# browser — that would mean adding Google's apt repo under sudo on a box that is
# serving a working kiosk. Fail loudly instead.
if ! command -v google-chrome &>/dev/null; then
    warn "google-chrome not found on PATH."
    warn "Install it, then re-run this script:"
    warn "  wget -qO /tmp/chrome.deb https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb"
    warn "  sudo apt install -y /tmp/chrome.deb"
    exit 1
fi
log "Using browser: google-chrome ($(google-chrome --version 2>/dev/null || echo 'version unknown'))"

# --- Step 2: Create directories ---
log "Creating directories..."
mkdir -p "${BIN_DIR}" "${PROFILE_DIR}" "${STATE_DIR}" "${SYSTEMD_USER_DIR}" \
         "${AUTOSTART_DIR}" "${DESKTOP_DIR}" 2>/dev/null || true
mkdir -p "${DATA_DIR}" 2>/dev/null || true

# --- Step 3: Install the launch scripts ---
log "Installing launch scripts into ${BIN_DIR}..."
install_file "${DEPLOY_DIR}/gv-bridge-ensure.sh"  "${BIN_DIR}/gv-bridge-ensure.sh"  755
install_file "${DEPLOY_DIR}/gv-bridge-restart.sh" "${BIN_DIR}/gv-bridge-restart.sh" 755

# --- Step 4: Install systemd user units ---
log "Installing systemd user units..."
for unit in gv-bridge-watchdog.service gv-bridge-watchdog.timer \
            gv-bridge-restart.service gv-bridge-restart.timer; do
    install_file "${DEPLOY_DIR}/systemd/${unit}" "${SYSTEMD_USER_DIR}/${unit}" 644
done
systemctl --user daemon-reload

# The watchdog is what actually keeps the bridge alive; enabling it is the point
# of this script. Its ExecStart is idempotent, so --now cannot disturb a bridge
# that is already running.
log "Enabling the 2-minute liveness watchdog..."
systemctl --user enable --now gv-bridge-watchdog.timer

# The nightly recycle is installed but left disabled, matching the box's current
# state. Enable it if renderer heap growth becomes a problem.
log "Installed the nightly restart timer (left DISABLED — enable if needed)."

# --- Step 5: Autostart entry ---
log "Creating autostart entry..."
write_mode "${AUTOSTART_DIR}/gv-bridge-chrome.desktop" 644 << EOF
[Desktop Entry]
Name=GV Bridge Chrome
Comment=Runs gv-bridge-ensure.sh at login to bring up the Google Voice bridge browser
Exec=${BIN_DIR}/gv-bridge-ensure.sh
Terminal=false
Type=Application
X-GNOME-Autostart-enabled=true
X-GNOME-Autostart-Delay=15
EOF

# --- Step 6: Desktop shortcut ---
# Mode 755, NOT 775 — see write_mode above for why the distinction matters.
log "Creating desktop shortcut..."
write_mode "${DESKTOP_DIR}/GV-Bridge.desktop" 755 << EOF
[Desktop Entry]
Name=GV Bridge
Comment=Start the Google Voice bridge browser if it is not already running
Exec=${BIN_DIR}/gv-bridge-ensure.sh
Icon=phone
Terminal=false
Type=Application
Categories=Utility;
EOF

if [ -d "${EXTENSION_DIR}" ]; then
    log "Extension source present at ${EXTENSION_DIR} (passed to Chrome, but inert — see below)."
else
    warn "No extension at ${EXTENSION_DIR}. Not fatal: the current path does not use it."
fi

# --- Step 7: Legacy snap-Chromium + extension service (superseded, opt-in) ---
# Why this is no longer the default, on evidence rather than preference:
#
#   * Chrome has ignored --load-extension since v137. This box runs Chrome 151,
#     and the live bridge profile's Preferences file lists only Chrome's five
#     built-in extensions — the GV Bridge extension is not loaded (2026-08-18).
#   * A live call still completed with audio in BOTH directions under exactly
#     that configuration: /api/diagnostics/audio-bridge reported
#     inboundFramesSent 345, outboundFramesReceived 341, bidirectionalAudio true,
#     zero errors. Audio therefore runs on the SIPSorcery DTLS-SRTP path from
#     docs/superpowers/specs/2026-03-27-gv-api-migration-design.md, not on the
#     extension's tabCapture relay, and answer/hangup go over SIP rather than DOM
#     clicking.
#   * The browser's only remaining job is holding a session CDP can read cookies
#     from — which needs no extension.
#
# The provisioning is kept, not deleted, so the older configuration can still be
# stood up deliberately. It is NOT enabled even when requested: the unit below
# binds the same CDP port (9224) as the live bridge and would race it.
if [ "${WITH_LEGACY_EXTENSION_SERVICE}" -eq 1 ]; then
    warn "Provisioning the SUPERSEDED snap-Chromium + extension configuration."
    warn "It will be installed but NOT enabled — it binds CDP port 9224, which the"
    warn "current bridge already owns. Stop the current bridge before starting it."

    LEGACY_PROFILE_DIR="${HOME}/snap/chromium/common/gv-bridge-profile"
    LEGACY_EXTENSION_DIR="${LEGACY_PROFILE_DIR}/Extension"

    CHROME_BIN=""
    for candidate in chromium chromium-browser; do
        if command -v "$candidate" &>/dev/null; then CHROME_BIN="$candidate"; break; fi
    done
    if [ -z "$CHROME_BIN" ]; then
        log "Installing Chromium via snap..."
        sudo snap install chromium
        CHROME_BIN="chromium"
    fi

    if [ ! -f "${EXTENSION_DIR}/manifest.json" ]; then
        warn "Chrome extension not found at ${EXTENSION_DIR}/manifest.json — skipping legacy setup."
    else
        log "Copying extension to the snap-accessible path..."
        mkdir -p "${LEGACY_EXTENSION_DIR}"
        cp -r "${EXTENSION_DIR}/"* "${LEGACY_EXTENSION_DIR}/"

        log "Creating systemd user service: gv-bridge-chrome.service (not enabled)"
        cat > "${SYSTEMD_USER_DIR}/gv-bridge-chrome.service" << EOF
[Unit]
Description=GV Bridge Chromium (voice.google.com with extension) — SUPERSEDED
After=graphical-session.target rotary-phone.service
Wants=rotary-phone.service

[Service]
Type=simple
ExecStartPre=/bin/sleep 5
ExecStart=${CHROME_BIN} \\
    --load-extension=${LEGACY_EXTENSION_DIR} \\
    --user-data-dir=${LEGACY_PROFILE_DIR} \\
    --no-first-run \\
    --disable-default-apps \\
    --disable-popup-blocking \\
    --disable-notifications \\
    --disable-background-timer-throttling \\
    --disable-renderer-backgrounding \\
    --disable-backgrounding-occluded-windows \\
    --autoplay-policy=no-user-gesture-required \\
    --mute-audio \\
    --window-size=800,600 \\
    --window-position=10000,10000 \\
    --ozone-platform=wayland \\
    --remote-debugging-port=9224 \\
    --remote-allow-origins=* \\
    https://voice.google.com
Restart=on-failure
RestartSec=10
Environment=DISPLAY=:0
Environment=XDG_RUNTIME_DIR=/run/user/$(id -u)
Environment=WAYLAND_DISPLAY=wayland-0
Environment=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus

[Install]
WantedBy=default.target
EOF

        # These policies apply to Chromium only; /etc/chromium/policies is not
        # read by Google Chrome, so they belong to the legacy path and nothing
        # else. The current path suppresses audio with --mute-audio instead.
        log "Installing Chromium notification/autoplay policies..."
        sudo mkdir -p /etc/chromium/policies/managed
        sudo tee /etc/chromium/policies/managed/gv-bridge.json > /dev/null << 'POLICYEOF'
{
  "DefaultNotificationsSetting": 2,
  "NotificationsBlockedForUrls": ["https://voice.google.com"],
  "AutoplayAllowlist": ["https://voice.google.com"]
}
POLICYEOF
        systemctl --user daemon-reload
    fi
fi

# --- Done ---
echo ""
log "=============================="
log "  GV Bridge setup complete!"
log "=============================="
echo ""
echo "Installed:"
echo "  ${BIN_DIR}/gv-bridge-ensure.sh    launch-if-down (idempotent)"
echo "  ${BIN_DIR}/gv-bridge-restart.sh   nightly recycle -> delegates to ensure"
echo "  gv-bridge-watchdog.timer          ENABLED - liveness check every 2 min"
echo "  gv-bridge-restart.timer           installed, DISABLED (nightly 04:00)"
echo "  ${AUTOSTART_DIR}/gv-bridge-chrome.desktop"
echo "  ${DESKTOP_DIR}/GV-Bridge.desktop"
echo ""
echo "FIRST TIME setup (one-time):"
echo ""
echo "  1. Bring the bridge up now (or wait up to 2 min for the watchdog):"
echo "     ${BIN_DIR}/gv-bridge-ensure.sh"
echo ""
echo "  2. Log into Google Voice in that window. The window is placed by the"
echo "     compositor - raise it from the GNOME overview if you cannot see it."
echo ""
echo "  3. Confirm CDP is answering (this is what cookie refresh needs):"
echo "     curl -s http://localhost:9224/json/version"
echo ""
echo "  4. Confirm cookie extraction works end to end:"
echo "     curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser \\"
echo "       -H 'Content-Type: application/json' -d '{}'"
echo "     Expected: {\"refreshed\":true,\"cookieCount\":<n>}"
echo ""
echo "  5. Verify:"
echo "     curl -s http://localhost:5004/api/gvbridge/status"
echo "     curl -s http://localhost:5004/api/diagnostics/status | python3 -m json.tool"
echo ""
echo "Optional - enable the nightly 04:00 recycle:"
echo "     systemctl --user enable --now gv-bridge-restart.timer"
echo ""
echo "Diagnostics:"
echo "  Watchdog log: ~/.local/state/gv-bridge-restart.log"
echo "  Timer state:  systemctl --user list-timers 'gv-bridge-*'"
echo "  Web UI:       http://$(hostname):5004/diagnostics"
echo "  SIP log:      curl http://localhost:5004/api/diagnostics/sip-log"
echo "  Audio bridge: curl http://localhost:5004/api/diagnostics/audio-bridge"
echo ""
