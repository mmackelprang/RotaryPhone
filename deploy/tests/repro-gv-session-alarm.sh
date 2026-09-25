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
#
# ⛔ delivered() is what the gateway KEPT. received() is what the ALARM SENT.
# Use received() for any claim about the alarm's own behaviour: the gateway strips
# `action`/`timestamp` from an info message before recording, so a test asserting
# "the alarm sent no action on info" against delivered() CANNOT FAIL. It did not,
# for a while — see the note in gv-alarm-gateway-stub.py's _record.
delivered() { jq -c 'select(.kind=="notify" and .status==202) | .body' "$GW_LOG" 2>/dev/null; }
received()  { jq -c 'select(.kind=="notify" and .status==202) | .received' "$GW_LOG" 2>/dev/null; }
# The delivered BODY for one condition's alert/warning message.
body_of() { jq -r --arg d "rotaryphone-gv-session-$1" \
    'select(.kind=="notify" and .status==202) | select(.body.dedupe_key==$d) | .body.body' "$GW_LOG" 2>/dev/null; }
sev_of()  { jq -r --arg d "rotaryphone-gv-session-$1" \
    'select(.kind=="notify" and .status==202) | select(.body.dedupe_key==$d) | .body.severity' "$GW_LOG" 2>/dev/null; }
# Does the delivered body for CONDITION contain SUBSTRING?
body_has() { case "$(body_of "$1")" in *"$2"*) echo yes ;; *) echo no ;; esac; }
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
# ⛔ MEASURED 2026-09-25: a Chrome parked on the Google sign-in page was reported as
# Unreachable and the alarm said "Chrome is gone". The service now says SignedOut, and
# it must not fall through to unknown_outcome or read as the Chrome-is-gone alert.
check "SIGNED OUT (Chrome up, no Google session) -> browser_signed_out" "browser_signed_out" \
      "$(classify '{"browserRefreshOutcome":"SignedOut","browserSessionStale":false}')"
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
# ⛔ READ received(), NOT delivered(). The gateway strips `action` from an info
# message BEFORE recording it, so the same assertion against delivered() cannot
# fail and passed for a while against an alarm that was sending one.
check "the ALARM sends no action on an info message" "0" \
      "$(received | jq -r 'select(.severity=="info") | select(has("action"))' | wc -l)"
# And prove the instrument itself is pointed at something: there IS an info message
# in this log, so the 0 above is a real absence rather than an empty population.
check "…and there was at least one info message to check" "yes" \
      "$([ "$(received | jq -r 'select(.severity=="info")' | jq -s length)" -ge 1 ] && echo yes || echo no)"

echo "=== Task 9 — the bearer token is NOT in any process's argv ==="
# ⛔ `radio` is SHARED with Radio Console, and /proc/<pid>/cmdline is world-readable.
# A token passed as `-H "Authorization: Bearer ..."` is visible to every user on the
# box for the life of each call, 288 times a day. Note the irony this PR contains:
# CookieRetriever.KillOwnDebugProfileChrome reads every process's cmdline for exactly
# this reason. Found in pre-merge review 2026-09-09; the header is fed on stdin now.
#
# ⚠ Asserted against the SOURCE, because the call is far too short-lived to catch by
# sampling /proc — a sampling test here would pass by missing it, which is worse than
# no test at all.
check "no curl invocation interpolates the token into an argument" "0" \
      "$(grep -c -- '-H "Authorization: Bearer' "$ALARM")"
check "…and the token reaches curl through --config on stdin" "2" \
      "$(grep -c -- '--config -' "$ALARM")"

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

echo "=== Task 10 — a REFUSED thread root is re-attempted, not silently abandoned ==="
# ⛔ The failure this guards against, measured in pre-merge review 2026-09-09: the
# thread key used to be persisted the instant it was minted, BEFORE the root post was
# attempted. A gateway refusing at the moment an incident opened therefore left a key
# on disk with no root behind it, and the next cycle's `[ -z "$key" ]` test found the
# key and never re-attempted. The alert then threaded under a root that does not
# exist — and the later RESOLVED, which is routed to the QUIET lane precisely because
# it threads under an alert the owner saw, replies into nothing.
#
# A gateway that is down when an incident opens is a CORRELATED failure, not an
# exotic one. That is what makes this worth a test.
start_gateway --fail-notify 500
reset
serve '{"browserRefreshOutcome":"Stale"}'
rc="$(run)"
check "root refused -> cycle exits 1" "1" "$rc"
start_gateway                       # the gateway comes back
serve '{"browserRefreshOutcome":"Stale"}'
run >/dev/null
roots="$(delivered | jq -r 'select(.title|test("🧵"))' | jq -s length)"
check "the thread root IS delivered on the next cycle" "1" "$roots"
root_thread="$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"
alert_thread="$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
check "…and the alert threads under that very root" "$root_thread" "$alert_thread"
check "…with no second root minted" "1" \
      "$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | sort -u | wc -l)"

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

