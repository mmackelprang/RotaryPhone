# GV API — Remaining Work

## Priority 1: Cookie Automation Tool
Build a complete tool that automates ALL cookie retrieval, not just the 7 original cookies.

Requirements:
- Playwright opens Chromium, user logs in once
- Extract all 12+ required cookies (SID, HSID, SSID, APISID, SAPISID, __Secure-1PSID, __Secure-3PSID, __Secure-1PSIDTS, __Secure-3PSIDTS, __Secure-1PAPISID, __Secure-3PAPISID, SIDCC, __Secure-1PSIDCC, __Secure-3PSIDCC)
- Extract GV API key from page source (AIzaSy... regex)
- Encrypt and save to `data/gv-cookies.enc`
- Save API key to appsettings or `data/gv-api-key.txt`
- Run health check to verify cookies work
- Single command: `dotnet run -- gv-login`
- Update `GvLoginTool.cs` cookie extraction switch to handle all 12+ cookie names
- Update `GvLoginTool.ImportFromJsonAsync` similarly

## Priority 2: Signaler Subscription Format
Fix the subscription data format so the signaler stays connected and delivers events.
See `signaler-subscriptions-todo.md` for investigation notes.

## Priority 3: Audio Path (Phase 2)
Replace Chrome extension tabCapture audio with direct SIPSorcery DTLS-SRTP to Google's media servers.
Depends on GVResearch repo's WebRTC implementation (agent working on it in parallel).

Key pieces:
- WebRTC SDP offer/answer exchange via signaler channel
- DTLS-SRTP handshake with Google's "xavier" SIP UA at 74.125.39.0/24:26500
- Opus codec negotiation
- RTP bridge: Opus (GV side) ↔ G.711 µ-law (HT801 side)
- SIPSorcery already supports all of this

## Priority 4: Live Call Testing
Once signaler subscriptions work:
- Test incoming call detection end-to-end
- Test outbound call initiation
- Test hangup via API
- Test full flow: signaler detects incoming → SIP INVITE to HT801 → answer → audio bridge → hangup
