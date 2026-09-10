#!/usr/bin/env bash
# =============================================================================
# GV session alarm — TRANSPORT for a signal the service already produces.
#
# ⛔ THIS SCRIPT DETECTS NOTHING. Two correct detectors already exist: the
# service's own [ERR] GVApi log lines, and browserRefreshOutcome on
# GET /api/gvbridge/status. On 2026-09-09 the service composed a correct,
# complete, correctly-worded alert SIX TIMES over 2h10m and it reached a journal
# nobody was reading. The gap was never detection. Do not add a third detector
# here; if this script ever seems to need one, the fix belongs in the service.
#
# Run by gv-session-alarm.timer every 5 minutes. Five minutes is generous on
# purpose: the underlying condition persists for hours and only changes when the
# 20-minute cron or the recovery ladder attempts a refresh. This is not chasing
# a transient.
#
# EXIT CODES — they are the systemd-visible half of the contract:
#   0  the cycle completed: a conclusion was reached and everything that had to
#      be delivered was delivered. Includes "the service is down and we said so."
#   1  the cycle did NOT complete: config missing, a notify failed, state could
#      not be persisted, or the heartbeat refresh failed.
# =============================================================================

# ⚠ NOT `set -e`. A script whose job is to report failures must not die on the
# first one — an early exit is exactly the silence this alarm exists to prevent.
# Every failure below is checked explicitly and turned into a journal line and
# an exit code.
set -uo pipefail

# ⚠ PIN THE LOCALE. `${s:0:N}` is CHARACTER-based under a UTF-8 locale and BYTE-based
# under C/POSIX, and a systemd USER unit inherits whatever the user manager has —
# commonly nothing, so LANG is unset and the C locale applies. Measured 2026-09-09:
# the same 150-character action truncated to 300 valid UTF-8 bytes under C.UTF-8 and to
# 200 bytes of INVALID UTF-8 under C, cut mid-character. jq replaced the broken
# sequence with U+FFFD — mojibake in a delivered alert — and a stricter encoder would
# have refused the message outright, which is the silent non-delivery this whole script
# exists to prevent. Latent while every action string is ASCII; this file's own copy is
# full of — ⚠ ⛔, and truncate_action exists FOR the future edit.
export LC_ALL="${LC_ALL:-C.UTF-8}"

VERSION="1"
SOURCE_NAME="rotaryphone"

now_utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
log()  { printf '%s gv-session-alarm[%s]: %s\n' "$(now_utc)" "$$" "$*" >&2; }
die()  { log "FATAL: $*"; exit 1; }

# --- Configuration -----------------------------------------------------------
# ⛔ A systemd USER timer inherits NO login-shell environment. This script
# SOURCES its configuration explicitly and never relies on inheritance.
#
# Measured, aitrader 2026-08-14: a refusal journaled correctly and notified
# NOBODY, because the credentials lived only in ~/.aitrader-env which only the
# cron wrapper sourced. The gateway, the bearer, the call site and the routing
# were all proved healthy the same hour by a positive control.
#
# ⛔ And aitrader's "unset => no-op, not error" rule is CORRECT for an optional
# third store on a trading bot and EXACTLY WRONG for the only alarm on a phone.
# Silence is not a valid state here. Missing configuration is a hard, non-zero,
# journaled failure — nothing below is best-effort.
ENV_FILE="${GV_ALARM_ENV_FILE:-${HOME}/.rotaryphone-env}"
STATE_FILE="${GV_ALARM_STATE_FILE:-${HOME}/.local/state/gv-session-alarm.state}"

if [ "${1:-}" = "--print-config" ]; then
    # Side-effect free, and it runs BEFORE the env file is required so the
    # deploy's post-install gate can call it on a box that has no token yet.
    printf 'gv-session-alarm version=%s\n' "$VERSION"
    printf '  script     %s\n' "$0"
    printf '  env_file   %s (%s)\n' "$ENV_FILE" \
        "$([ -r "$ENV_FILE" ] && echo readable || echo MISSING)"
    printf '  state_file %s (%s)\n' "$STATE_FILE" \
        "$([ -r "$STATE_FILE" ] && echo present || echo absent)"
    printf '  status_url %s\n' "${GV_ALARM_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"
    exit 0
