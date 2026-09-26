#!/usr/bin/env python3
"""A CONTRACT FIXTURE for check-relogin-driver.sh's self-test. NOT a sign-in driver.

It implements only the parts of docs/gv-relogin-driver-contract.md that happen BEFORE any
page is touched: read stdin to EOF, validate the five keys, and try to open a TCP connection
to the CDP port. It never speaks the DevTools protocol, never sends a byte to any page, and
has no notion of a sign-in form. Its only purpose is to give the checker a known-good subject,
so each of the checker's cases can be shown to fail against a deliberately broken copy.

    stdin not version=1 / keys missing   -> UNRECOGNISED
    CDP port refuses the connection      -> TRANSPORT  (before any interaction: legal)
    anything else                        -> UNRECOGNISED (the default)
"""
import socket
import sys

KEYS = ("version", "cdp_port", "target_id", "email", "password")


def main():
    raw = sys.stdin.read()
    fields = {}
    for line in raw.split("\n"):
        if "=" in line:
            k, v = line.split("=", 1)
            fields[k] = v
    if fields.get("version") != "1" or any(k not in fields for k in KEYS):
        print("UNRECOGNISED")
        return 0
    try:
        port = int(fields["cdp_port"])
    except ValueError:
        print("UNRECOGNISED")
        return 0
    try:
        socket.create_connection(("127.0.0.1", port), timeout=5).close()
    except ConnectionRefusedError:
        print("fixture: CDP port refused the connection before any page was touched", file=sys.stderr)
        print("TRANSPORT")
        return 0
    except OSError:
        print("UNRECOGNISED")
        return 0
    print("UNRECOGNISED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
