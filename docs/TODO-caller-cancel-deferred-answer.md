# TODO — Caller-cancel-keeps-ringing (inbound) — future fix via deferred answer

**Status:** OPEN (deferred). Logged 2026-06-13 per user request. Updated 2026-06-13 after PR #45/#46 (v2) — see "v2/v3 attempt" below.
**Severity:** minor — calls work both directions; only affects the case where the caller hangs up *before* the rotary handset is lifted.
**Deployed state (stable):** `origin/main @ 89282e4` (revert of #45). Running = pre-#45 build (auto-answer + clean rotary-hangup) + #44 SQLite call-history + #39 dormant CANCEL handler. **Do NOT re-attempt without reading the v2/v3 landmine section first.**

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

---

## v2/v3 attempt (PR #45 → reverted by #46) — the **hangup-propagation landmine**

PR #45 ("deferred-answer v2") was the second implementation of Approach A. It got **further than #40** but was reverted by **PR #46** for a *different* regression. This section captures exactly what v2 fixed, exactly what it broke, and the v3 requirement so the next session does not re-learn it.

### What v2 got RIGHT (keep these wins)
- Fixed the #40 session-retention bug: the `Ringing` session is now **kept in `_activeCalls` through the whole ring** (not removed when the auto-send was dropped), so `AcceptIncomingCallAsync(_activeCallId)` **found** the session on handset-lift. No more `AcceptIncomingCallAsync: no session`.
- **Normal inbound answer worked** — handset-lift sent the held `200 OK` to GV, two-way audio flowed. (UAT confirmed: *"Called, answered, audio works"*.)
- **Caller-cancel worked** — caller hangs up before answer → GV sends a real `CANCEL` → #39's handler tears down → HT801 stops ringing promptly. (UAT confirmed: *"Calling and hanging up before answering did stop ringing the RF"*.)

### What v2 BROKE (the reason for revert #46)
**Rotary-hangup no longer tore down the GV/cell leg.** UAT: *"Called, answered, audio works, but hanging up the RF didn't hang up my cell call."* The cell call stayed up after the handset went back on the hook — no BYE reached GV.

### Root cause of the v2 regression (the landmine)
Deferring the `200 OK` to handset-lift means the answer now flows through `GvSipTransport.AcceptIncomingCallAsync`, which fires `CallStatusChanged(Active)`. That event is wired:

```
GvSipTransport.AcceptIncomingCallAsync  →  fires CallStatusChanged(Active)
  →  GVApiAdapter.OnCallAnswered
  →  CallManager.HandleCallAnsweredOnCellPhone   ← THE LANDMINE
```

`HandleCallAnsweredOnCellPhone` (CallManager.cs ~:390-415) is the **"the far party answered on their cell"** path: it marks the call as already-bridged-elsewhere, keeps audio where it is, and **cancels the local ring WITHOUT arming the rotary-hangup→BYE propagation.** So when the handset later goes on-hook, CallManager no longer believes it owns a leg that needs a BYE → **no BYE to GV → cell call lingers** (until GV's own media-timeout, ~30s).

In the **auto-answer (pre-#45, current stable)** flow this never happens: the `200 OK` is sent at INVITE time by `HandleIncomingInvite`, the InCall transition + media bridge are driven from the **handset/InCall path**, and rotary-hangup correctly sends BYE. Deferring the 200 re-routed the "answered" signal through the wrong CallManager entry point.

### v3 requirement — send the held 200 OK WITHOUT tripping the "answered on cell" path
The next attempt must keep all three v2 wins **and** preserve rotary-hangup→BYE. Concretely:

1. **Decouple the held-`200 OK` send from `CallStatusChanged(Active)`.** Sending the deferred 200 to GV must NOT route into `HandleCallAnsweredOnCellPhone`. Drive the InCall transition + media bridge from the **handset-lift / local-answer path** (mirroring how the stable auto-answer flow does it), so CallManager still owns the leg and still arms rotary-hangup→BYE.
   - Options to evaluate: (a) a dedicated "local answer completed" event distinct from the "remote/cell answered" `OnCallAnswered`; (b) a flag on the answer so `GVApiAdapter.OnCallAnswered` skips `HandleCallAnsweredOnCellPhone` for *inbound-deferred* answers; (c) have `AcceptIncomingCallAsync` send the 200 but let the existing handset/InCall path (not `CallStatusChanged(Active)`) own the state transition. Pick after reading both call sites — don't guess.
2. **Add a mandatory hangup-propagation regression test:** inbound INVITE → defer → handset-lift (held 200 sent) → **rotary BYE → assert a BYE is sent to GV** (and the cell/GV leg is torn down). This is the gap #45's tests missed — they verified answering + caller-cancel but never asserted rotary-hangup still BYEs after a deferred answer.
3. Keep #40's mandatory test (normal answer sends the held 200) and #45's session-retention fix.
4. **UAT order (do not ship if any earlier step regresses):**
   (a) normal inbound answer → two-way audio;
   (b) **rotary-hangup after answer → cell call ends promptly (BYE to GV)**  ← the #45 regression, test this explicitly;
   (c) caller-cancel before answer → HT801 stops ringing promptly.

### The full set of coupled landmines (all three must hold simultaneously)
A correct v3 has to satisfy **all** of these at once — fixing one previously broke another:
| Concern | Broke in | Mechanism |
|---|---|---|
| Session found on handset-lift | #40 | session not retained in `_activeCalls` after auto-send removed → `AcceptIncomingCallAsync: no session` |
| Caller-cancel stops ring | (the original bug) | auto-answer suppresses GV's CANCEL → only media-stop (~30s) |
| Rotary-hangup BYEs the GV leg | #45 | deferred 200's `CallStatusChanged(Active)` → `HandleCallAnsweredOnCellPhone` → no BYE armed |

### References (v2/v3)
- Reverted: **PR #45** (deferred-answer v2) ← reverted by **PR #46** (`2b392dc`). Stable HEAD = `89282e4`.
- Earlier reverted: PR #40 ← reverted by #41 (session-retention break).
- On main (dormant until A makes GV send a CANCEL): PR #39 inbound CANCEL handler.
- Key code sites: `GvSipTransport.AcceptIncomingCallAsync` (fires `CallStatusChanged(Active)`); `GVApiAdapter.OnCallAnswered`; `CallManager.HandleCallAnsweredOnCellPhone` (~:390-415, the landmine) vs. the handset/InCall path that correctly arms rotary-hangup→BYE.