fi

[ -r "$ENV_FILE" ] || die "${ENV_FILE} is missing or unreadable. This alarm has no credentials and cannot notify anyone. Exiting NON-ZERO so the unit FAILS and systemd records it — a silent no-op here would reproduce the exact condition this alarm exists to detect."

# shellcheck disable=SC1090
. "$ENV_FILE" || die "could not source ${ENV_FILE}"

for required in ROTARYPHONE_GATEWAY_URL ROTARYPHONE_GATEWAY_TOKEN; do
    if [ -z "${!required:-}" ]; then
        die "${required} is unset or empty after sourcing ${ENV_FILE}. Refusing to run half-configured."
    fi
done

STATUS_URL="${GV_ALARM_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"
# ⚠ THE PORT AND PROFILE ARE INTERPOLATED HERE, and the C# interpolates them too
# (`{Port}` <- _config.ChromeCdpPort). The copy-drift guard's fragments deliberately stop
# just SHORT of the number — "CHROME WAS UNREACHABLE on CDP port" — so interpolation
# cannot break the quotation. The consequence is that the number itself is NOT pinned by
# any guard: a box configured with a different ChromeCdpPort would be told to check the
# wrong port, at the worst possible moment, with every check green. One variable rather
# than five literals is what makes that a single edit instead of a hunt.
# ⛔ These are the alarm's BEST KNOWN VALUES, not authority. The service's config is
# authoritative; if they ever disagree, the service wins and this is the bug.
GV_CDP_PORT="${GV_ALARM_CDP_PORT:-9224}"
# ⛔ THE THREE VERBATIM HEREDOCS BELOW KEEP THEIR LITERAL 9224, and that is correct:
# the number is inside a QUOTATION of the service, and editing text inside a quotation
# to make it agree with local config is how a quotation stops being one. Instead, a
# disagreement is made LOUD here — the operator is told the advice they are about to
# read names a different port than the one configured.
if [ "$GV_CDP_PORT" != "9224" ]; then
    log "WARNING: GV_ALARM_CDP_PORT=${GV_CDP_PORT}, but the service wording quoted in this alarm names 9224. The quoted text is reproduced verbatim and is NOT rewritten; read the port from the service's own config, not from the quotation."
fi
GATEWAY_URL="${ROTARYPHONE_GATEWAY_URL%/}"

for tool in curl jq; do
    command -v "$tool" >/dev/null 2>&1 || die "${tool} is not on PATH. Refusing to run: without it this script cannot tell a healthy session from a dead one, and would report the wrong answer rather than none."
done

# --- State -------------------------------------------------------------------
# Persisted so a transition survives a reboot, a service restart and a redeploy.
# In particular INCIDENT_THREAD_KEY must survive, or the all-clear opens a NEW
# thread instead of closing the one the owner was notified about — and a
# RESOLVED that does not thread under its alert is invisible, because RESOLVED
# is deliberately routed to the quiet lane.
LAST_POSTED_CONDITION=""
INCIDENT_THREAD_KEY=""
INCIDENT_OPENED_AT=""
PENDING_CONDITION=""
PENDING_POLLS=0
# ⛔ SEPARATE FROM THE KEY, and it has to be. The key used to be persisted the moment
# it was generated, before the root post was attempted — so a gateway refusing at the
# instant an incident opened left the key on disk, and the next cycle saw a non-empty
# key and never re-attempted the root. The result was an alert threaded under a root
# that does not exist, and a later RESOLVED — which is deliberately routed to the QUIET
# lane, and is only safe there because it threads under an alert the owner saw —
# replying into nothing. A gateway that is down when an incident opens is a correlated
# failure, not an exotic one. Found in pre-merge review 2026-09-09.
THREAD_ROOT_DELIVERED=0

if [ -r "$STATE_FILE" ]; then
    # shellcheck disable=SC1090
    . "$STATE_FILE" || log "WARNING: ${STATE_FILE} exists but could not be sourced; treating as empty"
fi

