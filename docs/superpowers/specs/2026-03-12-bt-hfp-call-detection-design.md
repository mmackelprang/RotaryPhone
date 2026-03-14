# BT HFP Call Detection + SIP REGISTER Fix

## Goal

Detect incoming phone calls on a Bluetooth-connected Pixel 8 Pro, ring the physical rotary phone via SIP INVITE, broadcast call state via SignalR, and fix post-restart SIP hook detection.

## Two Issues

### Issue 1: BT HFP Call Detection

**Problem:** The `BlueZHfpAdapter` monitors device connections but doesn't monitor HFP call indicators. When the phone receives an incoming call, the server doesn't know about it.

**Root cause:** BlueZ doesn't expose HFP call state via D-Bus without oFono. Nobody is handling the HFP Hands-Free profile — the RFCOMM channel for AT commands is never established.

**Solution:** Python helper script that registers as HFP Hands-Free unit via BlueZ Profile1 API, receives RFCOMM connections, and monitors AT command call indicators.

### Issue 2: SIP REGISTER Handler Missing

**Problem:** After service restart, the HT801 ATA never sends hook events (NOTIFY/INFO). The `SIPSorceryAdapter.OnSIPRequestReceived()` switch only handles NOTIFY, INFO, INVITE, BYE — it silently drops REGISTER requests.

**Root cause:** The HT801 sends SIP REGISTER to register with the server. Without a 200 OK response, registration fails, and the HT801 stops sending SIP traffic.

**Solution:** Add `case SIPMethodsEnum.REGISTER:` that responds with 200 OK including appropriate Contact and Expires headers.

## Architecture

```
Phone (AG) ──RFCOMM/HFP──> BlueZ ──Profile1 D-Bus──> hfp_monitor.py ──stdout JSON──> BlueZHfpAdapter.cs
                                                                                           │
                                                                                      fires events
                                                                                           │
                                                                              CallManager → SignalR hub
                                                                                           │
                                                                              SIP INVITE → HT801 → rings
```

### Component: hfp_monitor.py

A standalone Python 3 script deployed alongside the .NET server. Uses `dbus-python` and `gi` (GLib) — both already available on the target Ubuntu system.

**Responsibilities:**
1. Register HFP Hands-Free profile (UUID `0000111e-0000-1000-8000-00805f9b34fb`) via `org.bluez.ProfileManager1.RegisterProfile()`
2. Implement Profile1 interface: `NewConnection()`, `RequestDisconnection()`, `Release()`
3. On RFCOMM connection, complete HFP Service Level Connection (SLC) setup:
   - Send `AT+BRSF=0` (supported features — minimal set)
   - Query indicators: `AT+CIND=?` then `AT+CIND?`
   - Enable indicator reporting: `AT+CMER=3,0,0,1`
   - Enable caller ID: `AT+CLIP=1`
4. Monitor for unsolicited AT results from the AG:
   - `RING` → incoming call ringing
   - `+CLIP: "number",type` → caller phone number
   - `+CIEV: (call_index),1` → call answered/active
   - `+CIEV: (call_index),0` → call ended
   - `+CIEV: (callsetup_index),1` → incoming call setup
   - `+CIEV: (callsetup_index),0` → call setup ended
5. Output JSON events to stdout (one per line):
   - `{"event":"ring","number":"+15551234567"}` (number from CLIP if available)
   - `{"event":"call_active"}`
   - `{"event":"call_ended"}`
   - `{"event":"connected","address":"D4:3A:2C:64:87:9E"}`
   - `{"event":"disconnected","address":"D4:3A:2C:64:87:9E"}`
   - `{"event":"error","message":"..."}`
6. Support sending AT commands via stdin (for future AnswerCallAsync/TerminateCallAsync):
   - `{"command":"answer"}` → send `ATA` to AG
   - `{"command":"hangup"}` → send `AT+CHUP` to AG
   - `{"command":"dial","number":"5551234"}` → send `ATD5551234;` to AG