echo "=== 2026-09-25 — an incident that recovers UNDELIVERED retires its thread key ==="
# ⛔ MEASURED ON THE BOX. 2026-09-20 03:04–08:10 EDT every gateway POST timed out; an
# incident opened as rotaryphone-gv-session-20260920T071522Z with its root undelivered.
# At 08:14 the condition returned to ok — but LAST_POSTED_CONDITION was still `ok`,
# because nothing had ever been delivered, so the "unchanged since the last post" branch
# swallowed the recovery and the key was never cleared. FIVE DAYS LATER, 2026-09-25
# 15:08:02Z, an unrelated browser_unreachable "re-attempted the incident thread root"
# for the 09-20 key and delivered a new alert under a five-day-old thread identity.
#
# The rule: an incident whose condition returns to ok with NOTHING delivered is retired
# silently — no alert was ever seen, so there is nothing to close, and a RESOLVED would
# be an all-clear for an alarm nobody raised. The next incident opens its own thread.
# ⚠ The stub truncates its log on restart, so the refused key is read BEFORE restarting.
start_gateway
reset
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null      # LAST_POSTED_CONDITION=ok, as on the box
start_gateway --fail-notify 500
serve '{"browserRefreshOutcome":"Unreachable"}'
rc="$(run)"
check "gateway down at incident open -> exit 1" "1" "$rc"
retired_key="$(jq -r 'select(.kind=="notify" and .status==500) | .body.thread_key' "$GW_LOG" | head -1)"
check "…and a thread key WAS minted for the refused incident" "yes" \
      "$([ -n "$retired_key" ] && echo yes || echo no)"
start_gateway                                                     # the gateway comes back
serve '{"browserRefreshOutcome":"Succeeded"}'
rc="$(run)"
check "undelivered incident recovers -> exit 0" "0" "$rc"
check "…and posts NOTHING (no alert was ever seen, so there is nothing to close)" "0" \
      "$(delivered | wc -l)"
check "…and the journal says the key was retired" "yes" \
      "$(grep -qF "retired ${retired_key}" "$WORK/err.txt" && echo yes || echo no)"
sleep 1                  # keys are second-resolution; do not let a fast run mint the same one
serve '{"browserRefreshOutcome":"Unreachable"}'; run >/dev/null
new_root="$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"
check "the NEXT incident delivers its own thread root" "yes" \
      "$([ -n "$new_root" ] && echo yes || echo no)"
check "…under a NEW key, not the retired one" "different" \
      "$([ -n "$new_root" ] && [ "$new_root" != "$retired_key" ] && echo different || echo same)"
check "…its alert threads under that new root" "$new_root" \
      "$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
check "…and nothing is re-attempted under the retired key" "0" \
      "$(delivered | jq -c --arg k "$retired_key" 'select(.thread_key==$k)' | wc -l)"
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "…and its RESOLVED closes the NEW thread" "$new_root" \
      "$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"

# ⚠ PARTIAL DELIVERY, root refused but the ALERT accepted. The owner SAW an alert, under
# a thread_key whose root never arrived — so the alert itself is that thread's first
# message. It must be closed, and in that same thread. Retiring it silently would leave
# a notified alert open forever.
start_gateway --fail-notify 500 --fail-notify-matching=-thread-
reset
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
serve '{"browserRefreshOutcome":"Unreachable"}'; run >/dev/null
seen_alert="$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
check "root refused, alert accepted -> the alert WAS delivered" "yes" \
      "$([ -n "$seen_alert" ] && echo yes || echo no)"
start_gateway                             # the gateway accepts roots again by recovery time
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "…so recovery posts a RESOLVED, threaded under the alert the owner saw" "$seen_alert" \
      "$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"
check "…with the missing root placed in that thread FIRST" "$seen_alert" \
      "$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"

