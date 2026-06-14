# Known Issues

## Inbound: rotary keeps ringing after the cell caller hangs up pre-answer (RESOLVED 2026-06-13, v2)

**Status:** ✅ Resolved by the deferred-answer v2 PR (`feat/deferred-gv-answer-v2`), building on the
inbound-CANCEL handler shipped in PR #39 (`fix/gv-inbound-cancel-stops-ringing`). The FIRST attempt
(PR #40, `feat/deferred-gv-answer`) was reverted (PR #41) for breaking inbound answering — see the
regression note below.
**Symptom (was):** On an inbound GV call, if the cell caller hung up *before* the rotary handset was
lifted, the rotary phone kept ringing for ~30s (until GV's RTCP media timeout) instead of stopping
promptly.
**Root cause:** `GvSipTransport.HandleIncomingInvite` auto-answered every inbound INVITE — it sent
`200 OK` immediately. Because GV believed the call was already answered, it never sent a SIP `CANCEL`
on caller-hangup; it only stopped media. So the #39 CANCEL handler had nothing to fire on, and the
ring continued until the media-timeout teardown.
**Why PR #40 regressed (the bug v2 fixes):** #40 deferred the answer correctly, but its CANCEL
handler (and `AcceptIncomingCallAsync`) *evicted* the still-`Ringing` session from `_activeCalls`
(`TryRemove`). Because deferring the 200 OK lets GV run its standards-compliant un-answered-call
signaling and emit a `CANCEL`/`BYE` during the ring window, that handler removed the session
mid-ring. At handset-lift, `AcceptIncomingCallAsync(_activeCallId)` then found **no session**, the
held `200 OK` was never sent, and inbound calls never connected. #40's unit tests missed it because
they called `AcceptIncomingCallAsync(literal)` directly, bypassing the real
`INVITE → IncomingCallReceived → _activeCallId → OnCallAnsweredOnRotaryPhoneAsync` chain and never
fed a CANCEL/BYE during the ring.
**Fix (v2):** Defer the inbound answer AND keep the `Ringing` session alive through the ring.
`HandleIncomingInvite` sends `180 Ringing`, pre-computes the `200 OK` (SDP answer) but **holds** it in
`SipCallSession.PendingOkMessage`, sets the session `Ringing`, and rings the HT801. The held `200 OK`
is sent only when the handset lifts, via `GvSipTransport.AcceptIncomingCallAsync` (called from
`GVApiAdapter.OnCallAnsweredOnRotaryPhoneAsync` before the audio bridge starts).
- **No interim eviction:** a pre-200 `CANCEL` (or `BYE`) during the ring marks the session
  `Cancelled` **in place** (NOT removed) and 200+487s the CANCEL; the session entry survives until a
  single well-defined final teardown (`HangupAsync`, reached via the `Completed` chain). So
  `AcceptIncomingCallAsync` can always look the session up on handset-lift.
- **Robust accept:** `AcceptIncomingCallAsync` reads the session status. `Ringing` → send the held
  `200 OK`, go `Active`, fire `Active`, arm the session timer. `Cancelled`/`Completed` → log + fire
  teardown, do **not** send a 200 OK (the caller is gone). Not found → log + fire teardown. It never
  silently no-ops a live answer.
- **All inbound removal paths gated on status** (CANCEL, BYE) so none evict a `Ringing` inbound
  session out from under a pending answer; #39's CANCEL teardown stays gated on `Status == Ringing`.
- A pre-200 local hangup (e.g. ~60s ring timeout) declines with `480 Temporarily Unavailable`
  (echoing the INVITE's Via/To/From/CSeq per RFC 3261 §8.2.6), never a BYE — there is no confirmed
  dialog to terminate.
**Mirrors the captured GV reference flow:** `100 → 180 → [183 +SDP early media] → HOLD 200 OK →
send 200 only on handset-lift`; on pre-200 CANCEL → `200`+`487`.
**Tests:** `GvSipTransportInboundDeferredAnswerTests` (transport-level: 180-only/held-200,
accept-sends-200, CANCEL-during-ring 487+Completed, CANCEL-after-accept ignored, accept-after-cancel
sends no 200, hangup-while-ringing 480-not-BYE, Ringing→Active event, and the #40 root-cause guard
`CancelDuringRing_KeepsSessionFindable_ForLateAccept`) plus the MANDATORY real-path integration suite
`GVApiAdapterInboundAnswerIntegrationTests` that drives
`INVITE → IncomingCallReceived → HandleSipIncomingCall → _activeCallId → OnCallAnsweredOnRotaryPhoneAsync
→ AcceptIncomingCallAsync(_activeCallId)` with a transport-side Call-ID and asserts the held `200 OK`
(with `Content-Type: application/sdp`) is sent — the assertion #40 lacked. (Confirmed: the
`KeepsSessionFindable` guard FAILS if the CANCEL handler is reverted to evict the Ringing session.)
**Verify in UAT (order matters — normal answer FIRST):** (1) **normal inbound answer → two-way audio**
as before (verify this FIRST; do not ship if it regresses); (2) caller hangs up before answer → rotary
ring stops within ~1s (GV CANCEL → 487); (3) ~60s ring-timeout → 480; (4) outbound calls unaffected
(`AcceptIncomingCallAsync` is a no-op for the already-`Active` outbound session).
**Revert path:** single `git revert` of the squash-merge commit restores the auto-answer behavior.
**PRs:** #39 (inbound CANCEL handler), #40 (deferred answer — reverted by #41),
`feat/deferred-gv-answer-v2` (deferred answer, keep-Ringing-session — this fix).

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
