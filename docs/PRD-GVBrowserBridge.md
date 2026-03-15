# PRD: Google Voice Browser Bridge
**Project:** RotaryPhone  
**Component:** `RotaryPhoneController.GVBridge`  
**Companion PRD:** `PRD-GoogleVoiceTrunk.md` (SIP trunk path)  
**Setup Guide:** `SETUP-GVBridge.md` ← read this first before implementing  
**Status:** Ready for Implementation  
**Target:** Claude Code session  

> **Quickstart for Claude Code:**  
> 1. Read `SETUP-GVBridge.md` for repo layout, `.csproj` files, config, and smoke test checklist  
> 2. Implementation order: Core interfaces → `GVBridgeService` → `AudioBridge` → adapters/services → Chrome extension → UI  
> 3. The **only** change to existing code is `CallManager` (§9) — all other work is in new projects  
> 4. Both files live at `docs/` in the repo root: `docs/PRD-GVBrowserBridge.md` and `docs/SETUP-GVBridge.md`

---

## 1. Background & Motivation

The existing RotaryPhone project supports two call paths:

```
Path A (Bluetooth):  Rotary Phone → HT801 ATA → Raspberry Pi → Bluetooth HFP → Mobile Phone
Path B (SIP Trunk):  Rotary Phone → HT801 ATA → Raspberry Pi → VoIP.ms → Google Voice (forwarded DID)
```

Path B requires a paid SIP trunk subscription and a forwarding number. This PRD adds a third path that works directly through the `voice.google.com` web interface — no external subscription, no forwarding number, no SIP trunk required:

```
Path C (GV Browser): Rotary Phone → HT801 ATA → Raspberry Pi → GVBridge ↔ Chrome Extension ↔ voice.google.com
```

The Chrome extension drives the existing Google Voice web UI programmatically and bridges its WebRTC audio session to the local RTP stack via a WebSocket audio relay. SMS send/receive, call control, and notifications all flow through the extension's access to GV's own authenticated session and internal API calls.

This PRD also introduces the **Connection Mode Selector** — a UI control (in both the React and Blazor hosts) that allows switching between all three call paths at runtime without restarting the application.

---

## 2. Goals

| Priority | Goal |
|---|---|
| P0 | Rotary phone receives and places calls through `voice.google.com` with no SIP subscription |
| P0 | Full-duplex audio bridged between GV WebRTC session and the HT801 ATA |
| P0 | Connection mode selector allows switching between BT Phone, SIP Trunk, and GV Browser paths |
| P1 | SMS send and receive through the GV browser session |
| P1 | Dashboard shows live call state, call history, and SMS notifications (both React and Blazor hosts) |
| P1 | Mode selector visible and functional in both host UIs |
| P2 (future) | IVR / call routing automation via DTMF injection into GV session |

---

## 3. Architecture

### 3.1 System Overview

```
┌──────────────────────────────────────────────────────────────────────┐
│  Chrome Browser (voice.google.com — real authenticated session)      │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │  Chrome Extension (Manifest V3)                               │  │
│  │  ├── background/service-worker.js  → WebSocket client         │  │
│  │  ├── content/gv-bridge.js          → DOM control + fetch hook │  │
│  │  └── offscreen/audio-bridge.js     → tabCapture + PCM relay   │  │
│  └─────────────────────────────┬──────────────────────────────────┘  │
└────────────────────────────────┼─────────────────────────────────────┘
                                 │ ws://localhost:8765  (localhost only)
         ┌───────────────────────▼─────────────────────────┐
         │  GVBridgeService (.NET, IHostedService)          │
         │  ├── WebSocket server (accepts extension conn)   │
         │  ├── GVBrowserAdapter (ICallAdapter impl)        │
         │  ├── AudioBridge (PCM ↔ RTP/G.711 converter)    │
         │  ├── GVSmsService (ISmsProvider impl)            │
         │  └── CallLogService (shared with SIP PRD)        │
         └───────────────────────┬─────────────────────────┘
                                 │
         ┌───────────────────────▼─────────────────────────┐
         │  CallAdapterRegistry                             │
         │  (routes to active ICallAdapter)                 │
         └───────────────────────┬─────────────────────────┘
                                 │
         ┌───────────────────────▼─────────────────────────┐
         │  CallManager (existing, unchanged)               │
         └───────────────────────┬─────────────────────────┘
                                 │ SIP INVITE
         ┌───────────────────────▼─────────────────────────┐
         │  Grandstream HT801 ATA                           │
         └───────────────────────┬─────────────────────────┘
                                 │ Analog FXS
         ┌───────────────────────▼─────────────────────────┐
         │  Rotary Phone                                    │
         └─────────────────────────────────────────────────┘
```

### 3.2 Audio Signal Flow (Full Duplex)

```
INBOUND AUDIO (caller's voice → rotary phone earpiece):
  GV WebRTC session (Chrome tab speaker output)
    → tabCapture API (Chrome extension offscreen doc)
    → PCM frames over WebSocket (16-bit, 16kHz, mono)
    → AudioBridge: resample to 8kHz, encode G.711 µ-law
    → RTP stream → HT801 ATA → rotary phone earpiece

OUTBOUND AUDIO (rotary phone microphone → caller):
  Rotary phone mic → HT801 ATA
    → RTP stream → AudioBridge: decode G.711 µ-law, upsample to 16kHz
    → PCM frames over WebSocket
    → Chrome extension offscreen doc: inject as MediaStream track
    → GV WebRTC session uses injected track as microphone input
```

### 3.3 New Solution Projects

#### `RotaryPhoneController.GVBridge` (Razor Class Library)

