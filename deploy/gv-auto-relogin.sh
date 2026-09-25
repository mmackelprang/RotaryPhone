#!/usr/bin/env bash
# =============================================================================
# GV auto-relogin — the ACTUATOR. Handles the routine case silently so the owner
# is not in the loop; escalates through the PR #85 alarm when it cannot.
#
#   gv-auto-relogin.sh                 one cycle (what the timer runs)
#   gv-auto-relogin.sh --status        the breaker's state, and whether a driver is installed
#   gv-auto-relogin.sh --reset         a HUMAN re-arms the breaker (waits up to 60 s for the lock)
#   gv-auto-relogin.sh --print-config  resolved configuration; never a credential value
#
# ⛔ THIS SCRIPT IS SUBORDINATE TO ITS BREAKER. Read gv-auto-relogin-breaker.sh
# first. Nothing here may attempt a sign-in the breaker did not authorise, and the
# only state changes made here go through the breaker's own functions.
#
# ⛔ IT DOES NOT SIGN IN. The owner-written driver, gv-relogin-signin.py, does —
# see docs/gv-relogin-driver-contract.md for the interface this file holds it to.
# This file decides WHETHER an attempt is allowed, hands the driver the credential on
# STDIN, reads back ONE verdict word, and then verifies the outcome itself. A driver
# that says SIGNED_IN has made a claim; the forced navigation and Google's own
# acceptance of the cookies (Task 11) are the outcome.
#
# ⛔ IT NEVER CLEARS THE PROFILE AND NEVER LAUNCHES A BROWSER. Spec §5: same-profile
# re-login is materially safer than a fresh-device sign-in. Google already knows
# this device, profile and IP; a fresh browser converts routine re-auth into an
# unrecognised-device sign-in, which is far more likely to be challenged.
#
# ⛔ IT DETECTS NOTHING ABOUT THE SESSION. The service already does that, and the
# alarm already transports it. This script reads browserRefreshOutcome and acts;
# it does not form its own opinion about whether the session is healthy.
#
# ⭐ NO DRIVER, NO ACTION. Until the owner installs gv-relogin-signin.py beside this
# file, every cycle logs that auto-relogin is not installed and exits 0 without
# touching the breaker, the browser, or the credential file. That is the safe
# resting state, not a fault, and it raises no alarm.
#
# EXIT CODES:
#   0  the cycle completed — including "the breaker refused and we said so",
#      "a human sign-in is in progress", and "no driver installed"
#   1  the cycle did not complete: a tool is missing, state could not be written,
#      or the lock file could not be opened
# =============================================================================
# ⚠ NOT `set -e`. Same reasoning as the alarm: a script whose failures must be
# recorded cannot die on the first one.
set -uo pipefail

VERSION="1"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

STATUS_URL="${GV_RELOGIN_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"
REFRESH_URL="${STATUS_URL%/status}/cookies/refresh-from-browser"
ACCOUNT_FILE="${GV_RELOGIN_ACCOUNT_FILE:-/opt/rotary-phone/gv-account.conf}"
CDP_PORT="${GV_RELOGIN_CDP_PORT:-9224}"
CDP_HELPER="${GV_RELOGIN_CDP_HELPER:-${HERE}/gv-cdp.py}"
DRIVER="${GV_RELOGIN_SIGNIN_DRIVER:-${HERE}/gv-relogin-signin.py}"
# The reachable-reauth assist's state (PR #89, spec 2026-09-25-gv-reachable-reauth §5.1).
# While it says a HUMAN is mid sign-in, this actuator stands down.
ASSIST_STATE_FILE="${GV_RELOGIN_ASSIST_STATE_FILE:-${HOME}/.local/state/gv-reauth-assist.state}"
# ⚠ DERIVED FROM THE SPIKE, NOT CHOSEN: 15 s per navigation x2 (sign-in page, chooser ->
# password page) + 30 s submit -> settled, from docs/spikes/2026-09-09-gv-signin-cdp-recording.md
# "Timings", is 60 s of budget the driver may legitimately spend. This doubles it for
# process start and CDP round trips. A driver still running at the limit is killed and
# its outcome is UNRECOGNISED — a TRIP, never a retry.
DRIVER_TIMEOUT="${GV_RELOGIN_DRIVER_TIMEOUT:-120}"
# The one URL the outcome check navigates to (spec §5 step 6; spike row 5).
VERIFY_URL="https://voice.google.com/u/0/voicemail"

