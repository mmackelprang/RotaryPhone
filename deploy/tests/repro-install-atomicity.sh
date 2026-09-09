#!/usr/bin/env bash
# =============================================================================
# Demonstrates the window `install -m` opens, and that install-then-mv closes it.
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
# rename(2) is atomic within a filesystem, so install-then-mv removes the window
# entirely.
#
# ⚠ This is a TIMING test and can produce a false "no" on the first line on a
# fast filesystem -- hence the 60 MB payload. If line 1 ever reads "no", that is
# an INCONCLUSIVE run, not a passing one. Re-run; if it stays "no", record it and
# keep the mv anyway: the change is correct on rename(2) semantics regardless of
# whether this machine can catch the window.
#
# ⚠ Do not "verify" a replacement by comparing inode numbers. Measured
# 2026-09-09: the freed inode was immediately REUSED by the new file.
# =============================================================================
set -u
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT

head -c 60000000 /dev/urandom > "$W/new"
cp "$W/new" "$W/target"

watch_for_gap() {                       # prints yes if the path ever vanishes
    local seen=no deadline=$((SECONDS + 20))
    while [ $SECONDS -lt $deadline ]; do
        [ -e "$W/target" ] || { seen=yes; break; }
    done
    echo "$seen"
}

watch_for_gap > "$W/a.out" & sleep 0.05; install -m 755 "$W/new" "$W/target"; wait
a="$(cat "$W/a.out")"
echo "install -m   : path_missing_observed=$a   # expect yes"

cp "$W/new" "$W/target"
watch_for_gap > "$W/b.out" & sleep 0.05
install -m 755 "$W/new" "$W/target.new" && mv -f "$W/target.new" "$W/target"; wait
b="$(cat "$W/b.out")"
echo "install + mv : path_missing_observed=$b   # expect no"

echo ""
if [ "$a" = "yes" ] && [ "$b" = "no" ]; then
    echo "PASS: install -m exposes a window; install + mv does not"
    exit 0
elif [ "$a" = "no" ]; then
    echo "INCONCLUSIVE: the window was not caught on this filesystem (see the note above)."
    echo "              This is not a pass and not a failure. Re-run."
    exit 2
else
    echo "FAIL: install + mv exposed a window, which rename(2) should make impossible"
    exit 1
fi
