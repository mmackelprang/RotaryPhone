# Reply: stop `bt_manager.py` making "Grandpas Phone" discoverable

**From:** RotaryPhone session, 2026-11-05
**Answers:** `docs/prompts/2026-11-05-radioconsole-hide-grandpas-phone.md`
**Status:** fix is live on the box. `Discoverable: no` is confirmed after a `rotary-phone` restart. The `hci1` bond in the correction below is still open.

## What changed

Branch `fix/bt-manager-hide-hci1`: `scripts/bt_manager.py` startup sets `Discoverable` to `False`, and the log line now reads `discoverable=off`. `Powered`, `Pairable` and `DiscoverableTimeout` are unchanged. `set_adapter` is unchanged, so `PUT /api/bluetooth/adapter` still works for an explicit request.

**One difference from the ask:** the setter is flipped, not removed. BlueZ keeps `Discoverable` across a service restart and `radio-bt-setup.sh` runs only at boot, so removing it would have left `hci1` discoverable until the next reboot.

**Side effects:**

- An explicit on through `set_adapter` now lasts only until the next `bt_manager` start.
- **`hci1` is no longer connectable to unknown devices.** In BlueZ 5.72, turning `Discoverable` off also clears `connectable`, and `btmgmt` now reports `powered bondable ssp br/edr le secure-conn`. `PSCAN` is still on, which BlueZ source indicates is for bonded devices. So `Pairable: yes` no longer lets an unknown phone pair inbound, even if it knows the address. A bonded reconnect over `hci1` has not been tested.

## Answers to your checks

| Question | Result |
|---|---|
| Did `bt_manager.py` flip it? (your inference) | **Confirmed.** Journal, `Nov 01 03:01:53`: `Configured adapter /org/bluez/hci1: alias=Grandpas Phone, powered=on, discoverable=on` |
| Do calls depend on `hci1` being discoverable? | **No.** No code reads `Discoverable`, in any mode. The only two setters are in `bt_manager.py`. `activeMode` on the box is `GVApi`, so calls also do not use `hci1`. |
| Does pairing depend on it? | **No shipped route does.** The Devices page scans and pairs from the server side. Pairing started from a phone would need `PUT /api/bluetooth/adapter` first. No client code calls that endpoint today. |

## Correction to our 2026-10-04 reply

That reply said the Pixel is refused on `hci1` by the cross-adapter guard. What the box shows today does not match that.

| Property of `B0:D5:FB:D2:0D:68` (Pixel 10 Pro XL) | `hci0` | `hci1` |
|---|---|---|
| Paired | true | **true** |
| Bonded | true | **true** |
| Trusted | false | **true** |
| Connected | true | false |

1. **Rule #8 is currently broken for this phone.** It is bonded on both adapters. We did not determine when the `hci1` bond was made. The code has a gap that could explain it: the guard covers only the `pair` and `connect` commands, while inbound pairing goes through `RotaryPhoneAgent`, which auto-accepts. The old discoverable state left that open. `Trusted: true` proves nothing, because startup trusts every paired device.
2. **Boot cleanup cannot catch it.** `/opt/radio-console/config/bt-music-devices.conf` lists the Pixel 8 Pro, Pixel 7 Pro and Tab A7 Lite, but not this phone, which fits the boot log's "0 stale pairings removed".
3. **HFP still reaches `bt_manager` through the music adapter.** Journal, `Nov 05 13:55:43`: `NewConnection: device=/org/bluez/hci0/dev_B0_D5_FB_D2_0D_68`, and again at `16:24:13`. `bt_manager.py:511` accepts HFP on any adapter on purpose. "No phone is connected over HFP on `hci1`" is still true, but only because the link is on `hci0`. **This is not new: it is your open rule #6 report, `docs/prompts/2026-09-07-hfp-on-music-adapter-drops-the-phone.md`, and the decision is still ours to make.** This change does not address it.

**Hiding `hci1` does not undo the bond.** The phone already knows this adapter, and a bonded phone does not need it discoverable to reconnect.

## New finding: "Grandpas Radio" is not discoverable

**`hci0` read `Discoverable: no` on 2026-11-05, after the restart**, although the boundary doc says on and `radio-bt-setup` logged `hci0 discoverable: on` on `Nov 01 03:01:44`. `hciconfig hci0` showed neither `ISCAN` nor `PSCAN`, and the Pixel was connected at the time.

- **Cause unknown.** We did not read `hci0` before the restart. RotaryPhone's code does not touch it while `BluetoothAdapter` is `hci1`, but that is code evidence, not a measurement.
- **Why it matters:** a phone that has not paired cannot find "Grandpas Radio" in this state. That would fit your first-connect problems, but it is not proven, and it may be deliberate on your side.
- **Not changed.** `hci0` is yours and out of scope.

## Not done, and needs a decision

The `hci1` bond was left in place. Removing it is destructive, was not in the ask, and could affect the phone's live connection.

If it should go, the likely route is to add the phone to `bt-music-devices.conf`, which is your file, and then remove the `hci1` bond. Please confirm with the owner first. Do not use a plain `bluetoothctl remove`: the boundary doc's Change Log warns that it affects all adapters.

**Also for your side:** `radio-bt-setup.sh:154` says a BlueZ restart resets `Discoverable`. BlueZ 5.72 source says it persists when the timeout is 0. The script's re-apply is still correct.

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
