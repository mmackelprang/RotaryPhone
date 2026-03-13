#!/usr/bin/env python3
"""
BlueZ HFP Profile1 agent and Bluetooth adapter manager.

Manages the BT adapter (alias, power), registers as an HFP Hands-Free unit
with BlueZ, receives RFCOMM connections from the Audio Gateway (phone),
and outputs JSON events to stdout.

The .NET BlueZHfpAdapter reads these JSON events to fire call state changes.

Requires: python3-dbus, python3-gi (both available on Ubuntu 24.04)

Usage:
    python3 bt_manager.py [--adapter /org/bluez/hci1] [--alias "Rotary Phone"]

Events written to stdout (one JSON per line):
    {"event":"ready"}
    {"event":"adapter_ready","address":"AA:BB:CC:DD:EE:FF","name":"Rotary Phone"}
    {"event":"ring","number":"+15551234567"}
    {"event":"call_active"}
    {"event":"call_ended"}
    {"event":"answered_on_phone"}
    {"event":"connected","address":"D4:3A:2C:64:87:9E"}
    {"event":"disconnected","address":"D4:3A:2C:64:87:9E"}
    {"event":"error","message":"..."}

Commands read from stdin (one JSON per line):
    {"command":"answer"}
    {"command":"hangup"}
    {"command":"dial","number":"5551234"}
"""

import argparse
import dbus
import dbus.service
import dbus.mainloop.glib
import json
import os
import signal
import sys
import threading
import traceback
from gi.repository import GLib

HFP_HF_UUID = "0000111e-0000-1000-8000-00805f9b34fb"
HFP_AG_UUID = "0000111f-0000-1000-8000-00805f9b34fb"
PROFILE_PATH = "/org/rotaryphone/hfp_profile"

# HFP HF features bitmask (minimal: nothing fancy)
HF_FEATURES = 0


def emit(event_dict):
    """Write a JSON event to stdout (thread-safe)."""
    try:
        line = json.dumps(event_dict, separators=(",", ":"))
        sys.stdout.write(line + "\n")
        sys.stdout.flush()
    except (BrokenPipeError, IOError):
        sys.exit(0)


def log(msg):
    """Write to stderr for debugging (visible in journalctl)."""
    print(f"[bt_manager] {msg}", file=sys.stderr, flush=True)


