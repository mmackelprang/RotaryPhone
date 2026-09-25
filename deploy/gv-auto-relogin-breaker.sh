#!/usr/bin/env bash
# =============================================================================
# THE CIRCUIT BREAKER. This file is the feature.
#
# ⛔ Spec §3: automated login is only safe if login is RARE, and an account lock is
# STRICTLY WORSE than the problem being solved — it is a catastrophic event needing
# the owner urgently, with the phone down and no fallback. A version of this arc
# that logs in reliably but tries freely is worse than no automation at all.
#
# Sourced by gv-auto-relogin.sh. Also runnable directly for --status and --reset,
# which are the ONLY human interfaces it has.
#
# ⛔ FOUR RULES, AND NONE OF THEM IS NEGOTIABLE:
#
#   1. ONE credential rejection stops EVERYTHING, PERMANENTLY. Not a pause, not a
#      "later", not "once more in an hour". A rejected credential is never
#      transient — offering a wrong password a second time is the single most
#      reliable way to get an account locked.
#   2. A CHALLENGE stops everything, permanently. A challenge means Google ALREADY
#      considers this suspicious; another go deepens it.
#   3. UNKNOWN IS TERMINAL. See breaker_trip's callers: the classifier's default
#      branch trips. The rejection detector is built from a sample of exactly ONE
#      rejection (the spike's, and there will never be a second because of rule 1),
#      so the case this will most often meet is one it does not recognise. Mapping
#      unknown to a non-terminal class is a one-line path to the catastrophic
#      outcome.
#   4. FAIL CLOSED. A missing, unparseable or implausible state file means we cannot
#      account for how many times we have signed in today — which is the ONLY thing
#      this file exists to bound. It reports TRIPPED. Only --reset (a human) creates
#      an ARMED one.
#
# ⛔ NOTHING IN THIS FILE ARMS ITSELF AGAIN. There is no timeout, no daily reset, no
# "cool-off". Search for the assignment that sets BREAKER_STATE to ARMED: it appears
# in exactly one function, breaker_reset, reachable only from an explicit human
# --reset. (The harness counts that literal assignment and requires exactly one, so
# this comment deliberately does not spell it out.)
#
# ⚠ VOCABULARY IS ASSERTED. docs/plans/gv-auto-relogin.md Task 5 requires this file
# to contain none of the usual words for doing something a second time, and the
# harness greps for them. The comments above are phrased around that on purpose; a
# future edit that "clarifies" one of them with the forbidden word fails the harness.
# =============================================================================
set -uo pipefail

BREAKER_STATE_FILE="${GV_RELOGIN_STATE_FILE:-${HOME}/.local/state/gv-auto-relogin.state}"

# ⚠ MARKED FOR THE OWNER — spec §10 decision 4: "a starting point, not measured."
# Both numbers are the SPEC'S, carried through unchanged. This file invents neither
# and proposes no others. Revisit once the real re-login frequency is known.
BREAKER_MAX_PER_HOUR="${GV_RELOGIN_MAX_PER_HOUR:-1}"
BREAKER_MAX_PER_DAY="${GV_RELOGIN_MAX_PER_DAY:-3}"
# ⚠ The transport ceiling REUSES the spec's daily number rather than introducing a
# fourth one. It is not a measurement and is not claimed to be.
BREAKER_MAX_TRANSPORT_PER_DAY="${GV_RELOGIN_MAX_TRANSPORT_PER_DAY:-${BREAKER_MAX_PER_DAY}}"

