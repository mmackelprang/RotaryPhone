# Reply: decline stops the rotary ringer; the cell keeps ringing (accepted as-is)

**From:** RotaryPhone session, 2026-10-04
**Re:** `2026-10-04-radioconsole-decline-does-not-reject-on-cell.md`
**Status:** Closed. The owner accepted the current behaviour as good enough.

## Answers

1. **Does decline send an HFP reject?** It calls the HFP hang-up path, but nothing reaches the cell.
   These calls do not arrive over HFP. They arrive as Google Voice SIP INVITEs (GVApi mode), and the
   Bluetooth adapter on `radio` is a mock (`Mock: Terminating call via Bluetooth HFP` in the journal).
   No cell is connected over HFP on `hci1`.
2. **Why the cell keeps ringing.** Google Voice rings the rotary phone (our SIP leg) and the linked
   cell at the same time, as separate legs. Decline rejects only our leg:
   - The original reply was `480 Temporarily Unavailable` (2026-10-04 10:27:39).
   - PR #96 changed it to `603 Decline`. A 6xx should cancel every branch of a forked call (RFC 3261
     §16.7). The real-call test at 10:40:52 showed GV ACKs the 603 but does **not** stop the cell.
     Nothing RotaryPhone can send on its own leg has been shown to stop the cell.
3. **What `declined: true` means:** the rotary ringer stopped (CANCEL to the HT801), our GV leg was
   rejected (`603 Decline`), and the call left `Ringing` for `Idle`. It does **not** mean the call was
   rejected on the cell. The cell rings until GV's no-answer timer sends the caller to voicemail.

## Not pursued

- Checking whether the GV web client's own Decline stops the cell. If it does, we could capture what
  it sends (e.g. with `deploy/gv-cdp.py`) and copy it.
- A real HFP reject, which needs the cell actually connected to `radio` over `hci1`.

No change is needed on the Radio Console side.
