#!/usr/bin/env bash
# End-to-end harness for gv-session-alarm.sh. No box, no token, no network
# beyond loopback. Runs the stub gateway and a stub status endpoint, drives the
# alarm through every condition, and asserts against WHAT THE STUBS RECORDED —
# never against the alarm's own state file, and never against "it ran".
#
# ⛔ Read this before adding a case: an assertion that reads $GV_ALARM_STATE_FILE
# is asserting what the alarm INTENDED, not what the gateway received. Spec
# acceptance 4 is explicit about the difference, and it is the difference the
# whole arc is about. Every check below reads the stub's JSONL, the alarm's
# EXIT CODE, or its journal — nothing else.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ALARM="${HERE}/../gv-session-alarm.sh"

WORK="$(mktemp -d)"
GW_PID=""
ST_PID=""
cleanup() {
    [ -n "$GW_PID" ] && kill "$GW_PID" 2>/dev/null
    [ -n "$ST_PID" ] && kill "$ST_PID" 2>/dev/null
    chmod -R u+w "$WORK" 2>/dev/null
    rm -rf "$WORK"
}
trap cleanup EXIT

export HOME="$WORK/home"
mkdir -p "$HOME/.local/state"

cat > "$HOME/.rotaryphone-env" <<'EOF'
ROTARYPHONE_GATEWAY_URL=http://127.0.0.1:8099
ROTARYPHONE_GATEWAY_TOKEN=stub-token
EOF

GW_LOG="$WORK/gw.jsonl"
export GV_ALARM_STATUS_URL=http://127.0.0.1:8098/api/gvbridge/status
export GV_ALARM_STATE_FILE="$HOME/.local/state/gv-session-alarm.state"

# A port nothing listens on. This is how "the service is down" is produced: a
# REAL connection refusal, so the alarm takes the same curl-exit-status branch
# the box will take, rather than a stub pretending to be down.
DEAD_URL=http://127.0.0.1:8097/api/gvbridge/status

fail=0
cases=0
check() { # check NAME EXPECTED ACTUAL
    cases=$((cases + 1))
    if [ "$2" = "$3" ]; then printf '  PASS %s\n' "$1"
    else printf '  FAIL %s: expected [%s] got [%s]\n' "$1" "$2" "$3"; fail=1; fi
}

start_gateway() { # start_gateway [--fail-notify N]
    [ -n "$GW_PID" ] && { kill "$GW_PID" 2>/dev/null; wait "$GW_PID" 2>/dev/null; }
    python3 "$HERE/gv-alarm-gateway-stub.py" --port 8099 --log "$GW_LOG" "$@" 2>/dev/null &
    GW_PID=$!
    for _ in $(seq 40); do
        curl -s -o /dev/null --max-time 1 "http://127.0.0.1:8099/v1/heartbeat/none" && break
        sleep 0.1
    done
}

python3 "$HERE/gv-alarm-status-stub.py" --port 8098 --dir "$WORK" 2>/dev/null &
ST_PID=$!
start_gateway
for _ in $(seq 40); do
    printf '{}' > "$WORK/status.json"
    curl -s -o /dev/null --max-time 1 "$GV_ALARM_STATUS_URL" && break
    sleep 0.1
done

serve() { printf '%s' "$1" > "$WORK/status.json"; printf '%s' "${2:-200}" > "$WORK/status.code"; }
run()   { bash "$ALARM" >/dev/null 2>"$WORK/err.txt"; echo "$?"; }
# Only messages the gateway ACCEPTED and queued. A 422 is not a delivery.
delivered() { jq -c 'select(.kind=="notify" and .status==202) | .body' "$GW_LOG" 2>/dev/null; }
hb()    { curl -s --max-time 5 "http://127.0.0.1:8099/v1/heartbeat/rotaryphone"; }
hb_count() { hb | jq -r '.refresh_count // 0' 2>/dev/null || echo 0; }
reset() { : > "$GW_LOG"; rm -f "$GV_ALARM_STATE_FILE"; }

