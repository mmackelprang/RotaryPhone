# GV Bridge — Setup Guide

**Last updated:** March 23, 2026

This guide covers setting up the GV Bridge system on a fresh Ubuntu box. The GV Bridge enables incoming Google Voice calls to ring a physical rotary phone connected via a Grandstream HT801 ATA.

## Architecture

```
Google Voice call
  → Chromium browser (voice.google.com + extension)
  → Content script detects incoming call via button polling
  → Service worker relays event via HTTP POST
  → .NET RotaryPhoneController server receives event
  → CallManager sends SIP INVITE to HT801
  → HT801 rings the rotary phone
  → User picks up → 200 OK → call connected
```

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Ubuntu | 24.04+ | Tested on Ubuntu 24.04 (x64) |
| .NET SDK | 10.0 | For building from source |
| Chromium | 146+ | Installed via snap (`sudo snap install chromium`) |
| Grandstream HT801 | Firmware 1.0.5+ | Factory reset recommended before setup |
| Google Voice account | — | With a phone number |

## Hardware Setup

### HT801 ATA Configuration

1. Connect HT801 to your LAN and note its IP address
2. Log into the web interface at `http://<HT801-IP>`
3. Go to **Port Settings** and configure:
   - **SIP Server**: `<radio-box-IP>` (e.g., `192.168.86.50`)
   - **SIP User ID**: `1000`
   - **SIP Transport**: `UDP`
   - **SIP Registration**: Checked
   - **Enable SIP NOTIFY Authentication**: Unchecked
4. Verify **Port Status** shows "Registered"

**Important:** If the HT801 was previously configured, do a **factory reset** first. Hidden state can cause incoming SIP to be silently dropped even when settings appear correct.

### Rotary Phone

Connect the rotary phone to the HT801's FXS (Phone) port using a standard RJ11 cable.

## Deployment

### Quick Deploy (from Windows build machine)

```powershell
# Build and deploy to the radio box
.\deploy\Deploy-ToLinux.ps1 -TargetHost radio -Runtime linux-x64

# Run the GV Bridge setup on the radio box
ssh mmack@radio "bash /opt/rotary-phone/deploy/setup-gvbridge.sh"
```

### What the setup script does

1. Installs Chromium snap if not present
2. Copies the Chrome extension to a snap-accessible path
3. Creates a systemd user service (`gv-bridge-chrome.service`)
4. Installs Chromium notification/autoplay policies
5. Creates a desktop shortcut for manually starting the browser
6. Creates an autostart entry as backup

### What starts automatically on boot

| Service | Type | Starts |
|---------|------|--------|
| `rotary-phone.service` | System (systemd) | On boot |
| `gv-bridge-chrome.service` | User (systemd --user) | On user login |

## First-Time Setup (one-time steps)

After deploying, these steps need to be done once on the radio box's display:

### 1. Start the GV Bridge browser

```bash
systemctl --user start gv-bridge-chrome
```

Or click the **"GV Bridge"** desktop shortcut.

### 2. Make the window visible

The browser starts off-screen by default. Temporarily make it visible:

```bash
# Edit the service file
nano ~/.config/systemd/user/gv-bridge-chrome.service
# Change: --window-position=10000,10000  →  --window-position=50,50
systemctl --user daemon-reload
systemctl --user restart gv-bridge-chrome
```

### 3. Log into Google Voice

Navigate to `voice.google.com` in the Chromium window and sign in with your Google account.

### 4. Grant notification permission

Click the **lock icon** in the Chromium address bar → **Site settings** → **Notifications** → **Allow**

This is required for Google Voice to show incoming call UI in the browser.

### 5. Verify GV settings

In Google Voice settings (gear icon) → **Calls** → **Incoming calls** → **My devices**:
- Ensure **"Web"** toggle is **ON**

### 6. Move window back off-screen

```bash
# Edit the service file
nano ~/.config/systemd/user/gv-bridge-chrome.service
# Change: --window-position=50,50  →  --window-position=10000,10000
systemctl --user daemon-reload
systemctl --user restart gv-bridge-chrome
```

### 7. Switch to GVBrowser mode

```bash
curl -X PUT http://localhost:5004/api/gvbridge/adapter/mode \
  -H 'Content-Type: application/json' -d '{"mode":"GVBrowser"}'
```

### 8. Verify everything

```bash
# Check extension connected
curl -s http://localhost:5004/api/gvbridge/status
# Expected: {"extensionConnected":true,"extensionVersion":"1.0.0","activeMode":"GVBrowser"}

# Check HT801 registered
curl -s http://localhost:5004/api/diagnostics/status | python3 -m json.tool
```

## Configuration

### appsettings.Production.json

The GVBridge section (update IPs for your network):

