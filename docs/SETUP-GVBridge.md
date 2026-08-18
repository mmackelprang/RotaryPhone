# GV Bridge — Setup Guide

**Last updated:** August 18, 2026

This guide covers setting up the GV Bridge system on a fresh Ubuntu box. The GV Bridge enables incoming Google Voice calls to ring a physical rotary phone connected via a Grandstream HT801 ATA.

## Architecture

Two independent paths share the box. They are worth keeping apart in your head, because
only one of them still involves the browser.

**Calls — no browser involvement:**

```
Google Voice call
  -> SIP over WebSocket to the .NET RotaryPhoneController server
  -> CallManager sends SIP INVITE to HT801
  -> HT801 rings the rotary phone
  -> User picks up -> 200 OK -> call connected
  -> Audio flows both ways over DTLS-SRTP (SIPSorcery)
```

**SMS and voicemail — the browser holds the session, nothing more:**

```
gv-bridge-ensure.sh
  -> Google Chrome, dedicated profile, CDP on :9224
  -> holds ONE authenticated voice.google.com session
  -> cron (*/20) POSTs /api/gvbridge/cookies/refresh-from-browser
  -> the API reads that session's cookies over CDP
  -> the API calls Google's HTTP API directly for SMS + voicemail
```

The Chrome extension is **superseded** and is not loaded — see
[The extension is no longer in the path](#the-extension-is-no-longer-in-the-path).

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Ubuntu | 24.04+ | Tested on Ubuntu 24.04 (x64) |
| .NET SDK | 10.0 | For building from source |
| Google Chrome | 137+ | `google-chrome` on PATH. Chromium is no longer used. |
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

1. Verifies Google Chrome is installed (it will not install a browser for you)
2. Installs `gv-bridge-ensure.sh` and `gv-bridge-restart.sh` into `~/bin`
3. Installs the watchdog and nightly-restart systemd **user** units
4. Enables the 2-minute watchdog timer, which brings the bridge up if it is down
5. Installs the login autostart entry
6. Creates a desktop shortcut that runs the ensure script

No `sudo` is required for any of it. Pass `--with-legacy-extension-service` to also
provision the superseded snap-Chromium configuration; it is installed but never enabled.

### What starts automatically on boot

| Unit | Type | Starts | Enabled by setup |
|---|---|---|---|
| `rotary-phone.service` | System (systemd) | On boot | yes |
| `gv-bridge-watchdog.timer` | User (`systemctl --user`) | 2 min after boot, then every 2 min | **yes** |
| `gv-bridge-restart.timer` | User (`systemctl --user`) | Nightly 04:00 | no — installed, left disabled |
| `~/.config/autostart/gv-bridge-chrome.desktop` | GNOME autostart | 15 s after login | yes |

The watchdog is what actually keeps the bridge alive. `gv-bridge-ensure.sh` is idempotent
by contract: it looks for a process carrying this profile's `--user-data-dir` and exits 0
without touching anything if it finds one, so running it every 2 minutes costs nothing.

## First-Time Setup (one-time steps)

After deploying and running the setup script:

### 1. Bring the bridge up

```bash
~/bin/gv-bridge-ensure.sh
```

Or wait up to 2 minutes for the watchdog, or click the **"GV Bridge"** desktop shortcut.

### 2. Log into Google Voice

The window is placed by the Wayland compositor — `--window-position` is a no-op there, so
raise the window from the GNOME overview rather than editing coordinates. Sign in with the
Google account that owns the Voice number.

### 3. Confirm CDP is answering

This is what everything else depends on:

```bash
curl -s http://localhost:9224/json/version
```

### 4. Confirm cookie extraction works end to end

```bash
curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser \
  -H 'Content-Type: application/json' -d '{}'
# Expected: {"refreshed":true,"cookieCount":<n>}
```

Anything else here means SMS and voicemail lists will come back empty and the API will log
"authenticated client unavailable".

### 5. Verify

```bash
curl -s http://localhost:5004/api/gvbridge/status
curl -s http://localhost:5004/api/diagnostics/status | python3 -m json.tool
curl -s http://localhost:5004/api/diagnostics/audio-bridge
```

### Optional: enable the nightly recycle

Off by default. Enable it if renderer heap growth becomes a problem:

```bash
systemctl --user enable --now gv-bridge-restart.timer
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

> **There is no `GVBridge:HT801Ip` key.** It was removed — the HT801's address has exactly one
> home, `RotaryPhone:Phones[].HT801IpAddress`, and at runtime the address learned from the device's
> own SIP REGISTER wins over it. See **[docs/HT801-ADDRESS.md](HT801-ADDRESS.md)** for the one place
> to change the address and how to verify it correctly.

### Key files on the radio box

| Path | Purpose |
|------|---------|
| `/opt/rotary-phone/` | Main application |
| `~/bin/gv-bridge-ensure.sh` | Launch-if-down. Idempotent; the watchdog runs it every 2 min |
| `~/bin/gv-bridge-restart.sh` | Nightly recycle — kills, then delegates relaunch to ensure |
| `~/.config/systemd/user/gv-bridge-watchdog.{service,timer}` | 2-minute liveness check |
| `~/.config/systemd/user/gv-bridge-restart.{service,timer}` | Nightly 04:00 recycle |
| `~/.config/autostart/gv-bridge-chrome.desktop` | Runs the ensure script at login |
| `~/Desktop/GV-Bridge.desktop` | Desktop shortcut (mode 755 — see note below) |
| `~/.config/gv-bridge-chrome/` | Chrome profile holding the authenticated GV session |
| `~/.local/state/gv-bridge-restart.log` | Launch / restart log |
| `/opt/rotary-phone/refresh-gv-cookies.sh` | Cron `*/20` — refreshes cookies, mutes the tab |
| `/opt/rotary-phone/ChromeExtension/` | Extension source. Passed to Chrome but **not loaded** |

**.desktop files must be mode 755, never 775.** GNOME silently refuses to launch a
group-writable `.desktop` file, which is exactly why the previously shipped
`GV-Bridge.desktop` did nothing when clicked.

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

# GV bridge watchdog (launch / relaunch events)
journalctl --user -u gv-bridge-watchdog.service --since '-1h'
tail -f ~/.local/state/gv-bridge-restart.log

# Timer state
systemctl --user list-timers 'gv-bridge-*'
```

## Troubleshooting

### Phone doesn't ring

1. **Check HT801 registration**: `curl http://localhost:5004/api/diagnostics/status` → `ht801.isRegistered` should be `true`
2. **Send test INVITE**: `curl -X POST http://localhost:5004/api/diagnostics/test-ring` and check the SIP log for 100/180/200 responses
3. **If INVITE times out**: The HT801 may need a factory reset (hidden state blocks incoming SIP). After reset, reconfigure SIP Server, User ID, and registration.

### SMS or voicemail lists are empty

Almost always the CDP link to the bridge, not the API.

1. Is the bridge running? `pgrep -af 'user-data-dir=/home/mmack/.config/gv-bridge-chrome'`
2. Is CDP answering? `curl -s http://localhost:9224/json/version`
3. Does a manual refresh succeed?
   `curl -s -X POST http://localhost:5004/api/gvbridge/cookies/refresh-from-browser -H 'Content-Type: application/json' -d '{}'`
4. Has the session expired? Raise the bridge window and check it is still signed in.
5. Watchdog history: `tail ~/.local/state/gv-bridge-restart.log`

If the browser is up but CDP is silent, it was launched **without**
`--remote-debugging-port=9224 --remote-allow-origins=*`. Kill it and re-run
`~/bin/gv-bridge-ensure.sh`, which always supplies both.

### The extension is no longer in the path

Chrome has ignored `--load-extension` since v137. This box runs Chrome 151, and the live
profile's `Preferences` lists only Chrome's five built-in extensions — the GV Bridge
extension is absent (verified 2026-08-18).

Calls work regardless. A live test call under exactly that configuration reported
`inboundFramesSent: 345`, `outboundFramesReceived: 341`, `bidirectionalAudio: true` and
zero errors, because audio runs on the SIPSorcery DTLS-SRTP path
(`docs/superpowers/specs/2026-03-27-gv-api-migration-design.md`) rather than the
extension's tabCapture relay, and answer/hangup go over SIP rather than DOM clicking.

The flag is still passed so the command line matches the process the box runs today, but
nothing depends on it. Do not write code that assumes the extension is loaded.

### GV doesn't ring in the browser

This is no longer a meaningful symptom, and chasing it will waste your time.
Incoming calls are signalled over SIP; the browser is not in the ring path at all,
so whether Google Voice rings *in the browser* has no bearing on whether the
rotary phone rings. See [Phone doesn't ring](#phone-doesnt-ring) for the
troubleshooting that actually applies.

### After reboot

Both services auto-start, but you may need to:
1. Wait 1-2 minutes for HT801 to re-register
2. Switch adapter mode: `curl -X PUT http://localhost:5004/api/gvbridge/adapter/mode -H 'Content-Type: application/json' -d '{"mode":"GVBrowser"}'`

## Current Status & Known Limitations

### Working (call flow verified 2026-03-24; audio + cookie bridge verified 2026-08-18)
- **Full incoming call flow**: GV call → SIP INVITE → HT801 → phone rings → user answers (200 OK) → InCall → user hangs up (BYE) → Idle
- **Call state machine**: SIP events are authoritative (not browser extension events). 60-second ringing timeout prevents stuck state.
- **Incoming call detection**: signalled over SIP, not by the browser. The extension's
  500 ms DOM button poll is no longer in the path — the extension is not loaded (see
  Troubleshooting), yet the 2026-08-18 live call rang and connected normally.
- **SIP diagnostics**: Real-time message log, INVITE timeout detection, HT801 health, call timeline
- **Bidirectional call audio**: DTLS-SRTP via SIPSorcery. Live call 2026-08-18 reported `inboundFramesSent: 345`, `outboundFramesReceived: 341`, `bidirectionalAudio: true`, zero errors. This closes the June 13 investigation that recorded `inboundFramesSent: 0`.
- **SMS + voicemail**: read from Google's HTTP API using cookies scraped from the bridge browser over CDP. Live check 2026-08-18 listed 149 SMS messages and 50 voicemails.
- **Diagnostics web UI** at `/diagnostics` and REST API

### Not yet working
- **BYE handling**: HT801's BYE after a test-ring gets 481 response, leaving the device stuck. Workaround: reboot HT801 after using test-ring.
- **Auto mode on boot**: Adapter defaults to BluetoothHfp; needs manual switch to GVBrowser after each service restart
- **Outgoing calls**: Rotary dial → GV not yet implemented

### HT801 Quirks
- **Factory reset required** if incoming SIP stops working. Only configure 3 settings: SIP Server, SIP User ID, SIP Registration. Changing other settings (Register Expiration, NOTIFY Auth, etc.) can silently break incoming SIP.
- **Re-registration delay**: After service restart, HT801 won't re-register until its timer fires (up to 60 min). Reboot the HT801 to force immediate re-registration.
- **Test-ring caution**: The test-ring endpoint auto-answers after 4s and the BYE response (481) leaves the HT801 stuck. Always reboot HT801 after using test-ring.
