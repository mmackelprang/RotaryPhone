#!/usr/bin/env bash
# The circuit breaker, tested adversarially. No box, no network, no credential.
# docs/plans/gv-auto-relogin.md Task 6. Lane L: run on Linux (WSL is fine).
#
# ⛔ EVERY CASE BELOW IS WRITTEN SO THAT THE UNSAFE BEHAVIOUR IS WHAT FAILS IT.
# "TRIPPED is recorded" is a field. "A SECOND ATTEMPT IS REFUSED" is the property.
# Only the second one would have prevented a lockout.
#
# ⭐ AND THE HARNESS PROVES ITSELF. After the cases pass against the real breaker it
# builds MUTANTS — each one a copy of the breaker with one rule deliberately broken —
# and requires the named case to FAIL against each. A breaker nobody has watched fail
# is a breaker nobody knows is wired up; these controls run on every invocation, not
# once by hand.
#
# ⚠ Corrections to the plan's draft of this file, each found by running it:
#   * Its blocks shared one state file and `--reset` deliberately preserves the daily
#     counters, so by the transport block the credential counter was already 1 and
#     "transport spends NO credential budget: 0/3" could not pass on a correct
#     breaker. Every block now starts from a FRESH file (fresh()), and the transport
#     case compares the counter before and after rather than to an absolute.
#   * Its budget loop recorded FOUR attempts before asserting "the 4th is refused",
#     so an off-by-one (`-gt` for `-ge`) would still have passed. It now records
#     exactly the limit, and the off-by-one is one of the mutants.
#   * Its load-bearing "a SECOND attempt is REFUSED" ran inside the hour, so the
#     hourly spacing refused it on its own and a breaker that stayed ARMED after a
#     rejection passed it. The hour is now aged first (a negative control caught it).
#   * `faketime_hours=24` was a placeholder. Time travel here is done the one way the
#     plan permits: rewriting the recorded timestamps in the state file.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REAL_BREAKER="${HERE}/../gv-auto-relogin-breaker.sh"
BREAKER="${GV_RELOGIN_BREAKER:-$REAL_BREAKER}"

if [ "$(uname -s)" != "Linux" ]; then
    echo "FAILURES PRESENT: lane L must run on Linux (the mode-600 cases are meaningless on NTFS); got $(uname -s)"
    exit 2
fi

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
export GV_RELOGIN_STATE_FILE="$WORK/breaker.state"
export GV_RELOGIN_LOCK_FILE="$WORK/breaker.lock"
# The harness sets no limit of its own. These are the breaker's defaults, stated
# here only so the "N/3" strings below are not coincidences with the environment.
unset GV_RELOGIN_MAX_PER_HOUR GV_RELOGIN_MAX_PER_DAY GV_RELOGIN_MAX_TRANSPORT_PER_DAY

fail=0
cases=0
check() {
    cases=$((cases + 1))
    if [ "$2" = "$3" ]; then echo "  PASS $1"; else echo "  FAIL $1: expected [$2] got [$3]"; fail=1; fi
}

# Drive the library the way the actuator will, so the harness exercises the real
# entry points rather than a paraphrase of them.
drive() { bash -c '
    set -uo pipefail
    . "$1"
    breaker_load
    shift
    "$@"
' _ "$BREAKER" "$@"; }
# Run a sequence of library calls against the state file, then persist.
act() { bash -c '. "$1"; breaker_load; eval "$2"; breaker_write' _ "$BREAKER" "$1"; }
status_field() { bash "$BREAKER" --status | awk -v k="$1" '$1==k{print $NF; exit}'; }
state()   { status_field state; }
# ⛔ The decision is read as a TOKEN, never as an exit code. Pre-merge review
# 2026-09-25: with `echo $?` a CRASHED decision (division by zero, an octal
# overflow) read as "1", i.e. refused, so mutants that crashed passed every refusal
# case — while the same crash in a real caller fell through to the login. Now a crash
# prints nothing, and nothing is neither AUTHORISED nor REFUSED.
may()     { drive breaker_verdict 2>/dev/null | head -1 | cut -d' ' -f1; }
set_field() { sed -i "s/^$1=.*/$1=$2/" "$GV_RELOGIN_STATE_FILE"; }
fresh()   { rm -f "$GV_RELOGIN_STATE_FILE"; bash "$BREAKER" --reset >/dev/null; }

echo "=== static: the breaker has no vocabulary for trying again, and ONE way to arm ==="
check "no sleep/retry/backoff/attempt_again/re_arm/rearm" "0" \
      "$(grep -Ec 'sleep|retry|backoff|attempt_again|re_arm|rearm' "$BREAKER")"
check "exactly ONE assignment of ARMED" "1" \
      "$(grep -c 'BREAKER_STATE="ARMED"' "$BREAKER")"
# ⚠ The count above only sees one spelling. Any OTHER way of writing ARMED into the
# state is a second door and must not exist at all. (Review 2026-09-25: the harness's
# own mutants used the unquoted form, which the count above cannot see.)
check "no other spelling assigns ARMED (unquoted, single-quoted, printf -v, declare)" "0" \
      "$(grep -cE "BREAKER_STATE=('ARMED'|ARMED)|printf -v +\"?BREAKER_STATE|declare[^#]*BREAKER_STATE=" "$BREAKER")"
