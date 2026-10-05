# RotaryPhone Standalone Architecture Design

## Goal

Transform RotaryPhone into a standalone project that connects 1-2 cell phones to a legacy rotary phone via Bluetooth HFP, with full call handling (incoming/outgoing), bidirectional voice audio through the rotary handset, and a React pairing/management UI. RotaryPhone owns its Bluetooth adapter and pairing end-to-end.

## Context

RotaryPhone currently has partial HFP call detection deployed on branch `feature/bt-hfp-call-detection`. The initial implementation has known bugs (callsetup=1 not triggering ring, HT801 not re-registering, BT connection instability) and was designed in "passive mode" where Radio.API owned the Bluetooth adapter. This spec replaces that architecture with one where RotaryPhone is fully standalone.

A separate project, RTest (Radio.API), shares the same Linux host and uses a USB BT dongle for A2DP audio streaming to a radio. The two projects integrate via SignalR/HTTP but maintain separate repos and separate BT adapters.

## Hardware Architecture

```
┌─────────────────────────────────────────────────────┐
│                   Ubuntu Linux Host                  │
│                                                      │
│  ┌──────────────┐          ┌───────────────────────┐ │
│  │  RotaryPhone │          │       RTest           │ │
│  │  (.NET + Py) │          │   (Radio.API)         │ │
│  │              │          │                       │ │
│  │  hci1        │          │  hci0                 │ │
│  │  (built-in)  │          │  (USB dongle)         │ │
│  │  HFP phones  │          │  A2DP radio           │ │
│  └──────┬───────┘          └───────┬───────────────┘ │
│         │                          │                  │
│    Bluetooth                  Bluetooth               │
│         │                          │                  │
│   ┌─────┴─────┐             ┌─────┴─────┐           │
│   │ Pixel 8   │             │  Radio    │           │
│   │ Pro       │             │  Speaker  │           │
│   │ (phone)   │             │           │           │
│   └───────────┘             └───────────┘           │
│                                                      │
│   ┌─────────────┐                                    │
│   │ HT801 ATA   │←── SIP/RTP ──→ RotaryPhone        │
│   │ 192.168.86. │                                    │
│   │ 250         │                                    │
│   └──────┬──────┘                                    │
│          │ RJ-11                                     │
│   ┌──────┴──────┐                                    │
│   │ Rotary Phone│                                    │
│   │ (handset)   │                                    │
│   └─────────────┘                                    │
└─────────────────────────────────────────────────────┘
```

### Adapter Assignment

| Adapter | Controller | Interface | Profile | Purpose |
|---------|-----------|-----------|---------|---------|
| hci1 (built-in) | RotaryPhone | `org.bluez.Adapter1` at `/org/bluez/hci1` | HFP-HF | Connect to 1-2 cell phones |
| hci0 (USB TP-Link UB500) | RTest | `org.bluez.Adapter1` at `/org/bluez/hci0` | A2DP | Stream audio to radio |

Each project configures its own adapter via BlueZ D-Bus. No sharing, no conflicts.

**Adapter isolation note:** `ProfileManager1.RegisterProfile` is BlueZ daemon-wide (not per-adapter). Adapter isolation is achieved by: (a) only running discovery on hci1, (b) only initiating `ConnectProfile` on devices under `/org/bluez/hci1/dev_*`, and (c) verifying the device path prefix in `NewConnection` callbacks to reject connections arriving on the wrong adapter.

## System Architecture

### Component Overview

