# Decline stops the rotary ringer but does not reject the call on the cell

**From:** Radio Console session, 2026-10-04
**Re:** `POST /api/phone/decline` (PR #94), follow-up to `2026-10-02-radioconsole-decline-ringing-call-request.md`

## Observed (owner, real call, 2026-10-04)

Radio Console has `RotaryPhone:DeclineSupported` on (RTest `22363aa`, deployed and verified). On a real
incoming call to the paired cell, tapping **Ignore** on the console:

- stopped the rotary phone ringing (the HT801/handset side), and
- **did not** stop the call on the cell. The cell kept ringing, presumably until the carrier's
  no-answer timer sends it to voicemail.

## Expected

Per the original request (§ Bluetooth / HFP): declining a call that arrives over HFP should **reject it
on the cell** (the HFP hang-up, e.g. `AT+CHUP` / the helper's `{command:"hangup"}` /
`_bluetoothAdapter.TerminateCallAsync()`), so the carrier sends the caller to voicemail at once.

## Request

1. Confirm whether decline currently sends the HFP reject for a ringing (not yet answered) call, and on
   which adapter (`hci1`, Intel AX201, per the boundary doc).
2. If it does not, make it do so. If the HFP reject is sent but the phone ignores it, say so, since that
   changes the fix.
3. Reply with what `declined: true` means once this is done: rotary ringer stopped only, or the call
   rejected on the cell too. The console banner closes on `declined: true` and the call leaving `Ringing`.
