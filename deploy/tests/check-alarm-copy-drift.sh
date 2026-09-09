#!/usr/bin/env bash
# The alarm QUOTES the service. A quotation that has silently stopped matching
# its source is worse than a paraphrase: it attributes words to the service that
# the service does not say, in a message an operator will act on.
#
# Each sentence below must appear in BOTH deploy/gv-session-alarm.sh and the C#
# that emits it. Compared after collapsing whitespace, because the C# is split
# across string-concatenation lines and the shell is not.
#
# ⚠ THIS SCRIPT IS NOT THE ENFORCEMENT. It is the Linux-shell convenience copy.
# The enforcement is AlarmCopyDriftTests.cs, which makes the same comparison as a
# unit test and therefore runs on Windows too. The plan wired this script into
# MSBuild behind Condition="IsOSPlatform(Linux)"; this repo has no CI, the owner
# builds on Windows, and the box has no SDK — so that wiring would have produced
# a guard that could never fire anywhere. Keep the two quote lists identical.
set -uo pipefail
cd "$(dirname "$0")/../.."

SH="deploy/gv-session-alarm.sh"
CS="src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs"
flat() { tr -d '\n' < "$1" | tr -s ' '; }
SH_FLAT="$(flat "$SH")"
# Strip C# string-concatenation seams: `" + "` becomes nothing.
CS_FLAT="$(tr -d '\n' < "$CS" | tr -s ' ' | sed 's/" *+ *"//g')"

fail=0
while IFS= read -r q; do
    [ -z "$q" ] && continue
    case "$SH_FLAT" in *"$q"*) ;; *) echo "MISSING FROM $SH: $q"; fail=1 ;; esac
    case "$CS_FLAT" in *"$q"*) ;; *) echo "MISSING FROM $CS: $q"; fail=1 ;; esac
done <<'QUOTES'
Google refused it. The working on-disk set was NOT overwritten.
ACTION: re-login at voice.google.com.
CHROME WAS UNREACHABLE on CDP port
so the Google login was never tested.
the browser was NEVER CONSULTED
QUOTES

[ "$fail" -eq 0 ] && echo "alarm copy matches the service's own wording"
exit "$fail"