```
┌────────────────────────────────────────────────────────────────┐
│                    RotaryPhone Server (.NET)                    │
│                                                                │
│  ┌──────────────┐  ┌──────────────┐  ┌─────────────────────┐  │
│  │ PhoneManager  │  │ CallManager  │  │ CallManager         │  │
│  │ Service       │──│ (phone-1)    │  │ (phone-2)           │  │
│  │               │  └──────┬───────┘  └──────┬──────────────┘  │
│  └───────────────┘         │                 │                 │
│                      ┌─────┴─────────────────┴──────┐          │
│                      │  IBluetoothDeviceManager     │          │
│                      │  (replaces IBluetoothHfp-    │          │
│                      │   Adapter)                   │          │
│                      └──────────────┬───────────────┘          │
│                                     │ stdin/stdout JSON        │
│                      ┌──────────────┴───────────────┐          │
│                      │  bt_manager.py               │          │
│                      │  - BlueZ agent (pairing)     │          │
│                      │  - Multi-device HFP          │          │
│                      │  - SCO audio socket           │          │
│                      │  - Device discovery           │          │
│                      └──────────────┬───────────────┘          │
│                                     │ D-Bus                    │
│                              ┌──────┴──────┐                   │
│                              │ BlueZ       │                   │
│                              │ (hci1 only) │                   │
│                              └─────────────┘                   │
│                                                                │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │ SIPSorcery   │  │ RTP Audio    │  │ SignalR Hub          │  │
│  │ Adapter      │  │ Bridge       │  │ (/hub)               │  │
│  │ (SIP/UDP)    │  │ (SCO↔RTP)    │  │                      │  │
│  └──────────────┘  └──────────────┘  └──────────────────────┘  │
│                                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                    React UI (/web)                        │  │
│  │  Dashboard │ Pairing │ Devices │ Contacts │ Call History  │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions

1. **RotaryPhone owns hci1 exclusively.** It configures the adapter, runs the BlueZ agent, and manages pairing. No passive mode.

2. **bt_manager.py replaces hfp_monitor.py.** The Python script expands from HFP-only monitoring to full BT management: agent, discovery, multi-device HFP, and SCO audio.

3. **IBluetoothDeviceManager replaces IBluetoothHfpAdapter.** The new interface supports multi-device tracking with per-device events and commands.

4. **SCO audio flows through bt_manager.py.** The Python script opens SCO sockets for voice audio and bridges to the .NET server via a local UDP stream, where it gets transcoded and forwarded as RTP to the HT801.

5. **Phone selection is automatic.** For outgoing calls, RotaryPhone picks the first device in `ConnectedDevices` that does not have an active call (`HasActiveCall == false`). For incoming calls, both devices ring the rotary phone. Whichever call is answered first wins.

6. **RTest integration stays SignalR/HTTP.** No code coupling between repos. RTest connects to RotaryPhone's `/hub` for caller ID resolution and status. Hub URL contract is documented.

## Component Specifications

### bt_manager.py (expanded from hfp_monitor.py)

**Responsibilities:**
1. **Adapter ownership:** Configure hci1 via BlueZ D-Bus — set alias, discoverable, pairable
2. **BlueZ Agent1:** Handle pairing requests (PIN display, confirmation) — report to .NET for UI prompts
3. **Device discovery:** Start/stop scanning on hci1, report discovered devices
4. **Multi-device HFP:** Manage N simultaneous RFCOMM/HFP connections (one per paired phone)
5. **SCO audio:** Accept SCO connections only for calls we answered (`ATA`-initiated). Bridge audio data via local UDP
6. **Event output:** JSON events on stdout (same protocol as hfp_monitor.py, expanded)
7. **Command input:** JSON commands on stdin (same protocol, expanded)

**New events (additions to existing set):**
```json
{"event":"device_discovered","address":"AA:BB:CC:DD:EE:FF","name":"Pixel 8 Pro","paired":false}
{"event":"device_paired","address":"AA:BB:CC:DD:EE:FF","name":"Pixel 8 Pro"}
{"event":"device_removed","address":"AA:BB:CC:DD:EE:FF"}
{"event":"pairing_request","address":"AA:BB:CC:DD:EE:FF","type":"confirmation","passkey":"123456"}
{"event":"sco_connected","address":"AA:BB:CC:DD:EE:FF","codec":"CVSD"}
{"event":"sco_disconnected","address":"AA:BB:CC:DD:EE:FF"}
{"event":"adapter_ready","address":"AA:BB:CC:DD:EE:FF","name":"Rotary Phone"}
```

**New commands:**
```json
{"command":"start_discovery"}
{"command":"stop_discovery"}
{"command":"pair","address":"AA:BB:CC:DD:EE:FF"}
{"command":"connect","address":"AA:BB:CC:DD:EE:FF"}
{"command":"disconnect","address":"AA:BB:CC:DD:EE:FF"}
{"command":"remove_device","address":"AA:BB:CC:DD:EE:FF"}
{"command":"confirm_pairing","address":"AA:BB:CC:DD:EE:FF","accept":true}
{"command":"set_adapter","alias":"Rotary Phone","discoverable":true}
```

**Multi-device tracking:** Each HFP connection is identified by device address. Events include the address field so the .NET side can track per-device state.

**SCO audio bridge:**
- Listen on Bluetooth SCO socket (BTPROTO_SCO)
- Accept connections from paired phones during active calls
- Initial implementation: CVSD codec only (8kHz mono 16-bit PCM). HF_FEATURES bitmask remains 0 to force narrowband. mSBC (wideband, 16kHz) deferred to Phase 6+, requiring AT+BAC/AT+BCS codec negotiation and HF feature bit 5
- Bridge: read SCO → write to local UDP port (for .NET RTP bridge); read from local UDP → write to SCO
- Per-device UDP port pairs for multi-phone: phone 1 uses ports 49100/49101, phone 2 uses 49102/49103
- The .NET RTP bridge handles G.711 transcoding and RTP packetization
- **Requires `CAP_NET_ADMIN`:** BTPROTO_SCO sockets need elevated privileges. The systemd service unit must include `AmbientCapabilities=CAP_NET_ADMIN` (already required for BluetoothMgmtMonitor)
- **SCO failure handling:** If SCO connection fails or drops mid-call, the call remains active (user can still talk on cell phone) but audio will not flow through the rotary handset. An `sco_disconnected` event is emitted so the UI can indicate the audio path is down

### IBluetoothDeviceManager (replaces IBluetoothHfpAdapter)

```csharp
/// <summary>
/// Manages Bluetooth devices, HFP connections, and pairing.
/// All events may fire from background threads (Python subprocess reader).
/// Implementations must be thread-safe. Device list properties return snapshots.
/// </summary>
public interface IBluetoothDeviceManager : IAsyncDisposable
{
    // Lifecycle
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // Device tracking — returns snapshot copies (safe to enumerate)
    IReadOnlyList<BluetoothDevice> ConnectedDevices { get; }
    IReadOnlyList<BluetoothDevice> PairedDevices { get; }

