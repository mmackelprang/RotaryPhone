#!/usr/bin/env bash
# =============================================================================
# GV Bridge Setup Script
# Configures the Ubuntu radio box to run the GV Bridge Chrome extension
# alongside the kiosk display. Run once on the radio box after deployment.
#
# Usage: bash /opt/rotary-phone/deploy/setup-gvbridge.sh
#
# What this does:
#   1. Installs Chromium (if needed) — Google Chrome blocks --load-extension
#   2. Copies the extension to a snap-accessible path
#   3. Creates a systemd user service for the GV Bridge Chromium instance
#   4. Installs Chromium policies for notification/autoplay permissions
#   5. Creates a desktop shortcut to start the GV Bridge browser
#   6. Creates an autostart entry as backup
#
# After running:
#   - Start the GV Chrome: systemctl --user start gv-bridge-chrome
#   - FIRST TIME: Log into Google Voice and grant notification permission
#   - The extension auto-connects to the RotaryPhone server
# =============================================================================

set -euo pipefail

INSTALL_DIR="/opt/rotary-phone"
EXTENSION_DIR="${INSTALL_DIR}/ChromeExtension"
CHROME_PROFILE_DIR="${HOME}/snap/chromium/common/gv-bridge-profile"
EXTENSION_DEPLOY_DIR="${CHROME_PROFILE_DIR}/Extension"
DATA_DIR="${INSTALL_DIR}/data"
SYSTEMD_USER_DIR="${HOME}/.config/systemd/user"
AUTOSTART_DIR="${HOME}/.config/autostart"
DESKTOP_DIR="${HOME}/Desktop"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[GVBridge]${NC} $1"; }
warn() { echo -e "${YELLOW}[GVBridge]${NC} $1"; }

# --- Step 1: Install Chromium if needed ---
CHROME_BIN=""
for candidate in chromium chromium-browser; do
    if command -v "$candidate" &>/dev/null; then
        CHROME_BIN="$candidate"
        break
    fi
done

if [ -z "$CHROME_BIN" ]; then
    log "Installing Chromium via snap..."
    sudo snap install chromium
    CHROME_BIN="chromium"
fi
log "Using browser: ${CHROME_BIN} ($(${CHROME_BIN} --version 2>/dev/null || echo 'version unknown'))"

# --- Step 2: Create directories ---
log "Creating directories..."
mkdir -p "${DATA_DIR}" "${CHROME_PROFILE_DIR}" "${SYSTEMD_USER_DIR}" "${AUTOSTART_DIR}" "${DESKTOP_DIR}" 2>/dev/null || true

# --- Step 3: Verify and copy extension ---
if [ ! -f "${EXTENSION_DIR}/manifest.json" ]; then
    warn "Chrome extension not found at ${EXTENSION_DIR}/manifest.json"
    warn "Deploy the RotaryPhone project first, then re-run this script."
    exit 1
fi
log "Copying extension to snap-accessible path..."
mkdir -p "${EXTENSION_DEPLOY_DIR}"
cp -r "${EXTENSION_DIR}/"* "${EXTENSION_DEPLOY_DIR}/"
log "Extension deployed to ${EXTENSION_DEPLOY_DIR}"

# --- Step 4: Create systemd user service ---
log "Creating systemd user service: gv-bridge-chrome.service"
cat > "${SYSTEMD_USER_DIR}/gv-bridge-chrome.service" << EOF
[Unit]
Description=GV Bridge Chromium (voice.google.com with extension)
After=graphical-session.target rotary-phone.service
Wants=rotary-phone.service

[Service]
Type=simple
ExecStartPre=/bin/sleep 5
ExecStart=${CHROME_BIN} \\
    --load-extension=${EXTENSION_DEPLOY_DIR} \\
    --user-data-dir=${CHROME_PROFILE_DIR} \\
    --no-first-run \\
    --disable-default-apps \\
    --disable-popup-blocking \\
    --disable-background-timer-throttling \\
    --disable-renderer-backgrounding \\
    --disable-backgrounding-occluded-windows \\
    --autoplay-policy=no-user-gesture-required \\
    --window-size=800,600 \\
    --window-position=10000,10000 \\
    --ozone-platform=wayland \\
    --remote-debugging-port=9224 \\
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

