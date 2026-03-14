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
import socket
import struct
import sys
import threading
import time
import traceback
from gi.repository import GLib

HFP_HF_UUID = "0000111e-0000-1000-8000-00805f9b34fb"
HFP_AG_UUID = "0000111f-0000-1000-8000-00805f9b34fb"
PROFILE_PATH = "/org/rotaryphone/hfp_profile"
AGENT_PATH = "/org/rotaryphone/agent"

# HFP HF features bitmask
# Bit 2 = CLI presentation (caller ID), Bit 5 = enhanced call status
HF_FEATURES = 0b00100100  # 36 — CLIP + enhanced call status

# Bluetooth SCO socket constants
BTPROTO_SCO = 2


class ScoAudioBridge:
    """Bridges SCO audio to/from a local UDP port for .NET consumption."""

    def __init__(self, device_address, udp_send_port=49100, udp_recv_port=49101):
        self.device_address = device_address
        self.udp_send_port = udp_send_port
        self.udp_recv_port = udp_recv_port
        self.sco_sock = None
        self.udp_sock = None
        self.running = False
        self._threads = []

    def start(self, sco_fd):
        """Start bridging audio between SCO file descriptor and UDP."""
        self.running = True
        self.sco_sock = socket.fromfd(sco_fd, socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, BTPROTO_SCO)

        self.udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp_sock.bind(("127.0.0.1", self.udp_recv_port))
        self.udp_sock.settimeout(0.1)

        # SCO → UDP (phone voice to .NET)
        t1 = threading.Thread(target=self._sco_to_udp, daemon=True)
        t1.start()
        self._threads.append(t1)

        # UDP → SCO (.NET voice to phone)
        t2 = threading.Thread(target=self._udp_to_sco, daemon=True)
        t2.start()
        self._threads.append(t2)

        emit({"event": "sco_connected", "address": self.device_address, "codec": "CVSD"})
        log(f"SCO audio bridge started for {self.device_address}")

    def stop(self):
        self.running = False
        try:
            if self.sco_sock:
                self.sco_sock.close()
        except Exception:
            pass
        try:
            if self.udp_sock:
                self.udp_sock.close()
        except Exception:
            pass
        emit({"event": "sco_disconnected", "address": self.device_address})
        log(f"SCO audio bridge stopped for {self.device_address}")

    def _sco_to_udp(self):
        """Read PCM from SCO, forward to .NET via UDP."""
        while self.running:
            try:
                data = self.sco_sock.recv(480)
                if data:
                    self.udp_sock.sendto(data, ("127.0.0.1", self.udp_send_port))
            except (OSError, IOError):
                break
        log("SCO→UDP thread ended")

    def _udp_to_sco(self):
        """Read PCM from .NET via UDP, write to SCO."""
        while self.running:
            try:
                data, _ = self.udp_sock.recvfrom(480)
                if data:
                    self.sco_sock.send(data)
            except socket.timeout:
                continue
            except (OSError, IOError):
                break
        log("UDP→SCO thread ended")


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
        self._sco_bridge = None

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

        # Drain any unsolicited data from the AG (very short wait)
        while True:
            line = self._read_line(timeout=0.3)
            if line is None:
                break
            if line:
                log(f"RX (initial): {line}")

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

        elif line.startswith("+CLCC:"):
            # Current call list response — extract caller ID
            self._parse_clcc(line)

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
                we_answered = self._we_sent_ata
                self.call_active = True
                emit({"event": "call_active", "address": self.address,
                      "answered_locally": not we_answered})
                self._we_sent_ata = False
                log("Call active (answered)")
                # If we sent ATA, accept SCO connection for audio
                if we_answered:
                    threading.Thread(target=self._accept_sco, daemon=True).start()
            elif ind_value == 0 and self.call_active:
                # Call ended
                self.call_active = False
                self.clip_number = None
                self._we_sent_ata = False
                if self._sco_bridge:
                    self._sco_bridge.stop()
                    self._sco_bridge = None
                emit({"event": "call_ended", "address": self.address})
                log("Call ended")

        elif ind_index == callsetup_idx:
            prev_callsetup = self.callsetup
            self.callsetup = ind_value

            if ind_value == 1:
                # Incoming call setup — emit ring immediately, then query CLCC
                # for caller ID (Android doesn't send RING/+CLIP, only +CIEV)
                number = self.clip_number or "Unknown"
                emit({"event": "ring", "address": self.address, "number": number})
                log(f"Incoming call setup — ring (number: {number})")
                # Query current calls inline (response handled by _handle_unsolicited)
                self._send("AT+CLCC")
            elif ind_value == 0 and prev_callsetup == 1 and not self.call_active:
                # Call setup ended without answering (caller hung up / rejected)
                self.clip_number = None
                self._we_sent_ata = False
                if self._sco_bridge:
                    self._sco_bridge.stop()
                    self._sco_bridge = None
                emit({"event": "call_ended", "address": self.address})
                log("Call setup ended (unanswered)")

    def _parse_clcc(self, line):
        """Parse +CLCC: idx,dir,status,mode,mpty[,"number",type] for caller ID."""
        content = line[len("+CLCC:"):].strip()
        q1 = content.find('"')
        q2 = content.find('"', q1 + 1) if q1 >= 0 else -1
        if q1 >= 0 and q2 > q1:
            number = content[q1 + 1:q2]
            if number and number != self.clip_number:
                self.clip_number = number
                log(f"CLCC caller ID: {number}")
                emit({"event": "ring", "address": self.address, "number": number})

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

    def _accept_sco(self):
        """Listen for and accept an incoming SCO connection from the AG."""
        try:
            sco_listen = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_SEQPACKET, BTPROTO_SCO)
            sco_listen.bind(bytes(6))  # Bind to any local BT address
            sco_listen.listen(1)
            sco_listen.settimeout(10.0)  # AG should open SCO within seconds
            log(f"Waiting for SCO connection from {self.address}...")
            conn, addr = sco_listen.accept()
            sco_listen.close()

            self._sco_bridge = ScoAudioBridge(self.address)
            self._sco_bridge.start(conn.fileno())
        except socket.timeout:
            log(f"SCO accept timed out for {self.address}")
            emit({"event": "error", "message": f"SCO connection timed out for {self.address}"})
        except Exception as e:
            log(f"SCO accept failed: {e}")
            emit({"event": "error", "message": f"SCO connection failed: {e}"})


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

        # Accept HFP on any adapter — the phone may connect via hci0 or hci1.
        # Closing the fd for the "wrong" adapter causes the phone to drop ALL
        # HFP connections, including the one on the correct adapter.

        # Reject duplicate connections for the same device (race between phone and us)
        # Use address-based dedup since the same phone may connect via different adapters
        addr = HfpConnection._extract_address(device)
        existing = self._find_connection_by_address_any(addr)
        if existing and existing.running:
            log(f"Duplicate HFP for {addr} (via {device}) — already active, closing fd={fd}")
            os.close(fd)
            return

        if device in self._connections:
            existing = self._connections[device]
            if existing.running:
                log(f"Duplicate NewConnection for {device} — already active, closing fd={fd}")
                os.close(fd)
                return
            # Previous connection is dead, clean it up
            log(f"Replacing dead connection for {device}")

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

    def _find_connection_by_address_any(self, address):
        """Find any connection (running or not) by MAC address."""
        for conn in self._connections.values():
            if conn.address == address:
                return conn
        return None

    def _find_connection_by_address(self, address):
        """Find a running connection by MAC address."""
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
        elif command == "start_discovery":
            try:
                adapter_iface = dbus.Interface(
                    _bus.get_object("org.bluez", _adapter_path or "/org/bluez/hci0"),
                    "org.bluez.Adapter1"
                )
                adapter_iface.StartDiscovery()
                log("Discovery started")
            except Exception as e:
                emit({"event": "error", "message": f"StartDiscovery failed: {e}"})
        elif command == "stop_discovery":
            try:
                adapter_iface = dbus.Interface(
                    _bus.get_object("org.bluez", _adapter_path or "/org/bluez/hci0"),
                    "org.bluez.Adapter1"
                )
                adapter_iface.StopDiscovery()
                log("Discovery stopped")
            except Exception as e:
                emit({"event": "error", "message": f"StopDiscovery failed: {e}"})
        elif command == "pair":
            try:
                dev_path = device_path_for(address)
                dev_obj = _bus.get_object("org.bluez", dev_path)
                dev = dbus.Interface(dev_obj, "org.bluez.Device1")
                dev.Pair()
                # Auto-trust paired devices so RFCOMM connections aren't rejected
                props = dbus.Interface(dev_obj, "org.freedesktop.DBus.Properties")
                props.Set("org.bluez.Device1", "Trusted", dbus.Boolean(True, variant_level=1))
                log(f"Pair initiated and trusted: {address}")
            except Exception as e:
                emit({"event": "error", "message": f"Pair failed: {e}"})
        elif command == "remove_device":
            try:
                dev_path = device_path_for(address)
                adapter_iface = dbus.Interface(
                    _bus.get_object("org.bluez", _adapter_path or "/org/bluez/hci0"),
                    "org.bluez.Adapter1"
                )
                adapter_iface.RemoveDevice(dev_path)
                emit({"event": "device_removed", "address": address})
                log(f"Device removed: {address}")
            except Exception as e:
                emit({"event": "error", "message": f"RemoveDevice failed: {e}"})
        elif command == "connect":
            try:
                dev_path = device_path_for(address)
                dev = dbus.Interface(_bus.get_object("org.bluez", dev_path), "org.bluez.Device1")
                dev.Connect()
                log(f"Connect initiated for {address}")
            except Exception as e:
                emit({"event": "error", "message": f"Connect failed: {e}"})
        elif command == "disconnect":
            try:
                dev_path = device_path_for(address)
                dev = dbus.Interface(_bus.get_object("org.bluez", dev_path), "org.bluez.Device1")
                dev.Disconnect()
                log(f"Disconnect initiated for {address}")
            except Exception as e:
                emit({"event": "error", "message": f"Disconnect failed: {e}"})
        elif command == "confirm_pairing":
            accept = cmd_dict.get("accept", False)
            if _agent and address:
                _agent.confirm(address, accept)
        elif command == "set_adapter":
            try:
                ap = _adapter_path or "/org/bluez/hci0"
                adapter_props = dbus.Interface(
                    _bus.get_object("org.bluez", ap),
                    "org.freedesktop.DBus.Properties"
                )
                alias = cmd_dict.get("alias")
                discoverable = cmd_dict.get("discoverable")
                if alias is not None:
                    adapter_props.Set("org.bluez.Adapter1", "Alias", dbus.String(alias, variant_level=1))
                if discoverable is not None:
                    adapter_props.Set("org.bluez.Adapter1", "Discoverable", dbus.Boolean(discoverable, variant_level=1))
                log(f"Adapter configured: alias={alias}, discoverable={discoverable}")
            except Exception as e:
                emit({"event": "error", "message": f"SetAdapter failed: {e}"})
        else:
            emit({"event": "error", "message": f"Unknown command: {command}"})