# ⚠ The count AND where it is: a second assignment elsewhere, with this one deleted,
# would keep the count at 1.
check "...and it is inside breaker_reset" "breaker_reset" \
      "$(awk '/^[a-z_]+\(\) *\{/{fn=$1} /BREAKER_STATE="ARMED"/{sub(/\(\)/,"",fn); print fn}' "$BREAKER")"

echo "=== FAIL CLOSED: absence and corruption are TRIPPED, never ARMED ==="
rm -f "$GV_RELOGIN_STATE_FILE"
check "no state file -> TRIPPED" "TRIPPED" "$(state)"
check "no state file -> refuses an attempt" "REFUSED" "$(may)"
printf 'BREAKER_STATE=BANANA\n' > "$GV_RELOGIN_STATE_FILE"
check "corrupt state -> TRIPPED" "TRIPPED" "$(state)"
check "corrupt state -> refuses" "REFUSED" "$(may)"
printf 'this is not shell (\n' > "$GV_RELOGIN_STATE_FILE"
check "unparseable state -> TRIPPED" "TRIPPED" "$(state)"
check "unparseable state -> refuses" "REFUSED" "$(may)"
# ⛔ The fail-OPEN trap the plan's draft had: `[ abc -ge 3 ]` is an error, an error is
# false, and a false "budget used?" test would AUTHORISE.
fresh
set_field BREAKER_DAY_CREDENTIAL_ATTEMPTS abc
check "non-numeric counter -> TRIPPED" "TRIPPED" "$(state)"
check "non-numeric counter -> refuses" "REFUSED" "$(may)"
fresh
check "a zero hourly limit in the environment refuses (no division by zero)" "REFUSED" \
      "$(GV_RELOGIN_MAX_PER_HOUR=0 may)"
check "a leading-zero limit (octal 09) refuses rather than crashing" "REFUSED" \
      "$(GV_RELOGIN_MAX_PER_HOUR=09 may)"
check "an hourly limit above 3600 (spacing would round to 0s) refuses" "REFUSED" \
      "$(GV_RELOGIN_MAX_PER_HOUR=7200 may)"

echo "=== ⛔ MALFORMED NUMBERS, PARTIAL FILES, A BACKWARDS CLOCK: all refuse, none crash ==="
# Each of these AUTHORISED a login in review (2026-09-25) through an actuator shaped
# `breaker_may_attempt || exit`, with the daily budget already spent.
fresh
act 'breaker_record_credential_attempt; breaker_record_credential_attempt; breaker_record_credential_attempt'
set_field BREAKER_LAST_ATTEMPT_AT 099
check "leading-zero LAST_ATTEMPT_AT (octal error) -> REFUSED, not crashed" "REFUSED" "$(may)"
check "...and the breaker reads it as TRIPPED" "TRIPPED" "$(state)"
fresh
set_field BREAKER_DAY_CREDENTIAL_ATTEMPTS 99999999999999999999
check "a 20-digit counter (overflow) -> REFUSED" "REFUSED" "$(may)"
fresh
printf 'BREAKER_STATE=ARMED\n' > "$GV_RELOGIN_STATE_FILE"
check "a PARTIAL file (state only, no counters) -> TRIPPED" "TRIPPED" "$(state)"
check "...and refuses" "REFUSED" "$(may)"
check "...naming what is missing" "yes" \
      "$(bash "$BREAKER" --status | grep -q 'missing fields' && echo yes || echo no)"