class HfpConnection:
    """Manages a single RFCOMM connection to an HFP Audio Gateway."""

    def __init__(self, device_path, fd):
        self.device_path = device_path
        self.address = self._extract_address(device_path)
        self.fd = fd
        self.file = os.fdopen(fd, "r+b", buffering=0)
        self.running = True
        self.indicator_map = {}  # name -> index (1-based)
        self.call_active = False
        self.callsetup = 0
        self.clip_number = None
        self.slc_established = False
        self._read_buffer = b""
        self._we_sent_ata = False

    @staticmethod
    def _extract_address(device_path):
        """Extract MAC address from BlueZ device path."""
        # /org/bluez/hci0/dev_D4_3A_2C_64_87_9E -> D4:3A:2C:64:87:9E
        for part in device_path.split("/"):
            if part.startswith("dev_"):
                return part[4:].replace("_", ":")
        return device_path

    def run(self):
        """Main loop: establish SLC then monitor for call events."""
        emit({"event": "connected", "address": self.address})
        log(f"RFCOMM connected: {self.address}")

        try:
            self._establish_slc()
            self._monitor_events()
        except (OSError, IOError) as e:
            log(f"RFCOMM connection lost: {e}")
        except Exception as e:
            log(f"Unexpected error: {e}")
            traceback.print_exc(file=sys.stderr)
        finally:
            self.running = False
            try:
                self.file.close()
            except Exception:
                pass
            emit({"event": "disconnected", "address": self.address})
            log(f"RFCOMM disconnected: {self.address}")

    def _read_line(self, timeout=5.0):
        """Read a CR/LF-terminated line from RFCOMM."""
        import select

        while self.running:
            # Check for complete line in buffer
            for sep in (b"\r\n", b"\r", b"\n"):
                idx = self._read_buffer.find(sep)
                if idx >= 0:
                    line = self._read_buffer[:idx].decode("utf-8", errors="replace").strip()
                    self._read_buffer = self._read_buffer[idx + len(sep):]
                    return line

            # Wait for more data
            ready, _, _ = select.select([self.file], [], [], timeout)
            if not ready:
                return None  # timeout
            chunk = self.file.read(1024)
            if not chunk:
                raise IOError("RFCOMM EOF")
            self._read_buffer += chunk

    def _read_until_ok(self, timeout=5.0):
        """Read lines until OK or ERROR, return all lines."""
        lines = []
        while self.running:
            line = self._read_line(timeout)
            if line is None:
                break
            if line:
                lines.append(line)
            if line in ("OK", "ERROR"):
                break
        return lines

    def _send(self, command):
        """Send an AT command to the AG."""
        log(f"TX: {command}")
        self.file.write((command + "\r").encode("utf-8"))

    def _establish_slc(self):
        """Perform HFP Service Level Connection setup."""
        log("Starting SLC setup...")

        # The AG typically sends +BRSF first after RFCOMM connect.
        # Read any initial data from the AG.
        initial_lines = []
        while True:
            line = self._read_line(timeout=2.0)
            if line is None:
                break
            if line:
                initial_lines.append(line)
                log(f"RX (initial): {line}")

        # If AG sent +BRSF, respond with our features
        ag_sent_brsf = any("+BRSF" in l for l in initial_lines)
        if not ag_sent_brsf:
            # Some AGs wait for us to initiate. Send AT+BRSF first.
            pass

        # Send our supported features
        self._send(f"AT+BRSF={HF_FEATURES}")
        resp = self._read_until_ok()
        for line in resp:
            log(f"RX: {line}")

        # Query indicator descriptions
        self._send("AT+CIND=?")
        resp = self._read_until_ok()
        for line in resp:
            log(f"RX: {line}")
            if line.startswith("+CIND:"):
                self._parse_indicator_descriptions(line)

        # Query current indicator values
        self._send("AT+CIND?")
        resp = self._read_until_ok()
        for line in resp:
            log(f"RX: {line}")
            if line.startswith("+CIND:"):
                self._parse_indicator_values(line)

        # Enable indicator event reporting
        self._send("AT+CMER=3,0,0,1")
        resp = self._read_until_ok()
        for line in resp:
            log(f"RX: {line}")

        # Enable caller ID presentation
        self._send("AT+CLIP=1")
        resp = self._read_until_ok()
        for line in resp:
            log(f"RX: {line}")

        self.slc_established = True
        log(f"SLC established. Indicator map: {self.indicator_map}")

    def _parse_indicator_descriptions(self, line):
        """Parse +CIND: ("service",(0,1)),("call",(0-1)),... to build indicator_map."""
        # Strip +CIND: prefix
        content = line[len("+CIND:"):].strip()

        index = 1
        i = 0
        while i < len(content):
            if content[i] == "(":
                # Find the indicator name in quotes
                q1 = content.find('"', i)
                if q1 < 0:
                    break
                q2 = content.find('"', q1 + 1)
                if q2 < 0:
                    break
                name = content[q1 + 1:q2]
                self.indicator_map[name] = index
                index += 1
                # Skip past the closing paren of this indicator group
                depth = 0
                j = i
                while j < len(content):
                    if content[j] == "(":
                        depth += 1
                    elif content[j] == ")":
                        depth -= 1
                        if depth == 0:
                            i = j + 1
                            break
                    j += 1
                else:
                    break
            else:
                i += 1

        log(f"Parsed indicators: {self.indicator_map}")

    def _parse_indicator_values(self, line):
        """Parse +CIND: 1,0,0,0,5,0,5 to get current call state."""
        content = line[len("+CIND:"):].strip()
        values = [v.strip() for v in content.split(",")]

        call_idx = self.indicator_map.get("call")
        callsetup_idx = self.indicator_map.get("callsetup")

        if call_idx and call_idx <= len(values):
            self.call_active = values[call_idx - 1] == "1"
        if callsetup_idx and callsetup_idx <= len(values):
            self.callsetup = int(values[callsetup_idx - 1])

        log(f"Current state: call_active={self.call_active}, callsetup={self.callsetup}")

    def _monitor_events(self):
        """Monitor for unsolicited AT results from the AG."""
        log("Monitoring for call events...")

        while self.running:
            line = self._read_line(timeout=30.0)
            if line is None:
                continue  # timeout, just keep waiting
            if not line:
                continue

            log(f"RX: {line}")
            self._handle_unsolicited(line)

    def _handle_unsolicited(self, line):
        """Handle an unsolicited result from the AG."""
        if line == "RING":
            # Incoming call ringing
            number = self.clip_number or "Unknown"
            emit({"event": "ring", "address": self.address, "number": number})
            log(f"RING - incoming call from {number}")

        elif line.startswith("+CLIP:"):
            # Caller ID: +CLIP: "number",type
            self._parse_clip(line)

        elif line.startswith("+CIEV:"):
            # Indicator event: +CIEV: index,value
            self._handle_ciev(line)

    def _parse_clip(self, line):
        """Parse +CLIP: "number",type and store the number."""
        content = line[len("+CLIP:"):].strip()
        q1 = content.find('"')
        q2 = content.find('"', q1 + 1) if q1 >= 0 else -1
        if q1 >= 0 and q2 > q1:
            self.clip_number = content[q1 + 1:q2]
            log(f"CLIP number: {self.clip_number}")
            # Emit ring with number (may duplicate if RING already sent, but ensures number is included)
            emit({"event": "ring", "address": self.address, "number": self.clip_number})

    def _handle_ciev(self, line):
        """Handle +CIEV: indicator_index, value."""
        content = line[len("+CIEV:"):].strip()
        parts = content.split(",")
        if len(parts) != 2:
            return

        try:
            ind_index = int(parts[0].strip())
            ind_value = int(parts[1].strip())
        except ValueError:
            return

        call_idx = self.indicator_map.get("call")
        callsetup_idx = self.indicator_map.get("callsetup")

        if ind_index == call_idx:
            if ind_value == 1 and not self.call_active:
                # Call became active (answered)
                self.call_active = True
                emit({"event": "call_active", "address": self.address,
                      "answered_locally": not self._we_sent_ata})
                self._we_sent_ata = False
                log("Call active (answered)")
            elif ind_value == 0 and self.call_active:
                # Call ended
                self.call_active = False
                self.clip_number = None
                self._we_sent_ata = False
                emit({"event": "call_ended", "address": self.address})
                log("Call ended")

        elif ind_index == callsetup_idx:
            prev_callsetup = self.callsetup
            self.callsetup = ind_value

            if ind_value == 1:
                # Incoming call setup — emit ring event
                # Number may come later via +CLIP, so use what we have
                number = self.clip_number or "Unknown"
                emit({"event": "ring", "address": self.address, "number": number})
                log(f"Incoming call setup — ring (number: {number})")
            elif ind_value == 0 and prev_callsetup == 1 and not self.call_active:
                # Call setup ended without answering (caller hung up / rejected)
                self.clip_number = None
                self._we_sent_ata = False
                emit({"event": "call_ended", "address": self.address})
                log("Call setup ended (unanswered)")

    def send_command(self, command):
        """Send an AT command (from stdin JSON)."""
        try:
            self._send(command)
        except (OSError, IOError) as e:
            log(f"Failed to send command: {e}")

    def answer(self):
        self._we_sent_ata = True
        self.send_command("ATA")

    def hangup(self):
        self.send_command("AT+CHUP")

    def dial(self, number):
        self.send_command(f"ATD{number};")