echo "=== Task 8 — classification ==="
# The condition is read from the journal line the script emits, which is the
# script's own statement of what it concluded.
classify() { # classify BODY [CODE] -> prints condition
    serve "$1" "${2:-200}"
    rm -f "$GV_ALARM_STATE_FILE"
    bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
    sed -n 's/.*condition=\([a-z_]*\).*/\1/p' "$WORK/err.txt" | head -1
}

check "healthy -> ok" "ok" \
      "$(classify '{"browserRefreshOutcome":"Succeeded","browserSessionStale":false}')"
check "signed out -> browser_stale" "browser_stale" \
      "$(classify '{"browserRefreshOutcome":"Stale","browserSessionStale":true}')"
# ⛔ LOAD-BEARING: browserSessionStale is FALSE here. This is the case the boolean
# reports as healthy, and the whole reason browserRefreshOutcome was added.
check "CHROME GONE -> browser_unreachable (boolean reads false)" "browser_unreachable" \
      "$(classify '{"browserRefreshOutcome":"Unreachable","browserSessionStale":false}')"
check "not wired -> not_attempted" "not_attempted" \
      "$(classify '{"browserRefreshOutcome":"NotAttempted","browserSessionStale":false}')"
check "teardown -> ignore" "ignore" \
      "$(classify '{"browserRefreshOutcome":"TornDown","browserSessionStale":false}')"
# ⛔ LOAD-BEARING: a naive `// false` default would turn this green.
check "OLD BUILD (no field) -> field_missing, never ok" "field_missing" \
      "$(classify '{"available":true,"cookiesValid":true,"browserSessionStale":false}')"
check "future value -> unknown_outcome, never ok" "unknown_outcome" \
      "$(classify '{"browserRefreshOutcome":"Hibernating"}')"
check "service sick (HTTP 500) -> service_unreachable" "service_unreachable" \
      "$(classify '{"browserRefreshOutcome":"Succeeded"}' 500)"

# Real connection refusal, not a simulated one.
reset
GV_ALARM_STATUS_URL="$DEAD_URL" bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
check "service down (refused) -> service_unreachable" "service_unreachable" \
      "$(sed -n 's/.*condition=\([a-z_]*\).*/\1/p' "$WORK/err.txt" | head -1)"

echo "=== Task 8 — configuration must fail LOUDLY ==="
reset
serve '{"browserRefreshOutcome":"Succeeded"}'
mv "$HOME/.rotaryphone-env" "$WORK/env.bak"
rc="$(run)"
check "no env file -> exit 1" "1" "$rc"
check "no env file -> journal says FATAL" "yes" \
      "$(grep -q 'FATAL' "$WORK/err.txt" && echo yes || echo no)"
check "no env file -> journal names the path" "yes" \
      "$(grep -qF "$HOME/.rotaryphone-env" "$WORK/err.txt" && echo yes || echo no)"

# --print-config must work on an unconfigured install: the deploy's gate calls it
# on a box that has no token yet.
bash "$ALARM" --print-config > "$WORK/pc.txt" 2>&1
check "--print-config exits 0 with no env file" "0" "$?"
check "--print-config reports env_file MISSING" "yes" \
      "$(grep -q 'env_file.*MISSING' "$WORK/pc.txt" && echo yes || echo no)"

mv "$WORK/env.bak" "$HOME/.rotaryphone-env"

cat > "$HOME/.rotaryphone-env" <<'EOF'
ROTARYPHONE_GATEWAY_URL=http://127.0.0.1:8099
ROTARYPHONE_GATEWAY_TOKEN=
EOF
rc="$(run)"
check "empty token -> exit 1" "1" "$rc"
check "empty token -> names the variable" "yes" \
      "$(grep -q 'ROTARYPHONE_GATEWAY_TOKEN is unset or empty' "$WORK/err.txt" && echo yes || echo no)"
cat > "$HOME/.rotaryphone-env" <<'EOF'
ROTARYPHONE_GATEWAY_URL=http://127.0.0.1:8099
ROTARYPHONE_GATEWAY_TOKEN=stub-token
EOF

check "no .new debris after any run" "0" \
      "$(find "$HOME/.local/state" -name '*.new' | wc -l)"