# ⚠ PARTIAL DELIVERY, root accepted but the alert refused. A thread root exists and says
# what closes it; the thread must be closable, so it gets its RESOLVED — and the key must
# still be cleared, or the next incident threads under this one.
start_gateway --fail-notify 500 --fail-notify-matching=browser_unreachable
reset
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
serve '{"browserRefreshOutcome":"Unreachable"}'; run >/dev/null
root_only="$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"
check "root accepted, alert refused -> the root WAS delivered" "yes" \
      "$([ -n "$root_only" ] && echo yes || echo no)"
start_gateway
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "…so recovery closes that thread with a RESOLVED" "$root_only" \
      "$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"
sleep 1
serve '{"browserRefreshOutcome":"Stale"}'; run >/dev/null
next_root="$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"
check "…and the next incident opens a NEW thread" "different" \
      "$([ -n "$next_root" ] && [ "$next_root" != "$root_only" ] && echo different || echo same)"

# ⛔ A TIMEOUT IS NOT PROOF OF NON-DELIVERY (pre-merge review 2026-09-25). The 2026-09-20
# failure was curl 28. If the gateway accepted and delivered while our curl gave up, the
# owner SAW the alert — and retiring it silently would leave it open forever. So a timed-out
# incident is closed with a RESOLVED under its key, not retired.
start_gateway --delay-notify 3
reset
serve '{"browserRefreshOutcome":"Succeeded"}'; GV_ALARM_NOTIFY_MAX_TIME=1 bash "$ALARM" >/dev/null 2>&1
serve '{"browserRefreshOutcome":"Unreachable"}'
GV_ALARM_NOTIFY_MAX_TIME=1 bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
rc=$?
check "gateway slower than our timeout -> exit 1" "1" "$rc"
check "…and the journal says it MAY have been delivered, not that nothing was" "yes" \
      "$(grep -q 'MAY have delivered' "$WORK/err.txt" && echo yes || echo no)"
# Let the stub finish delivering what we gave up on. POLL with a deadline, never a fixed
# sleep: the stub is single-threaded, so root and alert land ~3s apart after the alarm exits.
timed_out_key=""
for _ in $(seq 150); do
    timed_out_key="$(delivered | jq -r 'select(.severity=="alert") | .thread_key' | head -1)"
    [ -n "$timed_out_key" ] && break
    sleep 0.1
done
check "…while the gateway DID deliver the alert" "yes" \
      "$([ -n "$timed_out_key" ] && echo yes || echo no)"
start_gateway
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "…so recovery closes it with a RESOLVED under that key, not a silent retirement" "$timed_out_key" \
      "$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"

# ⛔ A TIMEOUT THAT DELIVERED NOTHING (pre-merge re-review 2026-09-25). curl 28 also covers a
# connect-phase timeout, where no byte was sent — the most likely shape of 2026-09-20. The
# incident is "maybe delivered", so it is not retired silently; but a RESOLVED posted straight
# away would be the FIRST message in its thread — a RESOLVED rooting a thread, which the policy
# forbids. The root must be re-posted first. Produced here with a stub that sleeps past our
# timeout and then REFUSES, so curl sees 28 and the gateway kept nothing.
start_gateway --delay-notify 2 --fail-notify 500
reset
serve '{"browserRefreshOutcome":"Succeeded"}'; GV_ALARM_NOTIFY_MAX_TIME=1 bash "$ALARM" >/dev/null 2>&1
serve '{"browserRefreshOutcome":"Unreachable"}'
GV_ALARM_NOTIFY_MAX_TIME=1 bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
check "timed out AND refused -> journal says MAY have delivered (curl cannot tell)" "yes" \
      "$(grep -q 'MAY have delivered' "$WORK/err.txt" && echo yes || echo no)"
start_gateway
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
first_title="$(delivered | jq -r '.title' | head -1)"
check "recovery re-posts the ROOT first — a RESOLVED never starts a thread" "yes" \
      "$(case "$first_title" in *🧵*) echo yes ;; *) echo no ;; esac)"
root_k="$(delivered | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"
check "…and the RESOLVED follows under that root" "$root_k" \
      "$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"