class HfpProfile(dbus.service.Object):
    """BlueZ Profile1 implementation for HFP Hands-Free (multi-device)."""

    def __init__(self, bus, path):
        super().__init__(bus, path)
        self._connections = {}  # device_path -> HfpConnection
        self._conn_threads = {}

    @dbus.service.method("org.bluez.Profile1", in_signature="oha{sv}", out_signature="")
    def NewConnection(self, device, fd, fd_properties):
        """Called by BlueZ when an RFCOMM connection is established."""
        fd = fd.take()  # Take ownership of the file descriptor
        log(f"NewConnection: device={device}, fd={fd}, props={dict(fd_properties)}")

        conn = HfpConnection(device, fd)
        self._connections[device] = conn
        t = threading.Thread(target=self._run_connection, args=(device, conn), daemon=True)
        self._conn_threads[device] = t
        t.start()

    def _run_connection(self, device, conn):
        """Run connection handler and clean up when done."""
        try:
            conn.run()
        finally:
            self._connections.pop(device, None)
            self._conn_threads.pop(device, None)

    @dbus.service.method("org.bluez.Profile1", in_signature="o", out_signature="")
    def RequestDisconnection(self, device):
        """Called by BlueZ when the device disconnects."""
        log(f"RequestDisconnection: {device}")
        conn = self._connections.get(device)
        if conn:
            conn.running = False

    @dbus.service.method("org.bluez.Profile1", in_signature="", out_signature="")
    def Release(self):
        """Called by BlueZ when the profile is unregistered."""
        log("Profile released")

    def _find_connection_by_address(self, address):
        """Find a connection by MAC address."""
        for conn in self._connections.values():
            if conn.address == address:
                return conn
        return None

    def _find_active_connection(self):
        """Find a connection with an active or incoming call."""
        for conn in self._connections.values():
            if conn.running and (conn.call_active or conn.callsetup > 0):
                return conn
        # Fall back to any running connection
        for conn in self._connections.values():
            if conn.running:
                return conn
        return None

    def handle_stdin_command(self, cmd_dict):
        """Handle a command from stdin."""
        command = cmd_dict.get("command")
        address = cmd_dict.get("address")

        if command in ("answer", "hangup", "dial"):
            # Find connection by address or fall back to active connection
            conn = None
            if address:
                conn = self._find_connection_by_address(address)
            if not conn:
                conn = self._find_active_connection()
            if not conn:
                emit({"event": "error", "message": "No active RFCOMM connection"})
                return

            if command == "answer":
                conn.answer()
            elif command == "hangup":
                conn.hangup()
            elif command == "dial":
                number = cmd_dict.get("number", "")
                if number:
                    conn.dial(number)
        elif command in ("start_discovery", "stop_discovery", "pair", "remove_device",
                         "connect", "disconnect", "confirm_pairing", "set_adapter"):
            # These commands are handled at the adapter level, not per-connection
            # They'll be implemented in Phase 4 (Pairing UI)
            log(f"Command '{command}' received — not yet implemented")
        else:
            emit({"event": "error", "message": f"Unknown command: {command}"})


