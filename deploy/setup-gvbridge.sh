#!/usr/bin/env bash
# =============================================================================
# GV Bridge Setup Script
# Configures the Ubuntu radio box to run the GV Bridge Chrome extension
# alongside the kiosk display. Run once on the radio box after deployment.
#
# Usage: bash /opt/rotary-phone/deploy/setup-gvbridge.sh
#
# What this does:
#   1. Creates data directories
#   2. Creates a separate Chrome profile for GV Bridge
#   3. Installs a systemd user service to run Chrome with the extension
#   4. Creates an autostart entry for the GV Chrome instance
#   5. Configures appsettings.Production.json (if not already configured)
#
# After running:
#   - Start the GV Chrome: systemctl --user start gv-bridge-chrome
#   - Navigate to voice.google.com and log in (first time only)
#   - The extension auto-connects to ws://127.0.0.1:8765
# =============================================================================

set -euo pipefail

INSTALL_DIR="/opt/rotary-phone"
CHROME_PROFILE_DIR="${HOME}/.config/gv-bridge-chrome"
EXTENSION_DIR="${INSTALL_DIR}/ChromeExtension"
DATA_DIR="${INSTALL_DIR}/data"
SYSTEMD_USER_DIR="${HOME}/.config/systemd/user"
AUTOSTART_DIR="${HOME}/.config/autostart"

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[GVBridge]${NC} $1"; }
warn() { echo -e "${YELLOW}[GVBridge]${NC} $1"; }

# --- Step 1: Create directories ---
log "Creating data directories..."
mkdir -p "${DATA_DIR}"
mkdir -p "${CHROME_PROFILE_DIR}"
mkdir -p "${SYSTEMD_USER_DIR}"
mkdir -p "${AUTOSTART_DIR}"

# --- Step 2: Verify extension exists ---
if [ ! -f "${EXTENSION_DIR}/manifest.json" ]; then
    warn "Chrome extension not found at ${EXTENSION_DIR}/manifest.json"
    warn "Deploy the RotaryPhone project first, then re-run this script."
    exit 1
fi
log "Chrome extension found at ${EXTENSION_DIR}"

# --- Step 3: Verify Chrome is available ---
CHROME_BIN=""
for candidate in google-chrome google-chrome-stable chromium-browser chromium; do
    if command -v "$candidate" &>/dev/null; then
        CHROME_BIN="$candidate"
        break
    fi
done

if [ -z "$CHROME_BIN" ]; then
    warn "No Chrome/Chromium found. Install with: sudo apt install google-chrome-stable"
    exit 1
fi
log "Using browser: ${CHROME_BIN}"

# --- Step 4: Create systemd user service for GV Chrome ---
log "Creating systemd user service: gv-bridge-chrome.service"
cat > "${SYSTEMD_USER_DIR}/gv-bridge-chrome.service" << EOF
[Unit]
Description=GV Bridge Chrome (voice.google.com with extension)
After=graphical-session.target rotary-phone.service
Wants=rotary-phone.service

[Service]
Type=simple
# Wait for rotary-phone WebSocket server to start
ExecStartPre=/bin/sleep 5
ExecStart=${CHROME_BIN} \\
    --load-extension=${EXTENSION_DIR} \\
    --user-data-dir=${CHROME_PROFILE_DIR} \\
    --no-first-run \\
    --disable-default-apps \\
    --disable-popup-blocking \\
    --disable-background-timer-throttling \\
    --disable-renderer-backgrounding \\
    --disable-backgrounding-occluded-windows \\
    --window-size=800,600 \\
    --window-position=10000,10000 \\
    --ozone-platform=wayland \\
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

# --- Step 5: Create autostart entry (backup for systemd) ---
log "Creating autostart entry..."
cat > "${AUTOSTART_DIR}/gv-bridge-chrome.desktop" << EOF
[Desktop Entry]
Name=GV Bridge Chrome
Comment=Google Voice Bridge for RotaryPhone
Exec=${CHROME_BIN} --load-extension=${EXTENSION_DIR} --user-data-dir=${CHROME_PROFILE_DIR} --no-first-run --disable-default-apps --disable-background-timer-throttling --disable-renderer-backgrounding --window-size=800,600 --window-position=10000,10000 --ozone-platform=wayland https://voice.google.com
Terminal=false
Type=Application
X-GNOME-Autostart-enabled=true
X-GNOME-Autostart-Delay=15
EOF

# --- Step 6: Update appsettings.Production.json if GVBridge section missing ---
PROD_CONFIG="${INSTALL_DIR}/appsettings.Production.json"
if [ -f "$PROD_CONFIG" ] && ! grep -q '"GVBridge"' "$PROD_CONFIG"; then
    log "GVBridge config section not found in appsettings.Production.json"
    warn "Add this section manually to ${PROD_CONFIG}:"
    cat << 'CONFIGEOF'

  "GVBridge": {
    "WebSocketPort": 8765,
    "WebSocketHost": "127.0.0.1",
    "LocalRtpPort": 5070,
    "LocalIp": "REPLACE_WITH_BOX_IP",
    "HT801Ip": "192.168.86.250",
    "HT801RtpPort": 5004,
    "CallLogDbPath": "/opt/rotary-phone/data/gvbridge-calllog.db",
    "DefaultMode": "GVBrowser"
  }

CONFIGEOF
else
    log "appsettings.Production.json already has GVBridge config (or file not found)"
fi

# --- Step 7: Enable and reload systemd ---
log "Enabling systemd user service..."
systemctl --user daemon-reload
systemctl --user enable gv-bridge-chrome.service

# --- Done ---
echo ""
log "=============================="
log "  GV Bridge setup complete!"
log "=============================="
echo ""
echo "Next steps:"
echo ""
echo "  1. Start the GV Chrome instance:"
echo "     systemctl --user start gv-bridge-chrome"
echo ""
echo "  2. FIRST TIME ONLY: Log into Google Voice"
echo "     - Alt+Tab or use 'wmctrl -a Chrome' to find the GV window"
echo "     - Log in to your Google account at voice.google.com"
echo "     - The extension auto-connects (check: curl http://localhost:5004/api/gvbridge/status)"
echo "     - After login, the session persists across restarts"
echo ""
echo "  3. Verify the bridge is connected:"
echo "     curl -s http://localhost:5004/api/gvbridge/status | python3 -m json.tool"
echo ""
echo "  4. The GV Chrome starts automatically on boot via:"
echo "     - systemd user service: gv-bridge-chrome.service"
echo "     - Backup: ~/.config/autostart/gv-bridge-chrome.desktop"
echo ""
echo "  5. To stop/restart GV Chrome:"
echo "     systemctl --user stop gv-bridge-chrome"
echo "     systemctl --user start gv-bridge-chrome"
echo ""
echo "  6. To view GV Chrome logs:"
echo "     journalctl --user -u gv-bridge-chrome -f"
echo ""