# …and if the root is STILL refused at recovery, the RESOLVED is withheld, not orphaned.
start_gateway --delay-notify 2 --fail-notify 500
reset
serve '{"browserRefreshOutcome":"Succeeded"}'; GV_ALARM_NOTIFY_MAX_TIME=1 bash "$ALARM" >/dev/null 2>&1
serve '{"browserRefreshOutcome":"Unreachable"}'; GV_ALARM_NOTIFY_MAX_TIME=1 bash "$ALARM" >/dev/null 2>&1
start_gateway --fail-notify 500 --fail-notify-matching=-thread-
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "root still refused at recovery -> NO RESOLVED is delivered" "0" \
      "$(delivered | jq -c 'select(.title|test("recovered"))' | wc -l)"
check "…and the journal says it was withheld" "yes" \
      "$(grep -q 'RESOLVED WITHHELD' "$WORK/err.txt" && echo yes || echo no)"
start_gateway

# A state file written by the PRE-2026-09-25 script has no INCIDENT_MAY_HAVE_DELIVERED. It
# must load under `set -u` and still close a delivered incident normally.
reset
cat > "$GV_ALARM_STATE_FILE" <<'EOF'
LAST_POSTED_CONDITION=browser_stale
INCIDENT_THREAD_KEY=rotaryphone-gv-session-20260901T000000Z
INCIDENT_OPENED_AT=2026-09-01T00:00:00Z
PENDING_CONDITION=browser_stale
PENDING_POLLS=4
THREAD_ROOT_DELIVERED=1
EOF
serve '{"browserRefreshOutcome":"Succeeded"}'
rc="$(run)"
check "old-format state file -> exit 0" "0" "$rc"
check "…and the delivered incident is closed under its own key" "rotaryphone-gv-session-20260901T000000Z" \
      "$(delivered | jq -r 'select(.title|test("recovered")) | .thread_key' | head -1)"

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

echo "=== Task 10 — THE DELIVERED BODY, and the severity that routes it ==="
# ⛔ THIS BLOCK IS THE ONE THAT CERTIFIES THE ARC'S ACTUAL DELIVERABLE, and it was
# missing from the first version of this harness. Pre-merge review 2026-09-09 proved
# what that cost: `body_for()` could be replaced with `body_for() { : ; }` — deleting
# every word the service says, including "ACTION: re-login at voice.google.com." —
# and this file still printed ALL CASES PASSED. The copy-drift guard did not catch it
# either, because it greps the SOURCE FILE, not the wire.
#
#   the copy guard proves the sentence is IN THE SCRIPT.
#   only this block proves the sentence is IN THE MESSAGE.
#
# Likewise severity: only browser_stale was ever pinned, and only as a jq selector.
# Every other condition could be routed to `info` — the owner's QUIET, does-not-notify
# lane — with no test noticing. Including service_unreachable, which this script's own
# comment calls "the case in-process detection structurally cannot cover".
reset
serve '{"browserRefreshOutcome":"Stale"}';       run >/dev/null
check "browser_stale severity"  "alert" "$(sev_of browser_stale)"
check "browser_stale body quotes the service verbatim" "yes" \
      "$(body_has browser_stale 'Google refused it. The working on-disk set was NOT overwritten.')"
check "browser_stale body carries the service's own REMEDY" "yes" \
      "$(body_has browser_stale 'ACTION: re-login at voice.google.com.')"
check "browser_stale body says the phone still works" "yes" \
      "$(body_has browser_stale 'The phone still works.')"

serve '{"browserRefreshOutcome":"Unreachable","browserSessionStale":false}'; run >/dev/null
check "browser_unreachable severity" "alert" "$(sev_of browser_unreachable)"
check "browser_unreachable body quotes the service verbatim" "yes" \
      "$(body_has browser_unreachable 'CHROME WAS UNREACHABLE on CDP port')"
check "browser_unreachable body says the login was never tested" "yes" \
      "$(body_has browser_unreachable 'so the Google login was never tested.')"
# ⭐ The sentence that is the whole reason browserRefreshOutcome exists.
check "browser_unreachable body warns the boolean reads false here" "yes" \
      "$(body_has browser_unreachable 'reads **false** in this state')"

serve '{"browserRefreshOutcome":"SignedOut","browserSessionStale":false}'; run >/dev/null
check "browser_signed_out severity" "alert" "$(sev_of browser_signed_out)"
check "browser_signed_out body quotes the service verbatim" "yes" \
      "$(body_has browser_signed_out "the box's Chrome is SIGNED OUT")"
check "browser_signed_out body carries the service's own REMEDY — a human sign-in" "yes" \
      "$(body_has browser_signed_out "ACTION: a human must sign in at voice.google.com in the box's Chrome.")"
