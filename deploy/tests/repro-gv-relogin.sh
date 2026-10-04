#!/usr/bin/env bash
# The auto-relogin ACTUATOR, tested against stubs. docs/plans/gv-auto-relogin.md Tasks 9-12.
# Lane L: Linux only (the /proc and mode-600 cases are meaningless elsewhere). No box, no
# browser, no Google, no real credential.
#
#   bash deploy/tests/repro-gv-relogin.sh
#
# What stands in for what:
#   the service (status + refresh-from-browser)  gv-relogin-service-stub.py, records every request
#   deploy/gv-cdp.py                             gv-relogin-cdp-stub.py, same CLI, file-driven
#   the owner's sign-in driver                   gv-relogin-driver-stub.py — prints a chosen verdict
#                                                and records what it received on stdin/argv/env,
#                                                and every ancestor's cmdline, WHILE it runs
#
# ⛔ Every case asserts what the STUBS RECORDED or what the BREAKER FILE now says — never
# the actuator's own report of what it did. And every case is written so that the UNSAFE
# behaviour is what fails it.
#
# ⭐ THE HARNESS PROVES ITSELF. After the cases pass against the real actuator, it builds
# MUTANTS — copies of the actuator with one rule broken — and requires the named case to
# FAIL against each. (Pattern: repro-gv-relogin-breaker.sh.) It does not subsume the
# breaker's own harness: that one tests the breaker with no actuator; this one tests the
# actuator's USE of it.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REAL_ACTUATOR="${HERE}/../gv-auto-relogin.sh"
ACTUATOR="${GV_RELOGIN_ACTUATOR:-$REAL_ACTUATOR}"
BREAKER_LIB="${HERE}/../gv-auto-relogin-breaker.sh"
PORT="${GV_RELOGIN_HARNESS_PORT:-8198}"

if [ "$(uname -s)" != "Linux" ]; then
    echo "FAILURES PRESENT: lane L must run on Linux (got $(uname -s))"
    exit 2
fi

WORK="$(mktemp -d)"
SVC_PID=""
cleanup() { [ -n "$SVC_PID" ] && kill "$SVC_PID" 2>/dev/null; rm -rf "$WORK"; }
trap cleanup EXIT

export HOME="$WORK/home"
mkdir -p "$HOME/.local/state"
export GV_STUB_DIR="$WORK/stub"
mkdir -p "$GV_STUB_DIR/cdp" "$GV_STUB_DIR/driver"
SVC="$WORK/svc"; mkdir -p "$SVC"
export GV_RELOGIN_STATE_FILE="$WORK/breaker.state"
export GV_RELOGIN_LOCK_FILE="$WORK/breaker.lock"
export GV_RELOGIN_STATUS_URL="http://127.0.0.1:${PORT}/api/gvbridge/status"
export GV_RELOGIN_ACCOUNT_FILE="$WORK/gv-account.conf"
export GV_RELOGIN_CDP_HELPER="${HERE}/gv-relogin-cdp-stub.py"
export GV_RELOGIN_SIGNIN_DRIVER="${HERE}/gv-relogin-driver-stub.py"
export GV_RELOGIN_ASSIST_STATE_FILE="$WORK/assist.state"
unset GV_RELOGIN_MAX_PER_HOUR GV_RELOGIN_MAX_PER_DAY GV_RELOGIN_MAX_TRANSPORT_PER_DAY GV_RELOGIN_DRIVER_TIMEOUT GV_RELOGIN_CDP_PORT

# ⛔ The fixture credential. NOT exported: nothing in the harness's own environment may
# carry it, or the environ checks below would find it there and prove nothing. It contains
# every character a naive encoding breaks on: `=`, a space, a double quote, a backslash,
# a dollar sign.
FIXTURE_EMAIL='zz.fixture.account@example.invalid'
FIXTURE_PW='Qv7=Kp 9"x\$w!Tm'

python3 "${HERE}/gv-relogin-service-stub.py" --port "$PORT" --dir "$SVC" 2>/dev/null &
SVC_PID=$!
for _ in $(seq 40); do
    printf '{}' > "$SVC/status.json"
    curl -s -o /dev/null --max-time 1 "$GV_RELOGIN_STATUS_URL" && break
    sleep 0.1
done

fail=0
cases=0
check() { # check NAME EXPECTED ACTUAL
    cases=$((cases + 1))
    if [ "$2" = "$3" ]; then printf '  PASS %s\n' "$1"
    else printf '  FAIL %s: expected [%s] got [%s]\n' "$1" "$2" "$3"; fail=1; fi
}

