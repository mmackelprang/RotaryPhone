# TODO — Caller-cancel-keeps-ringing (inbound) — future fix via deferred answer

**Status:** OPEN (deferred). Logged 2026-06-13 per user request.
**Severity:** minor — calls work both directions; only affects the case where the caller hangs up *before* the rotary handset is lifted.

## Symptom
Inbound GV call (cell → Google Voice → RotaryPhone → HT801 rings). If the **caller hangs up before the rotary handset is lifted**, the HT801 keeps ringing ~30s (until SIPSorcery's media-inactivity timeout) instead of stopping promptly.

## Confirmed root cause (live Debug capture, 2026-06-13)
- GV does **not** send any SIP teardown (CANCEL/BYE) over the WebSocket on caller-hangup — it only **stops sending media** (`RTCP session ... has not had any activity for over 30 seconds`).
- Why no CANCEL: `GvSipTransport.HandleIncomingInvite` **auto-answers the GV INVITE immediately** (`200 OK`), so GV considers the call answered → caller-hangup is signaled only by media-stop, not a CANCEL. (A SIP CANCEL is only sent for an *un-answered* INVITE.)

## Chosen future fix — Approach A: defer the inbound 200 OK
Send `180 Ringing` to GV, **hold** the prepared `200 OK`/SDP, ring the HT801, and send the held `200 OK` only when the **handset is lifted** (`GVApiAdapter.OnCallAnsweredOnRotaryPhoneAsync` -> `GvSipTransport.AcceptIncomingCallAsync`). Then a caller-hangup during ring makes GV send a real SIP `CANCEL`, which PR #39's handler turns into a prompt teardown (`CancelPendingInvite` -> HT801 stops ringing ~1s). A design pass assessed this **architecturally SAFE** (GV is standards-compliant; the immediate 200 OK is an internal shortcut, not a GV requirement; deferring is also cleaner for DTLS/ICE setup).

## Why PR #40 was reverted (the pitfall to fix)
PR #40 implemented Approach A but **broke normal inbound answering** (reverted by PR #41). On handset-lift: `AcceptIncomingCallAsync: no session for call <id> -- ignoring` -- the session was not found in `_activeCalls`, so the held `200 OK` never went to GV -> inbound calls did not connect (cell kept ringing, no audio). Almost certainly the session stopped being retained/keyed in `_activeCalls` once the auto-send was removed.

## Requirements for the next attempt
1. Fix session retention/lookup so `AcceptIncomingCallAsync(_activeCallId)` reliably finds the session created in `HandleIncomingInvite` (verify the key == `GVApiAdapter._activeCallId`).
2. **Add a mandatory regression test asserting a NORMAL inbound answer sends the `200 OK` to GV** -- the gap PR #40's tests missed (unit tests passed but real answering failed).
3. UAT order: verify **normal inbound answer (two-way audio) FIRST**, then caller-cancel. Do not ship if normal answer regresses.
4. Re-gate PR #39's CANCEL handler on `Status == Ringing` (so a post-answer CANCEL can't tear down a live call) -- PR #40 did this; reverted with #40.
5. Keep the single-`git revert` safety path; UAT live on `radio` before declaring done.

## Alternative if A proves too risky -- Approach B (safe fallback)
Additive media-inactivity teardown that never touches the answer path: when GV media goes idle while still `Ringing`, cancel the HT801 INVITE (tune ~10-15s). Slower stop but cannot break answering.

## References
- Reverted: PR #40 (deferred-answer) <- reverted by PR #41.
- On main: PR #39 (inbound CANCEL handler -- dormant until A makes GV send a CANCEL).
- Live evidence: caller-hangup window shows only `RTCP ... no activity for over 30 seconds`; `Answered incoming call` (auto-200) fires at INVITE time.

## Captured evidence — GV web-client reference behavior (2026-06-13)

Captured the gv-bridge Chrome's voice.google.com SIP-over-WSS frames via CDP (auto-attach to page + workers), with `rotary-phone` stopped so the call stayed un-answered. GV's own web client is the reference callee — it does NOT auto-answer. Observed flow for an inbound call the caller hung up before answering (client -> GV, in order):

- `SIP/2.0 100 Trying`            (CSeq: 1 INVITE)
- `SIP/2.0 180 Ringing`           (CSeq: 1 INVITE)
- `SIP/2.0 183 Session Progress`  (CSeq: 1 INVITE) — WITH SDP answer (`Content-Type: application/sdp`) as early media; still NOT a 200 OK
- *(caller hangs up)*
- `SIP/2.0 200 OK`                (CSeq: 1 **CANCEL**) — ACKs GV's CANCEL
- `SIP/2.0 487 Request Terminated`(CSeq: 1 INVITE) — final response to the INVITE

### Conclusions (Approach A confirmed sound)
1. **GV DOES send a SIP `CANCEL` over the WS when the caller hangs up on an un-answered call** — proven by the client's `200 OK (CSeq CANCEL)` + `487` responses. Our auto-answer (immediate 200 OK) is exactly why we never observed the CANCEL.
2. Correct callee response to that CANCEL = **`200 OK` to the CANCEL + `487 Request Terminated` to the INVITE** — already implemented by PR #39's CANCEL handler.
3. GV's web-client inbound pattern = **`100` -> `180` -> `183`(+SDP early media) -> [hold] -> `200 OK` only on user-answer.** The deferred-answer fix should mirror this: send `180` (optionally `183` with the SDP answer for ringback/early media), hold the `200 OK` until handset-lift; on a pre-200 CANCEL, reply `200`+`487`.

### Remaining work is implementation-only
Approach A's protocol assumption is now verified. The next attempt just needs: (a) fix the `_activeCalls` session retention/lookup that broke PR #40 (`AcceptIncomingCallAsync: no session`), and (b) a mandatory regression test asserting a NORMAL inbound answer sends the held `200 OK`. (Capture limitation: only the client->GV direction was recorded; GV's exact CANCEL request bytes weren't, but the client responses unambiguously prove GV sent it, and #39 already parses an incoming CANCEL.)
