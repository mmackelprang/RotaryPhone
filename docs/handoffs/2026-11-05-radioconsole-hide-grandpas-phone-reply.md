# Reply: stop `bt_manager.py` making "Grandpas Phone" discoverable

**From:** RotaryPhone session, 2026-11-05
**Answers:** `docs/prompts/2026-11-05-radioconsole-hide-grandpas-phone.md`
**Status:** fix is live on the box. `Discoverable: no` is confirmed after a `rotary-phone` restart. The `hci1` bond in the correction below is still open.

## What changed

Branch `fix/bt-manager-hide-hci1`: `scripts/bt_manager.py` startup sets `Discoverable` to `False`, and the log line now reads `discoverable=off`. `Powered`, `Pairable` and `DiscoverableTimeout` are unchanged. `set_adapter` is unchanged, so `PUT /api/bluetooth/adapter` still works for an explicit request.

**One difference from the ask:** the setter is flipped, not removed. BlueZ keeps `Discoverable` across a service restart and `radio-bt-setup.sh` runs only at boot, so removing it would have left `hci1` discoverable until the next reboot.

**Side effect:** an explicit on through `set_adapter` now lasts only until the next `bt_manager` start.

## Answers to your checks

| Question | Result |
|---|---|
| Did `bt_manager.py` flip it? (your inference) | **Confirmed.** Journal, `Nov 01 03:01:53`: `Configured adapter /org/bluez/hci1: alias=Grandpas Phone, powered=on, discoverable=on` |
| Do calls depend on `hci1` being discoverable? | **No.** `activeMode` on the box is `GVApi`. |
| Does pairing depend on it? | **No shipped route does.** The Devices page scans and pairs from the server side. Pairing started from a phone would need `PUT /api/bluetooth/adapter` first. No client code calls that endpoint today. |

## Correction to our 2026-10-04 reply

That reply said the Pixel is refused on `hci1` by the cross-adapter guard. What the box shows today does not match that.

| Property of `B0:D5:FB:D2:0D:68` (Pixel 10 Pro XL) | `hci0` | `hci1` |
|---|---|---|
| Paired | true | **true** |
| Bonded | true | **true** |
| Trusted | false | **true** |
| Connected | true | false |

1. **Rule #8 is currently broken for this phone.** It is bonded on both adapters. We did not determine when or how the `hci1` bond was made.
2. **Boot cleanup cannot catch it.** `/opt/radio-console/config/bt-music-devices.conf` lists the Pixel 8 Pro, Pixel 7 Pro and Tab A7 Lite, but not this phone, which fits the boot log's "0 stale pairings removed".
3. **HFP reaches `bt_manager` through the music adapter.** Journal, `Nov 05 13:55:43`: `NewConnection: device=/org/bluez/hci0/dev_B0_D5_FB_D2_0D_68`. `GET /api/bluetooth/devices` lists the phone as connected. `bt_manager.py:511` accepts HFP on any adapter on purpose. "No phone is connected over HFP on `hci1`" is still true, but only because the link is on `hci0`.

**Hiding `hci1` does not undo the bond.** The phone already knows this adapter, and a bonded phone does not need it discoverable to reconnect.

These may bear on the "Grandpas Radio" first-connect problems. That link is not proven.

## Not done, and needs a decision

The `hci1` bond was left in place. Removing it is destructive, was not in the ask, and could affect the phone's live connection.

If it should go, the likely route is to add the phone to `bt-music-devices.conf`, which is your file, and then remove the `hci1` bond. Please confirm with the owner first.

## Verified on the box

After the `rotary-phone` restart at 16:24:12 EST on 2026-11-05:

| Check | Result |
|---|---|
| `bluetoothctl show` on `10:91:D1:FE:00:46` | `Discoverable: no`, `Powered: yes`, `Pairable: yes` |
| Journal | `Configured adapter /org/bluez/hci1: alias=Grandpas Phone, powered=on, discoverable=off` |
| `GET /api/gvbridge/status` | `activeMode: GVApi`, `sipRegistered: true`, `wsConnected: true`, `degraded: false` |
| Pixel `Connected` | `hci0` true, `hci1` false |
| HFP link | Reconnected through `hci0` at 16:24:13 |

**Not tested:** a real inbound call. The status above shows the call path registered and healthy, not a call completing.

**Not observed:** whether the phone's `hci0` connection dropped during the restart. It was connected before and after.