Same RCL pattern as the SIP trunk PRD — bundles backend services, REST API/SignalR hub (for React), and Razor components (for Blazor).

```
RotaryPhoneController.GVBridge/
├── Adapters/
│   └── GVBrowserAdapter.cs             # ICallAdapter impl — delegates to GVBridgeService
├── Services/
│   ├── GVBridgeService.cs              # WebSocket server, extension lifecycle management
│   ├── AudioBridge.cs                  # PCM ↔ G.711/RTP conversion and relay
│   ├── GVSmsService.cs                 # ISmsProvider impl — receives SMS events from extension
│   └── CallLogService.cs               # Shared with SIP PRD; reuse if in Core, else duplicate
├── Models/
│   ├── GVBridgeConfig.cs               # Config POCO (bound from appsettings)
│   ├── ExtensionMessage.cs             # Discriminated union of all WS message types
│   ├── CallLogEntry.cs                 # (share from Core or duplicate)
│   └── SmsNotification.cs             # (share from Core or duplicate)
├── Interfaces/
│   ├── ICallAdapter.cs                 # Unified adapter interface (see §4.2) — move to Core
│   ├── ICallAdapterRegistry.cs         # Mode switching service interface — move to Core
│   ├── ISmsProvider.cs                 # (share from GVTrunk or move to Core)
│   └── ICallLogService.cs             # (share from GVTrunk or move to Core)
├── Registry/
│   ├── CallAdapterRegistry.cs          # Runtime mode switcher
│   └── CallAdapterMode.cs              # Enum: BluetoothHfp | SipTrunk | GVBrowser
├── Api/
│   ├── GVBridgeController.cs           # REST endpoints — consumed by React
│   └── GVBridgeHub.cs                  # SignalR hub — real-time push to React
├── Components/
│   ├── GVBridgeDashboard.razor         # Top-level Blazor component
│   ├── ConnectionModeSelector.razor    # Mode picker (all three modes) — KEY NEW COMPONENT
│   ├── BridgeStatusPanel.razor         # Extension connected badge, call state, duration
│   ├── CallHistoryTable.razor          # Last 50 call log entries
│   ├── SmsNotificationsPanel.razor     # SMS / missed call feed
│   └── OutboundDialPanel.razor         # E.164 dial input + button
├── Extensions/
│   └── GVBridgeServiceExtensions.cs    # AddGVBridge() / MapGVBridge()
└── RotaryPhoneController.GVBridge.csproj

ChromeExtension/                        # Sibling folder in repo root (not a .NET project)
├── manifest.json
├── background/
│   └── service-worker.js               # WebSocket client, message routing
├── content/
│   └── gv-bridge.js                    # DOM observer, fetch() interceptor, call control
├── offscreen/
│   ├── offscreen.html
│   └── audio-bridge.js                 # tabCapture, PCM relay, MediaStream injection
└── icons/
    └── icon-*.png
```

#### `RotaryPhoneController.Core` — Shared Interfaces (additive)

The following interfaces are promoted to Core so both GVTrunk and GVBridge share the same contracts:

- `ICallAdapter` (replaces / extends `ISipAdapter` as the universal adapter contract)
- `ICallAdapterRegistry`
- `ISmsProvider`
- `ICallLogService`
- `CallLogEntry`, `SmsNotification`, `CallAdapterMode`

> **Implementation note for Claude Code:** If Core already defines `ISipAdapter`, do not break it. Define `ICallAdapter` as a new interface. Have `GVTrunkAdapter` and `GVBrowserAdapter` both implement `ICallAdapter`. Update `CallManager` to depend on `ICallAdapterRegistry` (which returns the currently active `ICallAdapter`) rather than a direct `ISipAdapter`. The existing `SIPSorceryAdapter` (HT801 / BT path) should also be wrapped or made to implement `ICallAdapter` so it participates in the registry.

---

## 4. Detailed Requirements

### 4.1 `GVBridgeConfig` (Configuration POCO)

Bound from `appsettings.json` under the key `"GVBridge"`.

```json
"GVBridge": {
  "WebSocketPort": 8765,
  "WebSocketHost": "127.0.0.1",
  "LocalRtpPort": 5070,
  "LocalIp": "192.168.1.20",
  "HT801Ip": "192.168.1.21",
  "HT801RtpPort": 5004,
  "AudioSampleRateHz": 16000,
  "AudioChannels": 1,
  "PcmFrameMs": 20,
  "ExtensionConnectTimeoutSeconds": 30,
  "CallLogDbPath": "/home/pi/.local/share/rotaryphone/calllog.db",
  "DefaultMode": "GVBrowser"
}
```

### 4.2 `ICallAdapter` Interface (move to Core)

Universal adapter contract implemented by all three call paths:

```csharp
public interface ICallAdapter
{
    CallAdapterMode Mode { get; }
    bool IsAvailable { get; }               // BT: device paired; SIP: registered; GV: extension connected
    event Action<bool> OnAvailabilityChanged;
    event Action<string> OnIncomingCall;    // string = caller number or display name
    event Action OnCallAnswered;
    event Action OnCallEnded;
    event Action<string> OnDtmfReceived;    // stub for Phase 2

    Task ActivateAsync(CancellationToken ct = default);
    Task DeactivateAsync(CancellationToken ct = default);
    Task<string> PlaceCallAsync(string e164Number, CancellationToken ct = default); // returns sessionId
    Task AnswerCallAsync(CancellationToken ct = default);
    Task HangUpAsync(CancellationToken ct = default);
}

public enum CallAdapterMode
{
    BluetoothHfp,
    SipTrunk,
    GVBrowser
}
```

