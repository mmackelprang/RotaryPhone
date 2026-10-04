#!/usr/bin/env bash
# install-gv-auto-relogin.sh against a fake HOME and a fake shipped tree. No box, no
# systemd: `systemctl` is a stub on PATH that records its arguments, so "the timer was not
# enabled" is an observed absence, not an inference. docs/plans/gv-auto-relogin.md Task 15.
#
# ⭐ Negative controls at the end: mutants of the installer, each caught by a named case.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="${HERE}/.."
REAL_INSTALLER="${SRC}/install-gv-auto-relogin.sh"
INSTALLER_SRC="${GV_RELOGIN_INSTALLER:-$REAL_INSTALLER}"

if [ "$(uname -s)" != "Linux" ]; then
    echo "FAILURES PRESENT: lane L must run on Linux (got $(uname -s))"; exit 2
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
export HOME="$WORK/home"
SHIP="$WORK/ship"
STUBBIN="$WORK/stubbin"
SYSLOG="$WORK/systemctl.log"
mkdir -p "$STUBBIN"
cat > "$STUBBIN/systemctl" <<EOF
#!/usr/bin/env bash
echo "\$*" >> "$SYSLOG"
exit 0
EOF
chmod 755 "$STUBBIN/systemctl"
export PATH="$STUBBIN:$PATH"
export GV_RELOGIN_ACCOUNT_FILE="$WORK/gv-account.conf"
export GV_RELOGIN_STATE_FILE="$HOME/.local/state/gv-auto-relogin.state"
export GV_RELOGIN_LOCK_FILE="$HOME/.local/state/gv-auto-relogin.lock"

fail=0; cases=0
check() { cases=$((cases + 1))
    if [ "$2" = "$3" ]; then printf '  PASS %s\n' "$1"
    else printf '  FAIL %s: expected [%s] got [%s]\n' "$1" "$2" "$3"; fail=1; fi; }

# A shipped tree exactly as the deploy leaves it, plus a fake HOME that already holds the
# two files this installer must NOT touch.
world() { # world [--with-driver]
    rm -rf "$SHIP" "$HOME" "$SYSLOG" "$GV_RELOGIN_ACCOUNT_FILE"
    mkdir -p "$SHIP/systemd" "$HOME/bin"
    cp "$SRC/gv-auto-relogin.sh" "$SRC/gv-auto-relogin-breaker.sh" "$SRC/gv-cdp.py" "$SHIP/"
    cp "$SRC/systemd/gv-auto-relogin.service" "$SRC/systemd/gv-auto-relogin.timer" "$SHIP/systemd/"
    cp "$INSTALLER_SRC" "$SHIP/install-gv-auto-relogin.sh"
    [ "${1:-}" = "--with-driver" ] && cp "$HERE/gv-relogin-driver-stub.py" "$SHIP/gv-relogin-signin.py"
    printf '#!/bin/sh\n# the installed alarm, a sentinel\n' > "$HOME/bin/gv-session-alarm.sh"
    printf '#!/bin/sh\n# the installed bridge-ensure, a sentinel\n' > "$HOME/bin/gv-bridge-ensure.sh"
    chmod 755 "$HOME/bin/gv-session-alarm.sh" "$HOME/bin/gv-bridge-ensure.sh"
    : > "$SYSLOG"
}
inst() { bash "$SHIP/install-gv-auto-relogin.sh" "$@" > "$WORK/out.txt" 2>&1; echo "$?"; }
mode_of() { stat -c %a "$1" 2>/dev/null || echo absent; }
account_ok() { printf 'GV_ACCOUNT_EMAIL=a@example.invalid\nGV_ACCOUNT_PASSWORD=x\n' > "$GV_RELOGIN_ACCOUNT_FILE"; chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; }
enabled() { grep -c 'enable --now gv-auto-relogin.timer' "$SYSLOG"; }