check "browser_signed_out body says Chrome is fine, so nobody restarts it" "yes" \
      "$(body_has browser_signed_out 'Chrome itself is fine; restarting it will not help.')"
signed_out_action="$(delivered | jq -r 'select(.dedupe_key=="rotaryphone-gv-session-browser_signed_out") | .action' | head -1)"
check "browser_signed_out ACTION sends a human to sign in" "yes" \
      "$(case "$signed_out_action" in *"a human must sign in at voice.google.com"*) echo yes ;; *) echo no ;; esac)"
# ⛔ The 2026-09-25 regression, asserted on the wire: the wrong fix, stated with confidence.
check "browser_signed_out ACTION does NOT send the owner after Chrome" "no" \
      "$(case "$signed_out_action" in *pgrep*|*gv-bridge-ensure*) echo yes ;; *) echo no ;; esac)"

reset
serve '{"browserRefreshOutcome":"NotAttempted"}'
run >/dev/null; run >/dev/null; run >/dev/null      # MIN_POLLS_TO_POST=3
check "not_attempted severity" "warning" "$(sev_of not_attempted)"
check "not_attempted body quotes the service verbatim" "yes" \
      "$(body_has not_attempted 'the browser was NEVER CONSULTED')"

reset
serve '{"available":true,"browserSessionStale":false}'; run >/dev/null
check "field_missing severity" "warning" "$(sev_of field_missing)"
check "field_missing body names the missing field" "yes" \
      "$(body_has field_missing 'no `browserRefreshOutcome` field')"
check "field_missing body refuses to default to green" "yes" \
      "$(body_has field_missing 'rather than defaulting to green')"

reset
serve '{"browserRefreshOutcome":"Hibernating"}'; run >/dev/null
check "unknown_outcome severity" "warning" "$(sev_of unknown_outcome)"
check "unknown_outcome body names the value it did not recognise" "yes" \
      "$(body_has unknown_outcome 'Hibernating')"

# ⛔ service_unreachable is an ALERT, not info. It is the case in-process detection
# structurally cannot cover; routing it to the quiet lane would make the one outage
# nobody else can report also the one nobody is told about.
reset
GV_ALARM_STATUS_URL="$DEAD_URL" bash "$ALARM" >/dev/null 2>&1
check "service_unreachable severity" "alert" "$(sev_of service_unreachable)"
check "service_unreachable body refuses to imply the session is fine" "yes" \
      "$(body_has service_unreachable 'this is not a report that the session is fine')"

# Every condition message carries a non-trivial body — not just the timestamp line.
# ⚠ Measure the LENGTH OF EACH BODY, not of each output line: `jq -r .body` prints a
# multi-line string across many lines, so counting short LINES counts word-wrapping.
check "no delivered alert/warning has an empty or timestamp-only body" "0" \
      "$(delivered | jq -r 'select(.severity=="alert" or .severity=="warning") | .body | length' \
         | awk '$1 < 80 { n++ } END { print n+0 }')"

echo "=== Task 10 — no severity in any title, across EVERY condition ==="
# ⛔ The gateway prepends its own severity_prefix(). A title carrying its own renders it
# twice, with the two vocabularies free to disagree — measured on the sibling project as
# "ℹ️ [INFO] [pmtrader] ℹ️ INFO · …".
#
# ⚠ Rebuilt from a CLEAN log that is made to contain every condition's title. The first
# version grepped whatever the previous block happened to leave behind, so
# field_missing, unknown_outcome and service_unreachable were never examined at all —
# and it matched only bracket forms this title format could not produce anyway.
reset
serve '{"browserRefreshOutcome":"Stale"}';        run >/dev/null
serve '{"browserRefreshOutcome":"Unreachable"}';  run >/dev/null
serve '{"browserRefreshOutcome":"SignedOut"}';    run >/dev/null
serve '{"browserRefreshOutcome":"Hibernating"}';  run >/dev/null
serve '{"available":true}';                       run >/dev/null
GV_ALARM_STATUS_URL="$DEAD_URL" bash "$ALARM" >/dev/null 2>&1
serve '{"browserRefreshOutcome":"Succeeded"}';    run >/dev/null
titles="$(delivered | jq -r '.title')"
check "every condition contributed a title" "yes" \
      "$([ "$(printf '%s\n' "$titles" | wc -l)" -ge 7 ] && echo yes || echo no)"
