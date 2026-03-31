# SIP-over-WebSocket + DTLS-SRTP Audio Integration

**Date:** 2026-03-30
**Status:** Approved
**Branch:** `feature/sip-wss-audio`

## Problem

The current GVBridge uses a Chrome extension for WebRTC audio relay (tabCapture → WebSocket → PCM → G.711 → RTP → HT801). This requires a running Chrome instance with a Manifest V3 extension. The GVResearch project has a working softphone that makes/receives Google Voice calls with bidirectional Opus audio via SIP-over-WebSocket + DTLS-SRTP, eliminating the Chrome dependency entirely.

## Solution

Port the working SIP-over-WebSocket call transport from GVResearch into RotaryPhone's GVBridge. Replace the Chrome extension audio path with direct SIP signaling and DTLS-SRTP media. The HT801 ATA RTP bridge remains unchanged — only the Google-facing side changes.

## Architecture

```
Rotary Phone ↔ HT801 ATA ↔ SIPSorceryAdapter (local SIP) ↔ CallManager
                                                                ↕
                                                          GVApiAdapter
                                                                ↕
                                                       GvSipTransport
                                                    (SIP-over-WebSocket)
                                                                ↕
                                                     DTLS-SRTP + Opus
                                                                ↕
                                                   Google Media Relay
                                                   (74.125.39.x:26500)
```

Audio flow when a call is active:
- **Outbound (rotary → cell):** HT801 G.711 RTP → decode → resample 8kHz→48kHz → Opus encode → SRTP → Google
- **Inbound (cell → rotary):** Google SRTP → Opus decode 48kHz → resample 48kHz→8kHz → G.711 encode → RTP → HT801

## Files Copied from GVResearch

Copy from `D:\prj\GVResearch\src\GvResearch.Sip\Transport\` into `src/RotaryPhoneController.GVBridge/Sip/`:

| Source | Destination | Purpose |
|---|---|---|
| `SipWssCallTransport.cs` | `Sip/GvSipTransport.cs` | SIP signaling + DTLS-SRTP + Opus encode/decode |
| `GvSipWebSocketChannel.cs` | `Sip/GvSipWebSocketChannel.cs` | WebSocket with `sip` subprotocol |
| `GvSipCredentialProvider.cs` | `Sip/GvSipCredentialProvider.cs` | Fetches SIP creds via `sipregisterinfo/get` |

Copy from `D:\prj\GVResearch\src\GvResearch.Shared\Auth\`:

| Source | Destination | Purpose |
|---|---|---|
| `CookieRetriever.cs` | `Auth/CookieRetriever.cs` | Chrome CDP cookie extraction (replaces GvLoginTool) |
| `GvCookieSet.cs` | `Auth/GvCookieSet.cs` | Cookie model with raw header support (replaces GvCookieJar) |

**Not copied** (confirmed unused in working softphone):
- `GvDtlsSrtpClient.cs` — ECDSA cipher suites defined but never instantiated
- `WebRtcCallTransport.cs` — obsolete experimental code
- `WebRtcCallSession.cs` — obsolete companion

## Modified SIPSorcery NuGet

Stock SIPSorcery 8.0.23 (current) will NOT work for DTLS. Must use `10.0.6-diag`:
- Removes `encrypt_then_mac` TLS extension (Google rejects it)
- Removes `status_request` extension
- Ensures `extended_master_secret` is always present
- Limits SRTP profiles to Chrome-compatible set

Copy from `D:\prj\GVResearch\local-packages\`:
- `SIPSorcery.10.0.6-diag.nupkg`
- `SIPSorceryMedia.Abstractions.10.0.6-diag.nupkg`

Update `RotaryPhoneController.GVBridge.csproj` to reference `10.0.6-diag`.
Add `local-packages` source to `nuget.config`.

## RTCPeerConnection Configuration (Verified Working)

```csharp
var pc = new RTCPeerConnection(new RTCConfiguration
{
    iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }],
    X_UseRsaForDtlsCertificate = true,    // Google uses RSA cipher suites
    X_UseRtpFeedbackProfile = true,       // UDP/TLS/RTP/SAVPF profile
});
```

## SIP Call Flow

```
1. sipregisterinfo/get → SIP username + Digest password (via SAPISIDHASH auth)
2. WSS connect to wss://web.voice.telephony.goog/websocket (subprotocol: "sip")
3. REGISTER (no auth) → 401 → REGISTER with MD5 Digest → 200 OK + Service-Route
4. INVITE sip:+1XXXXXXXXXX@web.c.pbx.voice.sip.google.com (with SDP offer)
5. 100 Trying → 183 Session Progress (SDP answer) → PRACK → 200 OK
6. 180 Ringing → PRACK → 200 OK
7. 200 OK (INVITE) → ACK → call connected
8. ICE → DTLS handshake → SRTP keys derived
9. Bidirectional Opus 48kHz audio flows
10. BYE → 200 OK (either direction)
```

Session timer: Google sends `Session-Expires: 90;refresher=uac`. Must re-INVITE every ~45s.

## GVApiAdapter Modifications

### ActivateAsync
After cookie/health check, create and register the SIP transport:
```csharp
var credProvider = new GvSipCredentialProvider(_gvHttp, _config.GvApiBaseUrl, _config.GvApiKey, logger);
_sipTransport = new GvSipTransport(logger, () => credProvider.GetCredentialsAsync(), loggerFactory);
_sipTransport.IncomingCallReceived += HandleSipIncomingCall;
_sipTransport.AudioReceived += HandleSipAudioReceived;
```

### PlaceCallAsync
SIP INVITE instead of HTTP API:
```csharp
var result = await _sipTransport.InitiateAsync(e164Number, ct);
```

### HangUpAsync
SIP BYE instead of HTTP API:
```csharp
await _sipTransport.HangupAsync(callId, ct);
```

### OnCallAnsweredOnRotaryPhoneAsync
Start audio bridge between SIP transport and HT801 RTP. No extension commands.

### OnCallHungUpAsync
Stop audio bridge, SIP BYE. No extension commands.

### Incoming Calls
`GvSipTransport.IncomingCallReceived` fires `OnIncomingCall` → CallManager sends SIP INVITE to HT801 → rotary phone rings. Replaces `GvSignalerClient`.

## Audio Bridge Modifications

Replace Chrome extension audio source/sink with SIP transport audio:

**Inbound (Google → HT801):**
```
GvSipTransport.AudioReceived (48kHz 16-bit mono PCM)
  → AudioResampler.Resample48kTo8k()
  → MuLawEncoder.Encode() → G.711 µ-law
  → RTPSession.SendAudio() → HT801