fresh
printf 'BREAKER_MAX_PER_DAY=99\n' >> "$GV_RELOGIN_STATE_FILE"
check "a state file cannot raise its own budget" "3" \
      "$(bash -c '. "$1"; breaker_load; echo "$BREAKER_MAX_PER_DAY"' _ "$BREAKER")"
fresh
act 'breaker_record_credential_attempt; breaker_record_credential_attempt; breaker_record_credential_attempt'
set_field BREAKER_LAST_ATTEMPT_AT "$(( $(date -u +%s) - 90000 ))"
set_field BREAKER_DAY_BUCKET 2999-01-01
check "⛔ a day bucket AFTER today (clock went back) -> REFUSED, budget not reset" "REFUSED" "$(may)"
check "...and the spent count is still on file" "3" \
      "$(sed -n 's/^BREAKER_DAY_CREDENTIAL_ATTEMPTS=//p' "$GV_RELOGIN_STATE_FILE")"
fresh
check "an unknown state set AFTER load is refused where the decision is made" "REFUSED" \
      "$(bash -c '. "$1"; breaker_load; BREAKER_STATE=WEIRD; breaker_verdict' _ "$BREAKER" 2>/dev/null | cut -d' ' -f1)"
rm -f "$GV_RELOGIN_STATE_FILE"
check "a fail-closed trip carries a timestamp" "yes" \
      "$(bash "$BREAKER" --status | awk '$1=="tripped_at"{print $2}' | grep -qE '^[0-9]{4}-' && echo yes || echo no)"

echo "=== ⛔ ONE WRITER AT A TIME: --reset does not touch the file while a run holds the lock ==="
# The lost update the lock prevents (review 2026-09-25): a writer that loaded before
# a trip writes its stale ARMED over it. Its interleaving is too narrow to hit by
# timing in a harness, so the property is tested directly: while the lock is held —
# as an in-flight actuator holds it — a --reset must refuse and change nothing.
fresh
act 'breaker_record_credential_attempt; breaker_trip credential_rejected "tripped by the in-flight run"'
( exec 9>"$GV_RELOGIN_LOCK_FILE"; flock 9; sleep 3 ) &
holder=$!
sleep 0.5
GV_RELOGIN_LOCK_WAIT=1 bash "$BREAKER" --reset >/dev/null 2>"$WORK/reset.err"
reset_rc=$?
wait "$holder"
check "--reset while a run holds the lock is refused (non-zero)" "1" "$reset_rc"
check "...and says why" "yes" "$(grep -q 'in progress' "$WORK/reset.err" && echo yes || echo no)"
check "⛔ ...and the trip SURVIVES untouched" "TRIPPED" "$(state)"
bash "$BREAKER" --reset >/dev/null
check "once the lock is free, --reset works" "ARMED" "$(state)"

echo "=== the first write is already private ==="
rm -f "$GV_RELOGIN_STATE_FILE"
bash "$BREAKER" --reset >/dev/null
check "mode 600 on the very first write" "600" "$(stat -c %a "$GV_RELOGIN_STATE_FILE")"

echo "=== ⛔ ONE CREDENTIAL REJECTION STOPS EVERYTHING, PERMANENTLY ==="
fresh
act 'breaker_record_credential_attempt; breaker_trip credential_rejected "Google rejected the stored password."'
check "after ONE rejection -> TRIPPED" "TRIPPED" "$(state)"
# ⛔ THE LOAD-BEARING ASSERTION OF THE WHOLE ARC — and the hour is aged FIRST. In the
# plan's draft this check ran straight after the attempt, so the hourly spacing
# refused it whatever the breaker state was: a mutant in which a rejection leaves the
# breaker ARMED passed it (measured 2026-09-25). With the spacing satisfied, the trip
# is the only thing left that can refuse.
set_field BREAKER_LAST_ATTEMPT_AT "$(( $(date -u +%s) - 3700 ))"
check "⛔ a SECOND attempt is REFUSED" "REFUSED" "$(may)"
# ...and it stays refused across time, a new day, and a reboot (a fresh shell).
day_ago=$(( $(date -u +%s) - 86400 - 60 ))
set_field BREAKER_LAST_ATTEMPT_AT "$day_ago"
check "⛔ still refused after 24h of simulated time" "REFUSED" "$(may)"
set_field BREAKER_DAY_BUCKET 1970-01-01
check "⛔ a NEW DAY does NOT re-arm a tripped breaker" "TRIPPED" "$(state)"
check "⛔ ...and still refuses" "REFUSED" "$(may)"
check "⛔ ...from a clean environment too (the reboot case)" "REFUSED" \
      "$(env -i PATH="$PATH" HOME="$WORK" GV_RELOGIN_STATE_FILE="$GV_RELOGIN_STATE_FILE" \
             bash -c '. "$1"; breaker_load; breaker_verdict' _ "$BREAKER" 2>/dev/null | cut -d' ' -f1)"

