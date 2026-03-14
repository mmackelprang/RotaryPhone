# RotaryPhone: Prevent Cross-Adapter BT Pairing Conflicts

**From:** Radio Console session (2026-03-14)
**Priority:** High — this caused multiple hours of debugging across 3+ sessions
**Boundary doc:** `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`

## Problem

When a phone (e.g., Pixel 8 Pro) is paired on BOTH BT adapters — hci0 (music, Radio Console) and hci1 (voice, RotaryPhone) — PipeWire creates duplicate `bluez_card` devices with the same MAC-derived name. This breaks WirePlumber's profile resolution: `audio-gateway` profile shows 0 sinks/sources, no `bluez_input` node is created, and A2DP music streaming silently fails.

This has happened multiple times when the phone connects to "Grandpas Phone" (hci1) for any reason — either auto-connect or discovery — while already paired on hci0.

## What Radio Console Already Does (as of PR #347)

The boot script (`radio-bt-setup.sh`) now:
1. Sets hci1 discoverable=**off** at boot (reduces accidental phone pairing on hci1)
2. Removes stale hci1 pairings for known music devices listed in `/opt/radio-console/config/bt-music-devices.conf`
3. Restarts BlueZ if stale pairings were removed

But this only runs at boot. **At runtime, RotaryPhone's `bt_manager.py` is the gatekeeper** — it must not pair devices that are already on hci0.

## What RotaryPhone Needs To Do

### 1. Add cross-adapter pairing check to `bt_manager.py`

Before pairing or connecting any device on hci1, check if that device's MAC is already paired on hci0:

```python
import os

HCI0_PAIRING_DIR = "/var/lib/bluetooth/78:20:51:F5:FB:A7"

def is_paired_on_music_adapter(device_mac: str) -> bool:
    """Check if device is already paired on hci0 (music adapter)."""
    pairing_path = os.path.join(HCI0_PAIRING_DIR, device_mac)
    return os.path.isdir(pairing_path)
```

Use this check:
- Before accepting a pairing request
- Before initiating a connection
- In any auto-connect logic
- In the discovery/pairing event handler

If the device IS paired on hci0, log a warning and reject the pairing/connection:
```python
if is_paired_on_music_adapter(device_mac):
    logger.warning(f"Device {device_mac} is already paired on music adapter (hci0) — "
                   f"rejecting pairing on voice adapter to prevent dual-adapter conflict")
    return  # Do not pair
```

### 2. Alternative: D-Bus check (more reliable but heavier)

Instead of checking the filesystem, you can query BlueZ via D-Bus:

```python
import dbus

def is_paired_on_hci0(device_mac: str) -> bool:
    """Check BlueZ D-Bus for device paired on hci0."""
    bus = dbus.SystemBus()
    device_path = f"/org/bluez/hci0/dev_{device_mac.replace(':', '_')}"
    try:
        device = bus.get_object("org.bluez", device_path)
        props = dbus.Interface(device, "org.freedesktop.DBus.Properties")
        paired = props.Get("org.bluez.Device1", "Paired")
        return bool(paired)
    except dbus.exceptions.DBusException:
        return False  # Device not known on hci0
```

### 3. What NOT to do

- Do NOT call `bluetoothctl remove <MAC>` to "fix" a dual-paired device — this removes it from ALL adapters, including hci0 where it should stay
- Do NOT set hci1 to discoverable unless needed for a specific pairing flow, and turn it off again immediately after
- Do NOT register A2DP profiles on hci1

## Testing

1. Pair a phone on hci0 (connect to "Grandpas Radio")
2. Try to pair the same phone on hci1 — bt_manager.py should reject it
3. Verify music audio still works on hci0 after the rejection
4. Pair a DIFFERENT phone on hci1 (one not on hci0) — should succeed normally

## Context: What Happens Without This Guard

Without this check, the failure mode is:
1. Phone pairs on hci1 (auto-connect, discovery, or explicit pairing)
2. PipeWire sees two `bluez_card.D4_3A_2C_64_87_9E` devices
3. WirePlumber's audio-gateway profile resolution breaks
4. No `bluez_input` node is created
5. Radio Console's BT capture stream fails after 20 retries
6. User hears nothing, no visualization, source shows "Stopped"
7. Only fix: manually delete `/var/lib/bluetooth/10:91:D1:FE:00:46/<MAC>/` and restart BlueZ

The boot script now automates step 7, but preventing step 1 is much better.
