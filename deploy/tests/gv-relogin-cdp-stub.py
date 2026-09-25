#!/usr/bin/env python3
"""Stand-in for deploy/gv-cdp.py in the actuator harness: same subcommands, same output
shape, same exit codes, driven by files. No browser, no network. (deploy/gv-cdp.py itself
is tested against a real headless Chrome by repro-gv-cdp.sh.)

Files under $GV_STUB_DIR/cdp/:
    pages          one "<id>\t<live href>" per page. `targets` prints "<id>\t<cached url>",
                   where the cached url is DELIBERATELY a stale lie (always the Voice page),
                   so an actuator that chose by the listing rather than by `url` is caught.
    targets.rc     exit code for `targets` (default 0; 4 = transport)
    landing        what `navigate` prints (the page's live href after the load event)
    navigate.rc    exit code for `navigate` (default 0)
    calls.log      every invocation, appended: "<subcommand> <args>"
"""
import os
import sys
from pathlib import Path

D = Path(os.environ["GV_STUB_DIR"]) / "cdp"


def rc(name):
    try:
        return int((D / name).read_text().strip())
    except (OSError, ValueError):
        return 0


def pages():
    out = []
    try:
        for line in (D / "pages").read_text().splitlines():
            if "\t" in line:
                i, h = line.split("\t", 1)
                out.append((i, h))
    except OSError:
        pass
    return out


def arg(name):
    a = sys.argv
    return a[a.index(name) + 1] if name in a else None


def main():
    D.mkdir(parents=True, exist_ok=True)
    with open(D / "calls.log", "a", encoding="utf-8") as fh:
        fh.write(" ".join(sys.argv[1:]) + "\n")
    cmd = sys.argv[1]
    if cmd == "targets":
        if rc("targets.rc"):
            print("transport: stub", file=sys.stderr)
            return rc("targets.rc")
        for i, _ in pages():
            print(f"{i}\thttps://voice.google.com/u/0/voicemail")
        return 0
    tid = arg("--target")
    if cmd == "url":
        for i, h in pages():
            if i == tid:
                print(h)
                return 0
        return 1
    if cmd == "navigate":
        if rc("navigate.rc"):
            return rc("navigate.rc")
        try:
            print((D / "landing").read_text().strip())
        except OSError:
            return 4
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