check "no title carries a severity word or marker" "0" \
      "$(printf '%s\n' "$titles" | grep -ciE '\[?(alert|warn|warning|info|critical|resolved)\]?[[:space:]]*[:·|-]|^(alert|warn|info)\b|ACTION:')"
check "every title starts with the [rotaryphone] source tag" "0" \
      "$(printf '%s\n' "$titles" | grep -cv '^\[rotaryphone\] ')"

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
# ⛔ WAS "5m", AND THAT ASSERTION PINNED A DEFECT. The real gateway rejects a bare
# duration: 422 {"detail":"bad schedule '5m' (use weekdays | daily | every:<N><s|m|h|d>)"},
# which means NO DEAD-MAN AT ALL, silently. This test passed anyway because its oracle is
# the stub in this directory, which validates nothing and accepts any string — so a green
# run here certified a value the live gateway refuses. Measured against the live gateway
# 2026-09-10: "every:5m" -> 200, and reads back from GET /v1/heartbeat/rotaryphone with a
# real next_deadline. ⚠ A stub can prove the POST was MADE. It cannot prove it was ACCEPTED.
check "schedule reached the gateway in every:<N><unit> form" "every:5m" "$(hb | jq -r '.schedule')"
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
# ⛔ chmod is a NO-OP for root (CAP_DAC_OVERRIDE), which would make this case pass
# vacuously in a root container rather than failing visibly. Prove the precondition
# before asserting on it — an unwritable-directory test that can write is not a test.
if : > "$RO/probe" 2>/dev/null; then
    rm -f "$RO/probe"
    check "PRECONDITION: the read-only dir is actually unwritable" "unwritable" "writable (running as root?)"
fi
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

echo "=== auto-relogin track (docs/plans/gv-auto-relogin.md Task 13) ==="
# ⛔ The breaker state is written by the REAL breaker library, not by a hand-made
# fixture, so the file-format coupling between the two scripts is what gets tested.
# That is how the %q finding below was made: a hand-written fixture in the plan's
# single-quoted shape would have passed against a parser the real file defeats.
BREAKER="${HERE}/../gv-auto-relogin-breaker.sh"
export GV_RELOGIN_STATE_FILE="$HOME/.local/state/gv-auto-relogin.state"
REASON='Auto-relogin is STOPPED PERMANENTLY: Google rejected the stored password (harness). A human must run: gv-auto-relogin.sh --reset'
trip() { # trip [REASON-TEXT] — a fresh breaker, tripped once, as the actuator would
    rm -f "$GV_RELOGIN_STATE_FILE"
    bash "$BREAKER" --reset >/dev/null
    bash -c '. "$1"; breaker_load; breaker_record_credential_attempt; breaker_trip credential_rejected "$2"; breaker_write' \
        _ "$BREAKER" "${1:-$REASON}" 2>/dev/null
}
rearm() { bash "$BREAKER" --reset >/dev/null; }
relogin_msgs()   { delivered | jq -c 'select(.dedupe_key|startswith("rotaryphone-gv-relogin"))'; }
relogin_alerts() { delivered | jq -c 'select(.severity=="alert" and (.dedupe_key|startswith("rotaryphone-gv-relogin-unavailable")))'; }
stale_alerts()   { delivered | jq -c 'select(.dedupe_key=="rotaryphone-gv-session-browser_stale")'; }
count() { jq -s length; }

# 1. ⛔ not installed: the alarm must not invent a condition from a missing file.
reset; rm -f "$GV_RELOGIN_STATE_FILE"
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
serve '{"browserRefreshOutcome":"Stale"}';     run >/dev/null
check "relogin: not installed -> NO relogin message" "0" "$(relogin_msgs | count)"
check "relogin: not installed -> the session alert still arrives" "1" "$(stale_alerts | count)"

# 2. trips once, on a HEALTHY session: its own thread, with a 🧵 root.
reset; trip
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "relogin: a trip -> exactly ONE relogin alert" "1" "$(relogin_alerts | count)"
check "relogin: the alert quotes the breaker's reason VERBATIM" "yes" \
      "$(relogin_alerts | jq -r .body | grep -qxF "> $REASON" && echo yes || echo no)"
# ⛔ The plan's grep|cut|sed parse delivers `Auto-relogin\ is\ STOPPED\ ...` here.
check "relogin: ...with no printf-%q backslashes in the delivered body" "0" \
      "$(relogin_alerts | jq -r .body | grep -c 'Auto-relogin\\ is')"