now_utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
log() { printf '%s gv-auto-relogin[%s]: %s\n' "$(now_utc)" "$$" "$*" >&2; }
die() { log "FATAL: $*"; exit 1; }

# shellcheck source=deploy/gv-auto-relogin-breaker.sh
. "${HERE}/gv-auto-relogin-breaker.sh" \
    || die "could not source the breaker at ${HERE}/gv-auto-relogin-breaker.sh. Refusing to run unrestrained."

driver_line() {
    if [ -f "$DRIVER" ]; then printf 'driver             %s (installed)\n' "$DRIVER"
    else printf 'driver             %s (NOT INSTALLED — auto-relogin does nothing until the owner supplies it)\n' "$DRIVER"; fi
}

case "${1:-}" in
    --status) breaker_status; driver_line; exit 0 ;;
    --reset)  breaker_reset;  exit $? ;;
    --print-config)
        # Side-effect free and runs before anything is required, so the deploy's
        # post-install gate can interrogate an unconfigured install.
        printf 'gv-auto-relogin version=%s\n' "$VERSION"
        printf '  script       %s\n' "$0"
        printf '  breaker      %s\n' "${HERE}/gv-auto-relogin-breaker.sh"
        printf '  state_file   %s (%s)\n' "$BREAKER_STATE_FILE" \
            "$([ -e "$BREAKER_STATE_FILE" ] && echo present || echo absent)"
        printf '  lock_file    %s\n' "$BREAKER_LOCK_FILE"
        # ⛔ Reports PRESENCE, MODE and OWNER. Never the contents, never a prefix, never a
        # length — a "first two characters" convenience is how a secret ends up in a log.
        printf '  account_file %s (%s, mode %s, owner %s)\n' "$ACCOUNT_FILE" \
            "$([ -e "$ACCOUNT_FILE" ] && echo present || echo MISSING)" \
            "$(stat -c %a "$ACCOUNT_FILE" 2>/dev/null || echo unknown)" \
            "$(stat -c %U "$ACCOUNT_FILE" 2>/dev/null || echo unknown)"
        printf '  driver       %s (%s)\n' "$DRIVER" "$([ -f "$DRIVER" ] && echo installed || echo 'NOT INSTALLED')"
        printf '  cdp_helper   %s (%s)\n' "$CDP_HELPER" "$([ -f "$CDP_HELPER" ] && echo present || echo MISSING)"
        printf '  assist_state %s (%s)\n' "$ASSIST_STATE_FILE" "$([ -e "$ASSIST_STATE_FILE" ] && echo present || echo absent)"
        printf '  status_url   %s\n' "$STATUS_URL"
        printf '  cdp_port     %s\n' "$CDP_PORT"
        printf '  driver_timeout_s %s\n' "$DRIVER_TIMEOUT"
        exit 0 ;;
    "") ;;
    *) echo "usage: $0 [--status | --reset | --print-config]" >&2; exit 2 ;;
esac

# --- No driver, no action -----------------------------------------------------
# ⭐ THE SAFE RESTING STATE. Checked before the lock, the breaker, the status poll and
# the credential file, so an install without the owner's driver can do NOTHING: no trip,
# no counter, no alarm. The alarm's relogin track journals "not installed" for the same
# reason. (A driver that vanishes between this check and its launch is a crash, and a
# crash is UNRECOGNISED -> TRIP. That race is not the resting state.)
if [ ! -f "$DRIVER" ]; then
    log "auto-relogin not installed: no sign-in driver at ${DRIVER}. Doing nothing; the session alarm still reports the session."
    exit 0
fi

for tool in curl jq flock python3 timeout stat; do
    command -v "$tool" >/dev/null 2>&1 || die "${tool} is not on PATH. Refusing to run half-equipped."