breaker_log() { printf '%s gv-relogin-breaker[%s]: %s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$$" "$*" >&2; }

breaker_today() { date -u +%Y-%m-%d; }

# ⛔ A COUNT IS `0` OR A DIGIT RUN WITH NO LEADING ZERO, AT MOST 12 DIGITS. Both limits
# are load-bearing (pre-merge review 2026-09-25): a leading zero makes `$(( ))` read
# the value as OCTAL, so `09` is a "value too great for base" error — and an arithmetic
# error ABANDONS the current command and carries on with the next line, which in a
# caller shaped `breaker_may_attempt || exit` reached the login with the budget spent.
# Twenty digits overflows `[ -ge ]`, which errors, which is false, which authorised.
breaker_is_count() { [[ "${1:-}" =~ ^(0|[1-9][0-9]{0,11})$ ]]; }

# The fields a state file must carry. A file that omits one is PARTIAL, and a partial
# file is corrupt: "no LAST_ATTEMPT_AT" must not read as "never attempted".
BREAKER_REQUIRED_FIELDS="BREAKER_STATE BREAKER_LAST_ATTEMPT_AT BREAKER_DAY_BUCKET BREAKER_DAY_CREDENTIAL_ATTEMPTS BREAKER_DAY_TRANSPORT_FAILURES BREAKER_ATTEMPTS_TOTAL"
BREAKER_ALL_FIELDS="${BREAKER_REQUIRED_FIELDS} BREAKER_REASON BREAKER_REASON_TEXT BREAKER_TRIPPED_AT BREAKER_LAST_OUTCOME"

breaker_now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# Fail closed with a reason. Used only by breaker_load's own checks; the actuator's
# terminal outcomes go through breaker_trip. Stamps the time, so the alarm can tell
# two fail-closed trips apart.
breaker_fail_closed() {
    BREAKER_STATE="TRIPPED"
    BREAKER_REASON="$1"
    BREAKER_REASON_TEXT="$2"
    BREAKER_TRIPPED_AT="$(breaker_now_iso)"
}

# Every field back to its zero value. breaker_load calls this FIRST, so a state file
# that omits a field cannot inherit a value from an earlier load in the same shell.
breaker_clear_fields() {
    BREAKER_STATE=""
    BREAKER_REASON=""
    BREAKER_REASON_TEXT=""
    BREAKER_TRIPPED_AT=""
    BREAKER_LAST_ATTEMPT_AT="0"
    BREAKER_DAY_BUCKET=""
    BREAKER_DAY_CREDENTIAL_ATTEMPTS="0"
    BREAKER_DAY_TRANSPORT_FAILURES="0"
    BREAKER_ATTEMPTS_TOTAL="0"
    BREAKER_LAST_OUTCOME=""
    BREAKER_REFUSAL=""
}
breaker_clear_fields
BREAKER_CLOCK_BEHIND=0

# --- Load, and FAIL CLOSED --------------------------------------------------
breaker_load() {
    breaker_clear_fields
    BREAKER_CLOCK_BEHIND=0

    if [ ! -e "$BREAKER_STATE_FILE" ]; then
        # ⛔ ABSENT IS NOT ARMED. Without the file there is no record of how many
        # sign-ins have already happened today, and bounding that count is the only
        # job this file has. An actuator that treats "I lost my memory" as "I may
        # proceed" has no rate limit at all — it has a rate limit that resets
        # whenever anything deletes a file.
        breaker_fail_closed state_missing \
            "Auto-relogin is stopped because its breaker state file is missing at ${BREAKER_STATE_FILE}. Without it there is no record of how many sign-ins have already been attempted today, so no further attempt can be authorised. A human must run: gv-auto-relogin.sh --reset"
    else
        # ⛔ READ IN A SUBSHELL, AND ONLY THE KNOWN FIELDS COME BACK. Sourcing the file
        # in THIS shell would let any line in it do anything — `BREAKER_MAX_PER_DAY=99`
        # would hand itself a budget, and a function definition could replace this
        # one. The subshell sources it, then prints the whitelisted fields in %q form;
        # only that print is evaluated here. A field the file does not set comes back
        # as the __UNSET__ marker, which is how a PARTIAL file is caught below.
        local dump
        # shellcheck disable=SC1090
        dump="$(
            for f in $BREAKER_ALL_FIELDS; do printf -v "$f" '%s' __UNSET__; done
            . "$BREAKER_STATE_FILE" >/dev/null 2>&1 || exit 1
            for f in $BREAKER_ALL_FIELDS; do printf '%s=%q\n' "$f" "${!f}"; done
        )"
        if [ $? -ne 0 ] || [ -z "$dump" ]; then
            breaker_fail_closed state_unreadable \
                "Auto-relogin is stopped because its breaker state file at ${BREAKER_STATE_FILE} could not be read. The attempt history is unknown, so no further attempt can be authorised. A human must run: gv-auto-relogin.sh --reset"
        else
            eval "$dump"
            local f missing=""
            for f in $BREAKER_REQUIRED_FIELDS; do
                [ "${!f}" = "__UNSET__" ] && missing="${missing} ${f}"
            done
            for f in BREAKER_REASON BREAKER_REASON_TEXT BREAKER_TRIPPED_AT BREAKER_LAST_OUTCOME; do
                [ "${!f}" = "__UNSET__" ] && printf -v "$f" '%s' ""
            done
            if [ -n "$missing" ]; then
                for f in $missing; do printf -v "$f" '%s' 0; done
                breaker_fail_closed state_corrupt \
                    "Auto-relogin is stopped because its breaker state file is missing fields (${missing# }). The attempt history cannot be trusted. A human must run: gv-auto-relogin.sh --reset"
            fi
        fi
    fi

    case "$BREAKER_STATE" in
        ARMED|TRIPPED) ;;
        *)
            # An unrecognised state is not a state. Same rule as rule 3, applied to
            # our own file.
            breaker_fail_closed state_corrupt \
                "Auto-relogin is stopped because its breaker state file records an unrecognised state. The attempt history cannot be trusted. A human must run: gv-auto-relogin.sh --reset"
            ;;
    esac

    # ⛔ A COUNTER THAT IS NOT A PLAIN NUMBER IS FAIL-OPEN IF LEFT ALONE: `[ abc -ge 3 ]`
    # is an error, an error is false, and a false "have we used the budget?" test
    # AUTHORISES. So an implausible counter is corruption, and corruption trips.
    local g
    for g in BREAKER_LAST_ATTEMPT_AT BREAKER_DAY_CREDENTIAL_ATTEMPTS \
             BREAKER_DAY_TRANSPORT_FAILURES BREAKER_ATTEMPTS_TOTAL; do
        if ! breaker_is_count "${!g}"; then
            breaker_fail_closed state_corrupt \
                "Auto-relogin is stopped because its breaker state file records an implausible ${g}. The attempt history cannot be trusted. A human must run: gv-auto-relogin.sh --reset"
            printf -v "$g" '%s' 0
        fi
    done

    # Roll the daily buckets — FORWARD ONLY. ⚠ Rolling the DAY does not arm a TRIPPED
    # breaker; the two are independent, and conflating them is how a permanent stop
    # quietly becomes a 24-hour pause. ⚠ And a bucket dated AFTER today means the
    # clock went backwards across midnight: rolling on "different" rather than
    # "later" would hand a flapping clock a fresh daily budget every flap. That case
    # is REFUSED (BREAKER_CLOCK_BEHIND) until the clock catches up, not rolled.
    local today; today="$(breaker_today)"
    if [ -z "${BREAKER_DAY_BUCKET:-}" ] || [[ "$BREAKER_DAY_BUCKET" < "$today" ]]; then
        BREAKER_DAY_BUCKET="$today"
        BREAKER_DAY_CREDENTIAL_ATTEMPTS=0
        BREAKER_DAY_TRANSPORT_FAILURES=0
    elif [[ "$BREAKER_DAY_BUCKET" > "$today" ]]; then
        BREAKER_CLOCK_BEHIND=1
    fi
    return 0
}