```

**Outbound (HT801 → Google):**
```
HT801 RTP → G.711 µ-law payload
  → MuLawEncoder.Decode() → 8kHz PCM
  → AudioResampler.Resample8kTo48k() → 48kHz PCM
  → GvSipTransport.SendAudio(pcmBytes, 48000)
  (SipTransport internally encodes to Opus via Concentus)
```

## AudioResampler Additions

New methods for 48kHz ↔ 8kHz conversion (6:1 ratio):
- `Resample48kTo8k(byte[] pcm48k)` — 6-sample averaging decimation
- `Resample8kTo48k(byte[] pcm8k)` — linear interpolation upsampling

Same approach as existing 16k↔8k methods. Telephony audio doesn't benefit from higher-quality resampling.

## Cookie Management Migration

### Current Approach (RotaryPhone)
- `GvCookieJar` enumerates 12+ cookies individually by name
- `GvCookieStore` encrypts/decrypts with AES-256
- `GvCookieRotationService` calls `RotateCookies` every 5 min
- `GvLoginTool` uses Playwright to extract named cookies
- Fragile: keeps breaking as Google adds more required cookies

### GVResearch Approach (Proven Working)
- `CookieRetriever` connects to Chrome via CDP (port 9222) — no separate Playwright browser
- Captures **ALL cookies as raw header string** (`RawCookieHeader`) — no enumeration
- `GvCookieSet.ToCookieHeader()` returns raw header verbatim
- First run: launches Chrome with `--remote-debugging-port=9222`, user logs in manually
- Subsequent runs: connects to existing Chrome, extracts fresh cookies automatically
- Encrypts to disk with random AES-256 key

### Migration Plan
1. **Replace `GvCookieJar`** with `GvCookieSet` — uses `RawCookieHeader` for all API requests
2. **Replace `GvLoginTool`** with `CookieRetriever` — CDP-based, connects to user's actual Chrome
3. **Keep `GvCookieStore`** (AES-256 encryption) — adapt to serialize `GvCookieSet`
4. **Replace `GvCookieRotationService`** — instead of calling RotateCookies endpoint, periodically reconnect to Chrome CDP (same running Chrome instance) to re-extract all cookies. This keeps PSIDTS fresh without needing to reverse-engineer which cookies to rotate. Interval: every 5 minutes. Falls back to RotateCookies if Chrome CDP is unavailable.
5. **Update `GvHttpClientHandler`** — use `cookieSet.ToCookieHeader()` for raw header injection
6. **First-run UX**: `dotnet run -- gv-login` opens Chrome, user logs in once. Cookies auto-extracted and encrypted. Subsequent startups reconnect to Chrome CDP to refresh.

### Key Improvement
The raw cookie header approach eliminates the "which cookies does Google need today?" problem. Instead of tracking SAPISID, SID, HSID, SSID, APISID, __Secure-1PSID, __Secure-3PSID, __Secure-1PSIDTS, __Secure-3PSIDTS, __Secure-1PAPISID, __Secure-3PAPISID, SIDCC, __Secure-1PSIDCC, __Secure-3PSIDCC individually, we just capture everything the browser has.

## What Gets Removed

- `GVBridgeService` — Chrome extension WebSocket server
- `ExtensionMessage` models — MuteTab, UnmuteTab, Answer, Hangup, AudioFrame, etc.
- `GVBridgeHub` — SignalR hub for extension communication
- `GVBridgeEventBridge` — hosted service bridging events to SignalR
- Extension-related config — `WebSocketPort`, `WebSocketHost`, `ExtensionConnectTimeoutSeconds`
- `ChromeExtension/` directory — entire Chrome extension
- `GvSignalerClient` + `SignalerEvent` — replaced by SIP INVITE reception
- `GvCallClient` — replaced by SIP INVITE/BYE
- `GvSmsClient` — deferred (not needed for call audio)
- `GvCookieJar` — replaced by `GvCookieSet` with raw header support
- `GvLoginTool` — replaced by `CookieRetriever` (Chrome CDP)

## What Survives Unchanged

- `CallManager` state machine (Idle, Dialing, Ringing, InCall)
- `SIPSorceryAdapter` (HT801 SIP communication)
- `ICallAdapter` interface
- Cookie auth (`GvSapisidHash`, `GvCookieJar`, `GvCookieStore`, `GvHttpClientHandler`, `GvCookieRotationService`)
- `GvAccountClient` (health check)
- `GvProtobuf` (protobuf-JSON helpers, used by credential provider)
- `MuLawEncoder` (G.711 codec)
- `AudioResampler` (existing methods, extended with 48k↔8k)
- `GVBridgeConfig` (extended, some fields removed)

## Key Technical Details

- SIP domain: `web.c.pbx.voice.sip.google.com`
- WebSocket: `wss://web.voice.telephony.goog/websocket` (no auth needed for upgrade)
- SIP REGISTER: MD5 Digest auth (username/password from `sipregisterinfo/get`)
- DTLS cipher: `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256`
- SRTP profile: `AES_CM_128_HMAC_SHA1_80`
- Opus: 48kHz stereo from Google, decode → downmix to mono; encode mono → send
- Frame size: 960 samples (20ms at 48kHz)
- Session timer: re-INVITE every ~45s (half of 90s Session-Expires)
- BYE: echo ALL Via headers in 200 OK response

## Testing Checklist

- [ ] SIP REGISTER succeeds (401 → Digest → 200 OK)
- [ ] Outbound call: INVITE → phone rings → call connects
- [ ] Inbound audio: Opus decode → resample → G.711 → HT801 → rotary phone speaker
- [ ] Outbound audio: rotary phone mic → HT801 → G.711 → resample → Opus encode → Google
- [ ] Incoming call: SIP INVITE → 180 → 200 OK → audio flows
- [ ] BYE: both directions, 200 OK stops retransmission
- [ ] Session timer: re-INVITE every ~45s keeps call alive beyond 90s