done
[[ "$CDP_PORT" =~ ^[1-9][0-9]{0,4}$ ]] || die "GV_RELOGIN_CDP_PORT='${CDP_PORT}' is not a port number."
[[ "$DRIVER_TIMEOUT" =~ ^[1-9][0-9]{0,2}$ ]] && [ "$DRIVER_TIMEOUT" -le 600 ] \
    || die "GV_RELOGIN_DRIVER_TIMEOUT='${DRIVER_TIMEOUT}' must be 1..600 seconds."

# --- One at a time -------------------------------------------------------------
# ⛔ breaker_lock IS THIS SCRIPT'S ONLY LOCK (plan §7.3): the draft's own fd-9 flock on the
# same file would have blocked breaker_lock for its full wait on every run. It is held
# from the load to the last write. The reachable-reauth assist takes the same file with
# `flock -n` for seconds at a time; the 60 s wait covers that. Every long-lived child is
# started with `8>&-` so it cannot inherit the lock and hold it after we exit.
BREAKER_LOCK_ERROR=""
if ! breaker_lock; then
    if [ "$BREAKER_LOCK_ERROR" = "held" ]; then
        log "another auto-relogin run (or the reauth assist) holds the lock; exiting without acting."
        exit 0
    fi
    die "could not open the lock file ${BREAKER_LOCK_FILE}."
fi

# --- Consult the breaker BEFORE anything else ------------------------------------
# ⛔ Before the poll, before the credential, before CDP. The breaker is not a check
# performed on the way to acting; it decides whether acting is on the table at all.
breaker_load

# ⛔ A RUN THAT DIED MID-ATTEMPT. The credential attempt is persisted with
# LAST_OUTCOME=in_flight BEFORE the driver starts (below). Finding that marker here
# means a previous run was killed after offering — or possibly offering — a credential,
# and nobody knows what Google said. That is an unrecognised outcome: TRIP.
if [ "$BREAKER_STATE" = "ARMED" ] && [ "$BREAKER_LAST_OUTCOME" = "in_flight" ]; then
    breaker_trip interrupted \
        "Auto-relogin is STOPPED PERMANENTLY: a previous run was killed while its sign-in driver was running, so whether a password reached Google, and what Google answered, is unknown. An unrecognised outcome is not evidence that trying again is safe. A human must check the box's Chrome (sign in by hand at voice.google.com if it is signed out), read: journalctl --user -u gv-auto-relogin -n 100 — then run: gv-auto-relogin.sh --reset"
    breaker_write || exit 1
    exit 0
fi

# ⛔ THE TOKEN, NOT THE EXIT CODE (plan §7.3). An arithmetic error inside the decision
# abandons that command and bash carries on; `breaker_may_attempt || exit` walked past a
# crashed decision. Only the exact word AUTHORISED authorises.
verdict="$(breaker_verdict)"
if [ "$verdict" != "AUTHORISED" ]; then
    log "not attempting: ${verdict:-REFUSED (the breaker returned nothing)}"
    # Persist what the load decided (a fail-closed trip, a rolled day) so --status and the
    # alarm read the same thing this run did.
    breaker_write || exit 1
    exit 0
fi

# --- Poll: act only on Stale or SignedOut ---------------------------------------
resp="$(curl -sS --max-time 10 -w $'\n%{http_code}' "$STATUS_URL" 8>&- 2>/dev/null)"
if [ $? -ne 0 ]; then
    log "status poll failed; nothing to act on. The alarm reports this condition, not us."
    exit 0
fi
http="${resp##*$'\n'}"; body="${resp%$'\n'*}"
[ "$http" = "200" ] || { log "status returned http=${http}; not acting."; exit 0; }
outcome="$(printf '%s' "$body" | jq -r '.browserRefreshOutcome // "FIELD_MISSING"' 2>/dev/null)" \
    || outcome="UNPARSEABLE"

# ⛔ ONLY `Stale` AND `SignedOut` — the two outcomes that mean "Chrome answered and holds
# no working Google session". `SignedOut` is PR #90's: before it, a Chrome parked on the
# sign-in page reported Unreachable (spike finding 3), so the plan's draft, written
# against Stale alone, would never have acted on the most direct signed-out shape.
# Not Unreachable — Chrome is not answering, there is nothing to drive, and an attempt
# would only spend budget learning what the status already said (the alarm covers it).
# Not FIELD_MISSING (an old build), not NotAttempted, not TornDown, not Succeeded, and
# emphatically not a value this script has never heard of.
case "$outcome" in
    Stale|SignedOut) ;;
    *)
        log "outcome=${outcome}; not a signed-out session. Nothing to do."
        exit 0 ;;
