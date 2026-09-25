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

breaker_is_count() { case "${1:-}" in ''|*[!0-9]*) return 1 ;; *) return 0 ;; esac; }

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

# --- Load, and FAIL CLOSED --------------------------------------------------
breaker_load() {
    breaker_clear_fields

    if [ ! -r "$BREAKER_STATE_FILE" ]; then
        # ⛔ ABSENT IS NOT ARMED. Without the file there is no record of how many
        # sign-ins have already happened today, and bounding that count is the only
        # job this file has. An actuator that treats "I lost my memory" as "I may
        # proceed" has no rate limit at all — it has a rate limit that resets
        # whenever anything deletes a file.
        BREAKER_STATE="TRIPPED"
        BREAKER_REASON="state_missing"
        BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file is missing at ${BREAKER_STATE_FILE}. Without it there is no record of how many sign-ins have already been attempted today, so no further attempt can be authorised. A human must run: gv-auto-relogin.sh --reset"
    else
        # shellcheck disable=SC1090
        if ! . "$BREAKER_STATE_FILE" 2>/dev/null; then
            breaker_clear_fields
            BREAKER_STATE="TRIPPED"
            BREAKER_REASON="state_unreadable"
            BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file at ${BREAKER_STATE_FILE} could not be read. The attempt history is unknown, so no further attempt can be authorised. A human must run: gv-auto-relogin.sh --reset"
        fi
    fi

    case "$BREAKER_STATE" in
        ARMED|TRIPPED) ;;
        *)
            # An unrecognised state is not a state. Same rule as rule 3, applied to
            # our own file.
            BREAKER_STATE="TRIPPED"
            BREAKER_REASON="state_corrupt"
            BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file records an unrecognised state. The attempt history cannot be trusted. A human must run: gv-auto-relogin.sh --reset"
            ;;
    esac

    # ⛔ A COUNTER THAT IS NOT A NUMBER IS FAIL-OPEN IF LEFT ALONE: `[ abc -ge 3 ]` is
    # an error, an error is false, and a false "have we used the budget?" test
    # AUTHORISES. So an implausible counter is corruption, and corruption trips.
    local f
    for f in BREAKER_LAST_ATTEMPT_AT BREAKER_DAY_CREDENTIAL_ATTEMPTS \
             BREAKER_DAY_TRANSPORT_FAILURES BREAKER_ATTEMPTS_TOTAL; do
        if ! breaker_is_count "${!f}"; then
            BREAKER_STATE="TRIPPED"
            BREAKER_REASON="state_corrupt"
            BREAKER_REASON_TEXT="Auto-relogin is stopped because its breaker state file records a non-numeric ${f}. The attempt history cannot be trusted. A human must run: gv-auto-relogin.sh --reset"
            printf -v "$f" '%s' 0
        fi
    done

    # Roll the daily buckets. ⚠ Rolling the DAY does not arm a TRIPPED breaker —
    # the two are independent, and conflating them is how a permanent stop quietly
    # becomes a 24-hour pause.
    local today; today="$(breaker_today)"
    if [ "${BREAKER_DAY_BUCKET:-}" != "$today" ]; then
        BREAKER_DAY_BUCKET="$today"
        BREAKER_DAY_CREDENTIAL_ATTEMPTS=0
        BREAKER_DAY_TRANSPORT_FAILURES=0
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

# ⛔ THE ONLY PLACE BREAKER_STATE BECOMES ARMED. If a future edit adds a second,
# the breaker has stopped being one.
breaker_reset() {
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