def stdin_reader(profile):
    """Read JSON commands from stdin in a separate thread."""
    try:
        if sys.stdin.closed or not sys.stdin.readable():
            return
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                cmd = json.loads(line)
                GLib.idle_add(lambda c=cmd: profile.handle_stdin_command(c) or False)
            except json.JSONDecodeError:
                log(f"Invalid JSON on stdin: {line}")
    except (IOError, BrokenPipeError, ValueError):
        pass
    log("stdin reader exited")


def main():
    parser = argparse.ArgumentParser(description="RotaryPhone Bluetooth Manager")
    parser.add_argument("--adapter", default=None, help="BlueZ adapter path (e.g., /org/bluez/hci1)")
    parser.add_argument("--alias", default="Rotary Phone", help="Adapter alias for pairing")
    args = parser.parse_args()

    # Set up D-Bus main loop
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SystemBus()

    # Configure adapter if specified
    adapter_path = args.adapter
    if adapter_path:
        try:
            adapter = dbus.Interface(
                bus.get_object("org.bluez", adapter_path),
                "org.freedesktop.DBus.Properties"
            )
            adapter.Set("org.bluez.Adapter1", "Alias", args.alias)
            adapter.Set("org.bluez.Adapter1", "Powered", dbus.Boolean(True))
            log(f"Configured adapter {adapter_path}: alias={args.alias}, powered=on")
        except dbus.exceptions.DBusException as e:
            log(f"Warning: could not configure adapter {adapter_path}: {e}")

    # Create Profile1 object
    profile = HfpProfile(bus, PROFILE_PATH)

    # Register with BlueZ ProfileManager
    manager = dbus.Interface(
        bus.get_object("org.bluez", "/org/bluez"),
        "org.bluez.ProfileManager1"
    )

    opts = {
        "Role": "client",  # We are the HF (client), phone is AG (server)
        "Name": "RotaryPhone HFP",
        "AutoConnect": dbus.Boolean(True),
    }

    try:
        manager.RegisterProfile(PROFILE_PATH, HFP_HF_UUID, opts)
        log(f"Registered HFP HF profile at {PROFILE_PATH}")
    except dbus.exceptions.DBusException as e:
        error_name = e.get_dbus_name()
        if "AlreadyExists" in str(error_name):
            log("Profile already registered, continuing...")
        else:
            emit({"event": "error", "message": f"Failed to register profile: {e}"})
            log(f"Failed to register profile: {e}")
            sys.exit(1)

    emit({"event": "ready"})
    if adapter_path:
        try:
            adapter_props = dbus.Interface(
                bus.get_object("org.bluez", adapter_path),
                "org.freedesktop.DBus.Properties"
            )
            adapter_addr = str(adapter_props.Get("org.bluez.Adapter1", "Address"))
            emit({"event": "adapter_ready", "address": adapter_addr, "name": args.alias})
        except Exception:
            pass
    log("BT manager ready, waiting for connections...")

    # Try to connect HFP on already-connected devices
    try:
        obj_manager = dbus.Interface(
            bus.get_object("org.bluez", "/"),
            "org.freedesktop.DBus.ObjectManager"
        )
        objects = obj_manager.GetManagedObjects()
        for path, interfaces in objects.items():
            if "org.bluez.Device1" not in interfaces:
                continue
            if adapter_path and not path.startswith(adapter_path + "/"):
                continue
            dev_props = interfaces["org.bluez.Device1"]
            if not dev_props.get("Connected", False):
                continue
            uuids = dev_props.get("UUIDs", [])
            # Check if device supports HFP-AG (0000111f)
            if any("111f" in str(u).lower() for u in uuids):
                name = str(dev_props.get("Alias", dev_props.get("Address", path)))
                log(f"Found connected HFP-AG device: {name} at {path}")
                try:
                    dev = dbus.Interface(
                        bus.get_object("org.bluez", path),
                        "org.bluez.Device1"
                    )
                    dev.ConnectProfile(HFP_AG_UUID)
                    log(f"Triggered HFP connection to {name}")
                except dbus.exceptions.DBusException as e:
                    log(f"Failed to connect HFP profile to {name}: {e}")
    except Exception as e:
        log(f"Error scanning for connected devices: {e}")

    # Watch for device connection changes to auto-connect HFP
    def on_properties_changed(interface, changed, invalidated, path=None):
        if interface != "org.bluez.Device1":
            return
        if adapter_path and path and not path.startswith(adapter_path + "/"):
            return
        if "Connected" not in changed:
            return
        connected = bool(changed["Connected"])
        if not connected:
            return
        # Device just connected — try to connect HFP after a short delay
        def try_connect_hfp():
            try:
                dev_obj = bus.get_object("org.bluez", path)
                dev_props = dbus.Interface(dev_obj, "org.freedesktop.DBus.Properties")
                uuids = dev_props.Get("org.bluez.Device1", "UUIDs")
                if any("111f" in str(u).lower() for u in uuids):
                    name = str(dev_props.Get("org.bluez.Device1", "Alias"))
                    log(f"Device connected with HFP-AG: {name}, triggering HFP connection...")
                    dev = dbus.Interface(dev_obj, "org.bluez.Device1")
                    dev.ConnectProfile(HFP_AG_UUID)
                    log(f"HFP connection triggered for {name}")
            except Exception as e:
                log(f"Auto-connect HFP failed: {e}")
            return False  # Don't repeat

        # Delay to let the device finish connecting other profiles
        GLib.timeout_add(2000, try_connect_hfp)

    bus.add_signal_receiver(
        on_properties_changed,
        signal_name="PropertiesChanged",
        dbus_interface="org.freedesktop.DBus.Properties",
        bus_name="org.bluez",
        path_keyword="path"
    )

    # Start stdin reader thread
    main_loop = GLib.MainLoop()

    stdin_thread = threading.Thread(target=stdin_reader, args=(profile,), daemon=True)
    stdin_thread.start()

    # Handle signals for clean shutdown
    def on_signal(signum, frame):
        log(f"Received signal {signum}, shutting down...")
        main_loop.quit()

    signal.signal(signal.SIGTERM, on_signal)
    signal.signal(signal.SIGINT, on_signal)

    try:
        main_loop.run()
    except KeyboardInterrupt:
        pass
    finally:
        try:
            manager.UnregisterProfile(PROFILE_PATH)
            log("Unregistered HFP profile")
        except Exception:
            pass


if __name__ == "__main__":
    main()