esac
log "outcome=${outcome} — the browser session is signed out. The breaker authorises an attempt."

# --- Stand down while a HUMAN is signing in ---------------------------------------
# ⛔ The reachable-reauth assist (PR #89, spec §5.1) opens the sign-in page for a human
# and records it. Driving the page then would navigate it out from under someone who is
# mid-password. This is NOT a trip and spends NO budget: nothing is written.
# Read as DATA — the file is never sourced. `STATE=` lines, value after the first `=`.
# ⚠ A file that EXISTS but has no single readable STATE line also stands down: we cannot
# tell whether a human is mid sign-in, and waiting costs only time (the alarm still
# reports the session). An absent file means no assist is installed.
if [ -e "$ASSIST_STATE_FILE" ]; then
    assist_state=""; assist_n=0
    if [ -f "$ASSIST_STATE_FILE" ] && [ -r "$ASSIST_STATE_FILE" ]; then
        while IFS= read -r aline || [ -n "$aline" ]; do
            case "$aline" in STATE=*) assist_n=$((assist_n + 1)); assist_state="${aline#STATE=}" ;; esac
        done < "$ASSIST_STATE_FILE"
    fi
    if [ "$assist_n" -ne 1 ]; then
        log "standing down: the reauth assist's state file ${ASSIST_STATE_FILE} has no single STATE line, so a human sign-in may be in progress. Not a trip; nothing spent."
        exit 0
    fi
    case "$assist_state" in
        PREPARED|SIGNED_IN_UNCONFIRMED)
            log "standing down: a human sign-in is in progress (reauth assist STATE=${assist_state}). Not a trip; nothing spent."
            exit 0 ;;
    esac
fi