class RotaryPhoneAgent(dbus.service.Object):
    """BlueZ Agent1 for handling pairing requests."""

    def __init__(self, bus, path):
        super().__init__(bus, path)
        self._pending_confirmations = {}  # address -> threading.Event

    @dbus.service.method("org.bluez.Agent1", in_signature="os", out_signature="")
    def AuthorizeService(self, device, uuid):
        log(f"AuthorizeService: {device} uuid={uuid}")

    @dbus.service.method("org.bluez.Agent1", in_signature="o", out_signature="s")
    def RequestPinCode(self, device):
        addr = HfpConnection._extract_address(device)
        log(f"RequestPinCode: {device}")
        emit({"event": "pairing_request", "address": addr, "type": "pin", "passkey": None})
        return "0000"

    @dbus.service.method("org.bluez.Agent1", in_signature="ou", out_signature="")
    def RequestConfirmation(self, device, passkey):
        addr = HfpConnection._extract_address(device)
        log(f"RequestConfirmation: {device} passkey={passkey} — auto-accepting")
        emit({"event": "pairing_accepted", "address": addr})
        # Auto-accept: just return without raising an exception

    @dbus.service.method("org.bluez.Agent1", in_signature="o", out_signature="u")
    def RequestPasskey(self, device):
        log(f"RequestPasskey: {device}")
        return dbus.UInt32(0)

    @dbus.service.method("org.bluez.Agent1", in_signature="", out_signature="")
    def Release(self):
        log("Agent released")

    @dbus.service.method("org.bluez.Agent1", in_signature="", out_signature="")
    def Cancel(self):
        log("Agent cancelled")
        # Reject any pending confirmations
        for event in self._pending_confirmations.values():
            event.set()

    def confirm(self, address, accept):
        """Called when user confirms/rejects pairing via stdin command."""
        event = self._pending_confirmations.get(address)
        if event:
            if accept:
                event.set()
            else:
                # Rejection: set the event to unblock, then the method will check
                # Actually for rejection, we need to raise, so we just let it timeout
                self._pending_confirmations.pop(address, None)
                log(f"Pairing rejected for {address}")