check "relogin: the alert names the human action" \
      "gv-auto-relogin.sh --status ; then --reset once the account is fixed" \
      "$(relogin_alerts | jq -r .action)"
check "relogin: its own thread opens with a 🧵 root" "1" \
      "$(relogin_msgs | jq -c 'select(.title|test("🧵"))' | count)"
own_thread="$(relogin_alerts | jq -r .thread_key | head -1)"
check "relogin: ...and the alert threads under that root" "$own_thread" \
      "$(relogin_msgs | jq -r 'select(.title|test("🧵")) | .thread_key' | head -1)"

# 3. stays tripped: transition only.
for _ in 1 2 3 4 5; do run >/dev/null; done
check "relogin: 5 more runs while tripped -> still ONE alert" "1" "$(relogin_alerts | count)"
check "relogin: ...and no other relogin message either" "2" "$(relogin_msgs | count)"

# 4. ⛔ THE LOAD-BEARING ONE (§0.9): a tripped, already-posted breaker must not mute a
# genuine session death that follows it.
serve '{"browserRefreshOutcome":"Stale"}'; run >/dev/null
check "⛔ relogin: TRIPPED + session goes Stale -> the session alert STILL ARRIVES" "1" \
      "$(stale_alerts | count)"
check "⛔ relogin: ...and the relogin alert is not re-posted" "1" "$(relogin_alerts | count)"

# 4-neg. ⭐ The wrong design, built once so its failure has a known shape: the breaker
# folded into the SESSION track's condition. Same inputs; the session alert must NOT
# arrive, which is the defect the separate track exists to prevent.
SHARED="$WORK/gv-session-alarm-shared-track.sh"
awk -v f="$GV_RELOGIN_STATE_FILE" '
  { print }
  /^case "\$outcome" in$/ { c = 1 }
  c && /^esac$/ { print "grep -q \"^BREAKER_STATE=TRIPPED\" \"" f "\" 2>/dev/null && condition=relogin_unavailable"; c = 0 }
  /^# --- Incident threading/ && !done {
    print "eval \"orig_$(declare -f severity_for)\"; severity_for() { if [ \"$1\" = relogin_unavailable ]; then echo alert; else orig_severity_for \"$1\"; fi; }"
    print "eval \"orig_$(declare -f title_for)\"; title_for() { if [ \"$1\" = relogin_unavailable ]; then echo \"[rotaryphone] GV auto-relogin — stopped\"; else orig_title_for \"$1\"; fi; }"
    done = 1
  }' "$ALARM" > "$SHARED"
check "relogin: PRECONDITION the shared-track mutant was built" "1" \
      "$(grep -c 'condition=relogin_unavailable' "$SHARED")"
reset; trip
serve '{"browserRefreshOutcome":"Succeeded"}'; bash "$SHARED" >/dev/null 2>&1
serve '{"browserRefreshOutcome":"Stale"}';     bash "$SHARED" >/dev/null 2>&1
check "relogin: NEGATIVE CONTROL the mutant posted its relogin condition" "yes" \
      "$([ "$(delivered | jq -c 'select(.dedupe_key=="rotaryphone-gv-session-relogin_unavailable")' | count)" -ge 1 ] && echo yes || echo no)"
check "relogin: NEGATIVE CONTROL a shared track MUTES the session alert" "0" "$(stale_alerts | count)"

# 5. re-armed: one quiet message, in the thread it closes.
reset; trip
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
alert_thread="$(relogin_alerts | jq -r .thread_key | head -1)"
rearm; run >/dev/null; run >/dev/null
rearmed="$(delivered | jq -c 'select(.title|test("re-armed"))')"
check "relogin: re-armed -> exactly ONE message over two runs" "1" "$(printf '%s\n' "$rearmed" | grep -c .)"
check "relogin: ...on the quiet lane" "info" "$(printf '%s' "$rearmed" | jq -r .severity)"
check "relogin: ...threaded under the alert it closes" "$alert_thread" "$(printf '%s' "$rearmed" | jq -r .thread_key)"

# 6. threading: an open session incident is joined, not duplicated.
reset; rm -f "$GV_RELOGIN_STATE_FILE"
serve '{"browserRefreshOutcome":"Stale"}'; run >/dev/null
trip; run >/dev/null
check "relogin: with an incident open, the relogin alert joins the SAME thread" \
      "$(stale_alerts | jq -r .thread_key | head -1)" "$(relogin_alerts | jq -r .thread_key | head -1)"