# --- The credential file, read as DATA --------------------------------------------
# ⛔ NEVER SOURCED, NEVER EXPORTED, NEVER IN ARGV. Measured on this box 2026-09-09:
#   /proc/<pid>/cmdline  -r--r--r--   world-readable
#   /proc/<pid>/environ  -r--------   owner only
# `radio` is shared, and Radio Console runs under OUR uid. argv is a boundary against
# nobody; environ is none against Radio Console. The password lives in two unexported
# shell variables and leaves this process only on the driver's STDIN.
#
# Format (docs/gv-relogin-driver-contract.md): `KEY=value` lines; the value is EVERYTHING
# after the first `=`, verbatim — no quotes stripped, no whitespace trimmed; blank lines
# and `#` lines ignored. Exactly GV_ACCOUNT_EMAIL and GV_ACCOUNT_PASSWORD, once each.
#
# ⚠ Every refusal below names a LINE NUMBER or a KEY WE EXPECTED — never a key or value
# found in the file. A line like `hunter2=` would otherwise put a password in the journal.
# Every refusal TRIPS without an attempt: a configuration fault is not transient, and
# none of them spends the credential budget.
ACCT_EMAIL=""
ACCT_PASSWORD=""
account_refuse() { # account_refuse REASON TEXT
    ACCT_EMAIL=""; ACCT_PASSWORD=""
    breaker_trip "$1" "Auto-relogin is stopped: $2 No sign-in was attempted. The file is written by the owner, on the box, by hand — no deploy creates it (format: docs/gv-relogin-driver-contract.md). Fix it, then run: gv-auto-relogin.sh --reset"
    breaker_write || exit 1
    exit 0
}
read_account_file() {
    local line n=0 key seen_e=0 seen_p=0
    [ -e "$ACCOUNT_FILE" ] || account_refuse account_file_missing "its credential file ${ACCOUNT_FILE} does not exist."
    [ ! -L "$ACCOUNT_FILE" ] && [ -f "$ACCOUNT_FILE" ] \
        || account_refuse account_file_unsafe "${ACCOUNT_FILE} is not a regular file (a symlink or something else); it must be the file itself."
    [ "$(stat -c %a "$ACCOUNT_FILE" 2>/dev/null)" = "600" ] \
        || account_refuse account_file_unsafe "${ACCOUNT_FILE} is mode $(stat -c %a "$ACCOUNT_FILE" 2>/dev/null || echo unknown), not 600. This box is shared with Radio Console under the same uid."
    [ "$(stat -c %u "$ACCOUNT_FILE" 2>/dev/null)" = "$(id -u)" ] \
        || account_refuse account_file_unsafe "${ACCOUNT_FILE} is not owned by $(id -un), the user this runs as."
    [ -r "$ACCOUNT_FILE" ] || account_refuse account_file_unreadable "${ACCOUNT_FILE} cannot be read."
    while IFS= read -r line || [ -n "$line" ]; do
        n=$((n + 1))
        case "$line" in
            *$'\r'*) account_refuse account_file_malformed \
                "line ${n} of ${ACCOUNT_FILE} ends in a carriage return (CRLF). The value is taken verbatim, so the CR would become part of the password and Google would reject it — which stops auto-relogin permanently. Save the file with LF line endings." ;;
            ''|'#'*) continue ;;
            *=*) ;;
            *) account_refuse account_file_malformed "line ${n} of ${ACCOUNT_FILE} is not KEY=value." ;;
        esac
        key="${line%%=*}"
        case "$key" in
            GV_ACCOUNT_EMAIL)
                [ "$seen_e" -eq 0 ] || account_refuse account_file_malformed "GV_ACCOUNT_EMAIL is set more than once in ${ACCOUNT_FILE}."
                ACCT_EMAIL="${line#*=}"; seen_e=1 ;;
            GV_ACCOUNT_PASSWORD)
                [ "$seen_p" -eq 0 ] || account_refuse account_file_malformed "GV_ACCOUNT_PASSWORD is set more than once in ${ACCOUNT_FILE}."
                ACCT_PASSWORD="${line#*=}"; seen_p=1 ;;
            *) account_refuse account_file_malformed \
                "line ${n} of ${ACCOUNT_FILE} sets a key other than GV_ACCOUNT_EMAIL or GV_ACCOUNT_PASSWORD (the key is not repeated here in case it is not a key)." ;;
        esac
    done < "$ACCOUNT_FILE"
    [ -n "$ACCT_EMAIL" ]    || account_refuse account_file_incomplete "GV_ACCOUNT_EMAIL is missing or empty in ${ACCOUNT_FILE}."
    [ -n "$ACCT_PASSWORD" ] || account_refuse account_file_incomplete "GV_ACCOUNT_PASSWORD is missing or empty in ${ACCOUNT_FILE}."
}
read_account_file

# --- Choose the ONE page to drive, by its LIVE location -----------------------------
# ⛔ Never by /json/list's cached .url (KNOWN-ISSUES.md:16-22; plan §0.7) and never "is
# there some tab that matches". Each page's own window.location.href is read, the host is
# PARSED (never a substring — PR #90's IsSignedOutPage rule), and:
#   tier 1  voice.google.com or accounts.google.com — the bridge's Voice tab, or the
#           sign-in page a signed-out Chrome sits on (spike rows 1 and 3, finding 3)
#   tier 2  workspace.google.com/products/voice… — where a signed-out Voice tab is sent;
#           considered only when tier 1 is EMPTY, because §0.7 measured a PARKED
#           workspace tab beside a healthy Voice tab
# Exactly one candidate, or UNRECOGNISED -> TRIP, with no credential spent. A CDP port
# that does not answer is a transport fault before any interaction with Google:
# retryable, no credential spent.
# Hosts only in the journal: a sign-in URL can carry the account address in its query.
host_of() { # host_of URL -> "host path" on stdout; returns 1 for anything not plain https
    [[ "$1" =~ ^https://([A-Za-z0-9.-]+)(:[0-9]+)?(/[^?#]*)?([?#].*)?$ ]] || return 1
    printf '%s %s' "${BASH_REMATCH[1],,}" "${BASH_REMATCH[3]:-/}"
}
record_transport_and_exit() { # record_transport_and_exit WHAT
    ACCT_EMAIL=""; ACCT_PASSWORD=""
    breaker_record_transport_failure
    log "transport failure (${1}) before any interaction with Google; no credential was offered. Will try again within the rate limit."
    breaker_write || exit 1
    exit 0
}
cdp() { timeout 40 python3 "$CDP_HELPER" "$@" --port "$CDP_PORT" 8>&- 2>/dev/null; }

TARGET_ID=""
listing="$(cdp targets)" || record_transport_and_exit "CDP port ${CDP_PORT} did not list its pages"
tier1=(); tier2=(); seen_hosts=""
while IFS=$'\t' read -r tid _cached; do
    [ -n "$tid" ] || continue
    [[ "$tid" =~ ^[A-Za-z0-9._-]+$ ]] || continue
    href="$(cdp url --target "$tid")" || record_transport_and_exit "could not read the live location of page ${tid}"
    hp="$(host_of "$href")" || { seen_hosts="${seen_hosts} (non-https)"; continue; }
    h="${hp%% *}"; p="${hp#* }"
    seen_hosts="${seen_hosts} ${h}"
    case "$h" in
        voice.google.com|accounts.google.com) tier1+=("$tid") ;;
        workspace.google.com) case "$p" in /products/voice*) tier2+=("$tid") ;; esac ;;
    esac
