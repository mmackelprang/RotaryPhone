#!/usr/bin/env bash
# Lane L harness for deploy/gv-cdp.py (docs/plans/gv-auto-relogin.md Tasks 3 and 11).
#
# Drives a THROWAWAY local Chrome — its own temp profile, its own port, headless —
# never the box's bridge browser and never Google. The tool under test is a
# transport; everything asserted here is about what it reads and where it reads it.
#
#   CHROME=/path/to/chrome   PYTHON=/path/to/python-with-websocket-client \
#       bash deploy/tests/repro-gv-cdp.sh
#
# ⚠ The HARNESS starts a browser. The TOOL must not be able to, and the static checks
# below assert that. The two are kept in different files for exactly that reason.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
TOOL="${GV_CDP_TOOL:-${HERE}/../gv-cdp.py}"
PY="${PYTHON:-python3}"
CHROME="${CHROME:-$(command -v chromium || command -v chromium-browser || command -v google-chrome || true)}"

fail=0
cases=0
check() { # check NAME EXPECTED ACTUAL
    cases=$((cases + 1))
    if [ "$2" = "$3" ]; then printf '  PASS %s\n' "$1"
    else printf '  FAIL %s: expected [%s] got [%s]\n' "$1" "$2" "$3"; fail=1; fi
}

echo "=== static: the tool has no vocabulary for a secret, and no way to start a browser ==="
check "no password/credential/secret vocabulary" "0" \
      "$(grep -ciE 'password|passwd|credential|secret' "$TOOL")"
check "no launch / --user-data-dir / Popen / subprocess" "0" \
      "$(grep -cE 'launch|--user-data-dir|Popen|subprocess' "$TOOL")"
# ⛔ SHIPPED NOW (Task 11), so its surface is pinned: the spike-only eval/shot/dump are
# gone, and the only script it evaluates in a page is window.location.href.
check "the shipped tool offers exactly targets/url/navigate" \
      'ap.add_argument("cmd", choices=["targets", "url", "navigate"])' \
      "$(grep -F 'ap.add_argument("cmd"' "$TOOL" | sed 's/^ *//')"
check "Runtime.evaluate is only ever window.location.href" "0" \
      "$(grep -F 'Runtime.evaluate' "$TOOL" | grep -vcF 'expression="window.location.href"')"
check "no screenshot / DOM dump / Input.* method" "0" \
      "$(grep -cE 'captureScreenshot|outerHTML|"Input\.' "$TOOL")"

if [ -z "$CHROME" ] || ! "$PY" -c 'import websocket' 2>/dev/null; then
    # ⛔ LOUD, never a silent pass: a protection test that goes quiet when its subject
    # is missing is the defect this repo corrected in its deploy gate on 2026-09-09.
    echo "  SKIPPED-LOUDLY live cases: need CHROME (got '${CHROME:-none}') and a PYTHON with websocket-client (got '$PY')"
    echo "FAILURES PRESENT: live cases not run (${cases} static cases run)"
    exit 2
fi

WORK="$(mktemp -d)"
winpath() { command -v cygpath >/dev/null 2>&1 && cygpath -w "$1" || printf '%s' "$1"; }
CHROME_PID=""; HTTP_PID=""
killtree() {
    # On Git Bash a native Windows Chrome is a TREE of processes and `kill` reaches
    # only the top one; the survivors keep the harness profile locked and make the
    # NEXT run's Chrome fail to come up. Measured on this workstation 2026-09-25.
    if [ -r "/proc/$1/winpid" ] && command -v taskkill >/dev/null 2>&1; then
        taskkill //F //T //PID "$(cat "/proc/$1/winpid")" >/dev/null 2>&1
    fi
    kill "$1" 2>/dev/null
}
cleanup() {
    [ -n "$CHROME_PID" ] && killtree "$CHROME_PID"
    [ -n "$HTTP_PID" ] && killtree "$HTTP_PID"
    sleep 1
    rm -rf "$WORK" 2>/dev/null
}
trap cleanup EXIT