write_state() {
    local dir; dir="$(dirname "$STATE_FILE")"
    mkdir -p "$dir" 2>/dev/null || { log "could not create ${dir}"; return 1; }
    # Atomic: a reader (or the next run) sees the whole old file or the whole new
    # one, never a half-written one. Same reasoning as the installer's
    # install_atomic, against the same 5-minute timer.
    {
        printf '# written %s by gv-session-alarm v%s\n' "$(now_utc)" "$VERSION"
        printf 'LAST_POSTED_CONDITION=%q\n' "$LAST_POSTED_CONDITION"
        printf 'INCIDENT_THREAD_KEY=%q\n'   "$INCIDENT_THREAD_KEY"
        printf 'INCIDENT_OPENED_AT=%q\n'    "$INCIDENT_OPENED_AT"
        printf 'PENDING_CONDITION=%q\n'     "$PENDING_CONDITION"
        printf 'PENDING_POLLS=%q\n'         "$PENDING_POLLS"
        printf 'THREAD_ROOT_DELIVERED=%q\n' "$THREAD_ROOT_DELIVERED"
    } > "${STATE_FILE}.new" || { rm -f "${STATE_FILE}.new"; log "could not write ${STATE_FILE}.new"; return 1; }
    mv -f "${STATE_FILE}.new" "$STATE_FILE" || { rm -f "${STATE_FILE}.new"; log "could not replace ${STATE_FILE}"; return 1; }
    return 0
}

# --- Poll --------------------------------------------------------------------
poll_ok=0
status_body=""
status_http=""

resp="$(curl -sS --max-time 10 -w $'\n%{http_code}' "$STATUS_URL" 2>/dev/null)"
curl_rc=$?
if [ "$curl_rc" -eq 0 ]; then
    status_http="${resp##*$'\n'}"
    status_body="${resp%$'\n'*}"
    [ "$status_http" = "200" ] && poll_ok=1
fi

# --- Classify ----------------------------------------------------------------
# ⛔ The mapping is per CONDITION, not per message. dedupe_key ignores severity,
# title and thread_key, so a key chosen per message collapses two different
# alerts into one.
outcome="UNPOLLED"
age_seconds=""
if [ "$poll_ok" -eq 1 ]; then
    outcome="$(printf '%s' "$status_body" | jq -r '.browserRefreshOutcome // "FIELD_MISSING"' 2>/dev/null)" \
        || outcome="UNPARSEABLE"
    age_seconds="$(printf '%s' "$status_body" | jq -r '.browserSessionAgeSeconds // ""' 2>/dev/null)"
fi

case "$outcome" in
    UNPOLLED)      condition="service_unreachable" ;;
    Stale)         condition="browser_stale" ;;
    Unreachable)   condition="browser_unreachable" ;;
    NotAttempted)  condition="not_attempted" ;;
    Succeeded)     condition="ok" ;;
    TornDown)      condition="ignore" ;;
    FIELD_MISSING) condition="field_missing" ;;
    *)             condition="unknown_outcome" ;;
esac

# ⭐ field_missing and unknown_outcome are NOT in the spec's §4.3 table, and both
# must exist rather than folding into "ok".
#
#   field_missing  — the box is running a build without browserRefreshOutcome.
#                    That is not hypothetical: merged != deployed != INSTALLED,
#                    and this arc's own §0.9 found a deploy path that reports
#                    success while changing nothing. Reading a missing field as
#                    healthy would make the alarm mute in precisely the state
#                    where the deploy has already failed once.
#   unknown_outcome — a future enum member. It must not read as green.
#
# Both are WARN: something is wrong with the instrument, not (yet) with the
# session, and the operator action is to look at the deploy rather than at
# Google.

# NotAttempted is only worth waking someone for if it PERSISTS — a single tick
# during startup is normal. Three consecutive polls is 15 minutes.
MIN_POLLS_TO_POST=1
[ "$condition" = "not_attempted" ] && MIN_POLLS_TO_POST=3

if [ "$condition" = "$PENDING_CONDITION" ]; then
    PENDING_POLLS=$((PENDING_POLLS + 1))
else
    PENDING_CONDITION="$condition"
    PENDING_POLLS=1
fi

log "outcome=${outcome} condition=${condition} age=${age_seconds:-none} polls=${PENDING_POLLS} last_posted=${LAST_POSTED_CONDITION:-none}"

