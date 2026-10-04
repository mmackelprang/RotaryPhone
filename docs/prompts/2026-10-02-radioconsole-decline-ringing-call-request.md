# Request: an endpoint that declines a RINGING inbound call (Radio Console "Ignore" button)

**From:** Radio Console session (RTest), Builder for row `PHN-11` · **Date:** 2026-10-02
**Status:** Request for your next session. **Nothing in this repo has been changed except this file.**
**Radio Console side:** branch `feat/phn-11-incoming-call-banner` (PR held for owner panel UAT).

## What the owner asked for

Radio Console now shows a large incoming-call banner on the console's touchscreen. The owner, 2026-10-02:

> *"for PHN-11 - there should be an 'Ignore' button on the overlay that will allow the user to cancell the call
> from the touchscreen."*

## Why this needs you

Declining a call is RotaryPhone's job, and RotaryPhone does not expose a way to do it today. Read-only, at
the working tree as of 2026-10-02:

- `PhoneController` (`api/phone`) has `GET status`, `POST bell-failure/ack`, `POST simulate/incoming`,
  `POST simulate/hook`, `POST simulate/dial`, `GET system-status`, `GET ht801/validate`. There is no
  reject, decline or hang-up route.
- `RotaryHub` has `SendCallState`, `SendIncomingCall`, `SendCallHistoryUpdate`, `SendSystemStatus` and
  `ReportCallerResolved`. There is no hub method for it either.
- `GVBridgeController` (`api/gvbridge`) has status, adapter mode and cookie routes only.
- `CallManager.HangUp()` (`CallManager.cs:871`) already does the right teardown from `Ringing`: cancels the
  pending HT801 INVITE (the bell stops), hangs up the active BT device (`HangupCallAsync`, the
  `{command:"hangup"}` to the HFP helper) or `_bluetoothAdapter.TerminateCallAsync()`, calls
  `_boundAdapter.OnCallHungUpAsync()` (the GV adapter tears down media and sends a SIP BYE), records the end
  time in call history, and returns to `Idle`, which broadcasts `CallStateChanged(phoneId, "Idle")`.

⚠ **`POST /api/phone/simulate/hook?offHook=false` would reach that same `HangUp()` today, and Radio Console
deliberately does NOT use it.** It is a developer simulation: its log says `Hook change: ON-HOOK` for a handset
nobody touched, it is unconditional (it would hang up an `InCall` call just as readily, so a tap that lands a
moment after the handset is lifted would cut off a call in progress), and wiring a production button to a
`simulate/` route is the kind of contract that breaks silently when that route changes. Radio Console has built
the button behind a capability flag instead and is waiting on a real endpoint.

## The request

```
POST /api/phone/decline?phoneId=default
```

| When | Response | Effect |
|---|---|---|
| The phone is `Ringing` | `200 {"declined": true}` | The same teardown as `HangUp()`; ends in `Idle`; `CallStateChanged(phoneId, "Idle")` is broadcast as usual |
| The phone is in any other state (`Idle`, `Dialing`, `InCall`) | `409 {"declined": false, "state": "<current state>"}` | **Nothing.** In particular, never hang up an `InCall` call |
| Unknown `phoneId` | `404` | Nothing |

Requirements, in order of importance:

1. **Only from `Ringing`, decided atomically.** The check and the teardown must not straddle a handset lift. If
   the rotary is picked up between the tap and the request, the answer is `409` and the call continues.
2. **Log it as what it is** — `Incoming call declined from Radio Console` (or similar), not a hook change. Please
   keep the number out of the line, or masked, as you do elsewhere.
3. **Call history:** the entry should read as not answered. A distinct "declined" marker would be nice to have
   but is not needed by Radio Console.
4. **Do not require a call id.** Radio Console never receives one on the hub; `GET /api/phone/status` does return
   `CallId`, and if you prefer a `callId` guard (`409` when it does not match), say so and Radio Console will send
   it from that read.

## What we need to know back: what does the CALLER experience on each path?

Radio Console will document this for the owner, and the owner will check it on a real call. From reading the
code we expect the following, but have not verified any of it:

- **Bluetooth / HFP (the call arrives on the paired cell phone):** the HFP hang-up rejects the ringing call on the
  cell, so the carrier sends the caller to the cell's voicemail (carrier-dependent; some play a busy tone).
- **Google Voice (`GVApi` adapter):** per our 2026-09-11 prompt
  (`2026-09-11-radioconsole-inbound-hangup-never-arrives.md`), the GV leg is **answered** before the rotary
  rings. So declining ends an already-connected GV call: the caller hears the call drop, not voicemail, and only
  after Google notices the media teardown (that prompt notes your SIP BYE to Google is silently ignored).
  Please confirm, and say whether there is a way to send a GV caller to voicemail instead.
- **SIP trunk (`SipTrunkCallAdapter`):** we did not establish this.

## How Radio Console uses it

`Radio.Web`'s `PhoneApiService.DeclineCallAsync` posts to the route above. The banner's **Ignore** button is
enabled only when `Radio.Web` is configured with `RotaryPhone:DeclineSupported = true`; it ships `false`. After a
`200`, the banner shows "Ending call…" and closes when your `Idle` broadcast arrives, the same as a caller
cancelling. A non-success response leaves the banner up with an error line.

When this ships, tell the Radio Console session (or the owner) the route and the caller behaviour per path, and
the flag is flipped there.

## Related, not requested here

- The inbound-hangup defect in the 2026-09-11 prompt matters to the banner too: if a GV caller's hang-up never
  reaches the box, the call stays `Ringing` until the handset is lifted, and so does the banner. Radio Console
  re-reads `GET /api/phone/status` every few seconds while the banner is up, so it closes as soon as you report
  anything other than `Ringing`.
- The boundary doc's Change Log was **not** edited from this side: the coordinator limited this session to this
  one file in your repo. Please add the entry when you take the work.