done <<< "$listing"
if [ "${#tier1[@]}" -eq 1 ]; then
    TARGET_ID="${tier1[0]}"
elif [ "${#tier1[@]}" -eq 0 ] && [ "${#tier2[@]}" -eq 1 ]; then
    TARGET_ID="${tier2[0]}"
else
    ACCT_EMAIL=""; ACCT_PASSWORD=""
    breaker_trip target_unrecognised \
        "Auto-relogin is STOPPED PERMANENTLY: it could not pick exactly one page to drive in the bridge's Chrome (${#tier1[@]} on voice/accounts.google.com, ${#tier2[@]} on the Workspace Voice page; live hosts:${seen_hosts:- none}). Driving the wrong page, or guessing, is not safe. No sign-in was attempted. A human must look at the bridge Chrome's tabs, re-login by hand at voice.google.com if it is signed out, and then run: gv-auto-relogin.sh --reset"
    breaker_write || exit 1
    exit 0
fi
log "driving page ${TARGET_ID}."

# --- Charge the attempt BEFORE the driver runs ---------------------------------------
# ⛔ Persisted first, with LAST_OUTCOME=in_flight, so a run killed mid-driver is still
# counted AND is found by the next run's in_flight check above. The counters are saved
# so a positively identified TRANSPORT verdict can hand the credential budget back
# (spec §9.4: a transport failure "does not consume the credential budget" — the draft
# charged it). If the charge cannot be written, the driver does not run.
pre_credential="$BREAKER_DAY_CREDENTIAL_ATTEMPTS"
pre_total="$BREAKER_ATTEMPTS_TOTAL"
breaker_record_credential_attempt
BREAKER_LAST_OUTCOME="in_flight"
if ! breaker_write; then
    ACCT_EMAIL=""; ACCT_PASSWORD=""
    die "could not persist the attempt before starting the driver; NOT starting it."
fi

# --- Run the owner's driver ---------------------------------------------------------
# ⛔ THE CREDENTIAL GOES DOWN A PIPE, written by bash's BUILTIN printf, which never execs:
# no argv anywhere carries it (timeout's argv is `timeout N python3 <path>`), and it is
# never exported, so the driver's environment does not carry it either. jq is not used
# here on purpose: its inputs arrive through --arg, which is argv.
# ⛔ The driver's STDERR goes to the journal only through redact_stream, which replaces
# the password and the email with markers, caps each line and the line count. A driver
# is required never to print either (the contract); this is the second line of defence,
# not the first. Its STDOUT is never logged at all — only the verdict word is.
redact_stream() {
    local line n=0
    while IFS= read -r line || [ -n "$line" ]; do
        n=$((n + 1))
        [ "$n" -le 40 ] || continue
        [ -n "$ACCT_PASSWORD" ] && line="${line//"$ACCT_PASSWORD"/[password redacted]}"
        [ -n "$ACCT_EMAIL" ] && line="${line//"$ACCT_EMAIL"/[email redacted]}"
        log "driver: ${line:0:300}"
    done
    [ "$n" -le 40 ] || log "driver: ($((n - 40)) further stderr lines not logged)"
    return 0
}
driver_out="$(
    set +o pipefail   # the DRIVER's status decides, not printf's
    printf 'version=1\ncdp_port=%s\ntarget_id=%s\nemail=%s\npassword=%s\n' \
        "$CDP_PORT" "$TARGET_ID" "$ACCT_EMAIL" "$ACCT_PASSWORD" \
        | timeout --kill-after=10 "$DRIVER_TIMEOUT" python3 "$DRIVER" 8>&- \
            2> >(exec 8>&- >/dev/null; redact_stream)
)"
# ⚠ The redactor's STDOUT is /dev/null, not this command substitution's pipe. Otherwise a
# driver that leaves a helper process holding its stderr would make "$(…)" wait for that
# helper to die (measured in the harness: the run stalled for the helper's full lifetime).
# The redactor writes only to stderr (the journal) through log().
driver_rc=$?
ACCT_EMAIL=""; ACCT_PASSWORD=""
unset ACCT_EMAIL ACCT_PASSWORD

