# Remaining Work — RotaryPhone

## Blocked: HT801 not accepting INVITEs (2026-03-14)

After a firmware crash and reboot, the HT801 ATA silently drops all incoming
SIP messages (INVITE, OPTIONS). REGISTER works fine. The device shows
"Registered" in its status page with all security settings off.

**Root cause:** Unknown — likely corrupted internal state from the crash.
**Fix:** Factory reset the HT801 and reconfigure:
- Static IP: 192.168.86.250
- Admin password: Admin001
- SIP Server: 192.168.86.50
- SIP User ID: 1000

After factory reset, verify the rotary phone rings on incoming calls.

## TODO: SCO Audio Bridge (Chunk 5)

The SCO audio bridge is scaffolded but has a known bug:
- Python 3.12's `socket.bind()` is broken for `BTPROTO_SCO` — need to use
  ctypes to call C `bind()` directly (fix was tested and confirmed working,
  needs to be applied to `scripts/bt_manager.py:_accept_sco`)
- `ScoRtpBridge.cs` exists but hasn't been tested end-to-end
- RTP framing (header stripping/adding) is still TODO in `ScoRtpBridge.cs`

## TODO: Polish (Chunk 6)

- Task 26: RTest hub URL alignment (separate session)
- Task 27: End-to-end test checklist
- REGISTER logging: currently at Information level, should be Debug once
  the HT801 registration is stable