    // Events — all include device for multi-device support
    /// <summary>Fired when a paired device establishes a BT connection.</summary>
    event Action<BluetoothDevice>? OnDeviceConnected;
    event Action<BluetoothDevice>? OnDeviceDisconnected;
    /// <summary>Incoming call detected. string parameter is the caller's phone number or "Unknown".</summary>
    event Action<BluetoothDevice, string>? OnIncomingCall;
    /// <summary>Call was answered on the cell phone (not the rotary phone).</summary>
    event Action<BluetoothDevice>? OnCallAnsweredOnPhone;
    event Action<BluetoothDevice>? OnCallEnded;
    event Action<BluetoothDevice>? OnScoAudioConnected;
    event Action<BluetoothDevice>? OnScoAudioDisconnected;

    // Pairing
    event Action<PairingRequest>? OnPairingRequest;
    event Action<BluetoothDevice>? OnDevicePaired;
    event Action<BluetoothDevice>? OnDeviceRemoved;
    event Action<BluetoothDevice>? OnDeviceDiscovered;

    // Call commands
    Task<bool> AnswerCallAsync(string deviceAddress);
    Task<bool> HangupCallAsync(string deviceAddress);
    Task<bool> DialAsync(string deviceAddress, string phoneNumber);

    // Connection commands
    Task<bool> ConnectDeviceAsync(string deviceAddress);
    Task<bool> DisconnectDeviceAsync(string deviceAddress);

    // Discovery & pairing commands
    Task StartDiscoveryAsync();
    Task StopDiscoveryAsync();
    Task<bool> PairDeviceAsync(string deviceAddress);
    Task<bool> RemoveDeviceAsync(string deviceAddress);
    Task<bool> ConfirmPairingAsync(string deviceAddress, bool accept);
    /// <summary>Configure adapter. Null parameters are ignored (only non-null values applied).</summary>
    Task<bool> SetAdapterAsync(string? alias, bool? discoverable);

