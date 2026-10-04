# GV API Migration Design — CDP/Extension to Direct HTTP API

**Date:** 2026-03-27
**Status:** Approved
**Branch:** `research/gv-api-integration`

## Problem

The current GVBrowser adapter relies on Chrome DevTools Protocol (CDP) to click buttons on the Google Voice web page and a Chrome extension for call detection and audio capture. This creates:

- **Chrome dependency** — a running Chromium instance with `--remote-debugging-port=9224` and a Manifest V3 extension must be active at all times
- **Architectural complexity** — 3-layer audio bridge (Chrome tabCapture → WebSocket → PCM → G.711 → RTP → HT801) with codec conversions at each hop
- **Fragility** — DOM selectors break on GV redesigns; CDP button clicks fail silently
- **MV3 service worker suspension** — WebSocket moved to content script as workaround, requiring dual-channel communication (WebSocket + HTTP POST fallback)

## Solution

Two-phase migration using Google Voice's undocumented internal HTTP API (reverse-engineered by the [GVResearch](https://github.com/mmackelprang/GVResearch) project).

### Phase 1: HTTP API for Call Control, Browser for Audio

Replace CDP and extension call-detection code with direct GV HTTP API calls. The Chrome extension is stripped down but retains three responsibilities: (1) tabCapture audio relay, (2) answer/hangup button clicking via DOM (required to trigger the browser's WebRTC session), and (3) tab audio muting via DOM. CDP is fully eliminated — the content script handles button clicks and muting directly since it has DOM access to the GV page.

### Phase 2: Direct WebRTC Audio (No Browser)

Replace the extension's audio relay with a SIPSorcery-based DTLS-SRTP connection directly to Google's media servers. Eliminates Chrome entirely. Depends on WebRTC audio channel implementation in GVResearch (in progress).

## Architecture

### Phase 1

```
┌──────────────────────────────────────────┐
│ GVApiAdapter : ICallAdapter              │
│ ├─ GvAuthService (SAPISIDHASH cookies)   │
│ ├─ GvSignalerClient (long-poll)          │  ← incoming call detection
│ ├─ GvCallClient (HTTP API)               │  ← answer, hangup, dial
│ ├─ GvSmsClient (HTTP API)                │  ← SMS send/receive
│ └─ GvAudioBridgeService (unchanged)      │  ← still uses extension for audio
└──────────────┬───────────────────────────┘
               │ WebSocket (audio + answer/hangup commands)
┌──────────────▼───────────────────────────┐
│ Chrome Extension (stripped down)          │
│ ├─ tabCapture → PCM → WebSocket          │  ← audio relay
│ ├─ DOM button click (answer/hangup)      │  ← triggers browser WebRTC session
│ └─ DOM audio mute (replaces CDP mute)    │  ← mute tab audio elements
└──────────────┬───────────────────────────┘
               │ RTP (G.711 µ-law)
         ┌─────▼─────┐
         │   HT801   │ → Rotary Phone
         └───────────┘
```

### Phase 2

```
┌──────────────────────────────────────────┐
│ GVApiAdapter : ICallAdapter              │
│ ├─ GvAuthService (SAPISIDHASH cookies)   │
│ ├─ GvSignalerClient (long-poll)          │
│ ├─ GvCallClient (HTTP API)               │
│ └─ WebRtcAudioBridge (SIPSorcery)        │  ← DTLS-SRTP + Opus to Google
└──────────────┬───────────────────────────┘
               │ RTP bridge (Opus ↔ G.711)
         ┌─────▼─────┐
         │   HT801   │ → Rotary Phone
         └───────────┘
```

## Components

### Absorbed from GVResearch (into `src/RotaryPhoneController.GVBridge/`)

| GVResearch Source | Our Destination | Purpose |
|---|---|---|
| `GvAuthService` + `GvHttpClientHandler` | `Auth/GvAuthService.cs`, `Auth/GvHttpClientHandler.cs` | SAPISIDHASH computation, cookie management, auto-inject auth headers |
| `GvProtobufJsonParser` + `GvRequestBuilder` | `Protocol/GvProtobufJsonParser.cs`, `Protocol/GvRequestBuilder.cs` | Parse GV's protobuf-JSON wire format |
| `GvCallClient` | `Clients/GvCallClient.cs` | Initiate, status, hangup via HTTP |
| `GvSmsClient` | `Clients/GvSmsClient.cs` | Send SMS (replaces fetch-intercept in extension) |
| `GvThreadClient` | `Clients/GvThreadClient.cs` | Thread/voicemail listing |
| `GvAccountClient` | `Clients/GvAccountClient.cs` | Account info (phone numbers, linked devices) |
| Cookie encryption model | `Auth/EncryptedCookieStore.cs` | AES-256 encrypted cookie persistence |

### Not absorbed

- `GvResearch.Sip` — we already have `SIPSorceryAdapter`
- `GvResearch.Api` REST facade — we have our own API controllers
- `GvResearch.Softphone` — Avalonia GUI, irrelevant
- `Iaet.Core` — capture toolkit, research-only
- GVResearch test infrastructure — we write our own

### Built new

- `GVApiAdapter : ICallAdapter` — replaces `GVBrowserAdapter`
- `GvSignalerClient` — long-poll client for real-time events
- Cookie extraction tool (Playwright + DevTools fallback)
- Stripped-down Chrome extension (Phase 1 only, audio-only)

## GVApiAdapter — ICallAdapter Implementation

### Lifecycle

```
ActivateAsync()
  ├─ GvAuthService.GetValidCookiesAsync()     → load/validate cookies
  ├─ GvAccountClient.GetAsync()               → verify account, get GV number
  ├─ GvSignalerClient.ConnectAsync()           → start long-poll for events
  └─ Set IsAvailable = true

DeactivateAsync()
  ├─ GvSignalerClient.DisconnectAsync()
  └─ Set IsAvailable = false
```

### Incoming Call Flow

```
GvSignalerClient receives push event
  → Parse incoming call notification
  → Extract caller ID
  → Fire OnIncomingCall(callerNumber)
  → CallManager receives event
  → Sends SIP INVITE to HT801 (rotary phone rings)
  → User lifts handset → SIP 200 OK
  → CallManager calls OnCallAnsweredOnRotaryPhoneAsync()
    → Send answer command to extension via WebSocket (content script clicks Answer button via DOM)
    → Extension mutes tab audio elements via DOM (replaces CDP mute)
    → Start audio bridge
```

### Outbound Call Flow

```
User lifts handset → dials digits → CallManager calls PlaceCallAsync(number)
  → GvCallClient.InitiateAsync(number)
  → GvSignalerClient receives call status push → track ringing/connected
  → When connected: start audio bridge
```

### Hangup Flow

```
User replaces handset → CallManager calls OnCallHungUpAsync()
  → Stop audio bridge
  → GvCallClient.HangupAsync(callId)
```

### SIP Remains Authoritative

Same design principle as `GVBrowserAdapter` — SIP controls the HT801 interaction. The GV API tells Google what to do; SIP tells the rotary phone what to do. CallManager coordinates. Browser/extension events are not used for state transitions.

## GvSignalerClient — Real-Time Event Channel

### Protocol (from GVResearch captured data)

1. **Server selection**: `POST signaler-pa.clients6.google.com/punctual/v1/chooseServer`
2. **Channel creation**: `GET /punctual/multi-watch/channel?VER=8&CVER=22&...` → returns `SID` + `gsessionid`
3. **Long-poll loop**: `GET /punctual/multi-watch/channel?SID=...&AID=...&TYPE=xmlhttp` → blocks until event, returns payload, reconnect immediately
4. **Ack**: Client increments `AID` to acknowledge received messages

### Events

- Incoming call (SDP offer from xavier, caller ID)
- Call ended
- SMS received
- Voicemail notification

### Reconnection

If the long-poll drops (network blip, timeout), attempt reconnect with same `SID`/`gsessionid`. If that fails, create a fresh channel. Log reconnection events.

### Fallback

If the signaler proves harder than expected to reverse-engineer, fall back to keeping the extension's DOM polling for incoming call detection only. This does not block the other Phase 1 wins (CDP elimination, HTTP API call control, direct SMS).

## Authentication

### SAPISIDHASH Mechanism

Every request to `clients6.google.com/voice/v1/voiceclient/` requires:

```
Authorization: SAPISIDHASH <unix_timestamp>_<SHA1(timestamp + " " + SAPISID + " " + "https://voice.google.com")>
Cookie: SID=...;HSID=...;SSID=...;APISID=...;SAPISID=...;__Secure-1PSID=...;__Secure-3PSID=...
Content-Type: application/json+protobuf
Origin: https://voice.google.com
Referer: https://voice.google.com/
X-Goog-AuthUser: 0
```

`GvHttpClientHandler` (a `DelegatingHandler`) injects these on every outgoing request automatically.

### Cookie Acquisition

**Primary: Playwright automated extraction**
- CLI command: `dotnet run -- gv-login`
- Opens Chromium via Playwright, navigates to `voice.google.com`
- User logs in manually (credentials + 2FA)
- Playwright waits for navigation to GV inbox
- Extracts 7 required cookies from browser context
- Encrypts with AES-256, saves to `data/gv-cookies.enc`
- Runs health check (`threadinginfo/get`) to confirm cookies work
- Closes browser

**Fallback: DevTools paste**
- CLI command: `dotnet run -- gv-cookies-import`
- User pastes JSON blob from Chrome DevTools Application tab
- Validates 7 required cookies present
- Encrypts and saves

### Cookie Lifecycle

- Typical lifetime: 2+ months
- Health check on startup and every 30 minutes: `POST threadinginfo/get`
- If stale (401/403): set `IsAvailable = false`, log warning, notify user to re-login
- Future: implement `RotateCookies` for automatic refresh (unverified endpoint)

## What Gets Deleted

- All CDP code in `GVBrowserAdapter` (`ClickGvButtonViaCdpAsync`, `MuteGvTabViaCdpAsync`) — replaced by content script DOM manipulation
- Extension DOM polling for incoming calls (content script button detection every 500ms) — replaced by `GvSignalerClient`
- Service worker HTTP relay for call events — no longer needed (signaler handles detection)
- Fetch intercept for SMS in content script — replaced by `GvSmsClient`
- `GVBrowserAdapter.cs` itself (replaced by `GVApiAdapter`)

## What the Extension Retains (Phase 1 Only)

- tabCapture audio relay (PCM → WebSocket) — the audio bridge
- Answer/hangup button clicking via DOM when commanded via WebSocket — required to trigger the browser's WebRTC session
- Tab audio muting via DOM (`document.querySelectorAll('audio,video').forEach(e => e.muted = true)`) — moved from CDP to content script
- Service worker manages offscreen document lifecycle for tabCapture

## What Survives Unchanged

- `CallManager` state machine (Idle, Dialing, Ringing, InCall)
- `ICallAdapter` interface
- `SIPSorceryAdapter` (HT801 SIP communication)
- `GVAudioBridgeService` + `AudioResampler` (Phase 1 audio path)
- `GVBridgeService` WebSocket server (Phase 1, audio frames only)
- `BluetoothCallAdapter` and `SipTrunkCallAdapter`
- All configuration, DI registration patterns

## Component Complexity

| Component | Work | Complexity |
|---|---|---|
| GVApiAdapter | New ICallAdapter wiring auth, signaler, API clients, audio bridge | Medium |
| GvAuthService | Absorb from GVResearch, adapt to our DI | Low |
| GvHttpClientHandler | Absorb from GVResearch | Low |
| GvProtobufJsonParser/Builder | Absorb from GVResearch | Low |
| GvCallClient | Absorb from GVResearch | Low |
| GvSmsClient | Absorb from GVResearch | Low |
| GvThreadClient | Absorb from GVResearch | Low |
| GvAccountClient | Absorb from GVResearch | Low |
| GvSignalerClient | New, reverse-engineer signaler protocol | High |
| Cookie extraction tool | Playwright login + DevTools fallback | Medium |
| Strip extension | Remove DOM polling, SMS intercept, HTTP relay; keep audio + button click + mute | Low |
| Phase 2: WebRtcAudioBridge | SIPSorcery DTLS-SRTP to Google (deferred) | High — deferred |

## Risks

1. **Signaler protocol complexity** — The long-poll channel uses Google's proprietary framing (VER=8, CVER=22). Event payload structure for incoming calls needs further reverse-engineering. Mitigated by fallback to extension DOM polling.
2. **Undocumented API stability** — Google can change endpoints at any time. Mitigated by: personal project, acceptable risk per user decision, and the API has been stable for years (it backs the GV web app).
3. **Cookie expiry UX** — Every 2+ months, user must re-login via Playwright. Mitigated by clear logging and the `gv-login` CLI tool.
4. **Phase 2 viability** — DTLS-SRTP to Google's media servers from a non-browser client is unproven. Mitigated by Phase 1 being independently useful; GVResearch agent actively working on WebRTC implementation.
