#!/usr/bin/env bash
# Sample the box's browser-session age, so a WARN threshold can be CHOSEN rather
# than guessed. Writes one CSV row per sample. Read-only: it polls an endpoint.
#
#   nohup bash sample-browser-session-age.sh >/dev/null 2>&1 &
#   bash sample-browser-session-age.sh --once      # one row, for a smoke test
#
# ⚠ IT LIVES IN deploy/ AND NOT deploy/tools/ FOR ONE REASON: that is what ships.
# Deploy-ToLinux.ps1 collects `Get-ChildItem -Path deploy -Filter "*.sh" -File`
# with NO -Recurse, so anything under deploy/tools/ never reaches the box — and a
# sampler that cannot reach the box cannot sample it. The analyser is the other
# half of this pair and DOES live in deploy/tools/, because it runs on the
# deploying machine against a CSV pulled off the box, not on the box itself.
#
# ⛔ THIS SCRIPT PRODUCES NO THRESHOLD, AND NEITHER DOES ITS ANALYSER.
# Spec §11 decision 3: "Needs a measured baseline before a number is chosen — do
# not guess one." Today's session was ~2h old at death; a healthy one runs for
# days. A plausible-looking number is WORSE than no number, because the alarm's
# whole credibility rests on it: too low and the owner mutes it, too high and it
# never fires. The threshold is the owner's to choose after reading the report.
set -uo pipefail

ONCE=0
[ "${1:-}" = "--once" ] && { ONCE=1; shift; }

OUT="${1:-$HOME/.local/state/gv-session-age-samples.csv}"
URL="${GV_ALARM_STATUS_URL:-http://127.0.0.1:5004/api/gvbridge/status}"

# Every 5 minutes — fine enough to see the 20-minute cron's sawtooth, which is
# the thing that matters. Sampling at 20 minutes could alias with the cron and
# report a flat line for a signal that is anything but.
INTERVAL="${GV_AGE_SAMPLE_INTERVAL:-300}"

mkdir -p "$(dirname "$OUT")" 2>/dev/null
[ -f "$OUT" ] || echo "utc,outcome,age_seconds,validated_at,stale,cookies_valid" > "$OUT"

sample_once() {
    local body
    body="$(curl -sS --max-time 10 "$URL" 2>/dev/null)"
    if [ -n "$body" ]; then
        # One jq pass over one body, so the six columns cannot disagree with each
        # other. ⚠ `// "ABSENT"` on outcome is deliberate: a build without the
        # field must be visible in the data, not silently blank — the whole
        # sample would otherwise look like a healthy window.
        printf '%s,%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
            "$(printf '%s' "$body" | jq -r '[
                 (.browserRefreshOutcome // "ABSENT"),
                 (.browserSessionAgeSeconds // ""),
                 (.browserSessionValidatedAt // ""),
                 (.browserSessionStale // ""),
                 (.cookiesValid // "")
               ] | @csv' 2>/dev/null | tr -d '"')" >> "$OUT"
    else
        printf '%s,UNREACHABLE,,,,\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$OUT"
    fi
}

if [ "$ONCE" -eq 1 ]; then sample_once; exit 0; fi
while :; do sample_once; sleep "$INTERVAL"; done
