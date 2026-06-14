# Known Issues

## Outbound: bridge started at placement → errno-101 blip + early-audio clipping (RESOLVED 2026-06-13)

**Status:** ✅ Resolved by the outbound InCall-ordering PR (`fix/outbound-incall-ordering`).
**Symptom (was):** On an outbound call (rotary → cell), the HT801↔GV audio bridge started and the
state flipped to `InCall` at call *placement* — roughly 6–10s before the far end actually answered.
This streamed audio while the far end was still ringing (potential clipped first syllable) and produced
a one-shot `errno-101` "Network is unreachable" cold-send blip as RTP was pushed before the peer was up.
The genuine answer signal (GV `CallStatusType.Active` → `OnCallAnswered`) was ignored for outbound
because the answer handler guarded on `Ringing` and the call was already `InCall`.
**Note:** This was NOT the 0-RTP / one-way-audio bug — that was fixed separately by the HT801
`Content-Type: application/sdp` fix (PR #35) and the outbound-RTP-port-from-INVITE-SDP fix (PR #34),
both shipped and UAT-verified. This ordering fix is purely about *when* the (working) bridge starts.
**Fix:** `PlaceGvCallAsync` now stays in `Dialing` after sending the GV INVITE (stashing the negotiated
RTP details), and defers both the bridge-start and the `InCall` transition to the GV-answered path.
`HandleCallAnsweredOnCellPhone` gained an outbound-`Dialing` branch that starts the bridge and goes
`InCall` when `Active` arrives — mirroring the proven BT outbound path (`HandleDeviceCallActive`). A
~45s outbound no-answer timeout resets a never-answered call cleanly to `Idle`. The bridge-start is
idempotent (guarded by `_outboundConnectPending`) so a duplicate `Active` (e.g. re-INVITE 200 OK)
starts it at most once.
**Verify in UAT:** Outbound two-way audio still works; no early audio/clipping before answer;
`State changed to: InCall` now logs at answer time (not ~6–10s earlier at placement); the `errno-101`
cold-send blip is gone (or, if present, a single benign blip). Inbound ring + answer unaffected.

## GV BYE not terminating calls (2026-05-25)

**Status:** Workaround in place (SRTP media teardown forces Google timeout)
**Impact:** When hanging up the rotary phone, the cell phone call ends after ~5-10 seconds (Google media timeout) instead of immediately.
**Root cause:** Our SIP BYE over WebSocket is structurally correct but Google's SIP proxy silently ignores it. Likely a dialog state mismatch (From/To tags, Contact URI, or CSeq) that requires proper SIP tracing to diagnose.
**Workaround:** On hangup, `GvSipTransport.HangupAsync()` now closes the DTLS-SRTP `RTCPeerConnection` immediately (sending `close_notify`) before sending the SIP BYE. This stops all media flow, and Google's RTP timeout detection terminates the far-end call within 5-10 seconds.
**Next step:** Set up WebSocket frame capture to compare our BYE with what Google's own web client sends when terminating a call. Compare headers field-by-field.
**PRs:** #25 (initial BYE), #26 (CSeq fix), #27 (diagnostic logging), #28 (session race fix), #29 (force media teardown)

## Idle SIP WebSocket never reconnects → inbound calls stop ringing (RESOLVED 2026-06-13)

**Status:** ✅ Resolved by the keep-alive / auto-reconnect / honest-status PR (`fix/gv-ws-keepalive-reconnect`).
**Symptom (was):** After the line sat idle for ~256s, Google closed the idle SIP-over-WebSocket signaling socket. The receive loop just `break`d with no event and no reconnect, so inbound `INVITE`s never arrived and the rotary phone never rang — yet `/api/gvbridge/status` still reported `sipRegistered:true` on the dead socket.
**Root cause:** No keep-alive was sent (Google advertises `keep=240` in the REGISTER 200-OK Via per RFC 6223), the channel raised no `Closed` event, and `GvSipTransport._registered` was never reset on socket death.
**Fix:**
- **Keep-alive (primary fix):** parse the RFC 6223 `keep=` frequency from the REGISTER 200-OK first Via (default 120s) and send the RFC 5626 §3.5.1 double-CRLF (`\r\n\r\n`) ping every `max(15, keep/2)`s, plus a secondary protocol-level `ClientWebSocket.Options.KeepAliveInterval` (defense-in-depth). A failed ping is treated as a dropped link and triggers reconnect.
- **Auto-reconnect:** the channel now raises a `Closed` event (with a `WasIntentional` flag); the transport runs a single-flight (`Interlocked`-guarded) reconnect loop with capped exponential backoff (1,2,4,8,16,30s) + ±20% jitter, retrying indefinitely until success or disposal, reusing the existing `RegisterAsync` path. The old channel is disposed and its handlers unsubscribed before a new one is created (fixes a latent handler/channel leak).
- **401 auth-recovery:** a real post-Digest 401/403 (or a 401/403 from `sipregisterinfo/get`) now escalates to a browser-less `RotateCookies` refresh of the rotating `__Secure-1PSIDTS/3PSIDTS` (primary), falling back to the CDP `cookies/refresh-from-browser` flow. Plain network drops do NOT trigger cookie work. (RotateCookies request shape is best-effort / unconfirmed — see `docs/research/gv-protocol-notes.md` §3.2 and the `GvCookieRotator` TODO.)
- **Honest status:** `IsRegistered` is now `registered AND socket-connected`; `/api/gvbridge/status` adds `wsConnected`, `lastConnectedAt`, and `psidtsAgeSeconds` (the original four field names are unchanged).
**Next step:** Confirm the exact `RotateCookies` request shape for the voice.google.com origin via a packet capture and tighten `GvCookieRotator` (fast-follow).