    // Adapter info
    bool IsAdapterReady { get; }
    string? AdapterAddress { get; }
}
```

**BluetoothDevice record:**
```csharp
public record BluetoothDevice(
    string Address,
    string? Name,
    bool IsConnected,
    bool IsPaired,
    bool HasActiveCall,
    bool HasIncomingCall,
    bool HasScoAudio
);
```

Note: `HasIncomingCall` is true when `callsetup=1` is active (ringing, not yet answered). `HasActiveCall` is true when `call=1` (answered/active). Both can be false simultaneously (idle) but not both true.

### PhoneManagerService Changes

Currently creates one CallManager per configured phone entry. With multi-device support:

- Each connected BT device maps to a CallManager dynamically (or a single CallManager handles whichever phone rings first)
- For simplicity in Phase 1-3: keep single CallManager, route first available device's calls through it
- Phase 4+: one CallManager per connected phone, with phone selection logic for outgoing calls

### SCO ↔ RTP Audio Bridge

**Architecture:**
```
Phone (AG) ──SCO──> bt_manager.py ──UDP──> .NET RTP Bridge ──RTP──> HT801 ──RJ-11──> Rotary Handset
                                                                                         (speaker)
Rotary Handset ──RJ-11──> HT801 ──RTP──> .NET RTP Bridge ──UDP──> bt_manager.py ──SCO──> Phone
  (microphone)
