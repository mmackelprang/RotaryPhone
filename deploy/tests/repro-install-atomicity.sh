#!/usr/bin/env bash
# =============================================================================
# Demonstrates the window `install -m` opens, and that setup-gvbridge.sh's
# install_atomic() closes it.
#
# Why this matters here: setup-gvbridge.sh installs ~/bin/gv-bridge-ensure.sh,
# which gv-bridge-watchdog.timer executes every 2 minutes (OnUnitActiveSec=2min,
# AccuracySec=20s). There is no quiet window to install in.
#
# `install -m` does NOT truncate in place -- strace shows unlink(dest) then
# open(dest, O_CREAT|O_EXCL, 0600) -- so a process already EXECUTING the old file
# is safe: it holds the unlinked inode and runs to completion. The exposure is
# the window between the unlink and the end of the copy, during which the
# destination PATH DOES NOT EXIST. A watchdog firing there gets ENOENT and the
# unit fails; at the tail of the window a newly started invocation could exec a
# partial file at mode 0600 -- which does not crash, it STOPS EARLY.
#
# rename(2) is atomic within a filesystem, so install-then-mv removes the window.
#
# ⚠ This test SOURCES install_atomic() out of setup-gvbridge.sh rather than
# inlining an equivalent. An earlier version inlined `install … && mv …`, which
# meant reverting install_atomic to a plain `install -m "$mode" "$src" "$dest"`
# left the test still printing PASS: it asserted a property of install+mv in the
# abstract, not a property of the shipped code. That is the same defect this
# whole branch exists to fix, so the test now drives the real function.
#
# ⚠ This is a TIMING test and can produce a false "no" on line 1 on a fast
# filesystem -- hence the 60 MB payload. If line 1 ever reads "no", that is an
# INCONCLUSIVE run, not a passing one. Re-run; if it stays "no", record it and
# keep the mv anyway: the change is correct on rename(2) semantics regardless of
# whether this machine can catch the window.
#
# ⚠ Do not "verify" a replacement by comparing inode numbers. Measured
# 2026-09-09: the freed inode was immediately REUSED by the new file.
# =============================================================================
set -u
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT

SETUP="$(dirname "$0")/../setup-gvbridge.sh"
[ -f "$SETUP" ] || { echo "FAIL: cannot find $SETUP"; exit 1; }
eval "$(sed -n '/^install_atomic() {/,/^}/p' "$SETUP")"
command -v install_atomic >/dev/null \
    || { echo "FAIL: could not load install_atomic() from setup-gvbridge.sh -- has it been renamed?"; exit 1; }

PAYLOAD=60000000
head -c "$PAYLOAD" /dev/urandom > "$W/new"
# A short write would silently degrade the run to INCONCLUSIVE while looking like
# an honest timing miss.
[ "$(stat -c %s "$W/new")" -eq "$PAYLOAD" ] \
    || { echo "FAIL: payload is $(stat -c %s "$W/new") bytes, expected $PAYLOAD"; exit 1; }
cp "$W/new" "$W/target"

# Bounded by a completion flag rather than only by a deadline: in the passing case
# the gap never appears, and spinning the full deadline burns a core for no reason.
watch_for_gap() {
    local seen=no deadline=$((SECONDS + 30))
    while [ $SECONDS -lt $deadline ]; do
        [ -e "$W/target" ] || { seen=yes; break; }
        [ -e "$W/done" ] && break
    done
    echo "$seen"
}

rm -f "$W/done"
watch_for_gap > "$W/a.out" & sleep 0.05
install -m 755 "$W/new" "$W/target"; touch "$W/done"; wait
a="$(cat "$W/a.out")"
echo "install -m      : path_missing_observed=$a   # expect yes"

cp "$W/new" "$W/target"
rm -f "$W/done"
watch_for_gap > "$W/b.out" & sleep 0.05
install_atomic "$W/new" "$W/target" 755; touch "$W/done"; wait
b="$(cat "$W/b.out")"
echo "install_atomic  : path_missing_observed=$b   # expect no"

mode="$(stat -c %a "$W/target")"
echo "install_atomic  : final mode=$mode  size=$(stat -c %s "$W/target")   # expect 755 / $PAYLOAD"

# The staging file must not survive a FAILURE either -- ~/Desktop is a kiosk screen,
# and leaving debris there is the problem Task 13 removed from backup_if_changed.
mkdir -p "$W/ro"
cp "$W/new" "$W/ro/t"
chmod 0555 "$W/ro"
install_atomic "$W/new" "$W/ro/t" 755 2>/dev/null
rc=$?
leftover="$([ -e "$W/ro/t.new" ] && echo present || echo cleaned)"
chmod 0755 "$W/ro"
echo "failed install  : rc=$rc  staging_file=$leftover   # expect non-zero / cleaned"

echo ""
fail=0
[ "$b" = "no" ]                          || { echo "FAIL: install_atomic exposed a window, which rename(2) should make impossible"; fail=1; }
[ "$mode" = "755" ]                      || { echo "FAIL: install_atomic did not preserve mode 755 (got $mode)"; fail=1; }
[ "$rc" -ne 0 ]                          || { echo "FAIL: install_atomic returned 0 for a failed install"; fail=1; }
[ "$leftover" = "cleaned" ]              || { echo "FAIL: install_atomic stranded a .new staging file on failure"; fail=1; }
if [ "$fail" -ne 0 ]; then exit 1; fi
if [ "$a" = "no" ]; then
    echo "INCONCLUSIVE: the window was not caught on this filesystem (see the note above)."
    echo "              This is not a pass and not a failure. Re-run."
    exit 2
fi
echo "PASS: install -m exposes a window; install_atomic does not, preserves the mode,"
echo "      and cleans up its staging file when it fails"