breaker_write() {
    local dir; dir="$(dirname "$BREAKER_STATE_FILE")"
    mkdir -p "$dir" 2>/dev/null || { breaker_log "could not create ${dir}"; return 1; }
    # Atomic replace, same reasoning as the alarm's write_state: a reader must see
    # the whole old file or the whole new one. And mode 600 from birth — umask is
    # not trusted to produce it, because this file records account-level events.
    ( umask 077
      {
        printf '# gv-auto-relogin breaker state, written %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '# Inspect with: gv-auto-relogin.sh --status\n'
        printf 'BREAKER_STATE=%q\n'                   "$BREAKER_STATE"
        printf 'BREAKER_REASON=%q\n'                  "$BREAKER_REASON"
        printf 'BREAKER_REASON_TEXT=%q\n'             "$BREAKER_REASON_TEXT"
        printf 'BREAKER_TRIPPED_AT=%q\n'              "$BREAKER_TRIPPED_AT"
        printf 'BREAKER_LAST_ATTEMPT_AT=%q\n'         "$BREAKER_LAST_ATTEMPT_AT"
        printf 'BREAKER_DAY_BUCKET=%q\n'              "$BREAKER_DAY_BUCKET"
        printf 'BREAKER_DAY_CREDENTIAL_ATTEMPTS=%q\n' "$BREAKER_DAY_CREDENTIAL_ATTEMPTS"
        printf 'BREAKER_DAY_TRANSPORT_FAILURES=%q\n'  "$BREAKER_DAY_TRANSPORT_FAILURES"
        printf 'BREAKER_ATTEMPTS_TOTAL=%q\n'          "$BREAKER_ATTEMPTS_TOTAL"
        printf 'BREAKER_LAST_OUTCOME=%q\n'            "$BREAKER_LAST_OUTCOME"
      } > "${BREAKER_STATE_FILE}.new" ) \
      || { rm -f "${BREAKER_STATE_FILE}.new"; breaker_log "could not write state"; return 1; }
    # Flush the new file to disk BEFORE the rename, so a power cut just after a trip
    # cannot bring the previous (ARMED) file back. A zero-length file is safe anyway —
    # it loads as corrupt — but an old, whole, ARMED one is not.
    sync "${BREAKER_STATE_FILE}.new" 2>/dev/null || sync
    mv -f "${BREAKER_STATE_FILE}.new" "$BREAKER_STATE_FILE" \
      || { rm -f "${BREAKER_STATE_FILE}.new"; breaker_log "could not replace state"; return 1; }
    chmod 600 "$BREAKER_STATE_FILE" 2>/dev/null
    return 0
}

