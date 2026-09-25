#!/usr/bin/env python3
"""A minimal Chrome DevTools Protocol driver for the GV bridge's existing browser.

⛔ THIS FILE HANDLES NOTHING SENSITIVE. It lists pages, reads a page's own location,
and navigates. Anything sensitive is the CALLER's business, travels on the caller's
stdin, and never appears here as a default, a constant, or an argv parameter.

⚠ This file deliberately has no VOCABULARY for the sensitive thing either: the plan's
Task 3 acceptance greps it for the usual words and requires zero hits, so the words
are absent even from this comment.

⛔ IT NEVER STARTS A BROWSER AND NEVER CLEARS A PROFILE. Spec §5: same-profile
re-login is materially safer than a fresh-device sign-in, because Google already
knows this device, profile and IP. Starting a clean browser converts routine
re-auth into an unrecognised-device sign-in, which is far more likely to be
challenged. There is deliberately no code path here that could do it.

⭐ SHIPPED, AND THE ONE HOME FOR THIS HELPER. It lived in deploy/tools/ for the
attended spike (Task 4), where nothing ships. gv-auto-relogin.sh (plan Task 11) needs
it on the box, and the reachable-reauth assist (PR #89) extends it with subcommands of
its own, so it now lives at deploy/gv-cdp.py and Deploy-ToLinux.ps1 ships deploy/*.py
beside deploy/*.sh. The spike-only `eval`, `shot` and `dump` subcommands were dropped
in the move: nothing shipped may evaluate arbitrary script in the bridge's pages or
write their DOM to disk.

Requires only python3 + websocket-client, both already on the box (measured
2026-09-09: websocket-client 1.9.0). No node, no Playwright, nothing installed.

  gv-cdp.py targets                              -> "<id>\\t<listed url>" per page
  gv-cdp.py url      --target <id>               -> the page's OWN window.location.href
  gv-cdp.py navigate --target <id> --url https://...
                                                 -> window.location.href after the load event

Exit codes: 0 ok · 1 usage / no such target · 3 Chrome reported the navigation failed
· 4 transport (CDP unreachable, websocket closed, or no load event within --timeout).
"""
import argparse
import json
import sys
import urllib.request

import websocket  # websocket-client


DEFAULT_PORT = 9224
# 15 s per navigation: the spike's budget (docs/spikes/2026-09-09-gv-signin-cdp-recording.md,
# "Timings" — 3-5x the worst observed navigation of ~3-5 s). Measured, not chosen here.
DEFAULT_TIMEOUT_S = 15.0


class Transport(Exception):
    """A positively identified transport fault: exit 4."""


def http_json(port, path):
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=10) as r:
            return json.loads(r.read())
    except OSError as e:  # URLError, refused, timeout
        raise Transport(f"CDP port {port} did not answer {path}: {e}") from e


def targets(port):
    # Only real pages. iframes and workers are not navigable subjects and including
    # them is how a caller ends up driving the RotateCookiesPage iframe by accident.
    return [t for t in http_json(port, "/json/list") if t.get("type") == "page"]


class Session:
    """One websocket to one target. Short-lived on purpose: a long-lived connection
    to the bridge's browser is a thing that can be left behind."""

    def __init__(self, ws_url, timeout):
        # ⚠ suppress_origin is NOT set. gv-bridge-ensure.sh starts Chrome with
        # --remote-allow-origins=* (verified on the RUNNING argv, not just the repo
        # copy, 2026-09-09), so the origin check is satisfied. If a future start
        # narrows that flag this connect is where it will fail — loudly, which is
        # what we want, rather than being papered over here.
        self.ws = websocket.create_connection(ws_url, timeout=timeout)
        self.n = 0
        # Events that arrive while send() is waiting for its own reply. Without this
        # buffer an event that lands BEFORE the command's reply (a fast page's load
        # event racing Page.navigate's reply) is thrown away, and wait_for() then
        # blocks for the full timeout waiting for something that already happened.
        self.events = []

    def send(self, method, **params):
        self.n += 1
        self.ws.send(json.dumps({"id": self.n, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self.n:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg.get("result", {})
            if "method" in msg:
                self.events.append(msg)

    def wait_for(self, event, timeout):
        for i, msg in enumerate(self.events):
            if msg.get("method") == event:
                del self.events[: i + 1]
                return msg.get("params", {})
        self.events.clear()
        self.ws.settimeout(timeout)
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("method") == event:
                return msg.get("params", {})

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def open_target(port, target_id, timeout):
    for t in targets(port):
        if t["id"] == target_id:
            return Session(t["webSocketDebuggerUrl"], timeout)
    raise SystemExit(f"no page target with id {target_id}")


def live_href(s):
    # ⛔ NOT /json/list's cached .url. Measured 2026-09-09: a parked
    # workspace.google.com page sits in the target list while the session is
    # perfectly healthy, and KNOWN-ISSUES.md:16-22 records both the title and
    # the URL as stale cached renders. window.location.href read INSIDE the
    # target is the only reading that means anything.
    r = s.send("Runtime.evaluate", expression="window.location.href", returnByValue=True)
    return r["result"]["value"]


def run(a):
    if a.cmd == "targets":
        for t in targets(a.port):
            print(f"{t['id']}\t{t['url']}")
        return 0

    if not a.target:
        raise SystemExit(f"{a.cmd} needs --target <id>; run `targets` to list them")

    s = open_target(a.port, a.target, a.timeout)
    try:
        if a.cmd == "url":
            print(live_href(s))
        elif a.cmd == "navigate":
            if not a.url:
                raise SystemExit("navigate needs --url")
            s.send("Page.enable")
            nav = s.send("Page.navigate", url=a.url)
            # A navigation Chrome itself reports as failed (DNS, refused, aborted) is a
            # POSITIVELY IDENTIFIED fault. Say so and exit non-zero rather than waiting
            # for a load event on Chrome's own error page and printing its URL as if the
            # navigation had worked.
            if nav.get("errorText"):
                print(f"navigation failed: {nav['errorText']}", file=sys.stderr)
                return 3
            s.wait_for("Page.loadEventFired", a.timeout)
            print(live_href(s))
    finally:
        s.close()
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["targets", "url", "navigate"])
    ap.add_argument("--port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--target")
    ap.add_argument("--url")
    ap.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_S)
    a = ap.parse_args()
    try:
        return run(a)
    except (Transport, OSError, websocket.WebSocketException) as e:
        # OSError covers a refused/reset socket; WebSocketException covers a closed
        # socket and WebSocketTimeoutException (no load event within --timeout).
        print(f"transport: {type(e).__name__}: {e}", file=sys.stderr)
        return 4


if __name__ == "__main__":
    sys.exit(main())
