#!/usr/bin/env bash
# Check the OWNER'S sign-in driver against docs/gv-relogin-driver-contract.md §9.2.
#
#   bash deploy/tests/check-relogin-driver.sh               # checks deploy/gv-relogin-signin.py
#   GV_RELOGIN_DRIVER_UNDER_TEST=/path bash …               # checks another file
#   bash deploy/tests/check-relogin-driver.sh --self-test   # proves the checker can fail
#
# ⛔ IT NEVER CONTACTS GOOGLE AND NEVER TOUCHES A BROWSER. The only CDP port it hands the
# driver is one where nothing listens, so the one run that gets past input validation is
# refused before any page exists to touch. The credential it feeds is a fixture.
#
# ⚠ It checks what can be checked from OUTSIDE without a sign-in page: input handling,
# the one legal TRANSPORT, secret hygiene on stdout/stderr, and a static scan for things
# the contract forbids. It cannot check visibility-based classification or the at-most-one
# submit; §9.3 of the contract lists those as the owner's own review.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
DRIVER="${GV_RELOGIN_DRIVER_UNDER_TEST:-${HERE}/../gv-relogin-signin.py}"
FIXTURE="${HERE}/gv-relogin-driver-contract-fixture.py"

if [ "$(uname -s)" != "Linux" ]; then
    echo "FAILURES PRESENT: run this on Linux (WSL or the harness container); got $(uname -s)"; exit 2
fi

fail=0; cases=0
check() { cases=$((cases + 1))
    if [ "$2" = "$3" ]; then printf '  PASS %s\n' "$1"
    else printf '  FAIL %s: expected [%s] got [%s]\n' "$1" "$2" "$3"; fail=1; fi; }

if [ "${1:-}" = "--self-test" ]; then
    WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
    echo "=== self-test: the contract fixture passes ==="
    out="$(GV_RELOGIN_DRIVER_UNDER_TEST="$FIXTURE" bash "$0" 2>&1)"
    check "the known-good fixture passes every case" "yes" \
          "$(printf '%s\n' "$out" | grep -q '^ALL .* CASES PASSED' && echo yes || { printf '%s\n' "$out" | grep FAIL >&2; echo no; })"
    echo "=== self-test: each broken copy is caught by its named case ==="
    mutant() { # mutant NAME EXPECTED-FAILING-CASE SED-SCRIPT
        local m="$WORK/$1.py" out
        sed -e "$3" "$FIXTURE" > "$m"
        if cmp -s "$m" "$FIXTURE"; then check "mutant $1 differs" "differs" "identical"; return; fi
        out="$(GV_RELOGIN_DRIVER_UNDER_TEST="$m" bash "$0" 2>&1)"
        check "mutant $1 is caught by: $2" "caught" \
              "$(printf '%s\n' "$out" | grep -qF "FAIL $2" && echo caught || echo MISSED)"
    }
    mutant leaks-password "the fixture password is in neither stdout nor stderr (all runs)" \
        's/^        print("TRANSPORT")$/        print("debug", fields["password"], file=sys.stderr); print("TRANSPORT")/'
    mutant refused-is-unrecognised "a CDP port where nothing listens -> TRANSPORT, exit 0" \
        '/^    except ConnectionRefusedError:$/,/^        return 0$/ s/print("TRANSPORT")/print("UNRECOGNISED")/'
    mutant ignores-version "stdin version=2 -> UNRECOGNISED, exit 0" \
        's/fields.get("version") != "1" or //'
    mutant nonzero-exit "empty stdin -> UNRECOGNISED, exit 0" \
        '0,/^        return 0$/ s/^        return 0$/        return 1/'
    mutant uses-subprocess "static: no subprocess, shell, browser launch, profile path, clearing or target create/close" \
        's/^import socket$/import socket, subprocess/'
    mutant argv-secret "static: no argv option for a secret" \
        's/^import sys$/import sys, argparse; argparse.ArgumentParser().add_argument("--password")/'
    echo
    if [ "$fail" -eq 0 ]; then echo "ALL ${cases} SELF-TEST CASES PASSED"; else echo "FAILURES PRESENT (${cases} self-test cases run)"; fi
    exit "$fail"
