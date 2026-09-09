#!/usr/bin/env python3
"""Stub of the chat gateway, reproducing ONLY its MEASURED behaviour.

Every rule below is from the spec's §4.4 table, which is itself from
aitrader/docs/chat-gateway-requirements.md. Each cost a real alarm.

  * `action` is capped at 200 CHARACTERS; over-long is 422 and DELIVERS NOTHING.
  * `action` and `timestamp` are SILENTLY DROPPED on severity=info.
  * `grace` is CASE-SENSITIVE; "30M" is a 422.
  * dedupe_key ignores severity, title and thread_key.

⚠ This stub is deliberately STRICTER than the real gateway in one way and
weaker in another, and both are on purpose:
  - stricter: it requires a bearer token, so a missing-credential bug fails
    here rather than being masked by a permissive endpoint.
  - weaker: it does not implement delivery, routing, or the in-memory queue.
    It cannot prove a message REACHED a human. Only Task 17 can do that.

Usage:
    python3 gv-alarm-gateway-stub.py --port 8099 --log /tmp/gw.jsonl
    python3 gv-alarm-gateway-stub.py --port 8099 --log /tmp/gw.jsonl --fail-notify 500
"""
import argparse, json, sys, time
from http.server import BaseHTTPRequestHandler, HTTPServer

ACTION_MAX_CHARS = 200
VALID_SEVERITY = {"alert", "warning", "info"}
TOKEN = "stub-token"
ARGS = None
CHECKS = {}


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        payload = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _record(self, kind, code, body):
        with open(ARGS.log, "a") as fh:
            fh.write(json.dumps({
                "t": time.time(), "kind": kind, "status": code, "body": body,
            }) + "\n")

    def _authed(self):
        return self.headers.get("Authorization") == f"Bearer {TOKEN}"

    def _read(self):
        n = int(self.headers.get("Content-Length") or 0)
        try:
            return json.loads(self.rfile.read(n) or b"{}")
        except json.JSONDecodeError:
            return None

    def do_POST(self):
        body = self._read()
        if body is None:
            return self._send(400, {"error": "malformed json"})
        if not self._authed():
            self._record("auth", 401, body)
            return self._send(401, {"error": "bad or missing bearer token"})

        if self.path == "/v1/notify":
            return self._notify(body)
        if self.path == "/v1/heartbeat":
            return self._heartbeat(body)
        self._send(404, {"error": "no such route"})

    def do_GET(self):
        if self.path.startswith("/v1/heartbeat/"):
            src = self.path.rsplit("/", 1)[-1]
            if src not in CHECKS:
                return self._send(404, {"error": "no such check"})
            return self._send(200, CHECKS[src])
        self._send(404, {"error": "no such route"})

    def _notify(self, body):
        if ARGS.fail_notify:
            self._record("notify", ARGS.fail_notify, body)
            return self._send(ARGS.fail_notify, {"error": "forced failure (--fail-notify)"})

        sev = body.get("severity")
        if sev not in VALID_SEVERITY:
            self._record("notify", 422, body)
            return self._send(422, {"error": f"severity must be one of {sorted(VALID_SEVERITY)}"})

        action = body.get("action")
        # THE MEASURED TRAP: characters, not bytes, and the whole message is refused.
        if action is not None and len(action) > ACTION_MAX_CHARS:
            self._record("notify", 422, body)
            return self._send(422, {
                "error": "action too long",
                "limit": ACTION_MAX_CHARS,
                "got": len(action),
                "note": "nothing was delivered",
            })

        stored = dict(body)
        if sev == "info":
            # Silently dropped. No error, no warning — which is what makes it a trap.
            stored.pop("action", None)
            stored.pop("timestamp", None)
        self._record("notify", 202, stored)
        self._send(202, {"queued": True})

    def _heartbeat(self, body):
        grace = body.get("grace", "")
        schedule = body.get("schedule", "")
        for name, val in (("grace", grace), ("schedule", schedule)):
            if not isinstance(val, str) or not val:
                self._record("heartbeat", 422, body)
                return self._send(422, {"error": f"{name} is required"})
            # THE MEASURED TRAP: case-sensitive. "30M" is a 422.
            if val != val.lower():
                self._record("heartbeat", 422, body)
                return self._send(422, {"error": f"{name} must be lower-case", "got": val})
        src = body.get("source")
        if not src:
            self._record("heartbeat", 422, body)
            return self._send(422, {"error": "source is required"})
        CHECKS[src] = {
            "source": src,
            "check_id": body.get("check_id"),
            "schedule": schedule,
            "grace": grace,
            "last_seen": time.time(),
            "refresh_count": CHECKS.get(src, {}).get("refresh_count", 0) + 1,
        }
        self._record("heartbeat", 200, body)
        self._send(200, CHECKS[src])

    def log_message(self, *a):
        pass  # stdout belongs to the harness


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8099)
    ap.add_argument("--log", default="/tmp/gv-alarm-gateway-stub.jsonl")
    ap.add_argument("--fail-notify", type=int, default=0,
                    help="return this status for every /v1/notify")
    ARGS = ap.parse_args()
    open(ARGS.log, "w").close()
    print(f"stub gateway on 127.0.0.1:{ARGS.port}, log {ARGS.log}", file=sys.stderr)
    HTTPServer(("127.0.0.1", ARGS.port), Handler).serve_forever()