echo "=== ⛔ A CHALLENGE STOPS EVERYTHING, and is distinguishable from a rejection ==="
fresh
act 'breaker_record_credential_attempt; breaker_trip challenged "Google presented a verification challenge."'
check "challenge -> TRIPPED" "TRIPPED" "$(state)"
check "challenge -> refuses" "REFUSED" "$(may)"
check "challenge reason is NOT credential_rejected" "challenged" "$(status_field reason)"

echo "=== TRANSPORT is the ONLY non-terminal class, and it spends no credential budget ==="
fresh
cred_before="$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
act 'breaker_record_transport_failure'
check "transport failure -> still ARMED" "ARMED" "$(state)"
check "⛔ transport spends NO credential budget" "$cred_before" \
      "$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
check "...and the credential counter is genuinely zero" "0/3" \
      "$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
check "transport spends its own budget" "1/3" \
      "$(bash "$BREAKER" --status | awk '$1=="transport"{print $3}')"
check "...but is still rate-limited this hour" "REFUSED" "$(may)"
# The transport ceiling is real: three transport failures in a day stop attempts
# until the day rolls, even with the hour spacing satisfied.
act 'breaker_record_transport_failure; breaker_record_transport_failure'
set_field BREAKER_LAST_ATTEMPT_AT "$(( $(date -u +%s) - 3700 ))"
check "3 transport failures today -> refused despite the hour having passed" "REFUSED" "$(may)"
check "...and the breaker is still ARMED (a ceiling, not a trip)" "ARMED" "$(state)"

echo "=== RATE LIMITS bound the Google-facing traffic ==="
fresh
check "a fresh armed breaker authorises" "AUTHORISED" "$(may)"
act 'breaker_record_credential_attempt'
check "⛔ a second attempt within the hour is REFUSED" "REFUSED" "$(may)"
set_field BREAKER_LAST_ATTEMPT_AT "$(( $(date -u +%s) - 3700 ))"
check "an attempt an hour later is authorised" "AUTHORISED" "$(may)"
# Bring the day's count to EXACTLY the limit (1 recorded above, 2 more), each spaced
# an hour apart, so the only thing left to refuse the next one is the daily budget.
for _ in 1 2; do
    act 'breaker_record_credential_attempt'
    set_field BREAKER_LAST_ATTEMPT_AT "$(( $(date -u +%s) - 3700 ))"
done
check "PRECONDITION: exactly 3/3 used" "3/3" \
      "$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
check "⛔ the 4th credential attempt today is REFUSED even with time available" "REFUSED" "$(may)"

echo "=== --reset is a HUMAN action and does not hand back a budget ==="
before="$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
reset_out="$(bash "$BREAKER" --reset)"
after="$(bash "$BREAKER" --status | awk '$1=="credential"{print $3}')"
check "⛔ --reset preserves the daily counter" "$before" "$after"
check "--reset says so" "yes" \
      "$(printf '%s' "$reset_out" | grep -q 'Counters preserved: 3/3' && echo yes || echo no)"
check "--reset arms" "ARMED" "$(state)"
check "⛔ ...and an armed breaker with a spent budget still refuses" "REFUSED" "$(may)"

echo "=== the state file is INSPECTABLE and PRIVATE ==="
fresh
act 'breaker_record_credential_attempt; breaker_trip credential_rejected "Google rejected the stored password. A human must run: gv-auto-relogin.sh --reset"'
check "mode 600 after a trip" "600" "$(stat -c %a "$GV_RELOGIN_STATE_FILE")"
check "⛔ --status explains WHY without reading code" "yes" \
      "$(bash "$BREAKER" --status | grep -q 'Google rejected the stored password' && echo yes || echo no)"