# --- fixtures -------------------------------------------------------------------
STALE='{"browserRefreshOutcome":"Stale","browserSessionValidatedAt":"2026-09-25T10:00:00Z"}'
MOVED='{"browserRefreshOutcome":"Succeeded","browserSessionValidatedAt":"2026-09-25T11:00:00Z"}'
serve()      { printf '%s' "$1" > "$SVC/status.json"; printf '%s' "${2:-200}" > "$SVC/status.code"; }
after_post() { printf '%s' "$1" > "$SVC/after-post.json"; printf '%s' "${2:-200}" > "$SVC/after-post.code"; }
post_code()  { printf '%s' "$1" > "$SVC/post.code"; }
pages()      { printf '%b' "$1" > "$GV_STUB_DIR/cdp/pages"; }
landing()    { printf '%s' "$1" > "$GV_STUB_DIR/cdp/landing"; }
mode()       { printf '%s' "$1" > "$GV_STUB_DIR/driver/mode"; }
write_account() {
    printf 'GV_ACCOUNT_EMAIL=%s\nGV_ACCOUNT_PASSWORD=%s\n' "$FIXTURE_EMAIL" "$FIXTURE_PW" > "$GV_RELOGIN_ACCOUNT_FILE"
    chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"
}
# A FRESH, ARMED breaker with zero counters, a signed-out session, one Voice page, a
# driver that will say SIGNED_IN, and a service that confirms the refresh. Each case then
# breaks exactly the one thing it is about.
fresh() {
    rm -f "$GV_RELOGIN_STATE_FILE" "$GV_RELOGIN_ASSIST_STATE_FILE" "$SVC"/*.log "$SVC"/after-post.* "$SVC"/post.code
    rm -rf "$GV_STUB_DIR/cdp" "$GV_STUB_DIR/driver"; mkdir -p "$GV_STUB_DIR/cdp" "$GV_STUB_DIR/driver"
    bash "$BREAKER_LIB" --reset >/dev/null 2>&1
    serve "$STALE"; after_post "$MOVED"; post_code 200
    pages 'T1\thttps://voice.google.com/u/0/voicemail\n'
    landing 'https://voice.google.com/u/0/voicemail'
    mode 'word:SIGNED_IN'
    write_account
}
act()   { bash "$ACTUATOR" "$@" >"$WORK/out.txt" 2>"$WORK/err.txt"; echo "$?"; }
field() { grep -m1 "^$1=" "$GV_RELOGIN_STATE_FILE" 2>/dev/null | cut -d= -f2-; }
count_lines() { if [ -f "$1" ]; then grep -c . "$1"; else echo 0; fi; }
driver_runs() { count_lines "$GV_STUB_DIR/driver/runs.log"; }
cdp_calls()   { count_lines "$GV_STUB_DIR/cdp/calls.log"; }
posts()       { if [ -f "$SVC/requests.log" ]; then grep -c 'POST /api/gvbridge/cookies/refresh-from-browser' "$SVC/requests.log"; else echo 0; fi; }
requests()    { count_lines "$SVC/requests.log"; }
age_hour()    { sed -i "s/^BREAKER_LAST_ATTEMPT_AT=.*/BREAKER_LAST_ATTEMPT_AT=$(( $(date -u +%s) - 7200 ))/" "$GV_RELOGIN_STATE_FILE"; }
reason_has()  { bash "$BREAKER_LIB" --status 2>/dev/null | grep -qF -- "$1" && echo yes || echo no; }
journal_has() { grep -qF -- "$1" "$WORK/err.txt" && echo yes || echo no; }

echo "=== Task 9 — the gate: act ONLY on Stale or SignedOut ==="
for o in Succeeded Unreachable NotAttempted TornDown Hibernating; do
    fresh; serve "{\"browserRefreshOutcome\":\"$o\"}"
    rc="$(act)"
    check "outcome=$o -> exit 0, NO driver run, NO CDP call" "0:0:0" "$rc:$(driver_runs):$(cdp_calls)"
done
fresh; serve '{"browserSessionValidatedAt":"x"}'
check "⛔ no browserRefreshOutcome field (an old build) -> no attempt" "0:0" "$(act):$(driver_runs)"
fresh; GV_RELOGIN_STATUS_URL="http://127.0.0.1:9/api/gvbridge/status" bash "$ACTUATOR" >/dev/null 2>"$WORK/err.txt"
check "status connection refused -> exit 0, no attempt" "0:0" "$?:$(driver_runs)"
fresh; serve '{}' 500
check "status http 500 -> no attempt" "0:0" "$(act):$(driver_runs)"
fresh
check "outcome=Stale -> exactly ONE driver run" "0:1" "$(act):$(driver_runs)"
fresh; serve '{"browserRefreshOutcome":"SignedOut","browserSessionValidatedAt":"2026-09-25T10:00:00Z"}'
check "⛔ outcome=SignedOut (PR #90) -> exactly ONE driver run" "0:1" "$(act):$(driver_runs)"

echo "=== Task 9 — the breaker decides first ==="
fresh; bash -c '. "$1"; breaker_load; breaker_trip challenged "harness trip. A human must run: gv-auto-relogin.sh --reset"; breaker_write' _ "$BREAKER_LIB" 2>/dev/null
rc="$(act)"
check "⛔ Stale + breaker TRIPPED -> no driver, no CDP, no status poll" "0:0:0:0" "$rc:$(driver_runs):$(cdp_calls):$(requests)"
check "…and the journal quotes the breaker's refusal" "yes" "$(journal_has 'not attempting: REFUSED breaker TRIPPED (challenged)')"
fresh; rm -f "$GV_RELOGIN_STATE_FILE"
check "no state file (fresh install) -> no attempt" "0:0" "$(act):$(driver_runs)"
check "…and the file it writes says TRIPPED (fail closed)" "TRIPPED" "$(field BREAKER_STATE)"
fresh; act >/dev/null
check "PRECONDITION one success spent the hour" "1" "$(driver_runs)"
serve "$STALE"; act >/dev/null
check "⛔ a second run inside the hour -> NO second attempt (1/hour)" "1" "$(driver_runs)"
# The stub's status turned Succeeded on each confirmed refresh; the session is signed out
# again for each of these, or the gate (correctly) would not act at all.
for _ in 1 2; do age_hour; serve "$STALE"; act >/dev/null; done
check "PRECONDITION three attempts today with the hour aged each time" "3:3" "$(driver_runs):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
age_hour; serve "$STALE"; act >/dev/null
check "⛔ the 4th attempt today is refused even with the hour aged (3/day)" "3" "$(driver_runs)"
check "…and says so" "yes" "$(journal_has 'rate limit: 3/3 credential attempts already used today')"

echo "=== Task 9 — no driver installed is the SAFE RESTING STATE ==="
fresh; before="$(sha256sum "$GV_RELOGIN_STATE_FILE")"
rc="$(GV_RELOGIN_SIGNIN_DRIVER="$WORK/absent-driver.py" act)"
check "no driver -> exit 0" "0" "$rc"
check "…the breaker file is byte-identical (no trip, no counter)" "$before" "$(sha256sum "$GV_RELOGIN_STATE_FILE")"
check "…no status poll, no CDP call" "0:0" "$(requests):$(cdp_calls)"
check "…and the journal says auto-relogin is not installed" "yes" "$(journal_has 'auto-relogin not installed: no sign-in driver')"
check "…and --status says so too" "yes" \
      "$(GV_RELOGIN_SIGNIN_DRIVER="$WORK/absent-driver.py" bash "$ACTUATOR" --status 2>/dev/null | grep -q 'NOT INSTALLED' && echo yes || echo no)"

echo "=== Task 9 — stand down while a HUMAN is signing in (reauth assist, PR #89 §5.1) ==="
for st in PREPARED SIGNED_IN_UNCONFIRMED; do
    fresh; printf 'STATE=%s\nFALLBACK_MODE=breaker-tripped\n' "$st" > "$GV_RELOGIN_ASSIST_STATE_FILE"
    before="$(sha256sum "$GV_RELOGIN_STATE_FILE")"
    rc="$(act)"
    check "⛔ assist STATE=$st -> exit 0, NO driver, NO CDP" "0:0:0" "$rc:$(driver_runs):$(cdp_calls)"
    check "…not a trip, no budget spent: breaker file byte-identical" "$before" "$(sha256sum "$GV_RELOGIN_STATE_FILE")"
    check "…and the journal says a human sign-in is in progress" "yes" "$(journal_has 'a human sign-in is in progress')"
done
fresh; printf 'STATE=IDLE\n' > "$GV_RELOGIN_ASSIST_STATE_FILE"
check "assist STATE=IDLE -> the attempt proceeds" "1" "$(act >/dev/null; driver_runs)"
fresh; printf 'garbage\n' > "$GV_RELOGIN_ASSIST_STATE_FILE"
check "assist file with no STATE line -> stand down (cannot tell)" "0" "$(act >/dev/null; driver_runs)"
fresh; printf 'STATE=IDLE\nSTATE=PREPARED\n' > "$GV_RELOGIN_ASSIST_STATE_FILE"
check "assist file with two STATE lines -> stand down" "0" "$(act >/dev/null; driver_runs)"
for st in CONFIRM_FAILED CONFIRM_REFUSED HIBERNATING; do
    fresh; printf 'STATE=%s\n' "$st" > "$GV_RELOGIN_ASSIST_STATE_FILE"
    check "⛔ assist STATE=$st (not IDLE) -> stand down, nothing spent" "0:0" "$(act >/dev/null; driver_runs):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
done
fresh; printf 'STATE=IDLE\r\n' > "$GV_RELOGIN_ASSIST_STATE_FILE"
check "assist STATE=IDLE with a stray CR -> stand down (not exactly IDLE)" "0" "$(act >/dev/null; driver_runs)"
fresh; SENT="$WORK/assist-executed"; printf 'STATE=$(touch %s)\n' "$SENT" > "$GV_RELOGIN_ASSIST_STATE_FILE"; act >/dev/null
check "the assist file is DATA: nothing in it is executed" "absent" "$([ -e "$SENT" ] && echo EXECUTED || echo absent)"

echo "=== Task 9 — the credential file is read as DATA, and refused loudly ==="
fresh; rm -f "$GV_RELOGIN_ACCOUNT_FILE"; rc="$(act)"
check "⛔ no account file -> TRIPPED account_file_missing, exit 0" "0:TRIPPED:account_file_missing" "$rc:$(field BREAKER_STATE):$(field BREAKER_REASON)"
check "…with NO driver run and NO credential spent" "0:0" "$(driver_runs):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
fresh; chmod 644 "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "mode 644 -> TRIPPED account_file_unsafe, no driver" "TRIPPED:account_file_unsafe:0" "$(field BREAKER_STATE):$(field BREAKER_REASON):$(driver_runs)"
fresh; mv "$GV_RELOGIN_ACCOUNT_FILE" "$WORK/real.conf"; ln -s "$WORK/real.conf" "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "a symlink -> TRIPPED account_file_unsafe" "account_file_unsafe:0" "$(field BREAKER_REASON):$(driver_runs)"
rm -f "$GV_RELOGIN_ACCOUNT_FILE" "$WORK/real.conf"
fresh; printf 'GV_ACCOUNT_EMAIL=%s\r\nGV_ACCOUNT_PASSWORD=%s\r\n' "$FIXTURE_EMAIL" "$FIXTURE_PW" > "$GV_RELOGIN_ACCOUNT_FILE"; chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "⛔ CRLF line endings -> TRIPPED malformed, never offered (a CR would be a wrong password)" "account_file_malformed:0" "$(field BREAKER_REASON):$(driver_runs)"
fresh; printf 'GV_ACCOUNT_EMAIL=%s\nhunter2secret=\nGV_ACCOUNT_PASSWORD=%s\n' "$FIXTURE_EMAIL" "$FIXTURE_PW" > "$GV_RELOGIN_ACCOUNT_FILE"; chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "an unknown key -> TRIPPED malformed" "account_file_malformed:0" "$(field BREAKER_REASON):$(driver_runs)"
check "⛔ …and the unknown key (which may be a password) is NOT in the journal or the state" "0:0" \
      "$(grep -c hunter2secret "$WORK/err.txt"):$(grep -c hunter2secret "$GV_RELOGIN_STATE_FILE")"
fresh; printf 'GV_ACCOUNT_EMAIL=%s\nGV_ACCOUNT_PASSWORD=\n' "$FIXTURE_EMAIL" > "$GV_RELOGIN_ACCOUNT_FILE"; chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "an empty password -> TRIPPED incomplete" "account_file_incomplete:0" "$(field BREAKER_REASON):$(driver_runs)"
fresh; printf 'GV_ACCOUNT_EMAIL=%s\nGV_ACCOUNT_PASSWORD=a\nGV_ACCOUNT_PASSWORD=b\n' "$FIXTURE_EMAIL" > "$GV_RELOGIN_ACCOUNT_FILE"; chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "a duplicated key -> TRIPPED malformed" "account_file_malformed:0" "$(field BREAKER_REASON):$(driver_runs)"
fresh; SENT2="$WORK/account-executed"
printf '# comment\n\nGV_ACCOUNT_EMAIL=%s\nGV_ACCOUNT_PASSWORD=$(touch %s)\n' "$FIXTURE_EMAIL" "$SENT2" > "$GV_RELOGIN_ACCOUNT_FILE"; chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; act >/dev/null
check "the account file is DATA: a \$(…) value is not executed" "absent" "$([ -e "$SENT2" ] && echo EXECUTED || echo absent)"
check "…it reaches the driver VERBATIM, comments and blank lines skipped" "yes" \
      "$(grep -qxF "password=\$(touch ${SENT2})" "$GV_STUB_DIR/driver/stdin.bin" && echo yes || echo no)"

echo "=== Task 11 — target selection by LIVE location ==="
fresh; pages 'T1\thttps://example.com/\n'; act >/dev/null
check "no page on a Google sign-in/Voice host -> transport, NOT a trip: ARMED, no driver, credential 0, transport 1" \
      "ARMED:0:0:1" "$(field BREAKER_STATE):$(driver_runs):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS):$(field BREAKER_DAY_TRANSPORT_FAILURES)"
fresh; pages 'T1\thttps://voice.google.com/u/0/voicemail\nT2\thttps://accounts.google.com/v3/signin/challenge/pwd\n'; act >/dev/null
check "⛔ two candidates -> TRIPPED target_unrecognised (never a guess)" "target_unrecognised:0" "$(field BREAKER_REASON):$(driver_runs)"
fresh; pages 'P1\thttps://workspace.google.com/products/voice/\nT9\thttps://accounts.google.com/v3/signin/accountchooser?x=1\n'; act >/dev/null
check "a parked Workspace tab beside the sign-in page -> the sign-in page is driven" "target_id=T9" \
      "$(grep -a '^target_id=' "$GV_STUB_DIR/driver/stdin.bin")"
fresh; pages 'W1\thttps://workspace.google.com/products/voice/\n'; act >/dev/null
check "only the Workspace Voice page (a signed-out Voice tab) -> it is driven" "target_id=W1" \
      "$(grep -a '^target_id=' "$GV_STUB_DIR/driver/stdin.bin")"
fresh; pages 'E1\thttps://accounts.google.com.evil.example/\n'; act >/dev/null
check "host is PARSED, not substring-matched (accounts.google.com.evil.example): no candidate, no driver" "0:1" "$(driver_runs):$(field BREAKER_DAY_TRANSPORT_FAILURES)"
fresh; printf '4' > "$GV_STUB_DIR/cdp/targets.rc"; rc="$(act)"
check "CDP does not answer -> transport: ARMED, no driver, credential 0, transport 1" "0:ARMED:0:0:1" \
      "$rc:$(field BREAKER_STATE):$(driver_runs):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS):$(field BREAKER_DAY_TRANSPORT_FAILURES)"

echo "=== Task 10 hand-off — each VERDICT drives the breaker ==="
fresh; act >/dev/null
check "SIGNED_IN + confirmed -> ARMED, last outcome succeeded, credential 1" "ARMED:succeeded:1" \
      "$(field BREAKER_STATE):$(field BREAKER_LAST_OUTCOME):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
check "…exactly one refresh-from-browser POST" "1" "$(posts)"
check "…and the driver was given the ONE chosen page" "target_id=T1" "$(grep -a '^target_id=' "$GV_STUB_DIR/driver/stdin.bin")"

fresh; mode 'word:CREDENTIAL_REJECTED'; act >/dev/null
check "⛔ CREDENTIAL_REJECTED -> TRIPPED credential_rejected after exactly ONE attempt" "TRIPPED:credential_rejected:1:1" \
      "$(field BREAKER_STATE):$(field BREAKER_REASON):$(driver_runs):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
check "…and no cookies are posted" "0" "$(posts)"
age_hour; act >/dev/null
check "⛔ …a SECOND run, hour aged, makes NO attempt" "1" "$(driver_runs)"
fresh; mode 'word:CHALLENGED'; act >/dev/null; age_hour; act >/dev/null
check "⛔ CHALLENGED -> TRIPPED challenged; a second run makes no attempt" "TRIPPED:challenged:1" \
      "$(field BREAKER_STATE):$(field BREAKER_REASON):$(driver_runs)"
fresh; mode 'word:TRANSPORT'; act >/dev/null
check "⛔ TRANSPORT -> ARMED, credential budget HANDED BACK (0), transport 1" "ARMED:0:1:0" \
      "$(field BREAKER_STATE):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS):$(field BREAKER_DAY_TRANSPORT_FAILURES):$(field BREAKER_ATTEMPTS_TOTAL)"
act >/dev/null
check "…and an immediate second run is refused by the hourly spacing" "1" "$(driver_runs)"
fresh; mode 'word:UNRECOGNISED'; act >/dev/null
check "UNRECOGNISED -> TRIPPED unclassified" "TRIPPED:unclassified" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
fresh; mode 'garbage'; act >/dev/null
check "⛔ a last line that is not a verdict word -> TRIPPED unclassified (never TRANSPORT)" "TRIPPED:unclassified:1" \
      "$(field BREAKER_STATE):$(field BREAKER_REASON):$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
fresh; mode 'silent'; act >/dev/null
check "silence -> TRIPPED unclassified" "TRIPPED:unclassified" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
fresh; mode 'crash'; act >/dev/null
check "a crash -> TRIPPED unclassified" "TRIPPED:unclassified" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
fresh; mode 'nonzero:TRANSPORT'; act >/dev/null
check "⛔ a verdict word with a NON-ZERO exit -> TRIPPED (a crash is not a transport fault)" "TRIPPED:unclassified" \
      "$(field BREAKER_STATE):$(field BREAKER_REASON)"
fresh; mode 'noisy:SIGNED_IN'; act >/dev/null
check "noise lines, then the verdict on the LAST line -> read correctly" "ARMED:succeeded" "$(field BREAKER_STATE):$(field BREAKER_LAST_OUTCOME)"
fresh; mode 'sleep'; t0=$(date +%s); GV_RELOGIN_DRIVER_TIMEOUT=2 act >/dev/null; t1=$(date +%s)
check "⛔ a driver still running at the limit is killed -> TRIPPED unclassified" "TRIPPED:unclassified" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
check "…promptly (the limit, not the driver's 30 s)" "yes" "$([ $((t1 - t0)) -lt 20 ] && echo yes || echo "no ($((t1 - t0))s)")"
fresh; GV_RELOGIN_SIGNIN_DRIVER="$WORK/vanishing.py"; printf 'import sys; sys.exit(0)\n' > "$WORK/vanishing.py"
act >/dev/null; unset GV_RELOGIN_SIGNIN_DRIVER; export GV_RELOGIN_SIGNIN_DRIVER="${HERE}/gv-relogin-driver-stub.py"
check "a driver that exits 0 and prints nothing -> TRIPPED" "TRIPPED:unclassified" "$(field BREAKER_STATE):$(field BREAKER_REASON)"

echo "=== a run killed mid-attempt is found by the next one ==="
fresh; sed -i 's/^BREAKER_LAST_OUTCOME=.*/BREAKER_LAST_OUTCOME=in_flight/' "$GV_RELOGIN_STATE_FILE"
act >/dev/null
check "⛔ LAST_OUTCOME=in_flight on an ARMED breaker -> TRIPPED interrupted, no driver" "TRIPPED:interrupted:0" \
      "$(field BREAKER_STATE):$(field BREAKER_REASON):$(driver_runs)"
fresh; mode 'word:CHALLENGED'
# Observe the file WHILE the driver runs: the charge must already be on disk.
cat > "$WORK/peek-driver.py" <<EOF
import shutil, sys
sys.stdin.read()
shutil.copyfile("${GV_RELOGIN_STATE_FILE}", "${WORK}/during.state")
print("CHALLENGED")
EOF
GV_RELOGIN_SIGNIN_DRIVER="$WORK/peek-driver.py" act >/dev/null
check "⛔ the attempt is persisted BEFORE the driver runs (credential 1, in_flight)" "1:in_flight" \
      "$(grep '^BREAKER_DAY_CREDENTIAL_ATTEMPTS=' "$WORK/during.state" | cut -d= -f2):$(grep '^BREAKER_LAST_OUTCOME=' "$WORK/during.state" | cut -d= -f2)"

echo "=== one run at a time ==="
fresh; mode 'slow:SIGNED_IN'
bash "$ACTUATOR" >/dev/null 2>"$WORK/err-a.txt" &
A=$!
sleep 1
GV_RELOGIN_LOCK_WAIT=1 bash "$ACTUATOR" >/dev/null 2>"$WORK/err-b.txt"; rc_b=$?
wait "$A"
# ⚠ "without acting" includes not TRIPPING: without the lock, the second run finds the
# first one's in_flight marker, reads it as a dead run, and trips a healthy breaker.
check "⛔ two runs at once: the second exits 0 without acting" "0:1:0" \
      "$rc_b:$(driver_runs):$(grep -c 'TRIPPED' "$WORK/err-b.txt")"
check "…says the lock is held" "yes" "$(grep -q 'holds the lock' "$WORK/err-b.txt" && echo yes || echo no)"
check "…and the counters advanced ONCE" "1" "$(field BREAKER_DAY_CREDENTIAL_ATTEMPTS)"
fresh; mode 'slow:SIGNED_IN'
bash "$ACTUATOR" >/dev/null 2>&1 &
A=$!
sleep 1
flock -n "$GV_RELOGIN_LOCK_FILE" true; held=$?
wait "$A"
check "PRECONDITION the lock IS held while a run is in progress" "1" "$held"
# A driver that leaves a helper process behind must not leave the LOCK behind with it,
# or every later run (and every human --reset) waits on a process nobody knows about.
fresh; mode 'daemon:SIGNED_IN'; t0=$(date +%s); act >/dev/null; t1=$(date +%s)
check "PRECONDITION the daemonising driver's run completed" "succeeded" "$(field BREAKER_LAST_OUTCOME)"
check "a helper left behind by the driver does not stall the run" "yes" \
      "$([ $((t1 - t0)) -lt 5 ] && echo yes || echo "no ($((t1 - t0))s)")"
check "the driver does not inherit the lock: free the moment the run ends" "0" \
      "$(flock -n "$GV_RELOGIN_LOCK_FILE" true; echo $?)"
sleep 6   # let the stub's leftover child finish before the next case

echo "=== Task 11 — verify by OUTCOME, never by the driver's word ==="
fresh; serve '{"browserRefreshOutcome":"Stale","browserSessionValidatedAt":"2026-09-25T10:00:00Z"}'
after_post '{"browserRefreshOutcome":"Succeeded","browserSessionValidatedAt":"2026-09-25T10:00:00Z"}'
act >/dev/null
check "⛔ Succeeded but validatedAt did NOT move (the cron's work, not ours) -> TRIPPED verification_failed" \
      "TRIPPED:verification_failed" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
fresh; landing 'https://workspace.google.com/products/voice/'; act >/dev/null
check "⛔ the driver lied (forced navigation lands on the signed-out page) -> TRIPPED" "TRIPPED:verification_failed" \
      "$(field BREAKER_STATE):$(field BREAKER_REASON)"
check "⛔ …and the service saw ZERO refresh-from-browser POSTs" "0" "$(posts)"
check "…and the reason says no cookies were posted" "yes" "$(reason_has 'No cookies were posted to the service')"
fresh; landing 'https://accounts.google.com/v3/signin/challenge/pwd'; act >/dev/null
check "forced navigation lands on the sign-in host -> TRIPPED, no POST" "TRIPPED:0" "$(field BREAKER_STATE):$(posts)"
fresh; printf '4' > "$GV_STUB_DIR/cdp/navigate.rc"; act >/dev/null
check "the forced navigation fails -> TRIPPED, no POST" "TRIPPED:verification_failed:0" "$(field BREAKER_STATE):$(field BREAKER_REASON):$(posts)"
fresh; post_code 502; after_post "$STALE"; act >/dev/null
check "Google refused the cookies (502, still Stale) -> TRIPPED" "TRIPPED:verification_failed" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
check "…and only THIS reason says the working set was NOT overwritten" "yes" "$(reason_has 'kept its previously-working set (it was NOT overwritten)')"
fresh; post_code 202; act >/dev/null
check "⛔ 202 (written but UNPROVEN) is not success, even with validatedAt moved -> TRIPPED" "TRIPPED:verification_failed" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
check "⛔ …and the reason says the previous file WAS overwritten, never that it was not" "yes:no" \
      "$(reason_has 'previous cookie file WAS overwritten'):$(reason_has 'NOT overwritten')"
fresh; after_post '{}' 500; act >/dev/null
check "the service died mid-verify -> TRIPPED" "TRIPPED:verification_failed" "$(field BREAKER_STATE):$(field BREAKER_REASON)"
check "…and the reason says the POST was accepted but not confirmed (no guess about the file)" "yes:no" \
      "$(reason_has 'adopted the harvested cookies (HTTP 200)'):$(reason_has 'NOT overwritten')"

echo "=== ⛔ the credential: stdin only, in the documented format ==="
fresh; act >/dev/null
printf 'version=1\ncdp_port=9224\ntarget_id=T1\nemail=%s\npassword=%s\n' "$FIXTURE_EMAIL" "$FIXTURE_PW" > "$WORK/expected.stdin"
check "the driver's stdin is EXACTLY the contract's five lines, value verbatim" "same" \
      "$(cmp -s "$WORK/expected.stdin" "$GV_STUB_DIR/driver/stdin.bin" && echo same || echo differs)"
check "PRECONDITION the ancestor walk saw timeout AND the actuator" "yes" \
      "$(grep -q 'timeout' "$GV_STUB_DIR/driver/ancestors.txt" && grep -q 'gv-auto-relogin.sh' "$GV_STUB_DIR/driver/ancestors.txt" && echo yes || echo no)"
check "⛔ password in NO process's argv (driver + every ancestor, read while it ran)" "0" \
      "$(cat "$GV_STUB_DIR/driver/argv.txt" "$GV_STUB_DIR/driver/ancestors.txt" | grep -caF "$FIXTURE_PW")"
check "⛔ password in NO environment (driver + every ancestor)" "0" \
      "$(cat "$GV_STUB_DIR/driver/environ.txt" "$GV_STUB_DIR/driver/ancestors.environ" | grep -caF "$FIXTURE_PW")"
check "the email in no argv and no environment either" "0" \
      "$(cat "$GV_STUB_DIR/driver/argv.txt" "$GV_STUB_DIR/driver/ancestors.txt" "$GV_STUB_DIR/driver/environ.txt" "$GV_STUB_DIR/driver/ancestors.environ" | grep -caF "$FIXTURE_EMAIL")"

fresh; ACCT_PASSWORD=pre-exported ACCT_EMAIL=pre-exported act >/dev/null
check "⛔ an ACCT_PASSWORD already EXPORTED by the caller does not carry the real one to the driver" "0:0" \
      "$(grep -caF "$FIXTURE_PW" "$GV_STUB_DIR/driver/environ.txt"):$(grep -caF "$FIXTURE_EMAIL" "$GV_STUB_DIR/driver/environ.txt")"

echo "=== ⛔ the password is in no journal, state file, status or config output ==="
# Every failure path, with the fixture account in place, into ONE journal.
: > "$WORK/all-err.txt"
for m in word:SIGNED_IN word:CREDENTIAL_REJECTED word:CHALLENGED word:TRANSPORT word:UNRECOGNISED garbage crash silent nonzero:SIGNED_IN; do
    fresh; mode "$m"; act >/dev/null; cat "$WORK/err.txt" "$GV_RELOGIN_STATE_FILE" >> "$WORK/all-err.txt"
done
fresh; landing 'https://workspace.google.com/products/voice/'; act >/dev/null; cat "$WORK/err.txt" "$GV_RELOGIN_STATE_FILE" >> "$WORK/all-err.txt"
fresh; mode 'leak:SIGNED_IN'; act >/dev/null; cat "$WORK/err.txt" >> "$WORK/all-err.txt"
check "PRECONDITION the leaking driver's stderr reached the journal (redacted)" "yes" "$(journal_has 'password=[password redacted]')"
check "⛔ a driver that PRINTS the password to stderr: the journal shows the marker, not the password" "0" \
      "$(grep -caF "$FIXTURE_PW" "$WORK/err.txt")"
check "PRECONDITION the combined journal is not empty" "yes" "$([ -s "$WORK/all-err.txt" ] && echo yes || echo no)"
check "⛔ password absent from every journal and state file above" "0" "$(grep -caF "$FIXTURE_PW" "$WORK/all-err.txt")"
check "email absent from every journal and state file above" "0" "$(grep -caF "$FIXTURE_EMAIL" "$WORK/all-err.txt")"
leaks=0
for i in $(seq 0 $(( ${#FIXTURE_PW} - 4 ))); do
    grep -qaF -- "${FIXTURE_PW:$i:4}" "$WORK/all-err.txt" && leaks=$((leaks + 1))
done
check "⛔ no 4-character substring of the password appears anywhere above" "0" "$leaks"
check "password absent from --status" "0" "$(bash "$ACTUATOR" --status 2>&1 | grep -caF "$FIXTURE_PW")"
check "password absent from --print-config (and no substring of it)" "0" \
      "$(bash "$ACTUATOR" --print-config 2>&1 | grep -caF -e "$FIXTURE_PW" -e "${FIXTURE_PW:0:4}" -e "${FIXTURE_PW: -4}")"

echo "=== ⛔ SOURCE assertions (plan §0.3: source, not sampling) ==="
check "no --password style flag anywhere" "0" "$(grep -cE -- '--password|--pass[ =]|--credential' "$ACTUATOR")"
check "the account file is never sourced" "0" "$(grep -cE '^[[:space:]]*(\.|source)[[:space:]]+"?\$\{?ACCOUNT_FILE' "$ACTUATOR")"
check "nothing ACCT_ is exported (only ever export -n)" "0" "$(grep -E '(export|declare -x)[^#]*ACCT_' "$ACTUATOR" | grep -vc 'export -n ACCT_')"
check "the password variable is expanded on exactly 3 lines (the stdin printf, the redaction, the empty check)" "3" \
      "$(grep -cE '\$\{?ACCT_PASSWORD' "$ACTUATOR")"
check "…none of which runs jq, curl, python3, timeout or a here-string" "0" \
      "$(grep -E '\$\{?ACCT_PASSWORD' "$ACTUATOR" | grep -cE 'jq|curl|python3|timeout|<<<')"
check "the driver is launched in exactly one place" "1" "$(grep -cE 'python3 "\$DRIVER"' "$ACTUATOR")"

echo "=== housekeeping ==="
check "the harness leaves no account fixture outside its temp dir" "0" "$(find "$HERE" -name 'gv-account.conf' 2>/dev/null | wc -l)"

# --- Negative controls ------------------------------------------------------------
if [ -n "${GV_RELOGIN_ACTUATOR:-}" ]; then
    echo; if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED (against ${ACTUATOR})"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
    exit "$fail"
fi

echo "=== NEGATIVE CONTROLS: each rule broken on purpose must FAIL a named case ==="
# Each mutant runs the WHOLE harness against a copy of the actuator with one rule broken,
# in parallel (own port, own temp dir), and must produce a FAIL line for its named case.
MUT_NAMES=(); MUT_CASES=(); MUT_PIDS=()
mutant() { # mutant NAME EXPECTED-FAILING-CASE SED-SCRIPT
    local d="$WORK/mutant-$1" i=${#MUT_NAMES[@]}
    mkdir -p "$d"
    cp "$BREAKER_LIB" "$d/gv-auto-relogin-breaker.sh"
    sed -e "$3" "$REAL_ACTUATOR" > "$d/gv-auto-relogin.sh"
    MUT_NAMES+=("$1"); MUT_CASES+=("$2")
    if cmp -s "$d/gv-auto-relogin.sh" "$REAL_ACTUATOR"; then
        echo "identical" > "$d/out.txt"; MUT_PIDS+=(""); return
    fi
    GV_RELOGIN_ACTUATOR="$d/gv-auto-relogin.sh" GV_RELOGIN_HARNESS_PORT="$((PORT + 1 + i))" \
        bash "$0" > "$d/out.txt" 2>&1 &
    MUT_PIDS+=("$!")
}
collect_mutants() {
    local i d
    for i in "${!MUT_NAMES[@]}"; do
        [ -n "${MUT_PIDS[$i]}" ] && wait "${MUT_PIDS[$i]}"
        d="$WORK/mutant-${MUT_NAMES[$i]}"
        if [ "$(cat "$d/out.txt")" = "identical" ]; then
            check "mutant ${MUT_NAMES[$i]} actually differs from the actuator" "differs" "identical (the sed matched nothing)"
            continue
        fi
        check "mutant ${MUT_NAMES[$i]} is caught by: ${MUT_CASES[$i]}" "caught" \
              "$(grep -qF "FAIL ${MUT_CASES[$i]}" "$d/out.txt" && echo caught || echo MISSED)"
    done
}
mutant gate-accepts-unreachable "outcome=Unreachable -> exit 0, NO driver run, NO CDP call" \
    's/^    Stale|SignedOut) ;;$/    Stale|SignedOut|Unreachable) ;;/'
mutant gate-stale-only "⛔ outcome=SignedOut (PR #90) -> exactly ONE driver run" \
    's/^    Stale|SignedOut) ;;$/    Stale) ;;/'
mutant verdict-bypassed "⛔ Stale + breaker TRIPPED -> no driver, no CDP, no status poll" \
    's/^if \[ "\$verdict" != "AUTHORISED" \]; then$/if false; then/'
mutant no-driver-check "…the breaker file is byte-identical (no trip, no counter)" \
    's/^if \[ ! -f "\$DRIVER" \]; then$/if false; then/'
mutant assist-ignored "⛔ assist STATE=PREPARED -> exit 0, NO driver, NO CDP" \
    's/^        IDLE) ;;$/        *) ;;/'
mutant crlf-accepted "⛔ CRLF line endings -> TRIPPED malformed, never offered (a CR would be a wrong password)" \
    '/^            \*\$'"'"'\\r'"'"'\*) account_refuse/,/^                "line \${n} of \${ACCOUNT_FILE} ends in a carriage return/d'
mutant key-echoed "⛔ …and the unknown key (which may be a password) is NOT in the journal or the state" \
    's/sets a key other than GV_ACCOUNT_EMAIL or GV_ACCOUNT_PASSWORD (the key is not repeated here in case it is not a key)./sets an unknown key ${key}./'
mutant assist-denylist "⛔ assist STATE=CONFIRM_FAILED (not IDLE) -> stand down, nothing spent" \
    's/^        IDLE) ;;$/        IDLE|CONFIRM_FAILED|CONFIRM_REFUSED|HIBERNATING) ;;/'
mutant no-export-n "⛔ an ACCT_PASSWORD already EXPORTED by the caller does not carry the real one to the driver" \
    '/^export -n ACCT_EMAIL ACCT_PASSWORD 2>\/dev\/null$/d'
mutant fate-202-says-kept "⛔ …and the reason says the previous file WAS overwritten, never that it was not" \
    's/^        202) echo "The service WROTE.*$/        202) echo "The previously-working cookie set was NOT overwritten." ;;/'
mutant zero-candidates-trips "no page on a Google sign-in/Voice host -> transport, NOT a trip: ARMED, no driver, credential 0, transport 1" \
    's/^    record_transport_and_exit "no page on a Google sign-in or Voice host.*$/    breaker_trip target_unrecognised x; breaker_write; exit 0/'
mutant listing-not-live "a parked Workspace tab beside the sign-in page -> the sign-in page is driven" \
    's/^    href="\$(cdp url --target "\$tid")" || record_transport_and_exit.*$/    href="$_cached"/'
mutant transport-charges-budget "⛔ TRANSPORT -> ARMED, credential budget HANDED BACK (0), transport 1" \
    '/^      BREAKER_DAY_CREDENTIAL_ATTEMPTS="\$pre_credential"$/d'
mutant unknown-is-transport "⛔ a last line that is not a verdict word -> TRIPPED unclassified (never TRANSPORT)" \
    's/^        \*)  driver_verdict="UNRECOGNISED"$/        *)  driver_verdict="TRANSPORT"/'
mutant nonzero-trusted "⛔ a verdict word with a NON-ZERO exit -> TRIPPED (a crash is not a transport fault)" \
    's/^if \[ "\$driver_rc" -ne 0 \]; then$/if false; then/'
mutant no-in-flight-check "⛔ LAST_OUTCOME=in_flight on an ARMED breaker -> TRIPPED interrupted, no driver" \
    's/^if \[ "\$BREAKER_STATE" = "ARMED" \] && \[ "\$BREAKER_LAST_OUTCOME" = "in_flight" \]; then$/if false; then/'
mutant no-in-flight-marker "⛔ the attempt is persisted BEFORE the driver runs (credential 1, in_flight)" \
    's/^BREAKER_LAST_OUTCOME="in_flight"$/BREAKER_LAST_OUTCOME="succeeded"/'
mutant no-lock "⛔ two runs at once: the second exits 0 without acting" \
    's/^if ! breaker_lock; then$/if false; then/'
mutant lock-inherited "the driver does not inherit the lock: free the moment the run ends" \
    's/python3 "\$DRIVER" 8>&- \\$/python3 "$DRIVER" \\/'
mutant redactor-on-the-pipe "a helper left behind by the driver does not stall the run" \
    's/2> >(exec 8>&- >\/dev\/null; redact_stream)$/2> >(exec 8>\&-; redact_stream)/'
mutant validatedat-ignored "⛔ Succeeded but validatedAt did NOT move (the cron's work, not ours) -> TRIPPED verification_failed" \
    's/ && \[ "\$validated_after" != "\$validated_before" \]; then$/; then/'
mutant host-unchecked "⛔ the driver lied (forced navigation lands on the signed-out page) -> TRIPPED" \
    's/^\[ "\$landed_host" = "voice.google.com" \] \\$/true \\/'
mutant accept-202 "⛔ 202 (written but UNPROVEN) is not success, even with validatedAt moved -> TRIPPED" \
    's/^if \[ "\$post_code" = "200" \]/if [ "${post_code#20}" != "$post_code" ]/'
mutant password-in-argv "⛔ password in NO process's argv (driver + every ancestor, read while it ran)" \
    's/python3 "\$DRIVER" 8>&- \\$/python3 "$DRIVER" "$ACCT_PASSWORD" 8>\&- \\/'
mutant password-exported "⛔ password in NO environment (driver + every ancestor)" \
    's/^read_account_file$/read_account_file; export ACCT_PASSWORD/'
mutant no-redaction "⛔ a driver that PRINTS the password to stderr: the journal shows the marker, not the password" \
    '/^        \[ -n "\$ACCT_PASSWORD" \] && line=/d'
collect_mutants

echo
if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
exit "$fail"