echo "=== Task 9 — the POST, and truncation ==="
reset
serve '{"browserRefreshOutcome":"Stale"}'
run >/dev/null
natural="$(delivered | jq -r 'select(.severity=="alert") | .action' | head -1)"
check "natural action delivered intact" "re-login at voice.google.com in the box's Chrome (CDP 9224)" "$natural"
check "natural action is inside the cap" "yes" \
      "$([ "${#natural}" -le 200 ] && echo yes || echo no)"

# ⭐ Simulate THE FUTURE EDIT the cap exists for, by patching action_for in a COPY
# of the shipped script. No test-only hook is added to the production script: the
# real truncate_action and the real post_notify are what run.
LONGALARM="$WORK/gv-session-alarm-longaction.sh"
LONG250="$(printf 'x%.0s' $(seq 250))"
sed "s|echo \"re-login at voice.google.com in the box's Chrome (CDP 9224)\"|echo \"${LONG250}\"|" \
    "$ALARM" > "$LONGALARM"
check "the long-action fixture actually patched" "1" \
      "$(grep -c "$LONG250" "$LONGALARM")"

reset
serve '{"browserRefreshOutcome":"Stale"}'
bash "$LONGALARM" >/dev/null 2>"$WORK/err.txt"
sent="$(delivered | jq -r 'select(.severity=="alert") | .action' | head -1)"
# ⛔ Assert the LENGTH, not that the call succeeded. "It returned 202" is satisfied
# by a message truncated wrongly and delivered anyway.
check "250-char action truncated to exactly 200" "200" "${#sent}"
check "truncated action ends in ASCII ..." "yes" \
      "$(case "$sent" in *...) echo yes ;; *) echo no ;; esac)"

echo "=== Task 9 — a forced 422 is a first-class failure ==="
reset
serve '{"browserRefreshOutcome":"Stale"}'
GV_ALARM_ACTION_MAX=9999 bash "$LONGALARM" >/dev/null 2>"$WORK/err.txt"
rc=$?
check "forced 422 -> exit 1" "1" "$rc"
check "forced 422 -> journal says NOTHING WAS DELIVERED" "yes" \
      "$(grep -q 'NOTHING WAS DELIVERED' "$WORK/err.txt" && echo yes || echo no)"
check "forced 422 -> journal carries the gateway's limit verbatim" "yes" \
      "$(grep -q '"limit": 200' "$WORK/err.txt" && echo yes || echo no)"
check "forced 422 -> NO alert was delivered for that dedupe" "0" \
      "$(delivered | jq -r 'select(.dedupe_key=="rotaryphone-gv-session-browser_stale")' | wc -l)"

echo "=== Task 9 — the silent info drop is not relied on ==="
# The 🧵 thread root is an info message. It must carry no action key at all.
check "no info message carries an action" "0" \
      "$(delivered | jq -r 'select(.severity=="info") | select(has("action"))' | wc -l)"

echo "=== Task 9 — a transport failure is a first-class failure ==="
start_gateway --fail-notify 500
reset
serve '{"browserRefreshOutcome":"Stale"}'
rc="$(run)"
check "--fail-notify 500 -> exit 1" "1" "$rc"
check "--fail-notify 500 -> journal says http=500" "yes" \
      "$(grep -q 'NOTIFY FAILED: http=500' "$WORK/err.txt" && echo yes || echo no)"
start_gateway

echo "=== Task 10 — post only on transition ==="
reset
serve '{"browserRefreshOutcome":"Stale"}'
for _ in 1 2 3 4 5; do run >/dev/null; done
check "Stale x5 -> exactly 2 messages (thread root + alert)" "2" "$(delivered | wc -l)"

reset
serve '{"browserRefreshOutcome":"Succeeded"}'
run >/dev/null
check "ok from a cold start posts NOTHING" "0" "$(delivered | wc -l)"

echo "=== Task 10 — Stale -> Succeeded closes in the SAME thread ==="
reset
serve '{"browserRefreshOutcome":"Stale","browserSessionStale":true}'
run >/dev/null
serve '{"browserRefreshOutcome":"Succeeded","browserSessionStale":false}'
run >/dev/null
alert_thread="$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
resolved_thread="$(delivered | jq -r 'select(.severity=="info" and (.title|test("recovered"))) | .thread_key' | head -1)"
check "thread_key is set" "yes" "$([ -n "$alert_thread" ] && echo yes || echo no)"
check "RESOLVED threads under the alert" "$alert_thread" "$resolved_thread"
alert_dedupe="$(delivered | jq -r 'select(.severity=="alert") | .dedupe_key' | head -1)"
resolved_dedupe="$(delivered | jq -r 'select(.severity=="info" and (.title|test("recovered"))) | .dedupe_key' | head -1)"
check "dedupe_keys differ" "different" \
      "$([ "$alert_dedupe" != "$resolved_dedupe" ] && echo different || echo same)"

# A third healthy run adds nothing.
before="$(delivered | wc -l)"
run >/dev/null
check "ok after a resolved incident is silent" "$before" "$(delivered | wc -l)"

echo "=== Task 10 — the thread key survives, and its loss is demonstrated ==="
reset
serve '{"browserRefreshOutcome":"Stale"}'
run >/dev/null
first_thread="$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
# A fresh shell, same state file — this is the reboot/redeploy case.
serve '{"browserRefreshOutcome":"Succeeded"}'
env -i HOME="$HOME" PATH="$PATH" \
    GV_ALARM_STATUS_URL="$GV_ALARM_STATUS_URL" GV_ALARM_STATE_FILE="$GV_ALARM_STATE_FILE" \
    bash "$ALARM" >/dev/null 2>&1
survived="$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"
check "thread_key survives a fresh shell" "$first_thread" "$survived"

# ⭐ The failure mode, demonstrated once so its shape is known — and it is NOT the
# shape the plan predicted. The plan's Task 10 says losing the state file
# mid-incident makes "the RESOLVED open a NEW thread". Measured here, it does
# something worse: there is NO all-clear at all. On a cold state file the ok
# branch finds no open incident and correctly stays silent, so the owner is left
# holding an alert that is never closed. This is not fixable without state — a
# cold start and a lost state file are indistinguishable — so it is recorded
# rather than patched, and the state file's durability is what covers it.
reset
serve '{"browserRefreshOutcome":"Stale"}'
run >/dev/null
lost_from="$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
rm -f "$GV_ALARM_STATE_FILE"
serve '{"browserRefreshOutcome":"Succeeded"}'
run >/dev/null
check "state lost mid-incident -> NO resolved message at all (nothing to close)" "0" \
      "$(delivered | jq -r 'select(.title|test("recovered"))' | wc -l)"
check "…and the alert's thread is therefore left open" "yes" \
      "$([ -n "$lost_from" ] && echo yes || echo no)"

echo "=== Task 10 — flapping threads under ONE incident ==="
reset
serve '{"browserRefreshOutcome":"Stale"}'; run >/dev/null
serve '{"browserRefreshOutcome":"Unreachable"}'; run >/dev/null
serve '{"browserRefreshOutcome":"Stale"}'; run >/dev/null
check "3 condition messages" "3" "$(delivered | jq -r 'select(.severity=="alert")' | jq -s length)"
check "…all under ONE thread_key" "1" \
      "$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | sort -u | wc -l)"
# ⚠ TWO distinct dedupe keys, not three. dedupe_key is per CONDITION by design
# (spec §4.4: "Keys chosen per condition, never per message"), and Stale recurs.
# The plan's Task 10 acceptance says "three distinct dedupe_keys" — that
# contradicts the design decision stated in the same plan. Two is correct.
check "…and TWO distinct dedupe_keys (per condition, and Stale recurs)" "2" \
      "$(delivered | jq -r 'select(.severity=="alert") | .dedupe_key' | sort -u | wc -l)"

echo "=== Task 10 — no severity in any title ==="
check "no title carries its own severity marker" "0" \
      "$(delivered | jq -r '.title' | grep -cE '\[ALERT\]|\[WARN\]|\[INFO\]|ACTION:')"

echo "=== Task 11 — the dead-man, read back from the gateway ==="
# ⛔ Every row reads GET /v1/heartbeat/rotaryphone. Verifying the refresh RAN and
# inferring the check MOVED is the §6 failure applied to the dead-man itself.
start_gateway
reset
serve '{"browserRefreshOutcome":"Succeeded"}'
before="$(hb_count)"
rc="$(run)"
check "healthy, nothing to say -> exit 0" "0" "$rc"
check "healthy -> refresh_count increments" "$((before + 1))" "$(hb_count)"

before="$(hb_count)"
serve '{"browserRefreshOutcome":"Stale"}'
rc="$(run)"
check "a real alert delivered -> exit 0" "0" "$rc"
check "a real alert delivered -> refresh_count increments" "$((before + 1))" "$(hb_count)"

# ⛔ THE CORRECTED RULE (plan Task 11, Q1). A reported service outage is the alarm
# WORKING. Suppressing here would raise "the alarm is dead" on top of a real
# outage — a false alarm in exactly the moment the owner is dealing with a real one.
reset
before="$(hb_count)"
GV_ALARM_STATUS_URL="$DEAD_URL" bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
rc=$?
check "service down but REPORTED -> exit 0" "0" "$rc"
check "service down but REPORTED -> refresh_count STILL increments" "$((before + 1))" "$(hb_count)"

check "grace reached the gateway as lower-case 30m" "30m" "$(hb | jq -r '.grace')"
check "schedule reached the gateway as lower-case 5m" "5m" "$(hb | jq -r '.schedule')"
check "check_id reached the gateway" "gv-session-alarm" "$(hb | jq -r '.check_id')"

echo "=== Task 11 — a failed notify SUPPRESSES the dead-man ==="
start_gateway --fail-notify 500
reset
serve '{"browserRefreshOutcome":"Stale"}'
run >/dev/null
check "notify refused -> check was NEVER registered (404)" "404" \
      "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8099/v1/heartbeat/rotaryphone")"