check "relogin: ...and opens no 🧵 root of its own" "0" \
      "$(relogin_msgs | jq -c 'select(.title|test("🧵"))' | count)"

# 7. ⛔ the incident closes, THEN a human re-arms: the RESOLVED must still reply into
# the thread the relogin alert went to. The plan's draft fell back to a key nothing had
# been posted under once INCIDENT_THREAD_KEY was cleared.
joined="$(relogin_alerts | jq -r .thread_key | head -1)"
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "relogin: PRECONDITION the session incident is closed" "1" \
      "$(delivered | jq -c 'select(.title|test("recovered"))' | count)"
rearm; run >/dev/null
check "⛔ relogin: re-armed after the incident closed still threads under its alert" "$joined" \
      "$(delivered | jq -r 'select(.title|test("re-armed")) | .thread_key' | head -1)"

# 8. a second trip inside one poll (reset + trip between runs) is a NEW event.
reset; trip "First trip. A human must run: gv-auto-relogin.sh --reset"
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
rearm
bash -c '. "$1"; breaker_load; breaker_trip challenged "Second trip. A human must run: gv-auto-relogin.sh --reset"; breaker_write' _ "$BREAKER" 2>/dev/null
sed -i 's/^BREAKER_TRIPPED_AT=.*/BREAKER_TRIPPED_AT=2099-01-01T00:00:00Z/' "$GV_RELOGIN_STATE_FILE"
run >/dev/null
check "relogin: a second trip with no ARMED poll between is still posted" "2" "$(relogin_alerts | count)"
check "relogin: ...under a different dedupe_key (a gateway must not swallow it)" "2" \
      "$(relogin_alerts | jq -r .dedupe_key | sort -u | wc -l)"
check "relogin: ...quoting the SECOND reason" "yes" \
      "$(relogin_alerts | jq -r .body | grep -qF '> Second trip.' && echo yes || echo no)"

# 10. a REFUSED relogin post is loud and is re-attempted under the SAME thread.
start_gateway --fail-notify 500
reset; trip
serve '{"browserRefreshOutcome":"Succeeded"}'
rc="$(run)"
check "relogin: a refused relogin post -> the cycle exits 1" "1" "$rc"
check "relogin: ...and the heartbeat was NOT refreshed (the dead-man speaks instead)" "404" \
      "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8099/v1/heartbeat/rotaryphone")"
# Read BEFORE restarting the stub: a restart truncates its log.
refused_thread="$(jq -r 'select(.kind=="notify") | .body.thread_key' "$GW_LOG" | head -1)"
check "relogin: PRECONDITION the refused attempt named a thread" "yes" \
      "$([ -n "$refused_thread" ] && echo yes || echo no)"
start_gateway
run >/dev/null
check "relogin: the next cycle delivers the alert" "1" "$(relogin_alerts | count)"
check "relogin: ...under the SAME thread key it first tried" "$refused_thread" \
      "$(relogin_alerts | jq -r .thread_key | head -1)"
check "relogin: ...with its 🧵 root delivered first" "1" \
      "$(relogin_msgs | jq -c 'select(.title|test("🧵"))' | count)"

# 11. the alarm finds the breaker file through the BREAKER's own override too.
reset; rm -f "$GV_RELOGIN_STATE_FILE"
ALT="$WORK/elsewhere/breaker.state"
GV_RELOGIN_STATE_FILE="$ALT" trip
serve '{"browserRefreshOutcome":"Succeeded"}'
GV_RELOGIN_STATE_FILE="$ALT" bash "$ALARM" >/dev/null 2>"$WORK/err.txt"
check "relogin: a breaker moved with GV_RELOGIN_STATE_FILE is still reported" "1" "$(relogin_alerts | count)"
rm -rf "$WORK/elsewhere"

# 9. an unreadable breaker file is logged, not posted.
reset
printf 'this is not shell (\n' > "$GV_RELOGIN_STATE_FILE"
serve '{"browserRefreshOutcome":"Succeeded"}'; run >/dev/null
check "relogin: unreadable breaker file -> nothing posted" "0" "$(relogin_msgs | count)"
check "relogin: ...and the journal says so" "yes" \
      "$(grep -q 'auto-relogin state=unreadable' "$WORK/err.txt" && echo yes || echo no)"
rm -f "$GV_RELOGIN_STATE_FILE"

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