echo "=== a default install ==="
# ⚠ Every --enable precondition is SATISFIED here (driver shipped, alarm installed, a good
# account file), so "not enabled" can only mean "not the default" — not "a guard refused".
world --with-driver; account_ok
before_alarm="$(sha256sum "$HOME/bin/gv-session-alarm.sh" "$HOME/bin/gv-bridge-ensure.sh")"
check "default install exits 0" "0" "$(inst)"
check "actuator, breaker, CDP helper at 755" "755 755 755" \
      "$(mode_of "$HOME/bin/gv-auto-relogin.sh") $(mode_of "$HOME/bin/gv-auto-relogin-breaker.sh") $(mode_of "$HOME/bin/gv-cdp.py")"
check "both units at 644" "644 644" \
      "$(mode_of "$HOME/.config/systemd/user/gv-auto-relogin.service") $(mode_of "$HOME/.config/systemd/user/gv-auto-relogin.timer")"
check "installed copies are byte-identical to the shipped ones" "same" \
      "$(cmp -s "$SHIP/gv-auto-relogin.sh" "$HOME/bin/gv-auto-relogin.sh" && cmp -s "$SHIP/gv-cdp.py" "$HOME/bin/gv-cdp.py" && echo same || echo differs)"
check "⛔ --enable is NOT the default: systemctl was never asked to enable" "0" "$(enabled)"
check "…but the daemon WAS reloaded" "1" "$(grep -c '^--user daemon-reload$' "$SYSLOG")"
check "⛔ a fresh install's breaker reports TRIPPED" "1" "$(grep -c 'breaker: state *TRIPPED' "$WORK/out.txt")"
check "⛔ the installer leaves the alarm and gv-bridge-ensure.sh byte-identical" "$before_alarm" \
      "$(sha256sum "$HOME/bin/gv-session-alarm.sh" "$HOME/bin/gv-bridge-ensure.sh")"
check "it creates no breaker state" "absent" "$([ -e "$GV_RELOGIN_STATE_FILE" ] && echo present || echo absent)"
world
check "no driver shipped -> exit 0, none installed, and the log says auto-relogin is inert" "0:absent:1" \
      "$(inst):$(mode_of "$HOME/bin/gv-relogin-signin.py"):$(grep -c 'Auto-relogin is inert' "$WORK/out.txt")"
check "…and it creates no credential file" "absent" "$([ -e "$GV_RELOGIN_ACCOUNT_FILE" ] && echo present || echo absent)"

echo "=== a fresh install makes NO attempt until a human --reset ==="
world --with-driver; inst >/dev/null
check "the shipped driver is installed at 755" "755" "$(mode_of "$HOME/bin/gv-relogin-signin.py")"
account_ok
export GV_STUB_DIR="$WORK/stub"; mkdir -p "$GV_STUB_DIR/driver"
bash "$HOME/bin/gv-auto-relogin.sh" >/dev/null 2>"$WORK/act.err"
check "⛔ the installed actuator, driver present, breaker never reset -> the driver never runs" "0" \
      "$([ -f "$GV_STUB_DIR/driver/runs.log" ] && grep -c . "$GV_STUB_DIR/driver/runs.log" || echo 0)"
check "…and it says the breaker refused" "1" "$(grep -c 'not attempting: REFUSED breaker TRIPPED (state_missing)' "$WORK/act.err")"

echo "=== --enable refuses, non-zero and saying which, on each count ==="
world --with-driver; inst >/dev/null; : > "$SYSLOG"
check "no account file -> refused" "1:1:0" \
      "$(inst --enable):$(grep -c 'refusing --enable: .*gv-account.conf is missing' "$WORK/out.txt"):$(enabled)"
account_ok; chmod 644 "$GV_RELOGIN_ACCOUNT_FILE"; : > "$SYSLOG"
check "account file mode 644 -> refused" "1:1:0" \
      "$(inst --enable):$(grep -c 'refusing --enable: .* is mode 644, not 600' "$WORK/out.txt"):$(enabled)"
