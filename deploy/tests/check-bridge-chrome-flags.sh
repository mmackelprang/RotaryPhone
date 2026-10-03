#!/usr/bin/env bash
# =============================================================================
# Pins the GV bridge Chrome's rendering flags in EVERY path that launches it.
#
#   bash deploy/tests/check-bridge-chrome-flags.sh
#
# Exit 0 only if (a) every check passes against the real files AND (b) every
# mutation below is caught. No box, no Chrome: systemd-run and pgrep are stubbed.
#
# Launch paths, as of 2026-09-25 (grep for google-chrome / systemd-run under deploy/):
#
#   1. gv-bridge-ensure.sh      -- THE launch line. The watchdog timer, the login
#                                  autostart entry and the desktop shortcut all run it.
#   2. gv-bridge-restart.sh     -- kills, then DELEGATES to ensure.sh. It must not
#                                  carry its own launch line: the box's installed copy
#                                  did, and silently lacked the CDP flags (2026-08-18).
#   3. setup-gvbridge.sh        -- the opt-in, never-enabled legacy snap-Chromium unit.
#                                  Superseded, but it is a launch path that exists.
#
# NOT a bridge launch path: CookieRetriever.cs (gv-login) starts Chrome on its OWN
# profile (chrome-debug-profile), never on ~/.config/gv-bridge-chrome.
#
# ⭐ WHY paths 1 and 2 are tested by RUNNING them rather than grepping them. A grep
# for the flag passes on a commented-out flag, on a flag added to --print-config's
# output but not to the array systemd-run receives, and on a restart script that
# mentions ensure.sh in a comment while launching Chrome itself. Each of those is a
# mutation below. The argv asserted here is the argv the stubbed systemd-run
# actually RECEIVED.
#
# ⚠ A passing run of this file is not evidence its checks can fail. The mutation
# section is: each mutant disables one check's subject and the run must go red.
# =============================================================================
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$(cd "${HERE}/.." && pwd)"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FLAG="--disable-backgrounding-occluded-windows"
# Companions that must survive alongside it. The CDP port is load-bearing for
# cookie refresh; the other three are the rest of "keep rendering while covered".
COMPANIONS=(
  "--disable-renderer-backgrounding"
  "--disable-background-timer-throttling"
  "--ozone-platform=wayland"
  # The DEFAULT port: the test deliberately does not set GV_BRIDGE_CDP_PORT, so a
  # drifted default in ensure.sh goes red here.
  "--remote-debugging-port=9224"
  "--remote-allow-origins=*"
)

# --- stubs --------------------------------------------------------------------
STUBS="${WORK}/stubs"
mkdir -p "$STUBS"
# systemd-run APPENDS the argv it was handed, one per line, then an end marker,
# and "succeeds". Appending (not overwriting) is what lets a check see a SECOND
# launch -- an extra flagless launch before the real one would otherwise vanish.
# The browser binaries are stubbed the same way, so a launch that bypasses
# systemd-run is recorded too (and can never start a real Chrome on a dev box).
# Not coverable: a launch by absolute path (/opt/google/chrome/chrome).
cat > "${STUBS}/systemd-run" <<'EOF'
#!/usr/bin/env bash
{ printf '%s\n' "$@"; echo '@@END-OF-CALL@@'; } >> "${RECORD:?RECORD unset}"
EOF
for b in google-chrome google-chrome-stable chrome chromium chromium-browser; do
    cp "${STUBS}/systemd-run" "${STUBS}/${b}"