# --- Step 5: Install Chromium policies ---
log "Installing Chromium notification/autoplay policies..."
sudo mkdir -p /etc/chromium/policies/managed
sudo tee /etc/chromium/policies/managed/gv-bridge.json > /dev/null << 'POLICYEOF'
{
  "NotificationsAllowedForUrls": ["https://voice.google.com"],
  "AutoplayAllowlist": ["https://voice.google.com"]
}
POLICYEOF

# --- Step 6: Create desktop shortcut ---
log "Creating desktop shortcut..."
cat > "${DESKTOP_DIR}/GV-Bridge.desktop" << EOF
[Desktop Entry]
Name=GV Bridge
Comment=Start Google Voice Bridge Browser
Exec=systemctl --user start gv-bridge-chrome
Icon=phone
Terminal=false
Type=Application
Categories=Utility;
EOF
chmod +x "${DESKTOP_DIR}/GV-Bridge.desktop"

# --- Step 7: Create autostart entry (backup) ---
log "Creating autostart entry..."
cat > "${AUTOSTART_DIR}/gv-bridge-chrome.desktop" << EOF
[Desktop Entry]
Name=GV Bridge Chrome
Comment=Google Voice Bridge for RotaryPhone
Exec=${CHROME_BIN} --load-extension=${EXTENSION_DEPLOY_DIR} --user-data-dir=${CHROME_PROFILE_DIR} --no-first-run --disable-default-apps --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --autoplay-policy=no-user-gesture-required --window-size=800,600 --window-position=10000,10000 --ozone-platform=wayland --remote-debugging-port=9224 https://voice.google.com
Terminal=false
Type=Application
X-GNOME-Autostart-enabled=true
X-GNOME-Autostart-Delay=15
EOF

# --- Step 8: Enable and reload systemd ---
log "Enabling systemd user service..."
systemctl --user daemon-reload
systemctl --user enable gv-bridge-chrome.service

# --- Done ---
echo ""
log "=============================="
log "  GV Bridge setup complete!"
log "=============================="
echo ""
echo "What starts automatically on boot:"
echo "  - rotary-phone.service (system service — already enabled)"
echo "  - gv-bridge-chrome.service (user service — just enabled)"
echo ""
echo "FIRST TIME setup (one-time steps):"
echo ""
echo "  1. Start the GV Chromium instance:"
echo "     systemctl --user start gv-bridge-chrome"
echo ""
echo "  2. Make the window visible (temporarily):"
echo "     Edit the service and change --window-position=10000,10000 to 50,50"
echo "     systemctl --user daemon-reload && systemctl --user restart gv-bridge-chrome"
echo ""
echo "  3. Log into Google Voice at voice.google.com"
echo ""
echo "  4. Grant notification permission:"
echo "     Click the lock icon in the address bar → Site settings → Notifications → Allow"
echo ""
echo "  5. Move window back off-screen:"
echo "     Change --window-position back to 10000,10000 and restart"
echo ""
echo "  6. Switch to GVBrowser mode:"
echo "     curl -X PUT http://localhost:5004/api/gvbridge/adapter/mode \\"
echo "       -H 'Content-Type: application/json' -d '{\"mode\":\"GVBrowser\"}'"
echo ""
echo "  7. Verify:"
echo "     curl -s http://localhost:5004/api/gvbridge/status"
echo "     curl -s http://localhost:5004/api/diagnostics/status | python3 -m json.tool"
echo ""
echo "Diagnostics:"
echo "  Web UI:  http://$(hostname):5004/diagnostics"
echo "  SIP log: curl http://localhost:5004/api/diagnostics/sip-log"
echo "  Test ring: curl -X POST http://localhost:5004/api/diagnostics/test-ring"
echo ""