# Ports chosen away from the box's 9224 so a harness run on a machine that happens to
# be running the bridge can never touch it.
CDP=9331
WEB=8331
HANG=8332

# A parent page on 127.0.0.1 embedding a child on localhost: two different SITES, so
# with --site-per-process the child is an out-of-process iframe and appears in
# /json/list as type "iframe" — the same shape as the box's RotateCookiesPage iframe.
mkdir -p "$WORK/www"
cat > "$WORK/www/parent.html" <<EOF
<!doctype html><title>parent</title>
<iframe src="http://localhost:${WEB}/child.html"></iframe>
EOF
printf '<!doctype html><title>child</title>child\n' > "$WORK/www/child.html"
printf '<!doctype html><title>second</title>second\n' > "$WORK/www/second.html"

"$PY" -m http.server "$WEB" --bind 127.0.0.1 --directory "$(winpath "$WORK/www")" >/dev/null 2>&1 &
HTTP_PID=$!

"$CHROME" --headless=new --disable-gpu --no-first-run --no-default-browser-check \
    --site-per-process --remote-debugging-port="$CDP" --remote-allow-origins='*' \
    --user-data-dir="$(winpath "$WORK/profile")" \
    "http://127.0.0.1:${WEB}/parent.html" >/dev/null 2>&1 &
CHROME_PID=$!

for _ in $(seq 60); do
    curl -s --max-time 1 "http://127.0.0.1:${CDP}/json/list" 2>/dev/null | grep -q '"iframe"' && break
    sleep 0.5
done
if ! curl -s --max-time 2 "http://127.0.0.1:${CDP}/json/version" >/dev/null 2>&1; then
    echo "FAILURES PRESENT: the harness Chrome never answered on port ${CDP}; no live case can mean anything"
    exit 2
fi

echo "=== targets: pages only ==="
raw_types="$(curl -s "http://127.0.0.1:${CDP}/json/list" | "$PY" -c 'import json,sys; print(" ".join(sorted(t["type"] for t in json.load(sys.stdin))))')"
echo "  /json/list types: ${raw_types}"
# ⛔ Precondition, proved rather than assumed: the fixture really does put an iframe
# in the raw list. Without this the next assertion could pass on an empty population.
check "PRECONDITION: /json/list contains an iframe target" "yes" \
      "$(case " $raw_types " in *" iframe "*) echo yes ;; *) echo no ;; esac)"
listed="$("$PY" "$TOOL" targets --port "$CDP" | tr -d '\r')"
check "the child iframe's URL is ABSENT from targets" "0" \
      "$(printf '%s\n' "$listed" | grep -c 'child.html')"
check "the parent page IS listed" "1" \
      "$(printf '%s\n' "$listed" | grep -c 'parent.html')"
TID="$(printf '%s\n' "$listed" | awk -F'\t' '/parent.html/{print $1; exit}')"

echo "=== url and navigate read window.location.href, not the target list ==="
check "url reads the page's own location" "http://127.0.0.1:${WEB}/parent.html" \
      "$("$PY" "$TOOL" url --port "$CDP" --target "$TID" | tr -d '\r')"