done
# pgrep: the bridge is never already running, so every run reaches the launch.
# pkill: never reached (pgrep says nothing matches), stubbed so a mistake cannot
# kill a real browser on the machine running this test.
printf '#!/usr/bin/env bash\nexit 1\n' > "${STUBS}/pgrep"
printf '#!/usr/bin/env bash\nexit 1\n' > "${STUBS}/pkill"
chmod +x "${STUBS}"/*

# --- the checks -----------------------------------------------------------------
# Every check prints PASS/FAIL. $failed counts FAILs within one check_all() run.
failed=0
say() { if [ "$1" = ok ]; then [ "$QUIET" = 1 ] || printf '  PASS %s\n' "$2"
        else [ "$QUIET" = 1 ] || printf '  FAIL %s\n' "$2"; failed=$((failed + 1)); fi; }

count_line() { grep -cxF -- "$1" "$2" 2>/dev/null || true; }

assert_argv() {  # <label> <recorded-argv-file>
    local label="$1" raw="$2" rec="${2}.argv"
    if [ ! -s "$raw" ]; then say fail "${label}: systemd-run was never called"; return; fi
    local calls; calls="$(count_line '@@END-OF-CALL@@' "$raw")"
    [ "$calls" = 1 ] && say ok   "${label}: exactly one launch" \
                     || say fail "${label}: ${calls} launches recorded (want 1)"
    grep -vxF '@@END-OF-CALL@@' "$raw" > "$rec"
    local n; n="$(count_line "$FLAG" "$rec")"
    [ "$n" = 1 ] && say ok "${label}: ${FLAG} passed exactly once" \
                 || say fail "${label}: ${FLAG} passed ${n} times (want 1)"
    for c in "${COMPANIONS[@]}"; do
        [ "$(count_line "$c" "$rec")" = 1 ] && say ok "${label}: ${c}" \
                                            || say fail "${label}: ${c} missing"
    done
    grep -q -- 'password-store' "$rec" && say fail "${label}: --password-store present (destroys the v11 cookies)" \
                                       || say ok   "${label}: no --password-store"
}

# Installs ENSURE and RESTART side by side, the way ~/bin holds them, and runs them.
check_all() {  # <ensure> <restart> <setup>
    local ensure="$1" restart="$2" setup="$3"
    failed=0
    local bin="${WORK}/bin" prof="${WORK}/profile" ext="${WORK}/ext"
    rm -rf "$bin" "$prof" "$ext"; mkdir -p "$bin" "$prof"
    install -m 755 "$ensure"  "${bin}/gv-bridge-ensure.sh"
    install -m 755 "$restart" "${bin}/gv-bridge-restart.sh"

    local -a env_=( PATH="${STUBS}:${PATH}" GV_BRIDGE_PROFILE="$prof"
                    GV_BRIDGE_LOG="${WORK}/bridge.log" GV_BRIDGE_LOCK="${WORK}/bridge.lock" )
    # GV_BRIDGE_CDP_PORT is deliberately NOT set: the default is what the box runs.
    unset GV_BRIDGE_CDP_PORT

    # Path 1, both branches of the --load-extension conditional: the array is built
    # in two appends around it, so a flag in the wrong half would vanish in one.
    rm -f "${WORK}/rec"
    env "${env_[@]}" RECORD="${WORK}/rec" GV_BRIDGE_EXTENSION_DIR="${WORK}/absent" \
        bash "${bin}/gv-bridge-ensure.sh" >/dev/null 2>&1
    assert_argv "ensure (no extension dir)" "${WORK}/rec"

    mkdir -p "$ext"; rm -f "${WORK}/rec"
    env "${env_[@]}" RECORD="${WORK}/rec" GV_BRIDGE_EXTENSION_DIR="$ext" \
        bash "${bin}/gv-bridge-ensure.sh" >/dev/null 2>&1
    assert_argv "ensure (extension dir present)" "${WORK}/rec"

    # --print-config must report the argv that is launched, not a second list.
    env "${env_[@]}" GV_BRIDGE_EXTENSION_DIR="$ext" bash "${bin}/gv-bridge-ensure.sh" --print-config \
        2>/dev/null | sed -n 's/^chrome_arg=//p' > "${WORK}/printed"
    # ensure's recorded argv is: --user --collect google-chrome <CHROME_ARGS...>
    tail -n +4 "${WORK}/rec.argv" > "${WORK}/launched"
    cmp -s "${WORK}/printed" "${WORK}/launched" \
        && say ok   "ensure: --print-config reports exactly the launched argv" \
        || say fail "ensure: --print-config and the launched argv differ"

    # Path 2: the nightly recycle, run for real against the ensure beside it.
    rm -f "${WORK}/rec"
    env "${env_[@]}" RECORD="${WORK}/rec" GV_BRIDGE_EXTENSION_DIR="${WORK}/absent" \
        bash "${bin}/gv-bridge-restart.sh" >/dev/null 2>&1
    assert_argv "restart -> relaunch" "${WORK}/rec"
    # And it must not own a launch line of its own (comments excluded).
    if grep -v '^[[:space:]]*#' "${bin}/gv-bridge-restart.sh" | grep -qE 'google-chrome|systemd-run'; then
        say fail "restart: carries its own Chrome launch line (must delegate to ensure.sh)"
    else
        say ok   "restart: no launch line of its own -- delegates to ensure.sh"
    fi

    # Path 3: the legacy unit's ExecStart block in setup-gvbridge.sh (static: running
    # it would need sudo and snap). The block runs from ExecStart= to the URL.
    sed -n '/^ExecStart=\${CHROME_BIN}/,/https:\/\/voice.google.com/p' "$setup" > "${WORK}/legacy"
    if [ ! -s "${WORK}/legacy" ]; then
        say fail "legacy unit: ExecStart block not found in setup-gvbridge.sh"
    elif grep -v '^[[:space:]]*#' "${WORK}/legacy" | grep -qF -- "$FLAG"; then
        say ok   "legacy unit: ${FLAG}"
    else
        say fail "legacy unit: ${FLAG} missing"
    fi

    [ "$failed" -eq 0 ]
}

# --- 1. the real files ------------------------------------------------------------
QUIET=0
echo "=== the shipped files ==="
check_all "${SRC}/gv-bridge-ensure.sh" "${SRC}/gv-bridge-restart.sh" "${SRC}/setup-gvbridge.sh"
real_ok=$?

# --- 2. mutations: every one must be caught ------------------------------------------
QUIET=1
M="${WORK}/mutants"; mkdir -p "$M"
caught=0; total=0
mutant() {  # <description> <ensure> <restart> <setup>
    total=$((total + 1))
    if check_all "$2" "$3" "$4"; then
        printf '  FAIL mutation NOT caught: %s\n' "$1"
    else
        printf '  PASS mutation caught (%d check(s) red): %s\n' "$failed" "$1"
        caught=$((caught + 1))
    fi
}

E="${SRC}/gv-bridge-ensure.sh"; R="${SRC}/gv-bridge-restart.sh"; S="${SRC}/setup-gvbridge.sh"

echo "=== mutations ==="
grep -vxF -- "  ${FLAG}" "$E" > "${M}/ensure-deleted"
mutant "ensure.sh with the flag line deleted" "${M}/ensure-deleted" "$R" "$S"

sed "s|^  ${FLAG}\$|  # ${FLAG}|" "$E" > "${M}/ensure-commented"
mutant "ensure.sh with the flag commented out (a grep would still find it)" "${M}/ensure-commented" "$R" "$S"

# Flag moved into --print-config's output only: the self-report claims it, the launch lacks it.
grep -vxF -- "  ${FLAG}" "$E" \
  | sed "s|^  printf 'chrome_arg=%s\\\\n' \"\${CHROME_ARGS\[@\]}\"\$|&\n  printf 'chrome_arg=%s\\\\n' '${FLAG}'|" \
  > "${M}/ensure-printonly"
mutant "ensure.sh reporting the flag in --print-config but not launching with it" "${M}/ensure-printonly" "$R" "$S"

# The box's INSTALLED restart shape (2026-07-16): its own launch line, no flag.
cat > "${M}/restart-own-launch" <<'EOF'
#!/usr/bin/env bash
# delegates to gv-bridge-ensure.sh -- says the comment; the code below does not.
set -u
systemd-run --user --collect google-chrome --user-data-dir="${GV_BRIDGE_PROFILE}" --disable-renderer-backgrounding --disable-background-timer-throttling --ozone-platform=wayland --remote-debugging-port=9224 https://voice.google.com
EOF
mutant "restart.sh launching Chrome itself without the flag (the box's installed shape)" "$E" "${M}/restart-own-launch" "$S"

# The three below escaped the first version of this test (pre-merge review 2026-09-25).
# An extra flagless launch BEFORE the handoff, spelled so the grep cannot see it.
cat > "${M}/restart-extra-launch" <<'EOF'
#!/usr/bin/env bash
set -u
sd=systemd
"${sd}-run" --user --collect "google""-chrome" --user-data-dir="${GV_BRIDGE_PROFILE}" https://voice.google.com
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/gv-bridge-ensure.sh"
EOF
mutant "restart.sh with an obfuscated extra launch before delegating" "$E" "${M}/restart-extra-launch" "$S"

sed 's|^systemd-run --user --collect google-chrome "\${CHROME_ARGS\[@\]}"|systemd-run --user --collect google-chrome --mute-audio; &|' "$E" > "${M}/ensure-twice"
mutant "ensure.sh launching twice, the first time without the flag" "${M}/ensure-twice" "$R" "$S"

sed 's|GV_BRIDGE_CDP_PORT:-9224|GV_BRIDGE_CDP_PORT:-9225|' "$E" > "${M}/ensure-port"
mutant "ensure.sh with its default CDP port drifted to 9225" "${M}/ensure-port" "$R" "$S"

grep -vF -- "    ${FLAG} \\\\" "$S" > "${M}/setup-deleted"
mutant "setup-gvbridge.sh legacy unit without the flag" "$E" "$R" "${M}/setup-deleted"

# Sanity for the mutation harness itself: each mutant must actually differ from its source,
# or a "caught" above could be a broken harness rather than a working check.
for pair in "ensure-deleted:$E" "ensure-commented:$E" "ensure-printonly:$E" "ensure-twice:$E" \
            "ensure-port:$E" "setup-deleted:$S"; do
    total=$((total + 1))
    if cmp -s "${M}/${pair%%:*}" "${pair#*:}"; then
        printf '  FAIL mutant %s is identical to its source -- the mutation did not apply\n' "${pair%%:*}"
    else
        caught=$((caught + 1))
    fi
done

echo
if [ "$real_ok" -eq 0 ] && [ "$caught" -eq "$total" ]; then
    echo "OK: shipped files pass; ${caught}/${total} mutation checks behave."
    exit 0
fi
echo "FAILED: shipped files $([ "$real_ok" -eq 0 ] && echo pass || echo FAIL); ${caught}/${total} mutation checks behave."
exit 1