# --- May we attempt? --------------------------------------------------------
# Returns 0 to authorise, 1 to refuse. On refusal, BREAKER_REFUSAL says why in
# words an operator can read without opening this file.
breaker_may_attempt() {
    BREAKER_REFUSAL=""

    if [ "$BREAKER_STATE" != "ARMED" ]; then
        # ⛔ `!= ARMED`, not `= TRIPPED`: a state this function has never heard of
        # is a refusal. breaker_load already maps one to TRIPPED; this is the same
        # rule held a second time, where the decision is actually made.
        BREAKER_REFUSAL="breaker ${BREAKER_STATE:-UNSET} (${BREAKER_REASON:-no reason recorded}) at ${BREAKER_TRIPPED_AT:-unknown}; a human must --reset"
        return 1
    fi

    # ⛔ The limits come from the environment, so they are validated like input. A
    # zero hourly limit would be a division by zero below; a non-numeric one would
    # make every comparison an error, and an erroring comparison is a false one.
    local v
    for v in BREAKER_MAX_PER_HOUR BREAKER_MAX_PER_DAY BREAKER_MAX_TRANSPORT_PER_DAY; do
        if ! breaker_is_count "${!v}" || [ "${!v}" -lt 1 ]; then
            BREAKER_REFUSAL="configuration: ${v}='${!v}' is not a positive integer; refusing rather than guessing a limit"
            return 1
        fi
    done
    # Above 3600/hour the spacing window below rounds to 0 seconds and the hourly
    # limit silently stops existing.
    if [ "$BREAKER_MAX_PER_HOUR" -gt 3600 ]; then
        BREAKER_REFUSAL="configuration: BREAKER_MAX_PER_HOUR=${BREAKER_MAX_PER_HOUR} exceeds 3600, which would disable the hourly spacing"
        return 1
    fi

    if [ "${BREAKER_CLOCK_BEHIND:-0}" != "0" ]; then
        BREAKER_REFUSAL="clock: the state file's day (${BREAKER_DAY_BUCKET}) is AFTER today ($(breaker_today)); the clock went backwards, so today's count cannot be trusted. Refusing until the clock catches up."
        return 1
    fi

    local now; now="$(date -u +%s)"
    local since=$(( now - BREAKER_LAST_ATTEMPT_AT ))
    local window=$(( 3600 / BREAKER_MAX_PER_HOUR ))
    # A clock that has moved BACKWARDS makes `since` negative, which is < window,
    # which refuses. That is the safe direction and it is deliberate.
    if [ "$BREAKER_LAST_ATTEMPT_AT" -gt 0 ] && [ "$since" -lt "$window" ]; then
        BREAKER_REFUSAL="rate limit: last attempt ${since}s ago, minimum spacing ${window}s (${BREAKER_MAX_PER_HOUR}/hour)"
        return 1
    fi

    if [ "$BREAKER_DAY_CREDENTIAL_ATTEMPTS" -ge "$BREAKER_MAX_PER_DAY" ]; then
        BREAKER_REFUSAL="rate limit: ${BREAKER_DAY_CREDENTIAL_ATTEMPTS}/${BREAKER_MAX_PER_DAY} credential attempts already used today (${BREAKER_DAY_BUCKET})"
        return 1
    fi

    if [ "$BREAKER_DAY_TRANSPORT_FAILURES" -ge "$BREAKER_MAX_TRANSPORT_PER_DAY" ]; then
        BREAKER_REFUSAL="transport ceiling: ${BREAKER_DAY_TRANSPORT_FAILURES}/${BREAKER_MAX_TRANSPORT_PER_DAY} transport failures today; not attempting again until tomorrow"
        return 1
    fi

    return 0
}