**AT Command Flow (SLC Setup):**
```
HF (us)                           AG (phone)
   |                                  |
   |<─── +BRSF: <ag_features> ───────|  (AG sends its features first)
   |──── AT+BRSF=<hf_features> ─────>|  (HF responds with its features)
   |<─── OK ──────────────────────────|
   |──── AT+CIND=? ─────────────────>|  (query indicator descriptions)
   |<─── +CIND: (...) ───────────────|
   |<─── OK ──────────────────────────|
   |──── AT+CIND? ──────────────────>|  (query current values)
   |<─── +CIND: 1,0,0,... ──────────|
   |<─── OK ──────────────────────────|
   |──── AT+CMER=3,0,0,1 ──────────>|  (enable indicator events)
   |<─── OK ──────────────────────────|
   |──── AT+CLIP=1 ─────────────────>|  (enable caller ID)
   |<─── OK ──────────────────────────|
   |                                  |
   |   === SLC Established ===        |
   |                                  |
   |<─── RING ────────────────────────|  (incoming call!)
   |<─── +CLIP: "+15551234",145 ─────|  (caller number)
   |<─── +CIEV: 3,1 ─────────────────|  (callsetup=incoming)
```

### Component: BlueZHfpAdapter.cs Changes

**New: HFP monitor subprocess management**
- In `InitializeAsync()`, launch `python3 scripts/hfp_monitor.py` as a child process
- Read stdout asynchronously, parse JSON events
- Fire appropriate events on the `IBluetoothHfpAdapter` interface
- Auto-restart the script on unexpected exit (with backoff)
- Kill the process in `Dispose()`

**New: AT command sending via stdin**
- `AnswerCallAsync()` → write `{"command":"answer"}` to stdin
- `TerminateCallAsync()` → write `{"command":"hangup"}` to stdin
- `InitiateCallAsync()` → write `{"command":"dial","number":"..."}` to stdin
- Replace the stub `SendAtCommandAsync()` method

**Event mapping:**
| Python event | .NET event | CallManager handler |
|---|---|---|
| `ring` (with number) | `OnIncomingCall(number)` | `HandleBluetoothIncomingCall` → Ringing + SIP INVITE |
| `call_active` | `OnCallAnsweredOnCellPhone()` | `HandleCallAnsweredOnCellPhone` → InCall |
| `call_ended` | `OnCallEnded()` | `HandleBluetoothCallEnded` → HangUp |

### Component: SIPSorceryAdapter.cs Changes

Add REGISTER handler in the switch statement:

```csharp
case SIPMethodsEnum.REGISTER:
    HandleRegister(sipRequest, localSIPEndPoint, remoteEndPoint);
    break;
```

The handler responds with `200 OK`, includes Contact and Expires headers matching the request.

### Component: SignalRNotifierService.cs Changes

The `OnStateChanged` handler already broadcasts `CallStateChanged`. Need to also broadcast `IncomingCall(phoneId, phoneNumber)` when transitioning to Ringing with a known phone number. This requires wiring the dialed number from CallManager into the broadcast.

## File Changes Summary

| File | Action | Description |
|---|---|---|
| `scripts/hfp_monitor.py` | Create | Python HFP Profile1 agent |
| `BlueZHfpAdapter.cs` | Modify | Launch/manage Python subprocess, wire events, stdin commands |
| `SIPSorceryAdapter.cs` | Modify | Add REGISTER handler |
| `SignalRNotifierService.cs` | Modify | Broadcast IncomingCall with phone number |
| `Deploy-ToLinux.ps1` | Modify | Include scripts/ in deployment |

## Target Environment

- Ubuntu 24.04.4 LTS, kernel 6.17
- BlueZ 5.72
- PipeWire audio
- python3-dbus, python3-gi available
- BT adapter: TP-Link UB500 on hci0
- Phone: Pixel 8 Pro (D4:3A:2C:64:87:9E)
- HT801 ATA at 192.168.86.250

## Success Criteria

1. When the Pixel 8 Pro receives an incoming call while BT-connected, the rotary phone physically rings
2. The SignalR hub broadcasts "Ringing" with the caller's phone number
3. When the call ends, the SignalR hub broadcasts "Ended"/"Idle" and the rotary phone stops ringing
4. Lifting/dropping the rotary phone handset is detected after service restart (SIP REGISTER fix)
5. Existing SIP call functionality continues to work
