#!/usr/bin/env bash
# =============================================================================
# Report the INSTALLED state of the box's user-level tooling — conditionally.
#
#   check-installed-drift.sh --group alarm  [--manifest FILE] [--ship-dir DIR]
#   check-installed-drift.sh --group bridge [--manifest FILE] [--ship-dir DIR]
#
# Exit: 0 = every file in the group matches end to end
#       1 = DRIFT — at least one link of the chain differs
#       2 = CANNOT DETERMINE — a file or the manifest is missing/unreadable
#
# THREE LINKS, NOT TWO. The chain is:
#
#     repo (expected digest, computed on the deploying machine)
#       -> shipped   (/opt/rotary-phone/deploy/...)
#         -> installed (~/bin/..., ~/.config/systemd/user/...)
#
# Comparing only shipped-vs-installed is a check that RUNS, PASSES, and answers
# a different question. It would report "the two copies match" and be read as
# "the box has the current file" — and on a deploy whose transfer silently did
# nothing, two stale copies MATCH. The repo end of the chain is therefore
# computed on the deploying machine and carried here in a manifest.
# See docs/plans/gv-session-alarm.md §0.9 and §0.10.
#
# ⚠ ABSENCE IS NOT SUCCESS. Every "cannot read" path exits 2 and says so. A
# check that goes quiet when its subject is missing is the defect this repo
# corrected in the deploy gate on 2026-09-09: test for PRESENCE, not absence.
#
# ⚠ THE SILENT PATH IS DELIBERATELY QUIET BUT NOT SILENT. One short line on
# success, so "the check ran and found nothing" is distinguishable from "the
# check did not run". The ⚠ marker and the ACTION text appear ONLY on drift.
# A warning that fires on a healthy deploy is not a warning; it is noise with an
# alarming shape, and it trains the operator to scroll past the one run where it
# means something.
# =============================================================================
set -uo pipefail

GROUP=""
SHIP_DIR="/opt/rotary-phone/deploy"
MANIFEST=""

while [ $# -gt 0 ]; do
    case "$1" in
        --group)    GROUP="${2:-}"; shift 2 ;;
        --ship-dir) SHIP_DIR="${2:-}"; shift 2 ;;
        --manifest) MANIFEST="${2:-}"; shift 2 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

: "${MANIFEST:=${SHIP_DIR}/.shipped-manifest.sha256}"

# group -> "shipped-relative-path|installed-absolute-path" pairs
case "$GROUP" in
  alarm)
    PAIRS=(
      "gv-session-alarm.sh|${HOME}/bin/gv-session-alarm.sh"
      "systemd/gv-session-alarm.service|${HOME}/.config/systemd/user/gv-session-alarm.service"
      "systemd/gv-session-alarm.timer|${HOME}/.config/systemd/user/gv-session-alarm.timer"
    ) ;;
  bridge)
    PAIRS=(
      "gv-bridge-ensure.sh|${HOME}/bin/gv-bridge-ensure.sh"
      "gv-bridge-restart.sh|${HOME}/bin/gv-bridge-restart.sh"
    ) ;;
  *) echo "--group must be 'alarm' or 'bridge'" >&2; exit 2 ;;
esac

if [ ! -r "$MANIFEST" ]; then
    echo "⚠ [drift-check] ${GROUP}: CANNOT DETERMINE — no shipped manifest at ${MANIFEST}."
    echo "    The deploy did not write one, or did not reach the box. Nothing here can be"
    echo "    stated about what is installed. ACTION: re-run the deploy and read its output."
    exit 2
fi

expected_of() { awk -v f="$1" '$2 == f { print $1 }' "$MANIFEST" | head -n1; }
digest_of()   { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }
stamp_of()    { date -r "$1" '+%Y-%m-%d %H:%M' 2>/dev/null || echo "unknown"; }

rc=0
matched=0
total=${#PAIRS[@]}

for pair in "${PAIRS[@]}"; do
    rel="${pair%%|*}"
    installed="${pair##*|}"
    shipped="${SHIP_DIR}/${rel}"

    want="$(expected_of "$rel")"
    have_ship="$(digest_of "$shipped")"
    have_inst="$(digest_of "$installed")"

    if [ -z "$want" ]; then
        echo "⚠ [drift-check] ${GROUP}: CANNOT DETERMINE — ${rel} is not in the manifest."
        echo "    ACTION: the deploy shipped a file it did not record, or recorded none. Re-deploy."
        rc=2; continue
    fi
    if [ -z "$have_ship" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${shipped} is MISSING or unreadable on the box."
        echo "    The deploy reported success but the file is not here. ACTION: re-run the deploy;"
        echo "    a transfer that fails after the manifest is written leaves exactly this state."
        rc=2; continue
    fi
    if [ "$have_ship" != "$want" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${rel} SHIPPED COPY IS STALE — /opt does not match the repo."
        echo "    repo    sha256 ${want}"
        echo "    shipped sha256 ${have_ship}   mtime $(stamp_of "$shipped")"
        echo "    The transfer did not land. ACTION: re-run the deploy and check the sync step."
        rc=1; continue
    fi
    if [ -z "$have_inst" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${installed} is NOT INSTALLED."
        echo "    shipped sha256 ${have_ship}   mtime $(stamp_of "$shipped")"
        if [ "$GROUP" = "bridge" ]; then
            echo "    setup-gvbridge.sh installs this and THE DEPLOY DOES NOT RUN IT."
            echo "    ACTION: bash ${SHIP_DIR}/setup-gvbridge.sh  (see plan §0.2 before you do)."
        else
            echo "    ACTION: bash ${SHIP_DIR}/install-gv-session-alarm.sh"
        fi
        rc=1; continue
    fi
    if [ "$have_inst" != "$have_ship" ]; then
        echo "⚠ [drift-check] ${GROUP}: ${installed} DIFFERS from the shipped copy."
        echo "    installed sha256 ${have_inst}   mtime $(stamp_of "$installed")"
        echo "    shipped   sha256 ${have_ship}   mtime $(stamp_of "$shipped")"
        echo "    The box is executing an older file than the one this deploy shipped."
        if [ "$GROUP" = "bridge" ]; then
            echo "    ACTION: bash ${SHIP_DIR}/setup-gvbridge.sh  (see plan §0.2 before you do)."
        else
            echo "    ACTION: bash ${SHIP_DIR}/install-gv-session-alarm.sh"
        fi
        rc=1; continue
    fi
    matched=$((matched + 1))
done

if [ "$rc" -eq 0 ]; then
    echo "[drift-check] ${GROUP}: ${matched}/${total} installed files match repo → shipped → installed."
fi
exit "$rc"