# Global reference set during main() for adapter-level commands
_bus = None
_adapter_path = None
_agent = None
_hfp_profile = None


def device_path_for(address):
    """Convert MAC address to BlueZ device path."""
    addr_part = address.replace(":", "_")
    base = _adapter_path or "/org/bluez/hci0"
    return f"{base}/dev_{addr_part}"


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

    # Reconnect cooldown tracker (address -> last disconnect timestamp)
    _last_disconnect = {}

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
            adapter.Set("org.bluez.Adapter1", "Alias", dbus.String(args.alias, variant_level=1))
            adapter.Set("org.bluez.Adapter1", "Powered", dbus.Boolean(True, variant_level=1))
            adapter.Set("org.bluez.Adapter1", "DiscoverableTimeout", dbus.UInt32(0, variant_level=1))
            adapter.Set("org.bluez.Adapter1", "Discoverable", dbus.Boolean(True, variant_level=1))
            adapter.Set("org.bluez.Adapter1", "Pairable", dbus.Boolean(True, variant_level=1))
            log(f"Configured adapter {adapter_path}: alias={args.alias}, powered=on, discoverable=on")
        except dbus.exceptions.DBusException as e:
            log(f"Warning: could not configure adapter {adapter_path}: {e}")

    # Set global references for adapter-level commands
    global _bus, _adapter_path, _agent, _hfp_profile
    _bus = bus
    _adapter_path = adapter_path

    # Create and register BlueZ Agent1 for pairing
    agent = RotaryPhoneAgent(bus, AGENT_PATH)
    _agent = agent
    try:
        agent_manager = dbus.Interface(
            bus.get_object("org.bluez", "/org/bluez"),
            "org.bluez.AgentManager1"
        )
        agent_manager.RegisterAgent(AGENT_PATH, "NoInputNoOutput")
        agent_manager.RequestDefaultAgent(AGENT_PATH)
        log("Registered BlueZ agent for pairing")
    except dbus.exceptions.DBusException as e:
        log(f"Warning: could not register agent: {e}")

    # Create Profile1 object
    global _hfp_profile
    profile = HfpProfile(bus, PROFILE_PATH)
    _hfp_profile = profile

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

    # Auto-trust all paired devices and connect HFP on already-connected ones
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
            is_paired = bool(dev_props.get("Paired", False))
            is_trusted = bool(dev_props.get("Trusted", False))

            # Auto-trust paired devices so RFCOMM isn't rejected
            if is_paired and not is_trusted:
                try:
                    props = dbus.Interface(
                        bus.get_object("org.bluez", path),
                        "org.freedesktop.DBus.Properties"
                    )
                    props.Set("org.bluez.Device1", "Trusted",
                              dbus.Boolean(True, variant_level=1))
                    addr = str(dev_props.get("Address", path))
                    log(f"Auto-trusted paired device: {addr}")
                except Exception as e:
                    log(f"Failed to trust device at {path}: {e}")

            is_connected = bool(dev_props.get("Connected", False))
            uuids = [str(u) for u in dev_props.get("UUIDs", [])]
            addr = str(dev_props.get("Address", ""))
            has_hfp_ag = any("111f" in u.lower() for u in uuids)
            name = str(dev_props.get("Alias", dev_props.get("Address", path)))

            if is_connected and has_hfp_ag:
                log(f"Found connected HFP-AG device: {name} ({addr}) at {path}")
                # Request HFP after a delay to let profile registration settle
                dev_path = path
                def connect_hfp_startup(dp=dev_path, nm=name):
                    if _hfp_profile and dp in _hfp_profile._connections:
                        log(f"HFP already active for {nm} — skipping")
                        return False
                    try:
                        dev_obj = bus.get_object("org.bluez", dp)
                        dev = dbus.Interface(dev_obj, "org.bluez.Device1")
                        dev.ConnectProfile(HFP_AG_UUID)
                        log(f"Requested HFP AG profile from {nm}")
                    except Exception as e:
                        log(f"ConnectProfile(HFP_AG) for {nm}: {e}")
                    return False
                GLib.timeout_add(2000, connect_hfp_startup)
            elif is_paired and not is_connected and has_hfp_ag:
                # Paired but not connected — initiate full BT connection.
                # Connect() establishes the ACL link; our Profile1 registration
                # with AutoConnect=True will then open the RFCOMM HFP channel.
                # The on_properties_changed handler won't duplicate because
                # NewConnection guards against simultaneous connections.
                log(f"Paired HFP device not connected: {name} ({addr}) — connecting")
                dev_path = path
                def auto_connect_device(dp=dev_path, nm=name):
                    try:
                        dev_obj = bus.get_object("org.bluez", dp)
                        dev = dbus.Interface(dev_obj, "org.bluez.Device1")
                        dev.Connect()
                        log(f"Connect() initiated for {nm}")
                    except Exception as e:
                        log(f"Connect() failed for {nm}: {e}")
                    return False
                GLib.timeout_add(3000, auto_connect_device)
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
            # Paired device disconnected — try to reconnect after delay
            addr = path.split("/")[-1].replace("dev_", "").replace("_", ":")

            # Cooldown: don't reconnect if we just disconnected recently
            now = time.time()
            last = _last_disconnect.get(addr, 0)
            if now - last < 10:
                log(f"Reconnect cooldown for {addr} — skipping (last disconnect {now - last:.0f}s ago)")
                return
            _last_disconnect[addr] = now

            def try_reconnect():
                try:
                    dev_obj = bus.get_object("org.bluez", path)
                    dev_props_iface = dbus.Interface(dev_obj, "org.freedesktop.DBus.Properties")
                    paired = bool(dev_props_iface.Get("org.bluez.Device1", "Paired"))
                    if not paired:
                        return False  # Only reconnect paired devices
                    connected = bool(dev_props_iface.Get("org.bluez.Device1", "Connected"))
                    if connected:
                        log(f"Device {addr} already reconnected — skipping")
                        return False
                    dev = dbus.Interface(dev_obj, "org.bluez.Device1")
                    dev.Connect()
                    log(f"Reconnected to {addr}")
                except Exception as e:
                    log(f"Reconnect failed for {addr}: {e}")
                return False  # Don't repeat
            GLib.timeout_add(10000, try_reconnect)
            return
        # Phone connected at BT level — request HFP AG connection after a
        # short delay so the phone has time to finish its own setup.
        addr = path.split("/")[-1].replace("dev_", "").replace("_", ":")
        log(f"Device connected: {addr} — will request HFP after delay")

        def request_hfp():
            # Skip if we already have an active HFP connection for this device
            if _hfp_profile and path in _hfp_profile._connections:
                log(f"HFP already active for {addr} — skipping ConnectProfile")
                return False
            try:
                dev_obj = bus.get_object("org.bluez", path)
                dev = dbus.Interface(dev_obj, "org.bluez.Device1")
                dev.ConnectProfile(HFP_AG_UUID)
                log(f"Requested HFP AG profile from {addr}")
            except Exception as e:
                log(f"ConnectProfile(HFP_AG) for {addr}: {e}")
            return False  # Don't repeat

        GLib.timeout_add(3000, request_hfp)

    bus.add_signal_receiver(
        on_properties_changed,
        signal_name="PropertiesChanged",
        dbus_interface="org.freedesktop.DBus.Properties",
        bus_name="org.bluez",
        path_keyword="path"
    )

    # Watch for newly discovered devices during scanning
    def on_interfaces_added(path, interfaces):
        if "org.bluez.Device1" not in interfaces:
            return
        if adapter_path and not path.startswith(adapter_path + "/"):
            return
        dev_props = interfaces["org.bluez.Device1"]
        addr = str(dev_props.get("Address", ""))
        name = str(dev_props.get("Alias", dev_props.get("Name", ""))) or None
        paired = bool(dev_props.get("Paired", False))
        if addr:
            emit({"event": "device_discovered", "address": addr, "name": name, "paired": paired})
            log(f"Discovered device: {name or addr}")

    bus.add_signal_receiver(
        on_interfaces_added,
        signal_name="InterfacesAdded",
        dbus_interface="org.freedesktop.DBus.ObjectManager",
        bus_name="org.bluez"
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
        try:
            agent_manager.UnregisterAgent(AGENT_PATH)
            log("Unregistered BlueZ agent")
        except Exception:
            pass


if __name__ == "__main__":
    main()
