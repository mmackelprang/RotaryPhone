#!/usr/bin/env python3
"""Stub of the RotaryPhone service for the auto-relogin actuator harness.

    GET  /api/gvbridge/status                        -> <dir>/status.json, code <dir>/status.code (200)
    POST /api/gvbridge/cookies/refresh-from-browser  -> code <dir>/post.code (200); THEN, if
         <dir>/after-post.json exists, it becomes status.json (and <dir>/after-post.code, if
         present, becomes status.code) — which is how "validatedAt moved across OUR post" and
         "the service died mid-verify" are produced.

Every request is appended to <dir>/requests.log as "<METHOD> <path>", so a case can assert
an ABSENCE that was positively observed ("zero refresh-from-browser POSTs"), not inferred
from the script's own code path.

    python3 gv-relogin-service-stub.py --port 8198 --dir /tmp/work
"""
import argparse
import shutil
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

ARGS = None


def _code(p, default):
    try:
        return int(p.read_text().strip() or default)
    except (OSError, ValueError):
        return default


class Handler(BaseHTTPRequestHandler):
    def _log(self):
        with open(Path(ARGS.dir) / "requests.log", "a", encoding="utf-8") as fh:
            fh.write(f"{self.command} {self.path}\n")

    def _send(self, code, body):
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self._log()
        d = Path(ARGS.dir)
        try:
            body = (d / "status.json").read_bytes()
        except OSError:
            body = b'{"error":"no status.json staged"}'
        self._send(_code(d / "status.code", 200), body)

    def do_POST(self):
        self._log()
        n = int(self.headers.get("Content-Length") or 0)
        if n:
            self.rfile.read(n)
        d = Path(ARGS.dir)
        code = _code(d / "post.code", 200)
        if (d / "after-post.json").exists():
            shutil.copyfile(d / "after-post.json", d / "status.json")
            if (d / "after-post.code").exists():
                shutil.copyfile(d / "after-post.code", d / "status.code")
        self._send(code, b'{"refreshed":true}' if code == 200 else b'{"error":"stub"}')

    def log_message(self, *a):
        pass  # stdout belongs to the harness


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8198)
    ap.add_argument("--dir", required=True)
    ARGS = ap.parse_args()
    print(f"stub service on 127.0.0.1:{ARGS.port}, dir {ARGS.dir}", file=sys.stderr)
    HTTPServer(("127.0.0.1", ARGS.port), Handler).serve_forever()
