# RotaryPhone GV Bridge — Setup & User Testing Guide

## What Was Built

The GV Bridge feature adds a third call path to the RotaryPhone system. The rotary phone can now make and receive calls through Google Voice's web interface — no SIP subscription or forwarding number needed.

### Architecture

```
Path A (Bluetooth):  Rotary Phone → HT801 → Pi → Bluetooth HFP → Mobile Phone
Path B (SIP Trunk):  Rotary Phone → HT801 → Pi → VoIP.ms → Google Voice
Path C (GV Bridge):  Rotary Phone → HT801 → Pi → WebSocket ↔ Chrome Extension ↔ voice.google.com
```

### Components Delivered (4 PRs)

| PR | Phase | What |
|----|-------|------|
| #12 | A — Core Interfaces | `ICallAdapter`, `ICallAdapterRegistry`, `CallAdapterRegistry`, adapter wrappers, `CallManager` refactor |
| #13 | B — GVBridge Backend | WebSocket server (`GVBridgeService`), `GVBrowserAdapter`, `GVSmsService`, `ExtensionMessage` protocol |
| #14 | C — Chrome Extension | Manifest V3 extension with service worker, content script (DOM observer + call control), offscreen audio scaffold |
| #15 | D — UI | React + Blazor dashboards, `ConnectionModeSelector`, REST API (`/api/gvbridge/*`), SignalR hub (`/hubs/gvbridge`) |

### Test Results

- **122 automated tests pass** (57 Core + 16 GVTrunk + 5 GVBridge + 44 Core-Windows)
- **0 build errors** across all projects
- React Vite build succeeds

---

## Prerequisites

### On the Ubuntu radio box (production host)

| Requirement | Version | Check |
|---|---|---|
| .NET 10 Runtime | 10.0+ | `dotnet --version` |
| Chrome / Chromium | 116+ | Required for `tabCapture` + `offscreen` APIs |
| SQLite | system | `sudo apt install sqlite3` |

### On the development machine (Windows)

| Requirement | Version |
|---|---|
| .NET 10 SDK | 10.0+ |
| Node.js | 18+ |
| Chrome | 116+ |

---

## Step 1: Configuration

### `appsettings.Production.json` on the radio box

The GVBridge config section should already be in `appsettings.json` with defaults. For production, add to `appsettings.Production.json`:

```json
"GVBridge": {
  "WebSocketPort": 8765,
  "WebSocketHost": "127.0.0.1",
  "LocalRtpPort": 5070,
  "LocalIp": "192.168.86.50",
  "HT801Ip": "192.168.86.250",
  "HT801RtpPort": 5004,
  "AudioSampleRateHz": 16000,
  "AudioChannels": 1,
  "PcmFrameMs": 20,
  "ExtensionConnectTimeoutSeconds": 30,
  "CallLogDbPath": "/opt/rotary-phone/data/gvbridge-calllog.db",
  "DefaultMode": "GVBrowser"
}
```

**Values to update for your environment:**

| Key | Where to find it |
|---|---|
| `LocalIp` | Radio box LAN IP — `hostname -I` |
| `HT801Ip` | HT801 ATA IP — check router DHCP or HT801 web UI |

---

## Step 2: Deploy

From the Windows dev machine:

```bash
cd D:\prj\RotaryPhone
powershell -File deploy/Deploy-ToLinux.ps1
```

This automatically deploys the .NET app, scripts, Chrome extension, and setup script.

Verify the service started:

```bash
ssh radio "journalctl -u rotary-phone --since '30 seconds ago' --no-pager | grep -E 'listening|GVBridge|WebSocket'"
```

Expected output should include:
```
GVBridgeService: WebSocket server listening on ws://127.0.0.1:8765
```

---

## Step 3: Run the Automated Setup (first time only)

SSH into the radio box and run the setup script:

```bash
ssh radio "bash /opt/rotary-phone/deploy/setup-gvbridge.sh"
```

This automatically:
- Creates data directories
- Creates a separate Chrome profile for the GV Bridge
- Installs a systemd user service (`gv-bridge-chrome.service`) that runs Chrome with `--load-extension` (no manual extension loading needed)
- Sets up autostart so Chrome launches GV on boot
- The GV Chrome runs with `--window-position=10000,10000` so it's off-screen behind the kiosk

### First-time Google Voice login (only manual step)

```bash
# Start the GV Chrome instance
ssh radio "systemctl --user start gv-bridge-chrome"

# Find and bring the Chrome window to front to log in
ssh radio "wmctrl -a Chrome"  # or Alt+Tab on the radio box
```