```

**Audio path details:**
1. **SCO → RTP (phone voice to rotary earpiece):**
   - bt_manager.py reads 8kHz 16-bit mono PCM from SCO socket
   - Sends raw PCM over local UDP to .NET (port configurable, e.g., 49100)
   - .NET receives PCM, encodes G.711 mu-law, wraps in RTP, sends to HT801
   - HT801 plays through rotary phone earpiece

2. **RTP → SCO (rotary microphone to phone):**
   - HT801 captures rotary phone microphone audio
   - Sends RTP (G.711 mu-law) to .NET
   - .NET decodes G.711 to PCM, sends raw PCM over local UDP to bt_manager.py (port e.g., 49101)
   - bt_manager.py writes PCM to SCO socket
   - Phone receives voice audio

**Why local UDP instead of pipe/socket?** UDP is connectionless, trivial to bridge, and matches the RTP paradigm. The .NET RTP bridge already handles UDP. Adding a second local UDP endpoint is minimal work.

**Bridge lifecycle is SCO-driven, not call-state-driven.** The SCO↔RTP bridge starts when `sco_connected` fires and stops when `sco_disconnected` fires. This correctly handles:
- Rotary-answered calls (ATA → SCO opens → bridge starts)
- Phone-answered calls (no ATA → no SCO → no bridge → audio on phone)
- Mid-call transfers (user switches audio to BT on phone → SCO opens → bridge starts)
- Mid-call transfers back (user switches audio off BT → SCO closes → bridge stops)

### React UI: Pairing Page

New page accessible from the nav drawer:

**Device Management:**
- List of paired devices with connection status
- "Start Scanning" button to discover new devices
- List of discovered devices with "Pair" button
- Pairing confirmation dialog (shows passkey when needed)
- "Remove" button for paired devices

**SignalR events consumed:**
- `DeviceDiscovered(address, name)`
- `DevicePaired(address, name)`
- `DeviceRemoved(address)`
- `DeviceConnected(address)`
- `DeviceDisconnected(address)`
- `PairingRequest(address, type, passkey)`

**API endpoints (new):**
- `POST /api/bluetooth/discovery/start`
- `POST /api/bluetooth/discovery/stop`
- `POST /api/bluetooth/pair` `{address}`
- `DELETE /api/bluetooth/devices/{address}`
- `POST /api/bluetooth/pairing/confirm` `{address, accept}`
- `GET /api/bluetooth/devices` — list paired + connected devices
- `PUT /api/bluetooth/adapter` `{alias, discoverable}`

### SIP REGISTER Handler (bug fix)

Already implemented but needs verification: `SIPSorceryAdapter.HandleRegister()` responds 200 OK with Contact and Expires headers. The HT801 at 192.168.86.250 should re-register after service restart once its SIP Server Address points to the correct IP.

### Configuration Changes

```json
{
  "RotaryPhone": {
    "BluetoothAdapter": "hci1",
    "BluetoothAdapterAlias": "Rotary Phone",
    "MaxConnectedPhones": 2,
    "ScoLocalUdpPort": 49100,
    "ScoRemoteUdpPort": 49101,
    ...existing config...
  }
}
```

## Audio Routing Rules

Audio routing is determined by **who answers the call**, and it must work correctly without the user needing to manually switch audio sources on the phone.

### Core Principle

The HFP spec gives us this for free **if we follow one rule:** only send `ATA` (answer) over RFCOMM when the user answers on the rotary phone. If the user answers on the cell phone, we do nothing — the phone keeps audio local.

| Scenario | Who sends answer | SCO audio opened? | Audio path | User experience |
|----------|-----------------|-------------------|------------|-----------------|
| User lifts rotary handset | We send `ATA` over RFCOMM | Yes — AG opens SCO to HF | Phone caller ↔ SCO ↔ UDP ↔ RTP ↔ HT801 ↔ rotary handset | Talk through rotary phone |
| User taps Answer on phone | Phone answers locally | No — we never request it | Phone caller ↔ phone speaker/earpiece | Talk through phone normally |
| Outgoing from rotary phone | We send `ATD` over RFCOMM | Yes — AG opens SCO to HF | Same as rotary answer | Talk through rotary phone |

### What bt_manager.py must NOT do

- **Never accept or initiate SCO connections for calls answered on the phone.** When `+CIEV: call=1` arrives without us having sent `ATA`, this is a phone-answered call. bt_manager.py must not open a SCO listener or accept SCO for this call.
- **Never send `AT+BCC` (Bluetooth Codec Connection) unsolicited.** This would request audio transfer to the HF device.
- **Never send `AT+BTRH` (Bluetooth Response and Hold) unless explicitly commanded.** This can interfere with audio routing on some phones.

### What the .NET side must track

`CallManager` needs to know *how* the call was answered to decide whether to start the SCO↔RTP bridge:

```
AnsweredOn = RotaryPhone  →  start SCO↔RTP bridge, audio through handset
AnsweredOn = CellPhone    →  no bridge, no SCO, audio stays on phone
```

The existing `CallManager` already has `AnsweredOn` tracking in call history. This same field should gate the audio bridge.

### Android audio routing behavior

When an HFP HF device is connected, Android gives the user an audio routing button during calls (speaker / phone earpiece / Bluetooth). If we answered with `ATA`, Android routes to Bluetooth by default. If the user answered on the phone, Android routes to phone earpiece by default. **We do not need to fight this — it works correctly as long as we only send `ATA` when the rotary phone answers.**

If the user manually switches audio to Bluetooth during a phone-answered call, Android will open SCO. bt_manager.py should accept this SCO connection and start the bridge — the user explicitly chose to route audio to the rotary phone. This is a mid-call audio route change, handled by the `OnScoAudioConnected` event.

### Mid-call audio transfer

A user may want to switch audio mid-call (e.g., answered on phone, then wants to continue on rotary). This is handled by:
- **Phone → Rotary:** User action in UI (or phone's BT audio button) triggers SCO connection. bt_manager.py accepts, bridge starts.
- **Rotary → Phone:** bt_manager.py closes SCO (or phone user taps audio routing button to switch away from BT). Bridge stops, audio returns to phone.

The `OnScoAudioConnected` / `OnScoAudioDisconnected` events drive the bridge lifecycle. The bridge starts when SCO opens and stops when SCO closes, regardless of who initiated the change.

## Call Flows

### Incoming Call (phone rings → rotary phone rings)

```
1. Phone receives call
2. Phone AG sends +CIEV: callsetup=1 (and/or RING, +CLIP)
3. bt_manager.py emits {"event":"ring","address":"...","number":"+1555..."}
4. .NET IBluetoothDeviceManager fires OnIncomingCall(device, number)
5. CallManager.HandleBluetoothIncomingCall(number) → state = Ringing
6. SIPSorceryAdapter.SendInviteToHT801() → HT801 rings rotary phone
7. SignalR broadcasts CallStateChanged("Ringing") + IncomingCall(phoneId, number)
8. React UI shows incoming call with caller number
```

### User Answers on Rotary Phone (audio → rotary handset)

```
1. User lifts handset
2. HT801 sends SIP NOTIFY (hook off)
3. SIPSorceryAdapter.OnHookChange(true)
4. CallManager.HandleHookChange(true) → if Ringing: AnswerCall()
5. CallManager sets AnsweredOn = RotaryPhone
6. IBluetoothDeviceManager.AnswerCallAsync(deviceAddress)
7. bt_manager.py sends ATA over RFCOMM ← THIS is what routes audio to us
8. Phone AG sends +CIEV: call=1 → call active
9. Phone AG opens SCO channel (because WE answered) → bt_manager.py accepts
10. SCO↔UDP↔RTP audio bridge starts automatically on sco_connected event
11. User talks through rotary handset, voice reaches phone caller
```

**Key:** Step 7 (`ATA`) is the only thing that causes the phone to route audio to Bluetooth. No `ATA` = no SCO = audio stays on phone.

### User Answers on Cell Phone (audio → phone)

```
1. Phone user taps Answer on phone screen
2. Phone AG sends +CIEV: call=1 → call active
3. bt_manager.py detects call=1 WITHOUT having sent ATA
4. bt_manager.py emits {"event":"call_active","address":"...","answered_locally":true}
5. CallManager.HandleCallAnsweredOnCellPhone() → state = InCall
6. CallManager sets AnsweredOn = CellPhone
7. NO SCO connection opened — audio stays on phone speaker/earpiece
8. NO audio bridge started
9. ISipAdapter.CancelPendingInviteAsync() → sends SIP CANCEL to HT801 to stop ringing
```

**Key:** We never sent `ATA`, so the phone keeps audio local. The user talks through their phone normally. bt_manager.py must NOT accept or initiate SCO for this call.

Note: `ISipAdapter` needs a `CancelPendingInviteAsync()` method to cancel an unanswered INVITE. Currently only `SendInviteToHT801` exists. This must be added.

### Outgoing Call from Rotary Phone

```
1. User lifts handset → hook off
2. User dials number on rotary dial
3. CallManager receives digits → state = Dialing
4. After dial timeout: CallManager.StartCall(number)
5. Selects first device in ConnectedDevices with HasActiveCall == false
6. IBluetoothDeviceManager.DialAsync(deviceAddress, number)
7. bt_manager.py sends ATD{number}; over RFCOMM
8. Phone AG dials out, sends +CIEV: callsetup=2 (outgoing setup)
9. State remains Dialing — do NOT transition to InCall until call=1
10. Remote answers → +CIEV: call=1, SCO opens
11. CallManager transitions Dialing → InCall
12. SCO↔UDP↔RTP bridge starts
13. User talks through rotary handset
```

Note: The existing `CallManager.StartCall()` transitions directly to `InCall` (line 312). This must be changed to defer the transition until `+CIEV: call=1` is received, so the audio bridge starts only after SCO is established. The existing `CallState` enum (`Idle, Dialing, Ringing, InCall`) is sufficient — `Dialing` covers both "entering digits" and "outgoing call ringing on remote end".

### No Phones Available

```
1. User lifts handset → hook off
2. User dials number
3. CallManager checks: no connected devices
4. Plays fast-busy tone via SIP (or sends error state)
5. State stays Idle or transitions to error display
```

## Integration with RTest

**Contract:** RTest connects to RotaryPhone's SignalR hub at `http://<host>:5004/hub`.

