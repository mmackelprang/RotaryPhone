#!/usr/bin/env python3
"""Unlock the running gnome-keyring 'login' collection with a password read from stdin.

Used at session start on an auto-login box, where no typed password ever unlocks the keyring and
Chromium (cookie key in "Chromium Safe Storage") would otherwise block on an unlock prompt.

Talks to the ALREADY RUNNING daemon over the session bus. Do not use `gnome-keyring-daemon --unlock`
for this: on gnome-keyring 46 it starts a SECOND daemon on the same control directory, leaves the
login keyring locked, and deletes the control directory when it exits (observed on radio 2026-10-04).

Exit codes: 0 unlocked (or already unlocked), 1 still locked after the call (wrong password),
2 no password on stdin, 3 D-Bus / daemon error.
"""
import sys

import gi

gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib  # noqa: E402

BUS = "org.freedesktop.secrets"
SVC = "/org/freedesktop/secrets"
LOGIN = "/org/freedesktop/secrets/collection/login"


def locked(bus):
    v = bus.call_sync(BUS, LOGIN, "org.freedesktop.DBus.Properties", "Get",
                      GLib.Variant("(ss)", ("org.freedesktop.Secret.Collection", "Locked")),
                      None, Gio.DBusCallFlags.NONE, 5000, None)
    return v.unpack()[0]


def main():
    password = sys.stdin.buffer.read()
    if not password:
        print("gv-keyring-unlock: no password on stdin", file=sys.stderr)
        return 2
    try:
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        if not locked(bus):
            print("gv-keyring-unlock: login keyring already unlocked")
            return 0
        # 'plain' session: the secret travels unencrypted over the local session bus, which is
        # private to this user; the daemon accepts it for UnlockWithMasterPassword.
        session = bus.call_sync(BUS, SVC, "org.freedesktop.Secret.Service", "OpenSession",
                                GLib.Variant("(sv)", ("plain", GLib.Variant("s", ""))),
                                None, Gio.DBusCallFlags.NONE, 5000, None).unpack()[1]
        secret = GLib.Variant("(o(oayays))",
                              (LOGIN, (session, b"", password, "text/plain")))
        bus.call_sync(BUS, SVC, "org.gnome.keyring.InternalUnsupportedGuiltRiddenInterface",
                      "UnlockWithMasterPassword", secret, None, Gio.DBusCallFlags.NONE, 10000, None)
        still = locked(bus)
    except GLib.Error as e:
        print(f"gv-keyring-unlock: D-Bus error: {e.message}", file=sys.stderr)
        return 3
    if still:
        print("gv-keyring-unlock: login keyring still locked (wrong password?)", file=sys.stderr)
        return 1
    print("gv-keyring-unlock: login keyring unlocked")
    return 0


if __name__ == "__main__":
    sys.exit(main())