landed="$("$PY" "$TOOL" navigate --port "$CDP" --target "$TID" --url "http://127.0.0.1:${WEB}/second.html" | tr -d '\r')"
check "navigate prints where the page LANDED" "http://127.0.0.1:${WEB}/second.html" "$landed"
# The page's own location is changed by an in-page script WITHOUT a navigation. The
# HARNESS does that itself over CDP — the shipped tool has no eval, by design.
"$PY" - "$CDP" "$TID" <<'PY' >/dev/null
import json, sys, urllib.request, websocket
port, tid = sys.argv[1], sys.argv[2]
t = [x for x in json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list")) if x["id"] == tid][0]
ws = websocket.create_connection(t["webSocketDebuggerUrl"], timeout=10)
ws.send(json.dumps({"id": 1, "method": "Runtime.evaluate",
                    "params": {"expression": "history.replaceState(null,'','/moved-in-page.html'); 1"}}))
while json.loads(ws.recv()).get("id") != 1:
    pass
ws.close()
PY
check "url reports an in-page location change" \
      "http://127.0.0.1:${WEB}/moved-in-page.html" \
      "$("$PY" "$TOOL" url --port "$CDP" --target "$TID" | tr -d '\r')"
# ⚠ THE LIVE CASE ABOVE CANNOT TELL window.location.href FROM /json/list's .url, and
# that was measured, not assumed: a mutant whose `url` printed the target list's cached
# .url passed every live case here on 2026-09-25, because a local headless Chrome keeps
# /json/list current. The staleness KNOWN-ISSUES.md:16-22 records is a property of the
# box's long-lived tab that a fresh local browser does not reproduce. So the property
# is pinned at the SOURCE instead: the only place the tool may read a target's .url is
# the `targets` listing. The mutant fails this check.
check "SOURCE: a target's cached .url is read in exactly one place (the listing)" "1" \
      "$(grep -cE "\[[\"']url[\"']\]|get\([\"']url[\"']" "$TOOL")"

if [ "${GV_CDP_TEST_INTERNET:-0}" = "1" ]; then
    check "navigate https://example.com/ (internet)" "https://example.com/" \
          "$("$PY" "$TOOL" navigate --port "$CDP" --target "$TID" --url https://example.com/ | tr -d '\r')"
else
    echo "  (example.com case not run; set GV_CDP_TEST_INTERNET=1 to include it)"
fi

echo "=== a navigation Chrome reports as failed is NOT printed as a landing ==="
"$PY" "$TOOL" navigate --port "$CDP" --target "$TID" --url "http://127.0.0.1:9/" \
      >"$WORK/nav.out" 2>"$WORK/nav.err"
check "failed navigation exits 3" "3" "$?"
check "…and prints no URL on stdout" "0" "$(wc -c < "$WORK/nav.out" | tr -d ' ')"

echo "=== transport faults are exit 4, never a printed landing ==="
# A page whose load event never fires inside --timeout: the server sends headers and a
# first chunk, then never finishes the response. The tool must give up at --timeout,
# exit 4, and print nothing on stdout.
"$PY" - "$HANG" <<'PY' >/dev/null 2>&1 &
import socket, sys
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", int(sys.argv[1]))); s.listen(5)
held = []
while True:
    c, _ = s.accept(); held.append(c)
    c.recv(4096)
    c.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n\r\n<!doctype html><title>slow</title>")
PY
HANG_PID=$!
sleep 1
t0=$(date +%s)
"$PY" "$TOOL" navigate --port "$CDP" --target "$TID" --url "http://127.0.0.1:${HANG}/" --timeout 3 \
      >"$WORK/hang.out" 2>"$WORK/hang.err"
rc=$?; t1=$(date +%s)
killtree "$HANG_PID"
check "no load event within --timeout -> exit 4" "4" "$rc"
check "…and prints no URL on stdout" "0" "$(wc -c < "$WORK/hang.out" | tr -d ' ')"
check "…and gave up near --timeout, not the default" "yes" \
      "$([ $((t1 - t0)) -le 10 ] && echo yes || echo "no ($((t1 - t0))s)")"
"$PY" "$TOOL" targets --port 9 >"$WORK/dead.out" 2>&1
check "a CDP port nothing listens on -> exit 4" "4" "$?"

echo "=== no websocket is left open ==="
sleep 1
if command -v ss >/dev/null 2>&1; then
    open="$(ss -tn state established "( dport = :${CDP} )" 2>/dev/null | tail -n +2 | wc -l)"
else
    open="$(netstat -an 2>/dev/null | grep -E "127\.0\.0\.1:${CDP}[[:space:]].*ESTABLISHED|:${CDP}[[:space:]]+ESTABLISHED" | wc -l)"
fi
check "no ESTABLISHED connection to the CDP port after the runs" "0" "$(echo "$open" | tr -d ' ')"

echo
if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
exit "$fail"