# --- Gateway ------------------------------------------------------------------
# MEASURED CAP: 200 CHARACTERS on `action`. Over-long is a 422 that DELIVERS
# NOTHING, silently. Our natural action fits comfortably; the cap exists because
# a future edit will not, and because the body must never be spliced into it.
ACTION_MAX="${GV_ALARM_ACTION_MAX:-200}"

NOTIFY_ATTEMPTED=0
NOTIFY_FAILED=0

# Truncate to (ACTION_MAX - 3) and append ASCII "...", NOT a one-character "…".
# The measured cap is in characters, but we do not control what the gateway
# counts on the far side of a proxy, and "…" is 1 character / 3 bytes. An
# ASCII-only marker is <= the cap under BOTH readings. Getting this wrong turns
# a truncation that was supposed to prevent a 422 into one that causes it.
truncate_action() {
    local s="$1"
    if [ "${#s}" -le "$ACTION_MAX" ]; then printf '%s' "$s"; return 0; fi
    printf '%s...' "${s:0:$((ACTION_MAX - 3))}"
    return 0
}

# post_notify SEVERITY TITLE BODY ACTION DEDUPE_KEY THREAD_KEY
post_notify() {
    local severity="$1" title="$2" body="$3" action="$4" dedupe="$5" thread="$6"
    local payload resp rc http out

    # MEASURED: `action` and `timestamp` are SILENTLY DROPPED on severity=info.
    # So anything whose action matters goes on `warning`, never `info` — and we
    # do not SEND an action on info, rather than sending one that vanishes and
    # believing it arrived.
    if [ "$severity" = "info" ] && [ -n "$action" ]; then
        log "dropping action on an info message by design (the gateway would drop it silently): ${action}"
        action=""
    fi

    payload="$(jq -nc \
        --arg source     "$SOURCE_NAME" \
        --arg severity   "$severity" \
        --arg title      "$title" \
        --arg body       "$body" \
        --arg action     "$(truncate_action "$action")" \
        --arg dedupe_key "$dedupe" \
        --arg thread_key "$thread" \
        --arg timestamp  "$(now_utc)" \
        '{source:$source, severity:$severity, title:$title, body:$body,
          dedupe_key:$dedupe_key, thread_key:$thread_key, timestamp:$timestamp}
         + (if $action == "" then {} else {action:$action} end)')" \
        || { log "NOTIFY FAILED: could not build the payload for ${dedupe}"; NOTIFY_ATTEMPTED=$((NOTIFY_ATTEMPTED+1)); NOTIFY_FAILED=$((NOTIFY_FAILED+1)); return 1; }

    NOTIFY_ATTEMPTED=$((NOTIFY_ATTEMPTED + 1))

    resp="$(printf 'header = "Authorization: Bearer %s"\n' "$ROTARYPHONE_GATEWAY_TOKEN" \
        | curl -sS --max-time 15 -X POST --config - \
        -H 'Content-Type: application/json' \
        -w $'\n%{http_code}' \
        --data-binary "$payload" \
        "${GATEWAY_URL}/v1/notify" 2>&1)"
    rc=$?
    http="${resp##*$'\n'}"
    out="${resp%$'\n'*}"

    if [ "$rc" -ne 0 ]; then
        NOTIFY_FAILED=$((NOTIFY_FAILED + 1))
        log "NOTIFY FAILED: curl exit ${rc} for severity=${severity} dedupe=${dedupe}. Transport error, nothing delivered. Detail: ${out}"
        return 1
    fi

    case "$http" in
        2*)
            log "notify DELIVERED http=${http} severity=${severity} dedupe=${dedupe} thread=${thread}"
            return 0
            ;;
        *)
            NOTIFY_FAILED=$((NOTIFY_FAILED + 1))
            log "NOTIFY FAILED: http=${http} severity=${severity} dedupe=${dedupe}. NOTHING WAS DELIVERED."
            log "gateway said: ${out}"
            # ⛔ Deliberately NOT retried with a reshaped message. A 422 names its
            # own limit in the body above, which is why the body is journaled
            # verbatim — but a script that rewrites its own message on refusal
            # delivers something nobody tested, and hides the defect that caused
            # the refusal. The heartbeat refresh is suppressed instead, so the
            # gateway raises the missing-check alert on our behalf.
            return 1
            ;;
    esac
}