# --- Recording outcomes -----------------------------------------------------
# ⛔ THE CREDENTIAL BUDGET IS SEPARATE FROM THE ATTEMPT BUDGET, and spec §9.4
# requires it: "a CDP transport failure ... does not consume the credential
# budget." A transport failure means we never reached the form, so Google saw
# nothing and no credential was spent. It still consumes the hourly spacing (or a
# broken CDP would spin) and its own daily ceiling.
breaker_record_credential_attempt() {
    BREAKER_LAST_ATTEMPT_AT="$(date -u +%s)"
    BREAKER_DAY_CREDENTIAL_ATTEMPTS=$(( BREAKER_DAY_CREDENTIAL_ATTEMPTS + 1 ))
    BREAKER_ATTEMPTS_TOTAL=$(( BREAKER_ATTEMPTS_TOTAL + 1 ))
}

breaker_record_transport_failure() {
    BREAKER_LAST_ATTEMPT_AT="$(date -u +%s)"
    BREAKER_DAY_TRANSPORT_FAILURES=$(( BREAKER_DAY_TRANSPORT_FAILURES + 1 ))
    BREAKER_LAST_OUTCOME="transport"
}

breaker_record_success() {
    BREAKER_LAST_OUTCOME="succeeded"
    # ⚠ Success does NOT decrement or clear anything. The budget bounds sign-ins,
    # not failures — three successful sign-ins in a day is exactly as much Google
    # traffic as three failed ones, and it is the traffic that is being bounded.
}

# ⛔ THE ONE-WAY DOOR. Everything that reaches here is terminal.
breaker_trip() {
    BREAKER_STATE="TRIPPED"
    BREAKER_REASON="$1"
    BREAKER_REASON_TEXT="$2"
    BREAKER_TRIPPED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    BREAKER_LAST_OUTCOME="$1"
    breaker_log "TRIPPED (${BREAKER_REASON}). No further attempt will be made until a human runs --reset."
}

# --- Human interfaces -------------------------------------------------------
breaker_status() {
    breaker_load
    printf 'state              %s\n' "$BREAKER_STATE"
    printf 'reason             %s\n' "${BREAKER_REASON:-none}"
    printf 'tripped_at         %s\n' "${BREAKER_TRIPPED_AT:-never}"
    printf 'last_attempt_at    %s\n' \
        "$([ "$BREAKER_LAST_ATTEMPT_AT" -gt 0 ] && date -u -d "@${BREAKER_LAST_ATTEMPT_AT}" +%Y-%m-%dT%H:%M:%SZ || echo never)"
    printf 'today              %s\n' "${BREAKER_DAY_BUCKET:-none}"
    printf 'credential today   %s/%s\n' "$BREAKER_DAY_CREDENTIAL_ATTEMPTS" "$BREAKER_MAX_PER_DAY"
    printf 'transport today    %s/%s\n' "$BREAKER_DAY_TRANSPORT_FAILURES" "$BREAKER_MAX_TRANSPORT_PER_DAY"
    printf 'attempts total     %s\n' "$BREAKER_ATTEMPTS_TOTAL"
    printf 'last outcome       %s\n' "${BREAKER_LAST_OUTCOME:-none}"
    if [ -n "${BREAKER_REASON_TEXT:-}" ]; then
        printf '\n%s\n' "$BREAKER_REASON_TEXT"
    fi
}

