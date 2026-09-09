#!/usr/bin/env bash
# Drives check-installed-drift.sh through every case in the plan's Task 3 table,
# against a fabricated shipped tree and a fake HOME. No box.
#
# ⛔ The load-bearing case is the FIRST one, and it is run TEN TIMES. A warning
# that fires on a healthy deploy is not a fix; it is noise with an alarming
# shape, and this repo already demonstrates what that costs — the "Cannot
# unlink" line printed on every successful deploy for long enough to train the
# operator to scroll past it.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CHECK="${HERE}/../check-installed-drift.sh"
SRC="${HERE}/.."

WORK="$(mktemp -d)"
trap 'chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT

export HOME="$WORK/home"
SHIP="$WORK/ship"
MAN="$SHIP/.shipped-manifest.sha256"

fail=0
cases=0
check() { cases=$((cases + 1))
    if [ "$2" = "$3" ]; then printf '  PASS %s\n' "$1"
    else printf '  FAIL %s: expected [%s] got [%s]\n' "$1" "$2" "$3"; fail=1; fi; }

# Build a clean, fully in-sync world: repo -> shipped -> installed.
build_world() {
    rm -rf "$HOME" "$SHIP"
    mkdir -p "$SHIP/systemd" "$HOME/bin" "$HOME/.config/systemd/user"
    cp "$SRC/gv-session-alarm.sh"                 "$SHIP/gv-session-alarm.sh"
    cp "$SRC/systemd/gv-session-alarm.service"    "$SHIP/systemd/"
    cp "$SRC/systemd/gv-session-alarm.timer"      "$SHIP/systemd/"
    # The manifest is what the DEPLOYING MACHINE recorded about the repo.
    {
      printf '%s  %s\n' "$(sha256sum "$SRC/gv-session-alarm.sh"              | cut -d' ' -f1)" "gv-session-alarm.sh"
      printf '%s  %s\n' "$(sha256sum "$SRC/systemd/gv-session-alarm.service" | cut -d' ' -f1)" "systemd/gv-session-alarm.service"
      printf '%s  %s\n' "$(sha256sum "$SRC/systemd/gv-session-alarm.timer"   | cut -d' ' -f1)" "systemd/gv-session-alarm.timer"
    } > "$MAN"
    install -m 755 "$SHIP/gv-session-alarm.sh"              "$HOME/bin/gv-session-alarm.sh"
    install -m 644 "$SHIP/systemd/gv-session-alarm.service" "$HOME/.config/systemd/user/gv-session-alarm.service"
    install -m 644 "$SHIP/systemd/gv-session-alarm.timer"   "$HOME/.config/systemd/user/gv-session-alarm.timer"
}

run() { bash "$CHECK" --group alarm --ship-dir "$SHIP" > "$WORK/out.txt" 2>&1; echo "$?"; }

echo "=== row 1: all in sync — quiet, and quiet TEN TIMES ==="
build_world
distinct=""
for _ in $(seq 10); do
    rc="$(run)"
    [ "$rc" = "0" ] || { check "in-sync run exits 0" "0" "$rc"; break; }
    distinct="${distinct}$(cat "$WORK/out.txt")"$'\n'
done
check "in-sync exits 0" "0" "$(run)"
check "in-sync prints exactly ONE line" "1" "$(wc -l < "$WORK/out.txt")"
check "in-sync says 3/3" "1" "$(grep -c '3/3 installed files match' "$WORK/out.txt")"
check "in-sync prints NO warning marker" "0" "$(grep -c '⚠' "$WORK/out.txt")"
check "ten in-sync runs are byte-identical" "1" "$(printf '%s' "$distinct" | sort -u | wc -l)"

echo "=== row 2: not installed ==="
build_world
rm -f "$HOME/bin/gv-session-alarm.sh"
check "not installed -> exit 1" "1" "$(run)"
check "…says NOT INSTALLED" "1" "$(grep -c 'NOT INSTALLED' "$WORK/out.txt")"
check "…names the installer in an ACTION line" "1" \
      "$(grep -c 'ACTION: bash .*install-gv-session-alarm.sh' "$WORK/out.txt")"

