# GV Audio Bridge & SIP Diagnostics Design

**Date:** 2026-03-22
**Status:** Approved

## Overview

Two workstreams that enable end-to-end Google Voice calls through the rotary phone and provide deep visibility into SIP/HT801 connectivity issues.

**Workstream 1 — GV Audio Bridge:** Bridges audio between the Chrome extension's WebRTC capture (via tabCapture) and the HT801 ATA (via RTP), with PCM↔G.711 transcoding and sample rate conversion.

**Workstream 2 — SIP Diagnostics:** Real-time instrumentation of SIP signaling, HT801 health, RTP audio stats, and call state timeline. Exposed via SignalR + REST API, consumed by a React diagnostics page (and later by Radio.Web Blazor UI).

## Context

### What works today
- GV Bridge Chrome extension connects to `GVBridgeService` via WebSocket (ws://127.0.0.1:8765)
- Content script on voice.google.com detects incoming calls, answers, hangs up, dials
- `GVBrowserAdapter` implements `ICallAdapter` — signaling flows correctly
- `CallManager` handles call state machine and routes events between adapters
- `SIPSorceryAdapter` sends SIP INVITEs to ring the HT801
- `RtpAudioBridge` handles bidirectional G.711 RTP audio for Bluetooth calls

### What's missing
- **Audio:** `GVBridgeService.InboundAudioQueue` is populated but never consumed. No RTP bridge starts when GV calls connect. No outbound audio path (rotary phone → GV caller).
- **Diagnostics:** SIP message flow is opaque. INVITE failures are silent. HT801 registration status requires manual API calls. No real-time visibility into what's happening during call setup.

### Chronic pain point
INVITE/ringing failures have been the most persistent debugging challenge. The phone doesn't ring and there's no way to tell why — wrong extension, codec mismatch, registration lapse, network issue, or SDP problem.

## Workstream 1: GV Audio Bridge

### Architecture

```
Chrome Extension                    .NET Service                    HT801 / Phone
┌──────────────────┐    WS:8765    ┌──────────────────┐   RTP:5070  ┌──────────────┐
│ voice.google.com │               │ GVBridgeService  │             │ RTP endpoint │
│   (WebRTC audio) │               │ InboundAudioQueue│             │ 192.168.86.250│
│        ↓         │               │        ↓         │             │      ↕       │
│ offscreen doc    │──audioFrame──→│ GVAudioBridge    │──G.711 RTP─→│ FXS port     │
│ tabCapture→PCM   │               │ (NEW)            │             │      ↕       │
│ PCM→AudioContext │←─audioFrame──│ resample+transcode│←─G.711 RTP─│ Rotary Phone │
└──────────────────┘               └──────────────────┘             └──────────────┘
```

### New files

#### `GVBridge/Services/GVAudioBridgeService.cs`

Core audio bridge service. Owns a dedicated SIPSorcery `RTPSession` on port 5070 (from `GVBridgeConfig.LocalRtpPort`).

**Public API:**
- `StartAsync()` — creates RTP session using `GVBridgeConfig.HT801Ip` and `GVBridgeConfig.HT801RtpPort` as the remote endpoint, starts inbound/outbound loops
- `StopAsync()` — tears down RTP session, cancels loops
- `bool IsActive { get; }` — bridge running state

**Inbound loop (GV caller → rotary phone):**
1. Dequeue PCM frames from `GVBridgeService.InboundAudioQueue`
2. Decode base64 → raw PCM bytes (16-bit, 16kHz mono)
3. Resample 16kHz → 8kHz
4. Encode PCM → G.711 µ-law
5. Send via RTP to HT801 endpoint

**Outbound loop (rotary phone → GV caller):**
1. Receive RTP packets from HT801 (via `RTPSession.OnRtpPacketReceived`)
2. Decode G.711 µ-law → PCM (8kHz)
3. Resample 8kHz → 16kHz
4. Encode raw PCM → base64
5. Send as `audioFrame` message via `GVBridgeService.SendAudioFrameAsync()`

**RTP session setup:**
- Codec: G.711 PCMU (payload type 0), matching HT801
- Local port: `GVBridgeConfig.LocalRtpPort` (5070)
- Remote endpoint: `GVBridgeConfig.HT801Ip`:`GVBridgeConfig.HT801RtpPort` (192.168.86.250:5004) — statically configured, not from SDP negotiation
- Uses SIPSorcery `RTPSession` directly (not `RtpAudioBridge`, which is coupled to NAudio/Bluetooth)

**SDP port alignment:** `SIPSorceryAdapter.SendInviteToHT801()` currently hardcodes the local RTP port as 49000 in its SDP. When operating in GVBrowser mode, this must be updated to advertise `GVBridgeConfig.LocalRtpPort` (5070) instead, so the HT801 sends its RTP audio to the port `GVAudioBridgeService` is actually listening on. This requires `SIPSorceryAdapter` to accept a configurable RTP port parameter for the SDP.

**Diagnostics hooks:**
- Tracks packets sent/received per second
- Tracks queue depth of `InboundAudioQueue`
- Exposes `OnStatsUpdate` event for real-time monitoring

#### `GVBridge/Audio/AudioResampler.cs`

Simple linear interpolation resampler for 16kHz↔8kHz conversion.

**Methods:**
- `byte[] Resample16kTo8k(byte[] pcm16bit)` — downsample by dropping every other sample with linear interpolation
- `byte[] Resample8kTo16k(byte[] pcm16bit)` — upsample with linear interpolation

This is intentionally simple. G.711 telephony audio doesn't benefit from high-quality resampling algorithms.

### Modified files

#### `GVBrowserAdapter.cs`

**Constructor change:** Add `GVAudioBridgeService` parameter alongside existing `GVBridgeService` and `ILogger<GVBrowserAdapter>`:

```csharp
public GVBrowserAdapter(GVBridgeService bridgeService, GVAudioBridgeService audioBridge, ILogger<GVBrowserAdapter> logger)
```

In `ActivateAsync()`, **chain** audio lifecycle calls inside the existing event lambdas (do not replace them — the existing event relay to CallManager must continue to fire):

```csharp
// Existing: _bridgeService.OnCallAnswered += msg => OnCallAnswered?.Invoke();
// Updated: chain audio bridge start
_bridgeService.OnCallAnswered += msg => {
    OnCallAnswered?.Invoke();
    _ = _audioBridge.StartAsync();
};

// Existing: _bridgeService.OnCallEnded += msg => { ... OnCallEnded?.Invoke(); };
// Updated: chain audio bridge stop
_bridgeService.OnCallEnded += msg => {
    _activeCallId = null;
    OnCallEnded?.Invoke();
    _ = _audioBridge.StopAsync();
};
```

#### `GVBridgeServiceExtensions.cs`

Register `GVAudioBridgeService` as singleton + hosted service.

### Chrome extension changes

#### `offscreen/audio-bridge.js`

Implement the audio capture/playback pipeline:

**Capture (GV → bridge):**
1. Receive `startCapture` message from content script with `streamId` from `tabCapture`
2. `navigator.mediaDevices.getUserMedia({ audio: { mandatory: { chromeMediaSource: 'tab', chromeMediaSourceId: streamId } } })`
3. Create `AudioContext` at default sample rate (typically 48kHz)
4. Connect source to an `AudioWorkletNode` (preferred in MV3; `ScriptProcessorNode` is deprecated)
5. In the AudioWorklet processor: downsample from context sample rate to 16kHz (average every N samples where N = contextRate/16000), convert stereo to mono, output Int16 PCM
6. Chunk into 20ms frames (320 samples × 2 bytes = 640 bytes at 16kHz), base64 encode each frame
7. Send `{ type: 'audioFrame', pcm: '<base64>' }` to content script via `chrome.runtime.sendMessage`

**Playback (bridge → GV):**
1. Receive `audioFrame` messages from content script
2. Decode base64 → Float32Array
3. Feed into `AudioContext` via `AudioBufferSourceNode` with scheduling for gapless playback

**Lifecycle:**
- Created by service worker via `chrome.offscreen.createDocument({ url: 'offscreen/offscreen.html', reasons: ['USER_MEDIA'], justification: 'Capture tab audio for GV Bridge call' })`
- `offscreen/offscreen.html` loads `audio-bridge.js` via `<script>` tag
- Stays alive while audio capture is active (USER_MEDIA reason persists while MediaStream is active)
- Destroyed when call ends

#### `content/gv-bridge.js`

Add relay logic for audio frames:
- When `callAnswered`, request `tabCapture` stream ID from service worker, forward to offscreen document
- Relay `audioFrame` messages between WebSocket and offscreen document
- When `callEnded`, tell offscreen document to stop capture

#### `background/service-worker.js`

Add `tabCapture` initiation:
- On receiving `startCapture` from content script, call `chrome.tabCapture.getMediaStreamId({ targetTabId })`
- Return stream ID to content script
- Create offscreen document if not already present

### Audio format pipeline

```
Browser capture:  Float32 48kHz stereo → downsample → Int16 16kHz mono → base64
WebSocket:        base64 PCM frames (20ms = 640 bytes per frame at 16kHz)
GVAudioBridge:    base64 → Int16 16kHz → resample → Int16 8kHz → G.711 µ-law
RTP to HT801:    G.711 PCMU, 8kHz, 20ms frames (160 bytes payload)
```

Reverse path mirrors this exactly.

## Workstream 2: SIP Diagnostics Instrumentation

### New files

#### `Core/Diagnostics/SipDiagnosticService.cs`

Central diagnostics aggregator. Implements `IHostedService`.

**Responsibilities:**
- Subscribes to `SIPSorceryAdapter` SIP message events
- Maintains ring buffer of last 200 `SipMessageEntry` records
- Tracks HT801 registration state (registered, expiry countdown, last seen)
- Generates diagnostic annotations for failure patterns:
  - "INVITE sent but no 100 Trying within 2s — device may be unreachable"
  - "INVITE sent but no 180 Ringing within 5s — check extension/codec"
  - "REGISTER not received for 1800s — registration may have lapsed"
  - "200 OK received but no ACK sent — SIP dialog incomplete"
- Tracks call timeline (ordered events per call)
- Periodic HT801 health ping (every 30s)

**Events exposed:**
- `OnSipMessageLogged(SipMessageEntry)` — every SIP message
- `OnDiagnosisGenerated(string issue, string[] suggestions)` — actionable alerts
- `OnHt801HealthUpdate(Ht801HealthStatus)` — registration, ping, hook state
- `OnCallTimelineEvent(CallTimelineEntry)` — call state transitions

**HT801 config comparison:**
- `Task<List<ConfigParameter>> CompareHt801ConfigAsync()` — reads P-values from device via `HT801ApiClient`, compares each against expected values from `RotaryPhoneConfig`
- Returns list of `ConfigParameter { Name, PCode, Expected, Actual, IsMatch }`
- Parameters checked: SIP Server (P47), Extension (P35), Auth ID (P36), SIP Port (P40), Transport (P2912), Registration (P72), Codec (P58)

#### `Core/Diagnostics/SipMessageEntry.cs`

```csharp
public record SipMessageEntry(
    DateTime Timestamp,
    SipDirection Direction,     // Sent, Received
    string Method,              // INVITE, REGISTER, NOTIFY, BYE, CANCEL, ACK, OPTIONS
    string FromAddress,
    string ToAddress,
    int? StatusCode,            // null for requests, 100/180/200/408/etc for responses
    string? StatusText,
    string? DiagnosticNote,     // populated by SipDiagnosticService
    string? CallId              // SIP Call-ID for correlation
);
```

#### `Core/Diagnostics/CallTimelineEntry.cs`

```csharp
public record CallTimelineEntry(
    DateTime Timestamp,
    string EventType,           // IncomingCall, InviteSent, Ringing, Answered, AudioStarted, Ended, Error
    string Description,
    Dictionary<string, string>? Metadata  // e.g., { "from": "+15551234567", "rtpPort": "49152" }
);
```

#### `Core/Diagnostics/Ht801HealthStatus.cs`

```csharp
public record Ht801HealthStatus(
    bool IsReachable,
    double? PingMs,
    bool IsRegistered,
    int? RegistrationExpiresIn,
    DateTime? LastRegisterReceived,
    string? HookState,
    string? FirmwareVersion
);
```

#### `Core/Diagnostics/ConfigParameter.cs`

```csharp
public record ConfigParameter(
    string Name,
    string PCode,
    string Expected,
    string? Actual,
    bool IsMatch
);
```

#### `Server/Controllers/DiagnosticsController.cs`

REST API endpoints:
- `GET /api/diagnostics/status` — full snapshot: HT801 health, SIP state, GV bridge status, call state, RTP stats
- `GET /api/diagnostics/sip-log?count=50&method=INVITE` — recent SIP messages with optional filtering
- `POST /api/diagnostics/test-ring` — sends a test INVITE to the HT801 to verify ringing works, returns the SIP response sequence
- `POST /api/diagnostics/test-audio` — sends a short RTP test tone (1kHz sine, 2 seconds) to verify audio path
- `GET /api/diagnostics/ht801/config` — returns `List<ConfigParameter>` comparison
- `POST /api/diagnostics/ht801/validate` — runs validation with optional auto-fix (`?autoFix=true`)

### Modified files

#### `SIPSorceryAdapter.cs`

Add SIP message event hooks. For every SIP message sent or received, emit an event with method, addresses, status code, and Call-ID.

New events:
- `event Action<SipMessageEntry> OnSipMessageLogged`

Instrument these existing methods:
- `OnSIPRequestReceived()` — emit for REGISTER, NOTIFY, INVITE, BYE, OPTIONS
- `SendInviteToHT801()` — emit for outgoing INVITE and each response (100, 180, 200, 408)
- `CancelPendingInvite()` — emit for CANCEL/BYE
- Response handlers — emit for every SIP response received

Track INVITE transaction state for diagnostic annotations:
- Record timestamp when INVITE is sent
- Track received responses (100 Trying, 180 Ringing, 200 OK)
- If no 100 Trying within 2s, flag
- If no 180 Ringing within 5s, flag

Update `SendInviteToHT801()` to accept an optional `localRtpPort` parameter (defaulting to 49000 for Bluetooth mode). When `CallManager` is using `GVBrowser` adapter mode, pass `GVBridgeConfig.LocalRtpPort` (5070) so the SDP advertises the correct port for `GVAudioBridgeService`.

#### `SignalRNotifierService.cs`

Subscribe to `SipDiagnosticService` events and broadcast to SignalR clients via `IHubContext<RotaryHub>` (consistent with existing broadcasting pattern — all server-push goes through `SignalRNotifierService`, not through Hub instance methods):

- `SipMessageLogged` → `Clients.All.SendAsync("SipMessage", entry)`
- `DiagnosisGenerated` → `Clients.All.SendAsync("SipDiagnosis", issue, suggestions)`
- `Ht801HealthUpdate` → `Clients.All.SendAsync("Ht801Health", status)`
- `RtpStatsUpdate` → `Clients.All.SendAsync("RtpStats", stats)`
- `CallTimelineEvent` → `Clients.All.SendAsync("CallTimeline", entry)`

Note: `RotaryHub.cs` does **not** need new methods. The existing pattern uses `IHubContext<RotaryHub>` for all server→client broadcasts.

#### `HT801ConfigService.cs`

Add method:
- `Task<List<ConfigParameter>> CompareConfigAsync(string phoneId)` — thin wrapper over the existing `ValidateDeviceAsync(phoneId, autoFix: false)`. Maps each `HT801ValidationItem` from the result to a `ConfigParameter` record. This avoids duplicating the P-value fetch and comparison logic.

### React UI

#### New route: `/diagnostics`

New page component `DiagnosticsPage` with sub-components:

**StatusBar** — four status cards across the top (HT801, SIP Server, GV Bridge, Call State). Color-coded borders: green=healthy, yellow=warning, red=error.

**SipMessageLog** — scrolling log of SIP messages. Filterable by method type (INVITE, REGISTER, NOTIFY, BYE). Each entry shows timestamp, direction arrow, method, addresses, response code. Failed transactions highlighted in red with diagnostic annotations below.

**Ht801HealthPanel** — device health summary (registration, ping, hook state, firmware). Action buttons: Test Ring, Validate Config, Read Config. Expandable config validation section showing parameter comparison table with expected vs actual values and "Fix All Mismatches" button.

**RtpStatsPanel** — live during active calls. Packets/s sent and received, jitter, packet loss percentage, audio levels, codec info, WebSocket↔RTP frame count comparison.

**CallTimeline** — chronological event log for the current/most recent call. Shows full lifecycle from incoming detection through INVITE, ringing, answer, audio start, and hangup.

All panels update in real-time via SignalR subscription.

## Design decisions

**Dedicated RTP session vs reusing RtpAudioBridge:** New dedicated `GVAudioBridgeService` rather than extending `RtpAudioBridge`. The existing bridge is tightly coupled to NAudio WaveIn/WaveOut and Bluetooth audio routing. The GV bridge has a fundamentally different audio source (WebSocket queue) and doesn't need NAudio. Clean separation avoids coupling.

**Content script WebSocket vs service worker:** WebSocket lives in the content script, not the service worker. MV3 service workers get suspended after ~30s, killing WebSocket connections. Content scripts persist as long as the page is open.

**Diagnostics as a separate service:** `SipDiagnosticService` is its own service rather than adding logging directly to `SIPSorceryAdapter`. This keeps the adapter focused on SIP logic and lets the diagnostics service correlate events, generate annotations, and maintain state independently.

**API-first diagnostics:** All diagnostics data accessible via REST + SignalR. The React UI is one consumer; the Radio.Web Blazor UI will be another. Backend does the heavy lifting.

## Testing approach

**GV Audio Bridge:**
- Unit tests for `AudioResampler` (verify sample rate conversion accuracy)
- Unit tests for `GVAudioBridgeService` (mock RTP session and GVBridgeService, verify frame flow)
- Integration test: send known PCM frames through bridge, verify G.711 output matches expected
- Manual test: place a GV call, verify bidirectional audio through rotary phone

**SIP Diagnostics:**
- Unit tests for `SipDiagnosticService` diagnostic annotation logic (given INVITE with no 180 within timeout, verify diagnosis generated)
- Unit tests for HT801 config comparison (mock P-value responses, verify mismatch detection)
- Integration test: `POST /api/diagnostics/test-ring` sends INVITE and returns response sequence
- Manual test: open diagnostics page, trigger various call scenarios, verify real-time updates

## Files summary

| File | Action | Purpose |
|------|--------|---------|
| `GVBridge/Services/GVAudioBridgeService.cs` | Create | Core audio bridge (WebSocket PCM ↔ RTP G.711) |
| `GVBridge/Audio/AudioResampler.cs` | Create | 16kHz↔8kHz sample rate conversion |
| `GVBridge/Adapters/GVBrowserAdapter.cs` | Modify | Wire audio bridge start/stop on call answer/end |
| `GVBridge/Extensions/GVBridgeServiceExtensions.cs` | Modify | Register audio bridge in DI |
| `ChromeExtension/offscreen/offscreen.html` | Modify | Load audio-bridge.js for offscreen document |
| `ChromeExtension/offscreen/audio-bridge.js` | Modify | Implement tabCapture→PCM and PCM→playback |
| `ChromeExtension/content/gv-bridge.js` | Modify | Relay audioFrame between WS and offscreen |
| `ChromeExtension/background/service-worker.js` | Modify | tabCapture stream ID + offscreen doc creation |
| `Core/Diagnostics/SipDiagnosticService.cs` | Create | SIP event aggregation, diagnostics, HT801 health |
| `Core/Diagnostics/SipMessageEntry.cs` | Create | SIP message data model |
| `Core/Diagnostics/CallTimelineEntry.cs` | Create | Call timeline event model |
| `Core/Diagnostics/Ht801HealthStatus.cs` | Create | HT801 health status model |
| `Core/Diagnostics/ConfigParameter.cs` | Create | Config comparison model |
| `Server/Controllers/DiagnosticsController.cs` | Create | REST endpoints for diagnostics |
| `Core/SIPSorceryAdapter.cs` | Modify | Add SIP message event hooks, INVITE tracking, configurable SDP RTP port |
| `Server/Services/SignalRNotifierService.cs` | Modify | Subscribe to diagnostic events, broadcast via IHubContext |
| `Core/HT801/HT801ConfigService.cs` | Modify | Add config comparison method |
| `wwwroot/` React components | Create | DiagnosticsPage and sub-components |