# ⛔ EXACTLY ONE VERDICT WORD, ON THE LAST LINE, AND EXIT 0. Anything else — a crash, a
# non-zero exit, a timeout (124/137), a missing or vanished driver (126/127), silence,
# or a word not on this list — is UNRECOGNISED. Plan §0.6: the default is a STOP, never
# a retry, because the case this will most often meet is one nobody has seen.
driver_last="${driver_out##*$'\n'}"
if [ "$driver_rc" -ne 0 ]; then
    driver_verdict="UNRECOGNISED"
    log "driver exited ${driver_rc}$([ "$driver_rc" -eq 124 ] || [ "$driver_rc" -eq 137 ] && echo " (killed at the ${DRIVER_TIMEOUT}s limit)"); treating the outcome as UNRECOGNISED."
else
    case "$driver_last" in
        SIGNED_IN|CREDENTIAL_REJECTED|CHALLENGED|TRANSPORT|UNRECOGNISED) driver_verdict="$driver_last" ;;
        *)  driver_verdict="UNRECOGNISED"
            log "driver's last line is not a verdict word ($(printf '%s' "$driver_out" | wc -l | tr -d ' ') newline(s), ${#driver_out} bytes of stdout; not logged); treating the outcome as UNRECOGNISED." ;;
    esac
fi
log "driver verdict=${driver_verdict}"

case "$driver_verdict" in
  SIGNED_IN)
      : ;;  # Verified below. A driver saying SIGNED_IN is not the outcome.
  CREDENTIAL_REJECTED)
      # ⛔ ONE REJECTION. NOT ANOTHER GO. Spec §6: offering a wrong password again is the
      # single most reliable way to get an account locked, and a rejection is never transient.
      breaker_trip credential_rejected \
          "Auto-relogin is STOPPED PERMANENTLY: Google rejected the stored password. This is never a transient failure and it will not be tried again — retrying a rejected credential is the most reliable way to get the account locked. A human must check the password in /opt/rotary-phone/gv-account.conf, re-login by hand at voice.google.com, and then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
  CHALLENGED)
      # ⛔ A challenge means Google ALREADY considers this suspicious. Spec §6, §10 decision 3.
      breaker_trip challenged \
          "Auto-relogin is STOPPED PERMANENTLY: Google presented a verification challenge instead of signing in. A challenge means Google already treats this sign-in as suspicious, so it will not be tried again. A human must re-login by hand at voice.google.com in the box's Chrome, and then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
  TRANSPORT)
      # The ONLY retryable verdict, and legal only for a fault BEFORE the driver's first
      # interaction with Google's page (the contract). The credential budget is handed
      # back; the hourly spacing and the transport ceiling are still spent, so a broken
      # CDP cannot spin.
      BREAKER_DAY_CREDENTIAL_ATTEMPTS="$pre_credential"
      BREAKER_ATTEMPTS_TOTAL="$pre_total"
      breaker_record_transport_failure
      log "driver reported a transport fault before touching Google's page; no credential was offered. Will try again within the rate limit."
      breaker_write || exit 1
      exit 0 ;;
  *)
      breaker_trip unclassified \
          "Auto-relogin is STOPPED PERMANENTLY: the sign-in driver's outcome was not recognised (a crash, a timeout, no verdict, or UNRECOGNISED). Nothing is known about how Google responded, and an unrecognised outcome is not evidence that trying again is safe. A human must check the box's Chrome, read: journalctl --user -u gv-auto-relogin -n 100 — then run: gv-auto-relogin.sh --reset"
      breaker_write || exit 1
      exit 0 ;;