# --- Copy ---------------------------------------------------------------------
# ⛔ EVERY BLOCK-QUOTED LINE BELOW IS THE SERVICE'S OWN WORDING, COPIED VERBATIM.
# Do not improve it, re-punctuate it, or shorten it. It is quoted precisely
# because the service already gets this right: on 2026-09-09 it composed a
# correct, complete, correctly-worded alert six times — correct severity
# vocabulary, the exact remedy, and the reassurance an operator most needs
# before panicking, that the working set survived. What failed was transport.
#
# ⚠ These strings are ALSO in C#. The copy-drift guard fails the build if the
# two ever diverge — deploy/tests/check-alarm-copy-drift.sh, and the same
# comparison as a unit test in AlarmCopyDriftTests.cs so it runs on every
# platform. If you edit one, edit both.
#   browser_stale       <- GVApiAdapter.cs REJECTED-a-cookie-set line
#   browser_unreachable <- GVApiAdapter.cs CHROME WAS UNREACHABLE case
#   not_attempted       <- GVApiAdapter.cs NEVER CONSULTED default case

body_for() {
    case "$1" in
      browser_stale)
        cat <<'QUOTE'
The service reported this, in its own words:

> GVApi: REJECTED a cookie set from refresh-from-browser — Google refused it. The working on-disk set was NOT overwritten. If the source is the box's Chrome, that session is dead: ACTION: re-login at voice.google.com.

**The phone still works.** It is running on a credential it can renew but cannot re-derive, and there is
nothing underneath that. Recovery has no floor below a working browser session.
QUOTE
        ;;
      browser_unreachable)
        cat <<'QUOTE'
Chrome could not be reached at all, so the Google login was **never tested**. The service's own words:

> GVApi: all cookie-recovery rungs failed and CHROME WAS UNREACHABLE on CDP port 9224 — our own rotation chain lapsed and the browser fallback could not be tried, so the Google login was never tested. ACTION: confirm Chrome is running (pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome") BEFORE touching the Google login; the session may be perfectly fine.

⚠ `browserSessionStale` reads **false** in this state, identically to a healthy one. That is why this
alarm reads `browserRefreshOutcome` instead.
QUOTE
        ;;
      not_attempted)
        cat <<'QUOTE'
The browser was never consulted, for 15 minutes or more. The service's own words:

> GVApi: all cookie-recovery rungs failed and the browser was NEVER CONSULTED (no CDP extractor wired, or no cookie store). Our rotation chain lapsed and nothing tested the Google login. ACTION: check the service's CDP wiring and that Chrome is up on port 9224; do NOT assume the login is dead.
QUOTE
        ;;
      service_unreachable)
        cat <<QUOTE
