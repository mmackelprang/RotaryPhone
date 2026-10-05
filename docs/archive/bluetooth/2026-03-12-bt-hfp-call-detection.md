# RotaryPhone Server: BT HFP Call Detection + Hook Fix

## Context

The RotaryPhone server (this repo) controls a physical rotary phone via an HT801 ATA adapter over SIP. It also has a `BlueZHfpAdapter` that monitors Bluetooth device connections in "passive mode" (Radio.API owns the BT adapter). The server runs on Ubuntu x64 at `http://0.0.0.0:5004`.

A separate Radio Console system (Radio.API + Radio.Web) connects to this server's SignalR hub at `/hub` and listens for `CallStateChanged` and `IncomingCall` events to:
- Announce callers via TTS through the radio console speakers
- Update the Phone page UI in the web interface

## Two Issues to Fix

### Issue 1: BT HFP Call Detection (New Feature)

**Problem:** When a Bluetooth-connected phone (Pixel 8 Pro, address `D4:3A:2C:64:87:9E`) receives an incoming call, the RotaryPhone server doesn't detect it. The `BlueZHfpAdapter` monitors device connection/disconnection but does NOT monitor HFP call indicators.

**The phone advertises these relevant profiles:**
- Handsfree Audio Gateway (UUID `0000111f-0000-1000-8000-00805f9b34fb`)
- Headset AG (UUID `00001112`)
- Telephony and Media Audio (UUID `00001855`)

**Desired behavior when a BT-connected phone receives a call:**

1. Detect the incoming call via BlueZ HFP indicators (the phone acts as Audio Gateway, the radio acts as Hands-Free unit)
2. Extract the caller's phone number (via CLIP/CLCC AT commands or BlueZ telephony interfaces)
3. Send a SIP INVITE to the HT801 ATA (192.168.86.250) to ring the physical rotary phone — the server already does this for simulated incoming calls
4. Broadcast via SignalR hub using the SAME message format as existing SIP calls:
   - `CallStateChanged(phoneId, state)` where state is "Ringing", "InCall", "Ended", or "Idle"
   - `IncomingCall(phoneId, phoneNumber)` with the caller's number
5. When the call ends (caller hangs up, or phone user answers on the phone itself), broadcast state change to "Ended" → "Idle" and cancel the SIP INVITE to stop the rotary phone ringing

**When the user picks up the rotary phone handset during a BT HFP incoming call:**
- The HT801 detects off-hook and accepts the SIP INVITE
- The server should bridge audio between the BT HFP audio path and the SIP/RTP path to the HT801
- OR, if audio bridging is too complex for now, at minimum: detect that the call was answered, update state to "InCall", and let the user talk on their actual cell phone (the rotary phone handset provides the physical ring notification, not the actual audio path)

**Implementation approach for HFP detection on Linux:**
- BlueZ exposes HFP state via D-Bus. When the phone (AG) has an incoming call, the HFP protocol sends call indicators
- Check `org.bluez` D-Bus interfaces for the connected device — specifically properties that change on incoming calls
- oFono is NOT installed on the target system; ModemManager is running but doesn't detect the BT device as a modem
- The `BlueZHfpAdapter` already has D-Bus access and monitors device connections — extend it to also monitor HFP call state properties
- A `dbus-monitor` process is already running (PID tracked at startup) — it may be possible to filter for HFP-related signals

### Issue 2: Hook Detection Broken After Service Restart

**Problem:** On March 11, hook detection worked correctly:
```
Mar 11 20:36:30  Hook change: OFF-HOOK → State changed to: Dialing → Broadcasting: Dialing
Mar 11 20:36:50  Hook change: ON-HOOK → State changed to: Idle → Broadcasting: Idle
Mar 11 20:37:00  Simulating incoming call → State changed to: Ringing → Broadcasting: Ringing
```

After a service restart on March 12 (PID changed from 218044 → 280865 → 297152), no hook events appear in logs despite:
- SIP transport starting successfully on 0.0.0.0:5060
- HT801 responding to ping at 192.168.86.250 (4-194ms)
- CallManager initializing successfully

**Likely causes:**
- The HT801's SIP registration to the RotaryPhone server may have expired and not re-registered after restart
- The SIP user agent may not be sending REGISTER responses to the HT801
- The HT801 may need its SIP server address re-configured or a reboot to re-register

**Investigation steps:**
1. Check the SIP registration state — is the HT801 registered with the SIP user agent?
2. Check if the RotaryPhone server logs SIP REGISTER messages from the HT801
3. The HT801 web config interface at http://192.168.86.250 may show registration status
4. May need to add SIP registration logging to diagnose the issue

## Current Architecture (from startup logs)

```
RotaryPhoneController.Server startup:
  → CallHistoryService (max 500 entries)
  → SIP transport on 0.0.0.0:5060
  → SIPUserAgent initialized
  → BlueZHfpAdapter (passive mode — Radio.API owns BT adapter)
  → MockRtpAudioBridge
  → CallManager for phone "default" (Rotary Phone)
  → PhoneManagerService
  → D-Bus device connection monitor (dbus-monitor process)
  → BlueZ mgmt socket (disconnect events)
  → SignalR Notifier Service (subscribes to phone events)
  → Listening on http://0.0.0.0:5004
```

## SignalR Message Format (consumed by Radio.API)

Radio.API's `PhoneCallClient` registers these handlers:

```csharp
// Two-arg version
_hubConnection.On<string, string>("CallStateChanged", (state, phoneNumber) => { ... });

// Three-arg version (with caller name)
_hubConnection.On<string, string, string>("CallStateChanged", (state, phoneNumber, callerName) => { ... });
```

Valid state strings (case-insensitive): `"ringing"`, `"ring"`, `"incoming"` → Ringing; `"incall"`, `"in_call"`, `"active"`, `"answered"` → InCall; `"ended"`, `"hangup"`, `"idle"` → Ended/Idle

Radio.Web's `PhoneHubService` registers:
```csharp
_hubConnection.On<string, string>("CallStateChanged", (phoneId, state) => { ... });
_hubConnection.On<string, string>("IncomingCall", (phoneId, phoneNumber) => { ... });
```

**Both clients connect to the same hub.** Ensure messages match both expected signatures.

## Target Environment

- Ubuntu 24.04.4 LTS, kernel 6.17
- BlueZ (system Bluetooth stack)
- PipeWire audio (not PulseAudio)
- BT adapter: TP-Link UB500 (MAC `78:20:51:F5:FB:A7`) on hci0
- Connected device: Pixel 8 Pro (Android, MAC `D4:3A:2C:64:87:9E`)
- HT801 ATA at 192.168.86.250 (SIP phone adapter)
- .NET 10 runtime

## Success Criteria

1. When the Pixel 8 Pro receives an incoming call while BT-connected, the rotary phone physically rings
2. The SignalR hub broadcasts "Ringing" with the caller's phone number
3. When the call ends, the SignalR hub broadcasts "Ended"/"Idle" and the rotary phone stops ringing
4. Lifting/dropping the rotary phone handset is detected and state changes are broadcast (fix the post-restart issue)
5. Existing SIP call functionality continues to work