1. Log into your Google account at `voice.google.com`
2. The extension auto-connects (check: `curl http://localhost:5004/api/gvbridge/status`)
3. After login, the session persists — no need to log in again

### Dev machine (for testing without the radio box)

If testing locally, load the extension manually:

1. Open `chrome://extensions` → Enable Developer mode
2. Click "Load unpacked" → Select `ChromeExtension/` from the repo
3. Navigate to `https://voice.google.com`

---

## Step 4: Verify Connection

### Via the Web UI

1. Open `http://radio:5004/gvbridge` (or `http://localhost:5004/gvbridge` on dev)
2. The **Bridge Status** panel should show:
   - Extension: **Connected** (green) — if Chrome is running with the extension loaded on `voice.google.com`
   - Active Mode: **BluetoothHfp** (default) or whatever was last selected

### Via REST API

```bash
curl http://radio:5004/api/gvbridge/status
# Expected: {"extensionConnected":true,"extensionVersion":"1.0.0","activeMode":"BluetoothHfp"}

curl http://radio:5004/api/gvbridge/adapter/mode
# Expected: {"activeMode":"BluetoothHfp","modes":[{"mode":"BluetoothHfp"},{"mode":"SipTrunk"},{"mode":"GVBrowser"}]}
```

### GVApi SIP status fields (keep-alive / auto-reconnect)

In `GVApi` mode, `GET /api/gvbridge/status` now reflects the **real** SIP-over-WebSocket
signaling state, not a stale flag:

```bash
curl http://radio:5004/api/gvbridge/status
# {"available":true,"activeMode":"GVApi","sipRegistered":true,
#  "wsConnected":true,"lastConnectedAt":"2026-06-13T16:02:11Z",
#  "cookiesValid":true,"psidtsAgeSeconds":420}
```

| Field | Meaning |
|---|---|
| `sipRegistered` | `true` only when registered **and** the socket is actually open (honest — a dead socket can no longer report `true`). |
| `wsConnected` | Whether the SIP WebSocket is currently open. |
| `lastConnectedAt` | UTC of the last successful REGISTER 200-OK. A **new** value after a gap means a reconnect happened. |
| `psidtsAgeSeconds` | Age of the rotating freshness cookies (`__Secure-1PSIDTS/3PSIDTS`). A large age hints the next request may 401 even if `cookiesValid` last passed. |

**Keep-alive:** the transport parses Google's RFC 6223 `keep=` (e.g. `keep=240`) from the
REGISTER 200-OK Via and sends an RFC 5626 §3.5.1 double-CRLF (`\r\n\r\n`) ping every
`max(15, keep/2)`s, plus a protocol-level `KeepAliveInterval`. Watch the logs for
`Keep-alive armed: keep=…` and `Sent keep-alive ping`.

**Auto-reconnect:** if the socket drops unexpectedly, the logs show
`SIP WebSocket dropped unexpectedly … reconnecting` → backoff (1,2,4,8,16,30s ± jitter,
single-flight) → `SIP reconnect succeeded`. During the gap `wsConnected` is `false`; after
recovery it is `true` with a new `lastConnectedAt`.

**Observing it works:** leave the line idle 6+ minutes (past the old ~256s drop) and poll
`/api/gvbridge/status` — `wsConnected` should stay `true` and `lastConnectedAt` should NOT
change (keep-alive kept the socket alive). Then place an inbound call without any manual
cookie refresh; the phone should ring.

---

## Step 5: Test Mode Switching

### Via Web UI

1. Go to `http://radio:5004/gvbridge`
2. In the **Call Path** section, click **GV Browser**
3. Verify the mode changes (the radio button updates)
4. Refresh the page — mode should persist

### Via REST API

```bash
# Switch to GV Browser mode
curl -X PUT http://radio:5004/api/gvbridge/adapter/mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"GVBrowser"}'

# Switch back to Bluetooth
curl -X PUT http://radio:5004/api/gvbridge/adapter/mode \
  -H "Content-Type: application/json" \
  -d '{"mode":"BluetoothHfp"}'
```

---

## Step 6: Test GV Bridge Call Flow

### Prerequisites
- Chrome running with extension loaded on `voice.google.com`
- User logged into Google Voice
- Mode set to **GV Browser**
- HT801 ATA powered on and registered (rotary phone connected)

### Inbound Call Test

1. From another phone, call your Google Voice number
2. **Expected:**
   - The GV web UI shows the incoming call dialog
   - The Chrome extension detects it via MutationObserver
   - `incomingCall` message sent via WebSocket to GVBridgeService
   - GVBrowserAdapter fires `OnIncomingCall`
   - CallManager sends SIP INVITE to HT801
   - Rotary phone rings