The alarm could not reach \`${STATUS_URL}\`. No status was obtained, so nothing is known about the Google
Voice session — **this is not a report that the session is fine.**

This is the case in-process detection structurally cannot cover: a service that is not running cannot
report that it is not running.
QUOTE
        ;;
      field_missing)
        cat <<QUOTE
\`${STATUS_URL}\` answered, but its payload has **no \`browserRefreshOutcome\` field**. The box is running a
build that predates it.

⚠ This alarm cannot tell a healthy session from a dead one against this build, and it is reporting that
rather than defaulting to green. **Merged ≠ deployed ≠ INSTALLED.**
ACTION: check what is actually deployed on the box.
QUOTE
        ;;
      unknown_outcome)
        cat <<QUOTE
\`${STATUS_URL}\` returned \`browserRefreshOutcome\` = **${outcome}**, which this alarm does not recognise.

Treated as a fault rather than as healthy, deliberately: a new enum member must not read as green.
QUOTE
        ;;
    esac
}

title_for() {
    # ⚠ NO SEVERITY IN THE TITLE. The gateway prepends its own severity_prefix();
    # a title carrying its own renders it twice, with the two vocabularies free
    # to disagree. Measured on the sibling project: "ℹ️ [INFO] [pmtrader] ℹ️ INFO · …".
    case "$1" in
      browser_stale)       echo "[${SOURCE_NAME}] GV session — signed out, re-login needed" ;;
      browser_unreachable) echo "[${SOURCE_NAME}] GV session — Chrome is gone, login untested" ;;
      not_attempted)       echo "[${SOURCE_NAME}] GV session — browser never consulted" ;;
      service_unreachable) echo "[${SOURCE_NAME}] GV session — the service is not answering" ;;
      field_missing)       echo "[${SOURCE_NAME}] GV session — the box is running an older build" ;;
      unknown_outcome)     echo "[${SOURCE_NAME}] GV session — unrecognised outcome" ;;
      ok)                  echo "[${SOURCE_NAME}] GV session — recovered" ;;
    esac
}

action_for() {
    # Every one of these is well inside 200 characters. truncate_action is the
    # guard for the edit that changes that, not for these.
    case "$1" in
      browser_stale)       echo "re-login at voice.google.com in the box's Chrome (CDP 9224)" ;;
      browser_unreachable) echo 'pgrep -f "user-data-dir=$HOME/.config/gv-bridge-chrome"; if absent: ~/bin/gv-bridge-ensure.sh' ;;
      not_attempted)       echo "check the CDP wiring and that Chrome answers on port 9224" ;;
      service_unreachable) echo "systemctl status rotary-phone on radio" ;;
      # ⛔ NOT `sha256sum …/RotaryPhoneController.Server`, which is what this line
      # said until 2026-09-10. That file is the SDK's generic apphost: measured the
      # same day, commit 1c8a22c (no browserRefreshOutcome) and the build that has it
      # produce a BYTE-IDENTICAL apphost, f500cf157697de69, while the .dll beside it
      # differs. So the advice sent an operator to a file that cannot distinguish the
      # two builds — an ACTION that yields a green light on the stale one, inside the
      # alert whose whole subject is a stale build.
      #
      # And a file hash is the wrong quantity anyway: field_missing means the RUNNING
      # PROCESS predates the field, which a hash of anything on disk cannot show. A
      # deploy can land the new build and leave the old one running — that is exactly
      # this box's state as this was written. Compare the two timestamps instead.
      field_missing)       echo "the RUNNING service predates the field. Compare: systemctl show rotary-phone -p ExecMainStartTimestamp   vs   stat -c %y /opt/rotary-phone/RotaryPhoneController.Server.dll" ;;
      unknown_outcome)     echo "read the journal: journalctl --user -u gv-session-alarm -n 50" ;;
      *)                   echo "" ;;
    esac
}

severity_for() {
    case "$1" in
      browser_stale|browser_unreachable|service_unreachable) echo "alert" ;;
      not_attempted|field_missing|unknown_outcome)           echo "warning" ;;
      ok)                                                    echo "info" ;;
    esac
}

# --- Incident threading -------------------------------------------------------
# ⛔ The thread key names the INCIDENT and is stable across its whole life — never
# a timestamp of the message, never a status, never the condition. One sign-out
# is ONE thread from detection to all-clear, and the RESOLVED replies INTO it.
#
# ⚠ This is load-bearing, not cosmetic. RESOLVED is routed to the quiet lane, and
# it is only safe to be quiet BECAUSE it threads under an alert the owner was
# already notified about. A RESOLVED that opens a new thread is invisible, and
# the owner is left believing an incident is still open.
#
# ⚠ RETRYABLE. The key is minted only when there is not one already, so a retry after
# a failed root post reuses the SAME key and the thread identity never moves. The
# dedupe_key is derived from that key, so a gateway that did receive an earlier attempt
# collapses the retry rather than showing two roots.
open_incident_thread() {
    if [ -z "$INCIDENT_THREAD_KEY" ]; then
        INCIDENT_THREAD_KEY="${SOURCE_NAME}-gv-session-$(date -u +%Y%m%dT%H%M%SZ)"
        INCIDENT_OPENED_AT="$(now_utc)"
    else
        log "re-attempting the incident thread root for ${INCIDENT_THREAD_KEY} — the previous attempt was not delivered, and an alert threaded under a root that does not exist leaves its RESOLVED invisible."
    fi
    if post_notify "info" \
        "[${SOURCE_NAME}] 🧵 GV session — browser session incident" \
        "Subject: the box's Chrome Google Voice session (profile \`~/.config/gv-bridge-chrome\`, CDP ${GV_CDP_PORT}) on \`radio\`.
Closes when: \`browserRefreshOutcome\` returns \`Succeeded\` after an owner re-login.
Identifiers: thread \`${INCIDENT_THREAD_KEY}\`, status \`${STATUS_URL}\`, opened ${INCIDENT_OPENED_AT}." \
        "" \
        "${SOURCE_NAME}-gv-session-thread-${INCIDENT_THREAD_KEY}" \
        "$INCIDENT_THREAD_KEY"
    then
        THREAD_ROOT_DELIVERED=1
    fi
}

# --- Decide and post ----------------------------------------------------------
# POST ONLY ON TRANSITION. The owner's chat policy forbids reposting an unchanged
# state: three "nothing to report" titles in a row means the threshold is wrong,
# not that three things happened. The underlying condition here persists for
# HOURS, so an every-tick alarm would be almost entirely repetition.
if [ "$condition" = "ignore" ]; then
    # ⚠ KNOWN AND ACCEPTED: TornDown while an incident is OPEN posts nothing and leaves
    # the incident open. That is right for a restart — the teardown is not a fault, and
    # the incident is still true — but a PERMANENT teardown means no all-clear ever
    # arrives and the thread stays open forever. Not resolved here because "the service
    # stopped and is never coming back" is indistinguishable from "it is restarting"
    # from inside a 5-minute poll; the next real poll after a restart reclassifies, and
    # the gateway dead-man covers a service that never returns. Raised in pre-merge
    # review 2026-09-09; recorded rather than guessed at.
    log "outcome=TornDown — service teardown, not a fault. Nothing posted."
elif [ "$condition" = "$LAST_POSTED_CONDITION" ]; then
    log "condition unchanged since the last post (${condition}); nothing posted."
elif [ "$PENDING_POLLS" -lt "$MIN_POLLS_TO_POST" ]; then
    log "condition=${condition} seen ${PENDING_POLLS}/${MIN_POLLS_TO_POST} consecutive polls; not posting yet."
elif [ "$condition" = "ok" ]; then
    if [ -n "$INCIDENT_THREAD_KEY" ]; then
        # RESOLVED — quiet, and it MUST reply into the open thread.
        post_notify "info" \
            "$(title_for ok)" \
            "$(now_utc) · RESOLVED
\`browserRefreshOutcome\` is **Succeeded**: cookies pulled from the box's Chrome passed a live probe against
Google. The session opened at ${INCIDENT_OPENED_AT} is closed.
Action: none." \
            "" \
            "${SOURCE_NAME}-gv-session-resolved-${INCIDENT_THREAD_KEY}" \
            "$INCIDENT_THREAD_KEY"
        if [ "$NOTIFY_FAILED" -eq 0 ]; then
            LAST_POSTED_CONDITION="ok"
            INCIDENT_THREAD_KEY=""
            INCIDENT_OPENED_AT=""
            THREAD_ROOT_DELIVERED=0
        fi
    else
        # Healthy, and no incident was ever open. Post nothing at all.
        log "healthy, no open incident; nothing to post."
        LAST_POSTED_CONDITION="ok"
    fi
else
    # ⚠ THE ALERT'S dedupe_key IS PER-CONDITION, NOT PER-INCIDENT, and that is deliberate
    # (spec §4.4: "Keys chosen per condition, never per message"). Note the asymmetry with
    # the thread root and the RESOLVED, whose keys DO embed INCIDENT_THREAD_KEY: if the
    # real gateway dedupes with any persistence window, the SAME condition recurring weeks
    # later would be silently dropped. The stub models no dedupe at all, so this is
    # untested here by construction.
    # ⛔ Confirm against the real gateway in Task 17, and correct it if a genuine
    # recurrence is swallowed. Raised in pre-merge review 2026-09-09.

    # Open the thread, OR re-attempt a root whose earlier post was refused.
    [ "$THREAD_ROOT_DELIVERED" = "1" ] || open_incident_thread
    post_notify \
        "$(severity_for "$condition")" \
        "$(title_for "$condition")" \
        "$(now_utc) · ${condition}
$(body_for "$condition")" \
        "$(action_for "$condition")" \
        "${SOURCE_NAME}-gv-session-${condition}" \
        "$INCIDENT_THREAD_KEY"
    [ "$NOTIFY_FAILED" -eq 0 ] && LAST_POSTED_CONDITION="$condition"
fi

# --- Dead-man -----------------------------------------------------------------
# POST /v1/heartbeat registers/refreshes a check; the gateway raises an alert on
# our route if it is not refreshed within `grace`.
#
# ⚠ GRACE MUST EXCEED THE TIMER INTERVAL WITH REAL MARGIN. The timer is 5m; grace
# is 30m — six intervals. Ordinary jitter, a slow poll, a boot, or one missed run
# must not raise "the alarm is dead", because a dead-man that cries wolf is a
# dead-man that gets muted, and then nothing is watching anything.
#
# ⚠ LOWER-CASE ONLY. `grace` is case-sensitive: "30M" is a 422, and a 422 here
# means THE CHECK IS NEVER REGISTERED — no dead-man at all, silently.
HEARTBEAT_CHECK_ID="${GV_ALARM_HEARTBEAT_CHECK_ID:-gv-session-alarm}"
HEARTBEAT_SCHEDULE="${GV_ALARM_HEARTBEAT_SCHEDULE:-5m}"
HEARTBEAT_GRACE="${GV_ALARM_HEARTBEAT_GRACE:-30m}"

refresh_heartbeat() {
    local resp rc http out payload
    payload="$(jq -nc \
        --arg source   "$SOURCE_NAME" \
        --arg check_id "$HEARTBEAT_CHECK_ID" \
        --arg schedule "$HEARTBEAT_SCHEDULE" \
        --arg grace    "$HEARTBEAT_GRACE" \
        '{source:$source, check_id:$check_id, schedule:$schedule, grace:$grace}')" || return 1

    resp="$(printf 'header = "Authorization: Bearer %s"\n' "$ROTARYPHONE_GATEWAY_TOKEN" \
        | curl -sS --max-time 15 -X POST --config - \
        -H 'Content-Type: application/json' \
        -w $'\n%{http_code}' \
        --data-binary "$payload" \
        "${GATEWAY_URL}/v1/heartbeat" 2>&1)"
    rc=$?
    http="${resp##*$'\n'}"
    out="${resp%$'\n'*}"

    if [ "$rc" -ne 0 ]; then
        log "HEARTBEAT FAILED: curl exit ${rc}. Detail: ${out}"
        return 1
    fi
    case "$http" in
        2*) log "heartbeat refreshed http=${http} schedule=${HEARTBEAT_SCHEDULE} grace=${HEARTBEAT_GRACE}"; return 0 ;;
        *)  log "HEARTBEAT FAILED: http=${http}. THERE IS NO DEAD-MAN until this succeeds — check grace/schedule case (lower-case only). Gateway said: ${out}"
            return 1 ;;
    esac
}

# --- Persist, then decide whether we have earned the heartbeat ----------------
state_ok=1
write_state || state_ok=0

if [ "$NOTIFY_FAILED" -gt 0 ]; then
    log "NOT refreshing the heartbeat: ${NOTIFY_FAILED}/${NOTIFY_ATTEMPTED} notifications this cycle were NOT delivered. The gateway will raise the missing-check alert on our behalf, through a path that is not the one that just broke."
    exit 1
fi
if [ "$state_ok" -eq 0 ]; then
    log "NOT refreshing the heartbeat: state could not be persisted, so the next run cannot tell a transition from a repeat and would either spam or go silent."
    exit 1
fi

# ⚠ A FAILED POLL DOES NOT SUPPRESS THE HEARTBEAT — see the plan's Task 11.
# "The service is down" is a condition this alarm exists to REPORT. Reporting it
# successfully is the alarm working, and raising "the alarm is dead" on top of a
# real outage is how an alarm gets muted.
if [ "$poll_ok" -ne 1 ]; then
    log "the status poll failed and was reported; the cycle still COMPLETED, so the heartbeat is refreshed."
fi

refresh_heartbeat || exit 1
exit 0