check "no .new debris" "0" "$(find "$WORK" -name '*.new' | wc -l)"

echo
if [ "$fail" -ne 0 ]; then
    echo "FAILURES PRESENT (${cases} cases run)"
    exit 1
fi

# --- Negative controls: each rule, broken on purpose, must be CAUGHT ---------
if [ -n "${GV_RELOGIN_BREAKER:-}" ]; then
    echo "ALL ${cases} CASES PASSED (against ${BREAKER})"
    exit 0
fi

echo "=== NEGATIVE CONTROLS: each rule broken on purpose must FAIL a named case ==="
mutant() { # mutant NAME EXPECTED-FAILING-CASE SED-SCRIPT
    local m="$WORK/mutant-$1.sh" out
    sed -e "$3" "$REAL_BREAKER" > "$m"
    if cmp -s "$m" "$REAL_BREAKER"; then
        check "mutant $1 actually differs from the breaker" "differs" "identical (the sed matched nothing)"
        return
    fi
    out="$(GV_RELOGIN_BREAKER="$m" bash "$0" 2>&1)"
    check "mutant $1 is caught by: $2" "caught" \
          "$(printf '%s\n' "$out" | grep -qF "FAIL $2" && echo caught || echo MISSED)"
}
mutant missing-is-armed "no state file -> TRIPPED" \
    's/^\( *\)breaker_fail_closed state_missing \\$/\1BREAKER_STATE=ARMED; : \\/'
mutant rejection-arms "⛔ a SECOND attempt is REFUSED" \
    '/^breaker_trip() {/,/^}/ s/^    BREAKER_STATE="TRIPPED"$/    [ "$1" = credential_rejected ] \&\& BREAKER_STATE=ARMED || BREAKER_STATE=TRIPPED/'
mutant new-day-arms "⛔ a NEW DAY does NOT re-arm a tripped breaker" \
    's/^\( *BREAKER_DAY_TRANSPORT_FAILURES=0\)$/\1; BREAKER_STATE=ARMED/'
mutant transport-spends-credential "⛔ transport spends NO credential budget" \
    '/^breaker_record_transport_failure() {/a\    breaker_record_credential_attempt'
mutant daily-off-by-one "⛔ the 4th credential attempt today is REFUSED even with time available" \
    's/"\$BREAKER_DAY_CREDENTIAL_ATTEMPTS" -ge "\$BREAKER_MAX_PER_DAY"/"$BREAKER_DAY_CREDENTIAL_ATTEMPTS" -gt "$BREAKER_MAX_PER_DAY"/'
mutant zero-limit-guard-removed "a zero hourly limit in the environment refuses (no division by zero)" \
    's/|| \[ "\${!v}" -lt 1 \]//'
mutant loose-count "leading-zero LAST_ATTEMPT_AT (octal error) -> REFUSED, not crashed" \
    's/\^(0|\[1-9\]\[0-9\]{0,11})\$/^[0-9]+$/'
# The pre-review behaviour: a field the file omits keeps its zero default, so a file
# holding only BREAKER_STATE=ARMED reads as "never attempted, nothing used today".
mutant partial-file-accepted "a PARTIAL file (state only, no counters) -> TRIPPED" \
    "/for f in \$BREAKER_ALL_FIELDS; do printf -v \"\$f\" '%s' __UNSET__; done/d"
mutant day-rolls-backwards "⛔ a day bucket AFTER today (clock went back) -> REFUSED, budget not reset" \
    's/\[\[ "\$BREAKER_DAY_BUCKET" < "\$today" \]\]/[[ "$BREAKER_DAY_BUCKET" != "$today" ]]/'
mutant decision-only-trips-on-TRIPPED "an unknown state set AFTER load is refused where the decision is made" \
    's/if \[ "\$BREAKER_STATE" != "ARMED" \]; then/if [ "$BREAKER_STATE" = "TRIPPED" ]; then/'
mutant reset-without-lock "⛔ ...and the trip SURVIVES untouched" \
    's/^    breaker_lock || { echo "Breaker NOT changed.*$/    :/'
mutant numeric-guard-removed "non-numeric counter -> refuses" \
    's/if ! breaker_is_count "\${!g}"; then/if false; then/'

echo
if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
exit "$fail"