**RTest listens for:**
- `CallStateChanged(phoneId, state)` — updates radio display
- `IncomingCall(phoneId, phoneNumber)` — triggers PBAP contact lookup

**RTest calls:**
- `ReportCallerResolved(phoneNumber, displayName)` — sends resolved caller name back

**No code sharing.** If the hub URL or event names change, both sides must be updated. This contract should be documented in both repos.

**RTest BT adapter isolation:** RTest must be configured to use only hci0 (USB dongle). This is a one-line config change in RTest's BlueZ adapter code — specify adapter path `/org/bluez/hci0` instead of default.

## Implementation Phases

### Phase 1: Bug Fixes (current branch)
- Fix hfp_monitor.py to emit `ring` on `+CIEV: callsetup=1`, not just RING AT command
- Raise HFP monitor stderr logging from Debug to Information
- Investigate HT801 SIP registration (check web config at 192.168.86.250)
- Test incoming call end-to-end

### Phase 2: Adapter Ownership
- Configure RotaryPhone to own hci1 exclusively
- Remove "passive mode" comments and architecture from BlueZHfpAdapter
- Add `BluetoothAdapter` config option (adapter path)
- hfp_monitor.py → bt_manager.py rename, add adapter configuration commands
- RTest-side: configure to use hci0 only (prompt for parallel session)

### Phase 3: Multi-Phone HFP
- Expand bt_manager.py for N simultaneous RFCOMM/HFP connections
- Implement IBluetoothDeviceManager interface in .NET
- Per-device event tracking (address on all events)
- Phone selection logic for outgoing calls (first available)
- Update CallManager to work with device-specific calls