echo "=== row 3: installed differs from shipped ==="
build_world
echo "# tampered" >> "$HOME/bin/gv-session-alarm.sh"
check "installed differs -> exit 1" "1" "$(run)"
check "…says DIFFERS" "1" "$(grep -c 'DIFFERS from the shipped copy' "$WORK/out.txt")"
check "…prints both sha256s" "2" "$(grep -c 'sha256 [0-9a-f]\{64\}' "$WORK/out.txt")"
check "…prints both mtimes" "2" "$(grep -c 'mtime [0-9]\{4\}-' "$WORK/out.txt")"

echo "=== row 4: SHIPPED copy stale — the case a two-link check CANNOT see ==="
# ⭐ This is what separates this check from the shipped-vs-installed comparison it
# replaces. Ship a stale file and install it faithfully: the two copies AGREE,
# and the box is running the wrong code.
build_world
echo "# stale in /opt" >> "$SHIP/gv-session-alarm.sh"
install -m 755 "$SHIP/gv-session-alarm.sh" "$HOME/bin/gv-session-alarm.sh"
check "shipped stale -> exit 1" "1" "$(run)"
check "…says SHIPPED COPY IS STALE" "1" "$(grep -c 'SHIPPED COPY IS STALE' "$WORK/out.txt")"
# Prove the premise: a two-link check would have passed this.
check "…while shipped and installed are IDENTICAL (the trap)" "identical" \
      "$([ "$(sha256sum < "$SHIP/gv-session-alarm.sh")" = "$(sha256sum < "$HOME/bin/gv-session-alarm.sh")" ] \
         && echo identical || echo different)"

echo "=== row 5: no manifest — never a silent pass ==="
build_world
rm -f "$MAN"
check "no manifest -> exit 2" "2" "$(run)"
check "…says CANNOT DETERMINE" "1" "$(grep -c 'CANNOT DETERMINE' "$WORK/out.txt")"

echo "=== row 6: shipped file missing, manifest present ==="
build_world
rm -f "$SHIP/gv-session-alarm.sh"
check "shipped missing -> exit 2" "2" "$(run)"
check "…says MISSING or unreadable" "1" "$(grep -c 'MISSING or unreadable' "$WORK/out.txt")"

echo "=== extra: a file shipped but not recorded in the manifest ==="
build_world
grep -v 'gv-session-alarm.timer' "$MAN" > "$MAN.tmp" && mv "$MAN.tmp" "$MAN"
check "unrecorded file -> exit 2" "2" "$(run)"
check "…says not in the manifest" "1" "$(grep -c 'is not in the manifest' "$WORK/out.txt")"

echo "=== extra: bad --group is a usage error, not a pass ==="
bash "$CHECK" --group nonsense --ship-dir "$SHIP" >/dev/null 2>&1
check "bad group -> exit 2" "2" "$?"

echo "=== extra: an option with no value must FAIL, not HANG ==="
# ⛔ `shift 2` with one argument left shifts nothing and returns non-zero; with `set -e`
# off that spins forever. Run over ssh from the deploy, that hangs the deploy with no
# output at all. Found in pre-merge review 2026-09-09 (exit 124 under `timeout`).
# ⚠ `timeout` is the instrument on purpose: asserting the exit code alone would hang
# this harness rather than fail it.
timeout 8 bash "$CHECK" --group >/dev/null 2>&1
check "trailing --group -> exit 2, and does NOT hang" "2" "$?"
timeout 8 bash "$CHECK" --group alarm --ship-dir >/dev/null 2>&1
check "trailing --ship-dir -> exit 2, and does NOT hang" "2" "$?"
timeout 8 bash "$CHECK" --group alarm --manifest >/dev/null 2>&1
check "trailing --manifest -> exit 2, and does NOT hang" "2" "$?"

echo "=== extra: CANNOT DETERMINE (2) outranks DRIFT (1), whatever the file order ==="
# ⛔ Plain `rc=` kept whichever failure came LAST, so an unreadable manifest entry on
# one file followed by ordinary drift on another reported the TAMER state. "I could not
# tell" is the more alarming answer and must survive.
build_world
grep -v 'gv-session-alarm.sh' "$MAN" > "$MAN.tmp" && mv "$MAN.tmp" "$MAN"   # file 1 -> unrecorded (2)
echo "# tampered" >> "$HOME/.config/systemd/user/gv-session-alarm.timer"    # file 3 -> drift (1)
rc="$(run)"
check "a 2 followed by a 1 still exits 2" "2" "$rc"
check "…and BOTH are reported, not just the last" "2" \
      "$(grep -c '⚠ \[drift-check\]' "$WORK/out.txt")"

echo
if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
exit "$fail"