start_gateway
reset
serve '{"browserRefreshOutcome":"Succeeded"}'
run >/dev/null                      # register the check on a healthy cycle
before="$(hb_count)"
serve '{"browserRefreshOutcome":"Stale"}'
GV_ALARM_ACTION_MAX=9999 bash "$LONGALARM" >/dev/null 2>"$WORK/err.txt"
rc=$?
check "forced 422 -> exit 1" "1" "$rc"
check "forced 422 -> refresh_count does NOT move" "$before" "$(hb_count)"

echo "=== Task 11 — unwritable state SUPPRESSES the dead-man ==="
RO="$WORK/readonly"
mkdir -p "$RO"; chmod 500 "$RO"
before="$(hb_count)"
serve '{"browserRefreshOutcome":"Succeeded"}'
GV_ALARM_STATE_FILE="$RO/sub/alarm.state" bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
rc=$?
check "unwritable state -> exit 1" "1" "$rc"
check "unwritable state -> refresh_count does NOT move" "$before" "$(hb_count)"
chmod 700 "$RO"

echo "=== Task 11 — an upper-case grace leaves NO dead-man at all ==="
# ⛔ Assert the READ-BACK is 404, not that the POST returned 422. A check that was
# never registered is silently absent, which is the whole trap.
start_gateway
reset
serve '{"browserRefreshOutcome":"Succeeded"}'
GV_ALARM_HEARTBEAT_GRACE=30M bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
rc=$?
check "grace=30M -> exit 1" "1" "$rc"
check "grace=30M -> journal names the lower-case rule" "yes" \
      "$(grep -q 'lower-case only' "$WORK/err.txt" && echo yes || echo no)"
check "grace=30M -> the check was NEVER REGISTERED (404)" "404" \
      "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8099/v1/heartbeat/rotaryphone")"

echo "=== Housekeeping ==="
check "no .new debris anywhere under HOME" "0" \
      "$(find "$HOME" -name '*.new' 2>/dev/null | wc -l)"

echo
if [ "$fail" -eq 0 ]; then
    echo "ALL ${cases} CASES PASSED"
else
    echo "FAILURES PRESENT (${cases} cases run)"
fi
exit "$fail"