fi

if [ ! -f "$DRIVER" ]; then
    # ⛔ LOUD, never a silent pass.
    echo "SKIPPED-LOUDLY: no driver at ${DRIVER}. Nothing was checked. (Auto-relogin is inert until it exists.)"
    exit 2
fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
# No English words in it: its 4-character windows must not occur in ordinary log text.
PW='Qk7-zP9q=Zx "w$v\7Lp'
EMAIL='check.fixture@example.invalid'
DEAD_PORT=9          # discard: nothing listens on loopback in any environment this runs in
ALL_OUT="$WORK/all.txt"; : > "$ALL_OUT"

echo "=== static ==="
check "the file compiles" "0" "$(python3 -m py_compile "$DRIVER" >/dev/null 2>&1; echo $?)"
check "static: reads the credential from sys.stdin" "yes" "$(grep -q 'sys\.stdin' "$DRIVER" && echo yes || echo no)"
check "static: no argv option for a secret" "0" \
      "$(grep -ciE 'add_argument\([^)]*(pass|secret|cred|email)' "$DRIVER")"
check "static: no subprocess, shell, browser launch, profile path, clearing or target create/close" "0" \
      "$(grep -cE 'subprocess|os\.system|os\.popen|Popen|os\.exec|pty\.|user-data-dir|Browser\.close|Browser\.crash|clearBrowserCookies|clearBrowserCache|Storage\.clear|Target\.createTarget|Target\.closeTarget|putenv' "$DRIVER")"
check "static: does not read gv-account.conf or call refresh-from-browser" "0" \
      "$(grep -cE 'gv-account\.conf|refresh-from-browser' "$DRIVER")"

# run_driver NAME STDIN-FILE -> "<last line>:<exit>:<seconds>"
run_driver() {
    local t0 t1 rc last
    t0=$(date +%s)
    timeout 30 python3 "$DRIVER" < "$2" > "$WORK/$1.out" 2> "$WORK/$1.err"; rc=$?
    t1=$(date +%s)
    cat "$WORK/$1.out" "$WORK/$1.err" >> "$ALL_OUT"
    last="$(tail -n 1 "$WORK/$1.out" 2>/dev/null)"
    printf '%s:%s:%s' "$last" "$rc" "$((t1 - t0))"
}
lastrc() { printf '%s' "${1%:*}"; }
secs()   { printf '%s' "${1##*:}"; }

echo "=== dynamic (no Google: every run is refused or rejected before any page) ==="
printf 'version=2\ncdp_port=%s\ntarget_id=NONE\nemail=%s\npassword=%s\n' "$DEAD_PORT" "$EMAIL" "$PW" > "$WORK/v2.in"
r="$(run_driver v2 "$WORK/v2.in")"
check "stdin version=2 -> UNRECOGNISED, exit 0" "UNRECOGNISED:0" "$(lastrc "$r")"
: > "$WORK/empty.in"
r="$(run_driver empty "$WORK/empty.in")"
check "empty stdin -> UNRECOGNISED, exit 0" "UNRECOGNISED:0" "$(lastrc "$r")"
printf 'version=1\ncdp_port=%s\ntarget_id=NONE\nemail=%s\npassword=%s\n' "$DEAD_PORT" "$EMAIL" "$PW" > "$WORK/dead.in"
r="$(run_driver dead "$WORK/dead.in")"
check "a CDP port where nothing listens -> TRANSPORT, exit 0" "TRANSPORT:0" "$(lastrc "$r")"
check "…within 15 s" "yes" "$([ "$(secs "$r")" -le 15 ] && echo yes || echo "no ($(secs "$r")s)")"
check "the fixture password is in neither stdout nor stderr (all runs)" "0" "$(grep -caF -- "$PW" "$ALL_OUT")"
leaks=0
for i in $(seq 0 $(( ${#PW} - 4 ))); do grep -qaF -- "${PW:$i:4}" "$ALL_OUT" && leaks=$((leaks + 1)); done
check "…nor any 4-character substring of it" "0" "$leaks"

echo
if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED (${DRIVER})"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
exit "$fail"