esac

# --- Verify by OUTCOME (Task 11) ------------------------------------------------------
# ⛔ STAGE 1 — the forced navigation, on the SAME page the driver was given, read from that
# page's own window.location.href after its load event. The tab's title and cached URL are
# stale renders that lie about login state (KNOWN-ISSUES.md:16-22); a forced navigation is
# the only reading that means anything. Anything but voice.google.com — the signed-out
# Workspace page, the sign-in host, a failure, silence — and NO cookies are posted.
verify_fail() { # verify_fail DETAIL
    breaker_trip verification_failed \
        "Auto-relogin is STOPPED PERMANENTLY: its sign-in driver reported success, but the outcome check did not confirm it (${1}). The driver's report cannot be trusted. The previously-working cookie set was NOT overwritten. A human must re-login by hand at voice.google.com in the box's Chrome, and then run: gv-auto-relogin.sh --reset"
    breaker_write || exit 1
    exit 0
}
landed="$(cdp navigate --target "$TARGET_ID" --url "$VERIFY_URL" --timeout 15)" \
    || verify_fail "the forced navigation to voice.google.com did not complete"
landed_hp="$(host_of "$landed")" || verify_fail "the forced navigation landed on a non-https location"
landed_host="${landed_hp%% *}"
[ "$landed_host" = "voice.google.com" ] \
    || verify_fail "a forced navigation to voice.google.com landed on ${landed_host}"
log "forced navigation settled on voice.google.com."

# ⭐ STAGE 2 — GOOGLE ADJUDICATES. The service adopts the harvested cookies in memory,
# probes them live against Google, and persists only on success; a 200 is that success
# (202 is "written but unproven", 502 "Google refused"). And browserSessionValidatedAt
# must MOVE ACROSS OUR OWN POST — read immediately before it — because the 20-minute cron
# can produce a Succeeded of its own (§0.8, §0.11).
status_read() { # status_read -> "<outcome>\t<validatedAt>"; returns 1 if the status is unreadable
    local r h b
    r="$(curl -sS --max-time 10 -w $'\n%{http_code}' "$STATUS_URL" 8>&- 2>/dev/null)" || return 1
    h="${r##*$'\n'}"; b="${r%$'\n'*}"
    [ "$h" = "200" ] || return 1
    printf '%s' "$b" | jq -r '[(.browserRefreshOutcome // ""), (.browserSessionValidatedAt // "")] | @tsv' 2>/dev/null
}
before_row="$(status_read)" || verify_fail "the service status could not be read before the refresh"
validated_before="${before_row#*$'\t'}"
post_code="$(curl -sS --max-time 30 -o /dev/null -w '%{http_code}' -X POST "$REFRESH_URL" \
                -H 'Content-Type: application/json' --data-binary '{}' 8>&- 2>/dev/null)"
after_row="$(status_read)" || verify_fail "refresh-from-browser answered ${post_code:-nothing}, and then the service status could not be read"
outcome_after="${after_row%%$'\t'*}"
validated_after="${after_row#*$'\t'}"

if [ "$post_code" = "200" ] && [ "$outcome_after" = "Succeeded" ] \
   && [ -n "$validated_after" ] && [ "$validated_after" != "$validated_before" ]; then
    breaker_record_success
    breaker_write || exit 1
    log "RESTORED: refresh-from-browser 200, browserRefreshOutcome=Succeeded, browserSessionValidatedAt moved ${validated_before:-none} -> ${validated_after}."
    exit 0
fi
verify_fail "refresh-from-browser answered ${post_code:-nothing}; browserRefreshOutcome is ${outcome_after:-unknown}; browserSessionValidatedAt ${validated_before:-none} -> ${validated_after:-none}. Google did not accept cookies harvested from the browser, or the service did not confirm it"