```json
{
  "GVBridge": {
    "WebSocketPort": 8765,
    "WebSocketHost": "127.0.0.1",
    "LocalRtpPort": 5070,
    "LocalIp": "0.0.0.0",
    "HT801Ip": "192.168.86.22",
    "HT801RtpPort": 5004,
    "AudioSampleRateHz": 16000,
    "AudioChannels": 1,
    "PcmFrameMs": 20,
    "ExtensionConnectTimeoutSeconds": 30,
    "CallLogDbPath": "/opt/rotary-phone/data/gvbridge-calllog.db",
    "DefaultMode": "GVBrowser"
  }
}
```

### Key files on the radio box

| Path | Purpose |
|------|---------|
| `/opt/rotary-phone/` | Main application |
| `/opt/rotary-phone/ChromeExtension/` | Extension source (deployed copy) |
| `~/snap/chromium/common/gv-bridge-profile/Extension/` | Extension (snap-accessible copy) |
| `~/.config/systemd/user/gv-bridge-chrome.service` | Chromium systemd service |
| `/etc/chromium/policies/managed/gv-bridge.json` | Chromium notification policies |
| `~/Desktop/GV-Bridge.desktop` | Desktop shortcut |

## Diagnostics

### Web UI

Open `http://<radio-box>:5004/diagnostics` for real-time:
- SIP message log (REGISTER, INVITE, BYE with timestamps)
- HT801 health (registration status, ping, config validation)
- Call state timeline
- GV Bridge extension status

### API Endpoints

```bash
# Full status snapshot
curl http://localhost:5004/api/diagnostics/status

# SIP message log (filterable)
curl "http://localhost:5004/api/diagnostics/sip-log?count=20&method=INVITE"

# Send test INVITE to ring the phone
curl -X POST http://localhost:5004/api/diagnostics/test-ring

# HT801 config comparison (expected vs actual)
curl http://localhost:5004/api/diagnostics/ht801/config

# Call timeline
curl http://localhost:5004/api/diagnostics/timeline
```

### Service logs

```bash
# RotaryPhone server
journalctl -u rotary-phone -f

# GV Bridge Chromium
journalctl --user -u gv-bridge-chrome -f
```

## Troubleshooting

### Phone doesn't ring

1. **Check HT801 registration**: `curl http://localhost:5004/api/diagnostics/status` → `ht801.isRegistered` should be `true`
2. **Send test INVITE**: `curl -X POST http://localhost:5004/api/diagnostics/test-ring` and check the SIP log for 100/180/200 responses
3. **If INVITE times out**: The HT801 may need a factory reset (hidden state blocks incoming SIP). After reset, reconfigure SIP Server, User ID, and registration.

### Extension not connected

1. Check Chromium is running: `systemctl --user status gv-bridge-chrome`
2. Check the page loaded: `curl -s http://localhost:9224/json` (CDP port)
3. Restart: `systemctl --user restart gv-bridge-chrome`

### GV doesn't ring in the browser

1. Verify notification permission is "Allow" (lock icon → Site settings)
2. In GV Settings → Calls → Incoming calls → "Web" must be ON
3. The Chromium window does NOT need to be visible — it works off-screen

### After reboot

Both services auto-start, but you may need to:
1. Wait 1-2 minutes for HT801 to re-register
2. Switch adapter mode: `curl -X PUT http://localhost:5004/api/gvbridge/adapter/mode -H 'Content-Type: application/json' -d '{"mode":"GVBrowser"}'`

## Current Status & Known Limitations

### Working (verified 2026-03-24)
- **Full incoming call flow**: GV call → extension detects → SIP INVITE → HT801 → phone rings → user answers (200 OK) → InCall → user hangs up (BYE) → Idle
- **Call state machine**: SIP events are authoritative (not browser extension events). 60-second ringing timeout prevents stuck state.
- **Incoming call detection**: Content script polls for Answer/Decline/Mute/EndCall buttons every 500ms
- **SIP diagnostics**: Real-time message log, INVITE timeout detection, HT801 health, call timeline
- **Diagnostics web UI** at `/diagnostics` and REST API

### Not yet working
- **Audio bridge**: GVAudioBridgeService is built but not yet tested with live calls (WebSocket PCM ↔ RTP G.711)
- **BYE handling**: HT801's BYE after a test-ring gets 481 response, leaving the device stuck. Workaround: reboot HT801 after using test-ring.
- **Auto mode on boot**: Adapter defaults to BluetoothHfp; needs manual switch to GVBrowser after each service restart
- **Outgoing calls**: Rotary dial → GV not yet implemented
- **Audio playback to GV caller**: tabCapture capture works, but playback direction (phone mic → GV) is Phase 2

### HT801 Quirks
- **Factory reset required** if incoming SIP stops working. Only configure 3 settings: SIP Server, SIP User ID, SIP Registration. Changing other settings (Register Expiration, NOTIFY Auth, etc.) can silently break incoming SIP.
- **Re-registration delay**: After service restart, HT801 won't re-register until its timer fires (up to 60 min). Reboot the HT801 to force immediate re-registration.
- **Test-ring caution**: The test-ring endpoint auto-answers after 4s and the BYE response (481) leaves the HT801 stuck. Always reboot HT801 after using test-ring.