chmod 600 "$GV_RELOGIN_ACCOUNT_FILE"; rm -f "$HOME/bin/gv-session-alarm.sh"; : > "$SYSLOG"
check "⛔ no alarm installed -> refused (the escalation path is absent)" "1:1:0" \
      "$(inst --enable):$(grep -c 'refusing --enable: the GV session alarm is not installed' "$WORK/out.txt"):$(enabled)"
world; inst >/dev/null; account_ok; : > "$SYSLOG"
check "no driver installed -> refused" "1:1:0" \
      "$(inst --enable):$(grep -c 'refusing --enable: no sign-in driver' "$WORK/out.txt"):$(enabled)"
world --with-driver; account_ok; : > "$SYSLOG"
check "all four satisfied -> --enable enables the timer" "0:1" "$(inst --enable):$(enabled)"
check "…and still does not arm the breaker" "absent" "$([ -e "$GV_RELOGIN_STATE_FILE" ] && echo present || echo absent)"

echo "=== partial ship, and an installed driver the deploy no longer carries ==="
world; rm -f "$SHIP/gv-cdp.py"
check "a missing shipped file -> non-zero, named" "1:1" "$(inst):$(grep -c 'missing .*gv-cdp.py' "$WORK/out.txt")"
world --with-driver; inst >/dev/null; rm -f "$SHIP/gv-relogin-signin.py"
check "installed driver, none shipped -> left in place, and said so" "0:755:1" \
      "$(inst):$(mode_of "$HOME/bin/gv-relogin-signin.py"):$(grep -c 'installed but this deploy did not ship one' "$WORK/out.txt")"

if [ -n "${GV_RELOGIN_INSTALLER:-}" ]; then
    echo; if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED (against ${INSTALLER_SRC})"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
    exit "$fail"
fi

echo "=== NEGATIVE CONTROLS ==="
mutant() { # mutant NAME EXPECTED-FAILING-CASE SED-SCRIPT
    local m="$WORK/mutant-$1.sh"
    sed -e "$3" "$REAL_INSTALLER" > "$m"
    if cmp -s "$m" "$REAL_INSTALLER"; then check "mutant $1 differs" "differs" "identical"; return; fi
    # Captured first: `bash … | grep -q` under pipefail reads a SIGPIPE'd child as a miss.
    local out; out="$(GV_RELOGIN_INSTALLER="$m" bash "$0" 2>&1)"
    check "mutant $1 is caught by: $2" "caught" \
          "$(printf '%s\n' "$out" | grep -qF "FAIL $2" && echo caught || echo MISSED)"
}
mutant enabled-by-default "⛔ --enable is NOT the default: systemctl was never asked to enable" 's/^ENABLE_TIMER=0$/ENABLE_TIMER=1/'
mutant no-alarm-guard "⛔ no alarm installed -> refused (the escalation path is absent)" \
    's/^    \[ -x "\${BIN_DIR}\/gv-session-alarm.sh" \] \\$/    true \\/'
mutant no-mode-guard "account file mode 644 -> refused" 's/^    \[ "\$mode" = "600" \] \\$/    true \\/'
mutant arms-breaker "…and still does not arm the breaker" \
    's/^    log "timer ENABLED. The breaker is NOT armed by this; see its state below."$/    "${BIN_DIR}\/gv-auto-relogin.sh" --reset >\/dev\/null/'
mutant touches-alarm "⛔ the installer leaves the alarm and gv-bridge-ensure.sh byte-identical" \
    's/^mkdir -p "\$BIN_DIR" "\$SYSTEMD_USER_DIR" "\$STATE_DIR"$/mkdir -p "$BIN_DIR" "$SYSTEMD_USER_DIR" "$STATE_DIR"; echo x >> "$BIN_DIR\/gv-session-alarm.sh"/'

echo
if [ "$fail" -eq 0 ]; then echo "ALL ${cases} CASES PASSED"; else echo "FAILURES PRESENT (${cases} cases run)"; fi
exit "$fail"