3. Lift the rotary phone handset
4. **Expected:** Call is answered in the GV web UI
5. Replace handset — call ends

### Outbound Call Test (via dashboard)

1. Go to `http://radio:5004/gvtrunk` (the GV Trunk dashboard has a dial panel)
2. Enter a phone number and click "Dial via GV Trunk"
3. **Expected:** The GV web UI initiates the call

---

## Step 7: Test SMS

### Inbound SMS

1. Send an SMS to your Google Voice number from another phone
2. **Expected:** SMS appears in the GVBridge dashboard SMS panel within ~30 seconds
3. Also visible via API: `curl http://radio:5004/api/gvbridge/sms`

### Outbound SMS (via API)

```bash
curl -X POST http://radio:5004/api/gvbridge/sms/send \
  -H "Content-Type: application/json" \
  -d '{"to":"+15551234567","body":"Test from rotary phone"}'
```

---

## Known Limitations

### Audio bridging (Phase 2)

The full-duplex audio bridge (GV WebRTC ↔ HT801 RTP) is **scaffolded but not yet implemented**:
- The `offscreen/audio-bridge.js` file exists but `tabCapture` + WebRTC hook are stubs
- You can detect calls, answer/hangup, and send SMS, but **voice audio does not flow through the rotary phone handset** in GV Browser mode
- Audio works normally in Bluetooth and SIP Trunk modes
- This is documented as Phase 2 work in the PRD

### DOM selector stability

Google Voice updates its web UI periodically. If call detection breaks:
1. Open `ChromeExtension/content/gv-bridge.js`
2. Update the `SELECTORS` constant at the top of the file
3. Use Chrome DevTools → Inspector on `voice.google.com` to find current ARIA labels
4. Reload the extension at `chrome://extensions`

### Extension update workflow

After modifying extension files:
1. Go to `chrome://extensions`
2. Click the refresh icon (↺) on the RotaryPhone GV Bridge card
3. Reload `voice.google.com`

---

## Troubleshooting

### Extension shows "Disconnected"

- Verify the .NET app is running: `ssh radio "systemctl is-active rotary-phone"`
- Verify port 8765 is listening: `ssh radio "ss -tlnp | grep 8765"`
- Check extension errors: `chrome://extensions` → GV Bridge → "Errors" button
- Check service worker console: click "Service Worker" link on the extension card

### Mode switch returns 409

A call is currently active. Hang up first, then switch modes.

### Extension not detecting incoming calls

- Verify you're on `https://voice.google.com` (not a cached/offline page)
- Check the content script console: DevTools → Console on the GV tab → look for `[GVBridge]` messages
- The incoming call dialog must contain text matching `/incoming call/i`

### HT801 not ringing

This is the HT801 INVITE issue documented in `docs/TODO-remaining-work.md`. After HT801 factory reset, reconfigure:
- SIP Server: radio box IP (e.g., `192.168.86.50`)
- SIP User ID: `1000`

---

## API Reference

### GV Bridge API (`/api/gvbridge`)

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/gvbridge/status` | Extension connection status + active mode |
| `GET` | `/api/gvbridge/sms` | Last 20 SMS/missed-call notifications |
| `POST` | `/api/gvbridge/sms/send` | Send SMS: `{ "to": "+1...", "body": "..." }` |
| `GET` | `/api/gvbridge/adapter/mode` | Current mode + available modes |
| `PUT` | `/api/gvbridge/adapter/mode` | Switch mode: `{ "mode": "GVBrowser" }` |

### GV Bridge SignalR Hub (`/hubs/gvbridge`)

| Event | Payload | Trigger |
|-------|---------|---------|
| `ExtensionConnectionChanged` | `{ connected: bool }` | Extension connects/disconnects |
| `ModeChanged` | `{ activeMode: string }` | Mode switched via registry |

### GV Trunk API (`/api/gvtrunk`) — from previous PRD

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/gvtrunk/status` | Registration status + call state |
| `GET` | `/api/gvtrunk/calls` | Call history (last 50) |
| `GET` | `/api/gvtrunk/sms` | Gmail SMS notifications |
| `POST` | `/api/gvtrunk/dial` | Place outbound call: `{ "number": "+1..." }` |
| `POST` | `/api/gvtrunk/reregister` | Force SIP re-registration |

---

## UI Pages

| URL | Framework | Description |
|-----|-----------|-------------|
| `/gvbridge` | React | GV Bridge dashboard — mode selector, extension status, SMS |
| `/gvtrunk` | React | GV Trunk dashboard — SIP registration, call history, dial pad |
| `/pairing` | React | Bluetooth device pairing management |