### Phase 4: Pairing UI
- Add BlueZ Agent1 implementation to bt_manager.py
- Device discovery start/stop commands
- React pairing page with device list, scan, pair, remove
- API endpoints for BT management
- SignalR events for pairing flow
- Note: Audio will not flow through the rotary handset until Phase 5. Call detection and state management work end-to-end in Phase 4.

### Phase 5: SCO Audio Bridge
- SCO socket handling in bt_manager.py (BTPROTO_SCO) — requires `CAP_NET_ADMIN`
- Local UDP bridge between Python and .NET (per-device port pairs)
- G.711 transcoding in .NET RTP bridge (reuse existing G711Codec)
- Bidirectional audio: SCO ↔ UDP ↔ RTP ↔ HT801 ↔ rotary handset
- CVSD (narrowband) only initially. mSBC deferred to Phase 6+
- `ScoRtpBridge` replaces `PipeWireRtpAudioBridge` for production use. `PipeWireRtpAudioBridge` retained for non-BT testing scenarios
- Add `CancelPendingInviteAsync()` to `ISipAdapter` for canceling unanswered INVITEs
- Fix `CallManager.StartCall()` to defer InCall transition until `call=1` received

### Phase 6: Polish
- Fast-busy tone generation when no phones available
- Reconnection logic for dropped BT connections
- Graceful multi-call handling (second call while first active)
- Error states and user-facing error messages
- Standalone documentation (README, setup guide)
- Remove RTest dependencies from core call flow

## Success Criteria

1. **Standalone operation:** RotaryPhone works without RTest running
2. **Incoming calls:** Phone rings → rotary phone rings → lift handset → talk through rotary phone
3. **Outgoing calls:** Lift handset → dial → call goes out through connected phone → talk through rotary phone
4. **Multi-phone:** Two phones paired and connected simultaneously, either can receive/make calls
5. **Pairing UI:** New phones can be paired from the React web interface without CLI
6. **Audio quality:** Bidirectional voice through rotary handset is intelligible
7. **Reliability:** BT connections survive phone sleep/wake cycles, service restarts

## Files Changed (Summary)

| File | Action | Phase |
|------|--------|-------|
| `scripts/hfp_monitor.py` | Fix callsetup=1 ring, raise log level | 1 |
| `scripts/bt_manager.py` | New (expanded from hfp_monitor.py) | 2-5 |
| `src/.../Audio/IBluetoothDeviceManager.cs` | New interface | 3 |
| `src/.../Audio/BlueZBtManager.cs` | New (replaces BlueZHfpAdapter subprocess mgmt) | 3 |
| `src/.../Audio/BlueZHfpAdapter.cs` | Deprecated, replaced by BlueZBtManager | 3 |
| `src/.../Audio/BluetoothDevice.cs` | New record type | 3 |
| `src/.../PhoneManagerService.cs` | Multi-device routing | 3 |
| `src/.../CallManager.cs` | Device-specific call handling | 3 |
| `src/.../Controllers/BluetoothController.cs` | New API controller | 4 |
| `src/.../Client/src/pages/Pairing.tsx` | New React page | 4 |
| `src/.../Audio/ScoRtpBridge.cs` | New SCO↔RTP bridge (replaces PipeWireRtpAudioBridge for prod) | 5 |
| `src/.../ISipAdapter.cs` | Add CancelPendingInviteAsync() | 5 |
| `src/.../SIPSorceryAdapter.cs` | Bug fix (already done) + SIP CANCEL support | 1, 5 |
| `src/.../Services/SignalRNotifierService.cs` | BT device events | 3-4 |
| `deploy/Deploy-ToLinux.ps1` | Already updated for scripts/ | done |
| `appsettings.json` | New BT config options | 2 |

## Target Environment

- Ubuntu 24.04.4 LTS, kernel 6.17
- BlueZ 5.72
- PipeWire audio (WirePlumber session manager)
- Python 3.12+ with dbus-python, PyGObject
- .NET 10.0
- Two BT adapters: hci0 (TP-Link UB500 USB), hci1 (built-in)
- Phone: Pixel 8 Pro (Android 15)
- HT801 ATA at 192.168.86.250
- React 19 + MUI 7 + Vite frontend
- **Required capabilities:** `CAP_NET_ADMIN` for BTPROTO_SCO and BlueZ mgmt sockets. Systemd unit must include `AmbientCapabilities=CAP_NET_ADMIN`
