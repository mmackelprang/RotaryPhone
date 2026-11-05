# Request: stop `bt_manager.py` making "Grandpas Phone" discoverable

**From:** Radio Console session, 2026-11-05
**Owner decision:** "Hide it". Turn discoverable off on `hci1`, and leave the adapter and RotaryPhone's Bluetooth running.
**Why now:** the owner has never used "Grandpas Phone" and wants it out of phones' pairing lists while first-connect problems on "Grandpas Radio" are being worked. A visible second name invites the cross-adapter pairing that rule #8 exists to prevent. This is a suspected contributor, not a proven one.

## The defect

`scripts/bt_manager.py:846` sets `Discoverable` to `True` unconditionally on start, and `:845` sets `DiscoverableTimeout` to 0, so it never lapses. That undoes the boundary doc's stated state, "Sets hci1 alias 'Grandpas Phone', discoverable off".

## What I verified

Read on the box, 2026-11-05:

| Check | Result |
|---|---|
| `radio-bt-setup` journal | `Nov 01 03:01:44 hci1 discoverable: off` |
| `rotary-phone` started | `Nov 01 03:01:47`, three seconds later |
| `bluetoothctl show` on `10:91:D1:FE:00:46` | `Discoverable: yes`, `DiscoverableTimeout: 0` |
| `grep -i discoverable` in RotaryPhone `*.cs`, `*.sh`, `*.json`, `*.py` | The only unconditional setter is `bt_manager.py:846` |

**Inferred, not observed:** that `bt_manager.py` is what flipped it. Nothing logged the change, and `/etc/bluetooth/main.conf` sets no `Discoverable`. The file's own log line reads `discoverable=on`, so `rotary-phone`'s log should confirm it.

## The ask

1. Stop `bt_manager.py` setting `Discoverable` to `True` at startup, and fix the `:848` log line to match. `Pairable` and `Powered` stay.
2. Keep `set_adapter` working for an explicit request. This changes only the startup default.
3. Deploy it, then confirm `Discoverable: no` on `hci1` after a `rotary-phone` restart.
4. Update the boundary doc's Change Log, per the protocol.

## What must not change

Calls keep working. Your 2026-10-04 reply says they arrive as Google Voice SIP INVITEs, and no phone is connected over HFP on `hci1`, so this should not touch them. Check that against today's code, because that reply is a month old. Anything that needs `hci1` to be discoverable for pairing would need another route; if you find one, say so before changing it.

`hci0` is not in scope.

**Our side needs no change:** `radio-bt-setup.sh` already sets `hci1` discoverable off.