### 4.3 `ICallAdapterRegistry` Interface (move to Core)

```csharp
public interface ICallAdapterRegistry
{
    CallAdapterMode ActiveMode { get; }
    ICallAdapter ActiveAdapter { get; }
    IReadOnlyList<CallAdapterMode> AvailableModes { get; }
    event Action<CallAdapterMode> OnModeChanged;

    Task SwitchModeAsync(CallAdapterMode mode, CancellationToken ct = default);
    void Register(ICallAdapter adapter);
}
```

`CallAdapterRegistry` implementation:
- Holds a dictionary of `CallAdapterMode → ICallAdapter`
- On `SwitchModeAsync`: calls `DeactivateAsync()` on the current adapter, then `ActivateAsync()` on the new one
- Fires `OnModeChanged` after successful switch
- Persists `ActiveMode` to a local state file (path configurable) so the selected mode survives restarts
- Logs all mode switches at Information level via Serilog

### 4.4 `GVBridgeService` (WebSocket Server)

Hosted service (`IHostedService`) that runs the local WebSocket server the Chrome extension connects to.

**Startup:**
- Listens on `ws://GVBridgeConfig.WebSocketHost:GVBridgeConfig.WebSocketPort`
- Binds only to `127.0.0.1` — never exposed to the network
- Accepts exactly one connection at a time (the extension); queues additional connection attempts and rejects them with a `409` close code
- Sets `IsAvailable = false` until extension connects; fires `OnAvailabilityChanged(true)` on connect

**Message protocol:**

All messages are JSON with a `type` discriminator. The full message schema is defined in `ExtensionMessage.cs` as a discriminated union (use `JsonDerivedType` with `System.Text.Json`).

Extension → Bridge (inbound):

```json
{ "type": "connected", "version": "1.0.0" }
{ "type": "incomingCall", "from": "+13365551234", "callId": "gv-abc123" }
{ "type": "callAnswered", "callId": "gv-abc123" }
{ "type": "callEnded",    "callId": "gv-abc123" }
{ "type": "smsReceived",  "from": "+13365551234", "body": "Hello", "threadId": "t-xyz" }
{ "type": "missedCall",   "from": "+13365551234" }
{ "type": "dtmfReceived", "digit": "5" }
{ "type": "audioFrame",   "pcm": "<base64 raw PCM bytes>" }
```

Bridge → Extension (outbound):

```json
{ "type": "dial",        "number": "+13365551234" }
{ "type": "answer" }
{ "type": "hangup" }
{ "type": "sendSms",     "to": "+13365551234", "body": "Hello" }
{ "type": "audioFrame",  "pcm": "<base64 raw PCM bytes>" }
{ "type": "ping" }
```

**Heartbeat:** Bridge sends `ping` every 10s; extension must respond with `{ "type": "pong" }` within 5s or the connection is marked lost and `OnAvailabilityChanged(false)` is fired.

**Error handling:**
- WebSocket exceptions logged at Error level; service auto-restarts listener after 2s
- If extension sends an unrecognized message type, log at Warning and continue
- All message handling wrapped in try/catch; a single bad message must not crash the service

### 4.5 `AudioBridge` Service

Runs as a pair of concurrent background loops once a call is active:

**Inbound loop (GV → HT801):**
1. Dequeue PCM frames arriving from the extension via `GVBridgeService`
2. Resample from 16kHz to 8kHz (linear interpolation sufficient; use NAudio's `WdlResamplingSampleProvider` if available, otherwise a simple decimation filter)
3. Encode to G.711 µ-law (use the standard ITU-T G.711 µ-law encoding table — include as a static lookup in `G711Codec.cs`)
4. Packetize into RTP packets (20ms frames = 160 bytes payload at 8kHz)
5. Send RTP packets via UDP to `HT801Ip:HT801RtpPort`

**Outbound loop (HT801 → GV):**
1. Receive RTP packets on `LocalIp:LocalRtpPort`
2. Extract G.711 µ-law payload; decode to 16-bit PCM at 8kHz
3. Upsample to 16kHz
4. Packetize as PCM frames (20ms = 640 bytes at 16kHz, 16-bit mono)
5. Base64-encode and send as `audioFrame` message to extension via `GVBridgeService`

**Timing:** Both loops must maintain 20ms jitter budget. Use `System.Diagnostics.Stopwatch` to measure actual frame dispatch time and compensate with `Task.Delay` remainder.

**NuGet:** Use `NAudio.Core` for resampling primitives. Do not bring in the full `NAudio` package (Windows-specific); `NAudio.Core` is cross-platform.

**Lifecycle:** `AudioBridge` starts its loops when `GVBrowserAdapter.AnswerCallAsync()` is called and stops them when `HangUpAsync()` is called or `OnCallEnded` fires.

### 4.6 `GVBrowserAdapter` (ICallAdapter Implementation)

Thin adapter that translates `ICallAdapter` method calls into outbound WebSocket messages via `GVBridgeService`, and translates inbound WebSocket events into `ICallAdapter` events.

```csharp
public class GVBrowserAdapter : ICallAdapter
{
    public CallAdapterMode Mode => CallAdapterMode.GVBrowser;
    public bool IsAvailable => _bridgeService.IsExtensionConnected;

    // ActivateAsync: starts GVBridgeService WebSocket listener
    // DeactivateAsync: stops listener, fires OnAvailabilityChanged(false)
    // PlaceCallAsync: sends { type: "dial", number } to extension; returns callId on next incomingCall echo
    // AnswerCallAsync: sends { type: "answer" }; starts AudioBridge
    // HangUpAsync: sends { type: "hangup" }; stops AudioBridge
}
```

### 4.7 `GVSmsService` (ISmsProvider Implementation)

Receives `smsReceived` and `missedCall` messages from the extension via `GVBridgeService` and fires the `ISmsProvider` events.

Also implements `Task SendSmsAsync(string toNumber, string body)` — sends `{ type: "sendSms", to, body }` to the extension (this is fully implemented in Phase 1, not a stub, because the extension can trigger GV's send-SMS flow via DOM).

Maintains an in-memory ring buffer of the last 50 notifications (not persisted). `GetRecentAsync()` returns the buffer in reverse-chronological order.

### 4.8 Chrome Extension — `manifest.json`

Manifest V3. Minimum Chrome version: 116 (required for `offscreen` API).

```json
{
  "manifest_version": 3,
  "name": "RotaryPhone GV Bridge",
  "version": "1.0.0",
  "permissions": [
    "tabCapture",
    "tabs",
    "scripting",
    "offscreen",
    "storage"
  ],
  "host_permissions": [
    "https://voice.google.com/*"
  ],
  "background": {
    "service_worker": "background/service-worker.js",
    "type": "module"
  },
  "content_scripts": [{
    "matches": ["https://voice.google.com/*"],
    "js": ["content/gv-bridge.js"],
    "run_at": "document_idle"
  }]
}
```

### 4.9 Chrome Extension — `background/service-worker.js`

**Responsibilities:**
- Maintains the WebSocket connection to `ws://127.0.0.1:8765`
- Reconnects on disconnect with exponential backoff (1s, 2s, 4s, max 30s)
- Routes inbound WS messages to the content script via `chrome.tabs.sendMessage`
- Routes outbound events from the content script to the WS server
- Manages the offscreen document lifecycle for audio capture

**WebSocket reconnection pattern:**
```javascript
function connect() {
  const ws = new WebSocket('ws://127.0.0.1:8765');
  ws.onopen = () => { backoff = 1000; ws.send(JSON.stringify({ type: 'connected', version: '1.0.0' })); };
  ws.onclose = () => setTimeout(connect, backoff = Math.min(backoff * 2, 30000));
  ws.onmessage = ({ data }) => dispatch(JSON.parse(data));
}
```

**Offscreen document management:**
- Create the offscreen document (`offscreen/offscreen.html`) on the first call that requires audio capture
- Destroy it after the call ends to release the `tabCapture` stream
- Use `chrome.offscreen.createDocument` / `closeDocument` (Chrome 116+)

### 4.10 Chrome Extension — `content/gv-bridge.js`

Injected into `https://voice.google.com/*` at `document_idle`.

**DOM Event Observers (MutationObserver):**

Target ARIA roles and `data-*` attributes rather than class names for stability.

| Event to detect | DOM signal to watch |
|---|---|
| Incoming call | Element with `role="dialog"` containing `aria-label` matching `/incoming call/i` appears |
| Call answered | Incoming call dialog disappears AND a call-duration timer element appears |
| Call ended | Call-duration timer element disappears |
| SMS received | New message element with `role="listitem"` appears in the active thread |

**fetch() interceptor:**

Wrap `window.fetch` to observe GV's internal API traffic:

```javascript
const _fetch = window.fetch;
window.fetch = async (url, opts) => {
  const resp = await _fetch(url, opts);
  if (typeof url === 'string' && url.includes('/voice/v1/voiceclient/conversation')) {
    resp.clone().json().then(data => reportSmsData(data)).catch(() => {});
  }
  return resp;
};
```

This provides SMS thread data without needing to scrape the DOM for message content.

**Call control DOM actions:**

```javascript
function dial(number) {
  // Click the new-call button, type number, click dial
  // Target: button[aria-label="New call"], input[aria-label="Type a number"], button[aria-label="Call"]
  // Use aria-label selectors — more stable than class names
}

function answer() {
  document.querySelector('button[aria-label="Answer"]')?.click();
}

function hangup() {
  document.querySelector('button[aria-label="End call"], button[aria-label="Hang up"]')?.click();
}

function sendSms(to, body) {
  // Navigate to thread, inject text, click Send
  // Target: textarea[aria-label="Message"], button[aria-label="Send message"]
}
```

**Stability guard:** Wrap each DOM action in a retry loop (up to 3 attempts, 200ms apart) with a guard that checks element existence before clicking. Log warnings via `chrome.runtime.sendMessage` if retries exhaust.

### 4.11 Chrome Extension — `offscreen/audio-bridge.js`

Runs in the offscreen document which has access to `tabCapture` and Web Audio APIs.

**Inbound audio (GV tab → bridge):**

```javascript
// Capture the GV tab's audio output
chrome.tabCapture.capture({ audio: true, video: false }, stream => {
  const ctx = new AudioContext({ sampleRate: 16000 });
  const source = ctx.createMediaStreamSource(stream);
  const processor = ctx.createScriptProcessor(320, 1, 1); // 20ms @ 16kHz
  processor.onaudioprocess = e => {
    const pcm = e.inputBuffer.getChannelData(0);
    const i16 = floatTo16BitPCM(pcm);
    sendToBackground({ type: 'audioFrame', pcm: arrayBufferToBase64(i16.buffer) });
  };
  source.connect(processor);
  processor.connect(ctx.destination);
});
```

**Outbound audio (bridge → GV mic input):**

```javascript
// Create a MediaStream from PCM frames received from bridge
const ctx = new AudioContext({ sampleRate: 16000 });
const injectedSource = ctx.createBufferSource(); // updated each frame

// Replace GV's microphone MediaStream track with the injected one
// Obtain GV's RTCPeerConnection by hooking window.RTCPeerConnection.prototype.addTrack
// on the content script side, capture the sender reference, then replaceTrack()
```

> **Implementation note:** The `RTCPeerConnection.addTrack` hook must be injected via a `<script>` tag into the page's main world from the content script (not the isolated world), because `window.RTCPeerConnection` in the isolated world is a different object. Use `chrome.scripting.executeScript` with `world: 'MAIN'` or inject a `<script>` tag via DOM manipulation.

---

## 5. Connection Mode Selector

This is the central new UI feature that ties all three PRDs together.

### 5.1 `CallAdapterRegistry` Behavior

- On startup, loads `ActiveMode` from the persisted state file (defaults to `GVBridgeConfig.DefaultMode` if file absent)
- Calls `ActivateAsync()` on the corresponding adapter immediately
- Exposes `AvailableModes` — all three modes are always listed; `IsAvailable` on each adapter indicates current health
- `SwitchModeAsync` is safe to call at any time; if a call is active it first calls `HangUpAsync()` on the current adapter before switching

### 5.2 Connection Mode Selector — React Component (RotaryPhone)

New component: `ClientApp/src/components/ConnectionModeSelector.tsx`

```
┌──────────────────────────────────────────────────────┐
│  Call Path                                           │
│                                                      │
│  ○ Bluetooth Phone    ● GV Browser    ○ SIP Trunk   │
│                            ↑ active                  │
│                                                      │
│  Status:  🟢 Extension Connected                    │
└──────────────────────────────────────────────────────┘
```

- Radio group bound to `GET /api/adapter/mode` (current mode) and `PUT /api/adapter/mode` (switch)
- Status line shows `ICallAdapter.IsAvailable` for the selected mode
- Disabled during an active call (switching mid-call is blocked by the API returning `409`)
- Receives real-time mode-change and availability events via SignalR (`ModeChanged`, `AvailabilityChanged`)

### 5.3 Connection Mode Selector — Blazor Component (RTest)

New component: `Components/ConnectionModeSelector.razor` (inside `GVBridge` RCL)

Same layout as the React version. Injects `ICallAdapterRegistry` from DI directly. Subscribes to `OnModeChanged` and adapter `OnAvailabilityChanged` events. Calls `SwitchModeAsync` on button click.

### 5.4 New REST Endpoints for Mode Selector

Add to `GVBridgeController`:

| Method | Route | Description |
|---|---|---|
| `GET` | `/api/adapter/mode` | Returns `{ activeMode, modes: [{ mode, isAvailable }] }` |
| `PUT` | `/api/adapter/mode` | Body: `{ "mode": "GVBrowser" }` — calls `SwitchModeAsync` |

Add to `GVBridgeHub` SignalR:

| Event | Payload | Trigger |
|---|---|---|
| `ModeChanged` | `{ activeMode: string }` | `ICallAdapterRegistry.OnModeChanged` |
| `AvailabilityChanged` | `{ mode: string, isAvailable: bool }` | Any adapter's `OnAvailabilityChanged` |

---

## 6. Dashboard UI (Both Hosts)

Dashboard panels mirror the SIP trunk PRD exactly, with the mode selector prepended and the status panel updated for the browser adapter.

### 6.1 REST Endpoints (GVBridgeController)

Base route: `/api/gvbridge`

| Method | Route | Description |
|---|---|---|
| `GET` | `/status` | `{ extensionConnected, callState, activeCallDurationSeconds, activeMode }` |
| `GET` | `/calls` | Last 50 `CallLogEntry` records |
| `GET` | `/sms` | Last 20 `SmsNotification` records |
| `POST` | `/dial` | Body: `{ "number": "+13365551234" }` |
| `POST` | `/sms/send` | Body: `{ "to": "+13365551234", "body": "Hello" }` |
| `PUT` | `/adapter/mode` | Body: `{ "mode": "GVBrowser" }` |
| `GET` | `/adapter/mode` | Returns current mode and availability for all adapters |

### 6.2 SignalR Hub Events (GVBridgeHub)

Mount at `/hubs/gvbridge`.

| Event | Payload | Trigger |
|---|---|---|
| `ExtensionConnected` | `{ version: string }` | Extension WS connects |
| `ExtensionDisconnected` | — | Extension WS drops |
| `CallStateChanged` | `{ state, durationSeconds }` | `CallManager.StateChanged` |
| `SmsReceived` | `SmsNotification` | `GVSmsService.OnSmsReceived` |
| `MissedCallReceived` | `SmsNotification` | `GVSmsService.OnMissedCallReceived` |
| `ModeChanged` | `{ activeMode }` | `CallAdapterRegistry.OnModeChanged` |
| `AvailabilityChanged` | `{ mode, isAvailable }` | Any adapter's `OnAvailabilityChanged` |

### 6.3 Blazor Components

**`GVBridgeDashboard.razor`** — top-level mount point:

```
<ConnectionModeSelector />      ← NEW — always visible at top
<BridgeStatusPanel />           ← extension status, call state, duration
<CallHistoryTable />
<SmsNotificationsPanel />
<OutboundDialPanel />
```

**`BridgeStatusPanel`:**
- Extension connection badge: `Connected` (green) / `Disconnected` (red) — bound to `GVBridgeService.IsExtensionConnected`
- Current call state and duration (same as SIP trunk PRD)
- No "Force Re-Register" button (not applicable); instead: "Open GV in Chrome" button (opens `https://voice.google.com` in default browser via `Process.Start`)

**All other components** (`CallHistoryTable`, `SmsNotificationsPanel`, `OutboundDialPanel`) — identical spec to SIP trunk PRD §4.8, substituting `GVSmsService` for `GmailSmsService` and `GVBrowserAdapter.PlaceCallAsync` for `GVTrunkAdapter.PlaceOutboundCall`.

**`OutboundDialPanel`** additionally: disabled when extension is disconnected OR active mode is not `GVBrowser`.

### 6.4 React Components (RotaryPhone)

New files under `ClientApp/src/components/gvbridge/`:

```
GVBridgeDashboard.tsx
ConnectionModeSelector.tsx      ← NEW
BridgeStatusPanel.tsx
CallHistoryTable.tsx
SmsNotificationsPanel.tsx
OutboundDialPanel.tsx
```

`useGVBridge.ts` hook (mirrors `useGVTrunk.ts` from the SIP PRD):
- Fetches `/api/gvbridge/status`, `/calls`, `/sms`, `/adapter/mode` on mount
- Opens SignalR connection to `/hubs/gvbridge`
- Exposes `dial(number)`, `sendSms(to, body)`, `switchMode(mode)` methods
- Returns `{ extensionConnected, callState, recentCalls, recentSms, activeMode, adapterModes }`

---

## 7. NuGet Dependencies (New Project Only)

| Package | Purpose |
|---|---|
| `NAudio.Core` | Cross-platform PCM resampling primitives |
| `Microsoft.AspNetCore.SignalR` | SignalR hub |
| `Microsoft.Data.Sqlite` | Call log persistence (if not shared from Core) |
| `Microsoft.Extensions.Hosting.Abstractions` | `IHostedService` |
| `Microsoft.Extensions.Options` | `IOptions<GVBridgeConfig>` binding |
| `Serilog` | Logging |
| `System.Net.WebSockets` | WebSocket server (built into .NET 8, no extra package) |

---

## 8. DI Registration — Extension Methods

`GVBridgeServiceExtensions.cs`:

### `AddGVBridge()` — both hosts

```csharp
public static IServiceCollection AddGVBridge(
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.Configure<GVBridgeConfig>(configuration.GetSection("GVBridge"));

    // Core services
    services.AddSingleton<GVBridgeService>();
    services.AddSingleton<AudioBridge>();
    services.AddSingleton<GVBrowserAdapter>();
    services.AddSingleton<ICallAdapter>(sp => sp.GetRequiredService<GVBrowserAdapter>());
    services.AddSingleton<GVSmsService>();
    services.AddSingleton<ISmsProvider>(sp => sp.GetRequiredService<GVSmsService>());
    services.AddSingleton<ICallLogService, CallLogService>();

    // Registry — registers all available adapters
    services.AddSingleton<ICallAdapterRegistry, CallAdapterRegistry>();
    services.AddHostedService<GVBridgeService>();

    services.AddSignalR();
    services.AddControllers();
    return services;
}
```

### `MapGVBridge()` — both hosts

```csharp
public static IEndpointRouteBuilder MapGVBridge(this IEndpointRouteBuilder endpoints)
{
    endpoints.MapControllers();
    endpoints.MapHub<GVBridgeHub>("/hubs/gvbridge");
    return endpoints;
}
```

### Usage in `RotaryPhone` (`Program.cs`)

```csharp
builder.Services.AddGVBridge(builder.Configuration);
// ...
app.MapGVBridge();
```

### Usage in `RTest` (`Program.cs`)

```csharp
builder.Services.AddGVBridge(builder.Configuration);
// ...
app.MapGVBridge();
```

---

## 9. CallManager Integration (Additive Only)

`CallManager` is updated minimally to depend on `ICallAdapterRegistry` instead of a direct adapter reference:

```csharp
// Before (existing):
public class CallManager(ISipAdapter adapter) { ... }

// After (updated):
public class CallManager(ICallAdapterRegistry registry) {
    private ICallAdapter Adapter => registry.ActiveAdapter;
}
```

All existing `CallManager` logic remains unchanged. The only difference is that `Adapter` is now a dynamic property resolved through the registry rather than a constructor-injected singleton. Subscribe to `registry.OnModeChanged` in `CallManager` to re-bind adapter events when the mode changes.

---

## 10. RotaryPhone Host Integration (React)

Additive changes only to the `RotaryPhone` project.

### `Program.cs`

```csharp
builder.Services.AddGVBridge(builder.Configuration);
// ...
app.MapGVBridge();
```

### `appsettings.json`

Add the `"GVBridge"` config block from §4.1.

### `App.tsx`

```tsx
import { GVBridgeDashboard } from './components/gvbridge/GVBridgeDashboard';
// ...
<Route path="/gvbridge" element={<GVBridgeDashboard />} />
```

### Nav

Add a link to `/gvbridge` — "GV Bridge" — in the existing navigation component.

### Chrome Extension delivery

The built extension lives at `ChromeExtension/` in the repo. It is not automatically installed — the user loads it via `chrome://extensions` → "Load unpacked". Add a one-time setup note to `README.md`.

---

## 11. RTest Integration Instructions

Additive changes only.

### Step 1 — Add project reference

In `RTest.csproj`:

```xml
<ProjectReference Include="..\RotaryPhoneController.GVBridge\RotaryPhoneController.GVBridge.csproj" />
```

### Step 2 — Register services and endpoints

In `RTest/Program.cs`:

```csharp
builder.Services.AddGVBridge(builder.Configuration);
// ...
app.MapGVBridge();
```

### Step 3 — Add `appsettings.json` config block

Add the `"GVBridge"` config block from §4.1 to `RTest/appsettings.json`. Adjust `CallLogDbPath` for the RTest environment.

### Step 4 — Add the Razor page

Create `RTest/Pages/GVBridge.razor`:

```razor
@page "/gvbridge"
@using RotaryPhoneController.GVBridge.Components

<PageTitle>GV Bridge</PageTitle>

<GVBridgeDashboard />
```

All UI logic — including the `ConnectionModeSelector` — lives inside the RCL component tree. No additional Razor code required in `RTest`.

### Step 5 — Add nav link (optional)

In `RTest/Shared/NavMenu.razor`:

```razor
<NavLink href="gvbridge">
    <span class="oi oi-wifi" aria-hidden="true"></span> GV Bridge
</NavLink>
```

### Step 6 — Chrome Extension

Same as RotaryPhone: the extension is loaded manually via `chrome://extensions`. The WebSocket server started by `GVBridgeService` is the same process regardless of which host is running.

### What RTest does NOT need

- No `@microsoft/signalr` npm package — Blazor components use DI-injected services directly
- No REST polling — all state arrives through service events
- No audio or WebSocket code — that lives entirely in `GVBridgeService`

---

## 12. Chrome Extension — Installation & Development Notes

### Loading the extension

1. Open `chrome://extensions`
2. Enable "Developer mode" (top-right toggle)
3. Click "Load unpacked" → select the `ChromeExtension/` folder from the repo
4. Pin the extension to the toolbar for easy status visibility

### Extension update workflow

When `content/gv-bridge.js` or `background/service-worker.js` is modified:
1. Go to `chrome://extensions`
2. Click the refresh icon on the RotaryPhone GV Bridge card
3. Reload `voice.google.com`

### Selector stability strategy

GV's DOM uses obfuscated class names that change with each deployment. All selectors in `gv-bridge.js` must use:
- `aria-label` attributes (e.g., `button[aria-label="Answer"]`)
- `role` attributes (e.g., `[role="dialog"]`)
- `data-*` attributes where present

Never use CSS class selectors (`.abc123`). If a selector stops working, update only the `SELECTORS` constant object at the top of `gv-bridge.js` — all DOM queries reference this object.

```javascript
// gv-bridge.js — top of file
const SELECTORS = {
  newCallButton:    'button[aria-label="New call"]',
  numberInput:      'input[aria-label="Type a number"]',
  dialButton:       'button[aria-label="Call"]',
  answerButton:     'button[aria-label="Answer"]',
  hangupButton:     'button[aria-label="End call"], button[aria-label="Hang up"]',
  incomingDialog:   '[role="dialog"]',
  messageInput:     'textarea[aria-label="Message"]',
  sendButton:       'button[aria-label="Send message"]',
  callDurationTimer: '[data-call-duration], [aria-label*="call duration"]',
};
```

### Known breakage risk

Google updates `voice.google.com` frequently. The most likely breakage points in order of risk:

1. **ARIA labels change** (medium risk) → update `SELECTORS` constant, 5-minute fix
2. **Internal fetch URL path changes** (low risk) → update the URL substring in the fetch interceptor
3. **WebRTC `addTrack` hook breaks** (low risk) → test outbound audio after each GV update
4. **`tabCapture` behavior changes** (very low risk) → Chrome API; changes are versioned and announced

Recommend a monthly smoke test: place a test call through the rotary phone and verify full-duplex audio and SMS in both directions.

---

## 13. Future Hooks (Phase 2 — IVR / Automation)

- `ICallAdapter.OnDtmfReceived` — content script intercepts digit clicks on GV's keypad and reports them; fully wired from extension to service, consumed by IVR logic in Phase 2
- `CallLogService` schema includes nullable `Notes` column — reserved for IVR transcripts
- `PlaceCallAsync` returns a `sessionId` — reserved for mid-call control (transfer, hold via GV UI automation)
- `GVSmsService.SendSmsAsync` — already fully implemented (not a stub) because the extension can drive the compose UI
- `GVBridgeService` WebSocket protocol version field — reserved for future extension protocol upgrades with backward compatibility negotiation

---

## 14. Testing Guidance for Claude Code

### Unit tests (`RotaryPhoneController.GVBridge.Tests/`)

1. **`GVBridgeServiceTests`**
   - Mock WebSocket client; verify `IsExtensionConnected` flips on connect/disconnect
   - Verify `OnAvailabilityChanged` fires correctly
   - Verify heartbeat timeout triggers disconnect after 15s with no pong

2. **`AudioBridgeTests`**
   - Verify G.711 µ-law encode/decode round-trip produces ≤ 1% RMS error against reference samples
   - Verify 16kHz → 8kHz downsample produces expected sample count
   - Verify 8kHz → 16kHz upsample produces expected sample count
   - Use pre-recorded PCM fixtures as test inputs

3. **`GVSmsServiceTests`**
   - Simulate inbound `smsReceived` and `missedCall` WS messages
   - Verify `OnSmsReceived` / `OnMissedCallReceived` events fire with correct data
   - Verify ring buffer caps at 50 entries

4. **`CallAdapterRegistryTests`**
   - Mock two `ICallAdapter` implementations
   - Verify `ActivateAsync` / `DeactivateAsync` called correctly on mode switch
   - Verify `OnModeChanged` fires after successful switch
   - Verify switch during active call calls `HangUpAsync` first

5. **`GVBrowserAdapterTests`**
   - Verify `PlaceCallAsync` sends correct WS message
   - Verify `AnswerCallAsync` starts `AudioBridge`
   - Verify `HangUpAsync` stops `AudioBridge` and sends correct WS message

### Integration tests

- **`AudioBridgeIntegrationTest`**: loopback test — bridge PCM output back to its own input, verify round-trip latency < 100ms
- **`WebSocketServerIntegrationTest`**: real WebSocket client connects to the bridge server; verify connect/disconnect/ping-pong flow without using mocks

### Manual verification checklist (extension)

Because the extension cannot be unit tested in .NET:

- [ ] Extension connects to bridge server on startup (badge shows "Connected")
- [ ] Incoming call: GV dialog appears → `incomingCall` WS message received by server
- [ ] Answer: `answer` message sent → GV UI answers call
- [ ] Audio inbound: speech through GV heard on rotary phone earpiece
- [ ] Audio outbound: speech into rotary phone mic heard on remote caller
- [ ] Hang up: `hangup` message sent → GV ends call
- [ ] SMS inbound: GV receives SMS → appears in dashboard within poll interval
- [ ] SMS outbound: dial panel sends SMS → message appears in GV thread

---

## 15. File / Folder Placement in Repository

### GVBridge component (self-contained RCL)

```
RotaryPhoneController.GVBridge/
├── Adapters/
│   └── GVBrowserAdapter.cs
├── Services/
│   ├── GVBridgeService.cs
│   ├── AudioBridge.cs
│   ├── GVSmsService.cs
│   └── CallLogService.cs
├── Models/
│   ├── GVBridgeConfig.cs
│   ├── ExtensionMessage.cs
│   ├── CallLogEntry.cs
│   └── SmsNotification.cs
├── Interfaces/
│   ├── ICallAdapter.cs
│   ├── ICallAdapterRegistry.cs
│   ├── ISmsProvider.cs
│   └── ICallLogService.cs
├── Registry/
│   ├── CallAdapterRegistry.cs
│   └── CallAdapterMode.cs
├── Api/
│   ├── GVBridgeController.cs
│   └── GVBridgeHub.cs
├── Components/
│   ├── GVBridgeDashboard.razor
│   ├── ConnectionModeSelector.razor
│   ├── BridgeStatusPanel.razor
│   ├── CallHistoryTable.razor
│   ├── SmsNotificationsPanel.razor
│   └── OutboundDialPanel.razor
├── Extensions/
│   └── GVBridgeServiceExtensions.cs
└── RotaryPhoneController.GVBridge.csproj

ChromeExtension/
├── manifest.json
├── background/
│   └── service-worker.js
├── content/
│   └── gv-bridge.js
├── offscreen/
│   ├── offscreen.html
│   └── audio-bridge.js
└── icons/
    └── icon-*.png
```

### `RotaryPhoneController.Core` — additive changes only

```
RotaryPhoneController.Core/
└── Interfaces/
    ├── ICallAdapter.cs           # Promoted from GVBridge
    ├── ICallAdapterRegistry.cs   # Promoted from GVBridge
    ├── ISmsProvider.cs           # Promoted from GVTrunk
    └── ICallLogService.cs        # Promoted from GVTrunk
```

### `RotaryPhone` — React host (additive only)

```
RotaryPhone/
├── Program.cs                              # + AddGVBridge / MapGVBridge
└── ClientApp/src/
    ├── hooks/
    │   └── useGVBridge.ts                  # NEW
    └── components/
        ├── ConnectionModeSelector.tsx       # NEW — shared across all mode UIs
        └── gvbridge/
            ├── GVBridgeDashboard.tsx
            ├── BridgeStatusPanel.tsx
            ├── CallHistoryTable.tsx
            ├── SmsNotificationsPanel.tsx
            └── OutboundDialPanel.tsx
```

### `RTest` — Blazor host (additive only)

```
RTest/
├── Program.cs                              # + AddGVBridge / MapGVBridge
└── Pages/
    └── GVBridge.razor                      # NEW — mounts <GVBridgeDashboard />
```

### Tests

```
RotaryPhoneController.GVBridge.Tests/
└── RotaryPhoneController.GVBridge.Tests.csproj
```

---

## 16. Acceptance Criteria

| # | Criterion |
|---|---|
| AC-1 | Chrome extension connects to bridge WebSocket server; `BridgeStatusPanel` shows "Connected" within 5s of GV tab load |
| AC-2 | Inbound GV call: rotary phone rings via HT801 within 3s of GV UI showing incoming call dialog |
| AC-3 | Lifting handset answers the GV call; full-duplex audio established within 2s |
| AC-4 | Caller's voice is audible on rotary phone earpiece with no clipping or dropout |
| AC-5 | Rotary phone microphone audio is received clearly by the remote caller |
| AC-6 | Outbound dial from rotary phone (or dashboard) initiates a call on `voice.google.com` |
| AC-7 | Outbound caller ID shown to remote party matches the Google Voice number |
| AC-8 | Inbound GV SMS appears in both host dashboards within 2× heartbeat interval of receipt |
| AC-9 | SMS composed in OutboundDialPanel is sent and appears in `voice.google.com` thread |
| AC-10 | `ConnectionModeSelector` switches between BT, SIP Trunk, and GV Browser modes; active mode persists across restart |
| AC-11 | Mode switch is blocked (returns 409) when a call is active |
| AC-12 | React dashboard at `/gvbridge` in `RotaryPhone` reflects real-time state via SignalR |
| AC-13 | Blazor dashboard at `/gvbridge` in `RTest` reflects real-time state via DI events |
| AC-14 | Both hosts integrate by adding ≤ 5 lines to `Program.cs` and one new page file |
| AC-15 | Existing BT HFP and SIP trunk paths remain fully functional when GV Browser mode is not active |
| AC-16 | All unit tests pass on `dotnet test` |
| AC-17 | Application starts cleanly on Raspberry Pi (ARM64, .NET 8) |
| AC-18 | Extension loads on Chrome 116+ with no console errors on a freshly loaded `voice.google.com` |