# --- The decision, as a token -------------------------------------------------
# ⛔ THE ACTUATOR MUST ASK THROUGH THIS, NOT THROUGH breaker_may_attempt's EXIT CODE.
# Prints exactly one line: `AUTHORISED`, or `REFUSED <why>`. Anything else — including
# nothing at all — is a refusal. Found in pre-merge review 2026-09-25: an arithmetic
# error inside the decision ABANDONS the current command and bash carries on with the
# next line, so a caller shaped `breaker_may_attempt || exit 0` walked straight past a
# crashed decision into the login. Here a crash prints nothing, and "nothing" is not
# the word AUTHORISED.
breaker_verdict() {
    ( if breaker_may_attempt; then printf 'AUTHORISED\n'
      else printf 'REFUSED %s\n' "$BREAKER_REFUSAL"; fi ) 2>/dev/null
}

# --- One writer at a time ------------------------------------------------------
# ⛔ load -> modify -> write is not atomic, and two writers can UNDO A TRIP: A loads
# ARMED, B trips and writes, A writes ARMED over it (measured in review, 2026-09-25).
# The realistic case is a human running --reset while a sign-in is in flight. So every
# writer holds this lock around its whole load..write — the actuator (Task 9) takes the
# SAME file, and a --reset waits for an in-flight run to finish rather than racing it.
# ⚠ Do not call breaker_reset from inside a process that already holds the lock on
# another descriptor: flock locks are per open file, so it would wait on itself.
BREAKER_LOCK_FILE="${GV_RELOGIN_LOCK_FILE:-${HOME}/.local/state/gv-auto-relogin.lock}"
breaker_lock() {
    mkdir -p "$(dirname "$BREAKER_LOCK_FILE")" 2>/dev/null
    exec 8>"$BREAKER_LOCK_FILE" || { breaker_log "could not open the lock at ${BREAKER_LOCK_FILE}"; return 1; }
    flock -w "${GV_RELOGIN_LOCK_WAIT:-60}" 8 \
        || { breaker_log "another auto-relogin run holds ${BREAKER_LOCK_FILE}; not touching the breaker"; return 1; }
}

# ⛔ THE ONLY PLACE BREAKER_STATE BECOMES ARMED. If a future edit adds a second,
# the breaker has stopped being one.
breaker_reset() {
    breaker_lock || { echo "Breaker NOT changed: an auto-relogin run is in progress. Try --reset again when it finishes." >&2; return 1; }
    breaker_load
    printf 'Clearing:\n'
    printf '  state   %s\n' "$BREAKER_STATE"
    printf '  reason  %s\n' "${BREAKER_REASON:-none}"
    printf '  since   %s\n' "${BREAKER_TRIPPED_AT:-never}"
    BREAKER_STATE="ARMED"
    BREAKER_REASON=""
    BREAKER_REASON_TEXT=""
    BREAKER_TRIPPED_AT=""
    BREAKER_LAST_OUTCOME="reset"
    # ⚠ The COUNTERS are deliberately NOT cleared. A reset says "I have fixed the
    # account", not "today did not happen". Clearing them would let a human hand
    # back a full daily budget by typing one command, which is the loophole that
    # makes a daily limit decorative.
    breaker_write || return 1
    printf 'Breaker ARMED. Counters preserved: %s/%s credential attempts used today.\n' \
        "$BREAKER_DAY_CREDENTIAL_ATTEMPTS" "$BREAKER_MAX_PER_DAY"
}

# Direct invocation: --status / --reset only.
if [ "${BASH_SOURCE[0]}" = "${0}" ]; then
    case "${1:-}" in
        --status) breaker_status ;;
        --reset)  breaker_reset ;;
        *) echo "usage: $0 --status | --reset" >&2; exit 2 ;;
    esac
fi
