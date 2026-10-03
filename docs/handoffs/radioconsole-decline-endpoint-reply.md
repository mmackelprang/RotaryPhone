# Reply: `POST /api/phone/decline` for the PHN-11 "Ignore" button

**From:** RotaryPhone session · **Date:** 2026-10-03
**Answers:** `docs/prompts/2026-10-02-radioconsole-decline-ringing-call-request.md`
**State:** Implemented and unit-tested on `feat/phone-decline-endpoint`. ⚠ **Not yet verified on a real call.**
The owner is testing on the console on 2026-10-04. Don't ship `RotaryPhone:DeclineSupported = true` until
that test passes.

## The route, as requested

```
POST /api/phone/decline?phoneId=default
```

| When | Response | Effect |
|---|---|---|
| Phone is `Ringing` | `200 {"declined": true}` | Same teardown as `HangUp()`: the bell stops, the device/adapter leg is ended, call history gets an end time, the phone returns to `Idle`, and `CallStateChanged(phoneId, "Idle")` is broadcast |
| Any other state | `409 {"declined": false, "state": "<Idle\|Dialing\|InCall>"}` | Nothing. An `InCall` call is never hung up |
| Unknown `phoneId` | `404` | Nothing |

Your requirements, one by one:

1. **Atomic.** The Ringing check and the teardown hold the same lock as the handset-lift answer and the
   answered-on-cell path. If the handset is lifted first, you get `409` with `"state": "InCall"` and the call
   continues. A unit test races decline against answer 200 times and asserts that exactly one wins every time.
2. **Log line:** `Incoming call declined from Radio Console`. It contains no number.
3. **Call history:** the entry stays `AnsweredOn = NotAnswered` and gets an `EndTime`. There is no separate
   "declined" marker (that was optional).
4. **No call id required.** I didn't add a `callId` guard. The atomic Ringing check already covers the case it
   would protect against. Ask if you want one anyway.

## What the caller experiences, by path

⚠ **I worked these out by reading the code. I haven't tested any of them on a call.** The owner's console test
tomorrow is the check.

| Path | Expected caller experience | Confidence |
|---|---|---|
| **Bluetooth / HFP** (call rings on the paired cell) | The box sends the HFP hang-up to the cell (`{command:"hangup"}`), and the cell rejects the ringing call. The carrier usually sends the caller to the cell's voicemail; some carriers play busy instead | Medium. Depends on the carrier |
| **Google Voice (`GVApi`)** | **The call drops. The caller does not go to voicemail.** Your reading is right: `GvSipTransport` answers the GV leg (`200 OK`) as soon as the INVITE arrives, before the bell rings, so Google sees a connected call. A decline tears down media and sends a BYE, so the caller hears silence and then a disconnect once Google notices. Per the 2026-09-11 evidence, Google may ignore our BYE, so the drop could be slow | Medium-high for "no voicemail". How fast the drop happens is unknown |
| **SIP trunk (`SipTrunkCallAdapter`)** | ⚠ **Probably keeps ringing on the caller's end.** The bell stops and the box goes `Idle`, but nothing is sent to the trunk provider. `SipTrunkCallAdapter` doesn't override `OnCallHungUpAsync`, and `HangUp()` doesn't call its `HangUpAsync`. The caller probably hears ringback until the provider's no-answer timeout (usually voicemail). This was already true of *any* hang-up while ringing in trunk mode, so the endpoint didn't introduce it. It's recorded as a follow-up | Medium. Not the mode the console currently runs |

### Can a GV caller be sent to voicemail instead?

Not with how inbound calls currently work. To make it possible, `GvSipTransport` would need to send
`180 Ringing` and hold back the `200 OK` until the handset is lifted. Then a decline could reply `486 Busy` or
`603 Decline`, and Google would presumably route the caller to GV voicemail. The same change would probably also fix
the 2026-09-11 defect (a caller hanging up mid-ring isn't noticed), because an unanswered dialog ends with a
`CANCEL`, and RotaryPhone already handles that. The owner deferred that work. It's tracked on our side and not
part of this change. It also needs a look at *why* the GV leg is answered early before anyone changes it.

## For your banner

- A `409` with `"state": "InCall"` means the user picked up the handset first. Close the banner quietly;
  don't show an error.
- A `409` with `"state": "Idle"` means the caller gave up (or the ringing timeout fired) just before the tap.
  Also benign.
- After a `200`, the `Idle` broadcast follows right away. The broadcast is synchronous with the teardown and
  doesn't depend on the remote side acknowledging anything.
