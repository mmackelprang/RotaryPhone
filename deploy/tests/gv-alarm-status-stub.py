#!/usr/bin/env python3
"""Stub of GET /api/gvbridge/status, driven by two files so a case can be
changed without restarting anything.

    <dir>/status.json   the body to serve   (required)
    <dir>/status.code   the HTTP status     (optional, default 200)

⚠ It does NOT model "the service is down". That case is produced by pointing
the alarm at a port nothing listens on, which is a real connection refusal
rather than a simulation of one — the alarm's poll branch keys on curl's exit
status, and a stub that merely CLOSED the connection would exercise a
different code path than the one the box will take.

Usage:
    python3 gv-alarm-status-stub.py --port 8098 --dir /tmp/work
"""
import argparse, sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

ARGS = None


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        d = Path(ARGS.dir)
        try:
            body = (d / "status.json").read_bytes()
        except OSError:
            body = b'{"error":"no status.json staged"}'
        try:
            code = int((d / "status.code").read_text().strip() or 200)
        except (OSError, ValueError):
            code = 200
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a):
        pass  # stdout belongs to the harness


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8098)
    ap.add_argument("--dir", required=True)
    ARGS = ap.parse_args()
    print(f"stub status endpoint on 127.0.0.1:{ARGS.port}, dir {ARGS.dir}", file=sys.stderr)
    HTTPServer(("127.0.0.1", ARGS.port), Handler).serve_forever()
