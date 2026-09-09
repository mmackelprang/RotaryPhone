#!/usr/bin/env bash
# =============================================================================
# Reproduces the tar-pipe fallback's handling of appsettings.Production.json.
#
# Why this exists: docs/KNOWN-ISSUES.md attributed the clobber to `set -e`
# aborting the chain before the restore. That `set -e` is in the LOCAL script
# (the `set -e -o pipefail` at the top of $syncScript); the chain runs in the
# REMOTE shell, which does not inherit it. Case A is the falsification. Cases B1
# and B2 are the two ways the clobber is actually produced. Case C is the fix, and
# case D is whether the deploy can now TELL when the extract fails.
#
# ---------------------------------------------------------------------------
# ⚠ CORRECTION 2026-09-09, measured while building this script.
#
# The plan (docs/plans/deploy-tooling-honest-deploy-plan.md, Task 1) proposed a
# single case B whose fixture was a read-only parent directory holding a stale
# backup, commented as "cp -f fails and is swallowed; `[ -f ]` then sees the
# STALE file". Its assertions pass. Its explanation does not:
#
#     cp -f src ro/bak        -> exit 0, backup content becomes BOX-AUTHORITATIVE
#     mv -f ro/bak dst/...    -> mv: cannot move ...: Permission denied, exit 1
#
# Writing to an ALREADY-EXISTING, writable file needs no write permission on the
# containing directory -- only unlinking it does. So in that fixture the backup
# SUCCEEDS and it is the RESTORE that fails. It is a real clobber, but it is a
# restore-side one, and the plan attributed it to the backup side.
#
# That is the same defect this PR exists to fix, one level up: a green check
# whose stated mechanism is wrong. So the fixture is kept -- as B2, correctly
# labelled -- and B1 is added to demonstrate the backup-side failure the plan
# describes. Both are real; neither is hypothetical.
# ---------------------------------------------------------------------------
#
# Run as a NORMAL USER. Case B2 relies on not being able to write a read-only
# directory, which root ignores.
# =============================================================================
set -u
[ "$(id -u)" -eq 0 ] && { echo "run as a non-root user; case B2 is meaningless as root"; exit 2; }

WORK="$(mktemp -d)"
trap 'chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT

FAILED=0
check() {  # check <label> <condition-result> <detail>
    if [ "$2" -eq 0 ]; then echo "$1: PASS $3"; else echo "$1: UNEXPECTED $3"; FAILED=1; fi
}

build_fixture() {
    rm -rf "$WORK/src" "$WORK/dst"
    mkdir -p "$WORK/src/wwwroot/assets" "$WORK/dst/wwwroot/assets"
    echo 'TEMPLATE-FROM-REPO'  > "$WORK/src/appsettings.Production.json"
    echo 'NEW-BINARY'          > "$WORK/src/RotaryPhoneController.Server"
    echo 'NEW-ASSET'           > "$WORK/src/wwwroot/assets/app.js"
    echo 'BOX-AUTHORITATIVE'   > "$WORK/dst/appsettings.Production.json"
    echo 'OLD-BINARY'          > "$WORK/dst/RotaryPhoneController.Server"
    echo 'OLD-ASSET'           > "$WORK/dst/wwwroot/assets/app.js"
    # A .playwright tree, so the prune is exercised rather than assumed, and an empty
    # directory so case D can assert the known empty-directory loss.
    mkdir -p "$WORK/src/.playwright/package" "$WORK/src/emptydir"
    echo 'PLAYWRIGHT' > "$WORK/src/.playwright/package/chromiumSwitches.js"
    # The archive as the PRE-FIX code built it: `tar -C <dir> -czf - .`, which carries
    # a './' member and a directory member for every directory.
    tar -C "$WORK/src" -czf "$WORK/payload.tgz" .
}

# The archive as the SHIPPED code builds it. This must stay a verbatim copy of the
# create side in deploy/Deploy-ToLinux.ps1's $syncScript -- files-only, no './'
# member, no directory members.
#
# ⚠ It deliberately does NOT use the simpler `tar -C src --exclude=… -czf - .` form.
# That form was the intermediate step (config excluded, directory members still
# present), and an archive built that way STILL makes `tar -xzf --unlink-first` exit
# 2 on its './' member -- so a test using it would print PASS while reproducing the
# very failure the shipped code exists to remove. Measured: with the './' form,
# extraction prints "tar: .: Cannot unlink: Invalid argument" and exits 2.
build_fixed_archive() {
    ( cd "$WORK/src" && find . -mindepth 1 -path ./.playwright -prune -o \( -type f -o -type l \) -print0 \
        | tar --null --exclude=./appsettings.Production.json -czf "$WORK/fixed.tgz" -T - )
}

echo "=== Case A: the chain exactly as the PRE-FIX code built it ==="
# Expected: tar exits 2 on the directory members, every regular file is extracted
# anyway, the restore mv RUNS and succeeds, and the chain exits 0. The chain exit
# of 0 is Defect 4: it is chmod's status, not tar's.
build_fixture
sh -c "
  cp -f '$WORK/dst/appsettings.Production.json' '$WORK/rp-prod.bak' 2>/dev/null || true
  tar -xzf '$WORK/payload.tgz' --unlink-first -C '$WORK/dst'
  [ -f '$WORK/rp-prod.bak' ] && mv -f '$WORK/rp-prod.bak' '$WORK/dst/appsettings.Production.json' || true
  chmod +x '$WORK/dst/RotaryPhoneController.Server'
"
a_exit=$?
a_cfg="$(cat "$WORK/dst/appsettings.Production.json")"
a_bin="$(cat "$WORK/dst/RotaryPhoneController.Server")"
a_bak="$([ -f "$WORK/rp-prod.bak" ] && echo present || echo consumed)"
echo "A: chain_exit=$a_exit  config=$a_cfg  binary=$a_bin  backup=$a_bak"
[ "$a_exit" -eq 0 ] && [ "$a_cfg" = "BOX-AUTHORITATIVE" ] && [ "$a_bin" = "NEW-BINARY" ] \
    && [ "$a_bak" = "consumed" ]
check "A" $? "(restore ran and worked; chain reported 0 while tar exited 2)"
echo ""

echo "=== Case B1: BACKUP-side failure -- first deploy, stale /tmp/rp-prod.bak ==="
# The plan's §0.2 lists "first deploy with no file yet" as a way the backup step
# fails. Measured here: `cp -f` fails because the SOURCE does not exist, the
# `2>/dev/null || true` swallows it, the `[ -f ]` guard is nonetheless TRUE
# because a backup from an earlier run is still sitting in /tmp, and the restore
# cheerfully installs that STALE content.
#
# ⛔ This is the worst of the three. The box does not get the repo template --
# which is at least a reviewable file in version control -- it gets arbitrary
# content from a previous deploy, possibly of a different box, and the chain
# exits 0.
build_fixture
rm -f "$WORK/dst/appsettings.Production.json"          # first deploy: no config yet
echo 'STALE-FROM-ANOTHER-RUN' > "$WORK/rp-prod.bak"    # left by an earlier run
sh -c "
  cp -f '$WORK/dst/appsettings.Production.json' '$WORK/rp-prod.bak' 2>/dev/null || true
  tar -xzf '$WORK/payload.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null
  [ -f '$WORK/rp-prod.bak' ] && mv -f '$WORK/rp-prod.bak' '$WORK/dst/appsettings.Production.json' || true
  chmod +x '$WORK/dst/RotaryPhoneController.Server'
"
b1_exit=$?
b1_cfg="$(cat "$WORK/dst/appsettings.Production.json")"
echo "B1: chain_exit=$b1_exit  config=$b1_cfg"
[ "$b1_exit" -eq 0 ] && [ "$b1_cfg" = "STALE-FROM-ANOTHER-RUN" ]
check "B1" $? "(a stale backup from an earlier run was installed as the box's config, silently)"
echo ""

echo "=== Case B2: RESTORE-side failure -- read-only parent directory ==="
# The plan's original case B fixture, kept and correctly labelled. Stands in for
# a /tmp this uid cannot unlink from (a sticky /tmp holding an rp-prod.bak owned
# by another uid). Measured: the `cp -f` SUCCEEDS -- overwriting an existing
# writable file needs no directory write permission -- and it is the restore `mv`
# that fails, because the rename has to unlink the source. Net: tar's template
# stays on the box AND the backup survives in /tmp, which is exactly the state
# PR #72 UAT found (finding L3).
build_fixture
mkdir -p "$WORK/ro"
echo 'STALE-FROM-ANOTHER-RUN' > "$WORK/ro/rp-prod.bak"
chmod 0555 "$WORK/ro"
sh -c "
  cp -f '$WORK/dst/appsettings.Production.json' '$WORK/ro/rp-prod.bak' 2>/dev/null || true
  tar -xzf '$WORK/payload.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null
  [ -f '$WORK/ro/rp-prod.bak' ] && mv -f '$WORK/ro/rp-prod.bak' '$WORK/dst/appsettings.Production.json' 2>/dev/null || true
  chmod +x '$WORK/dst/RotaryPhoneController.Server'
"
b2_exit=$?
b2_cfg="$(cat "$WORK/dst/appsettings.Production.json")"
b2_bak="$([ -f "$WORK/ro/rp-prod.bak" ] && echo survives || echo consumed)"
chmod 0755 "$WORK/ro"
echo "B2: chain_exit=$b2_exit  config=$b2_cfg  backup=$b2_bak"
[ "$b2_exit" -eq 0 ] && [ "$b2_cfg" = "TEMPLATE-FROM-REPO" ] && [ "$b2_bak" = "survives" ]
check "B2" $? "(clobbered with the repo template, silently; backup stranded in /tmp)"
echo ""

echo "=== Case C: the fix -- the config is never a member of the archive ==="
# The backup/restore dance is REMOVED. The box's file is untouched because
# nothing ever writes to it, whichever way the dance would have failed.
build_fixture
build_fixed_archive
before="$(sha256sum "$WORK/dst/appsettings.Production.json" | cut -d' ' -f1)"
sh -c "tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null; chmod +x '$WORK/dst/RotaryPhoneController.Server'"
after="$(sha256sum "$WORK/dst/appsettings.Production.json" | cut -d' ' -f1)"
c_bin="$(cat "$WORK/dst/RotaryPhoneController.Server")"
c_asset="$(cat "$WORK/dst/wwwroot/assets/app.js")"
echo "C: sha_before=$before"
echo "C: sha_after =$after"
echo "C: binary=$c_bin  asset=$c_asset"
if tar -tzf "$WORK/fixed.tgz" | grep -q appsettings.Production.json; then
    echo "C: UNEXPECTED (config is in the archive)"; FAILED=1
else
    [ "$before" = "$after" ] && [ "$c_bin" = "NEW-BINARY" ] && [ "$c_asset" = "NEW-ASSET" ]
    check "C" $? "(config byte-identical; new binary and assets still landed)"
fi
echo ""

echo "=== Case C-B1 / C-B2: the fix re-run against BOTH clobber fixtures ==="
# Task 3's strengthened acceptance criterion. The scope doc's original criterion
# ("a failed rsync followed by the tar path leaves the config byte-identical")
# PASSES against the unfixed code in the plain case -- a live deploy on
# 2026-09-09 satisfied it while the defect was present. These do not.

# C-B1: first deploy, stale backup present. The fixed chain must not create the
# config at all -- Deploy-ToLinux.ps1's RP_CFG_MISSING probe scps the template in
# on a genuine first deploy, and that is the only path that should ever write it.
build_fixture
build_fixed_archive
rm -f "$WORK/dst/appsettings.Production.json"
echo 'STALE-FROM-ANOTHER-RUN' > "$WORK/rp-prod.bak"
sh -c "tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null; chmod +x '$WORK/dst/RotaryPhoneController.Server'"
cb1_present="$([ -f "$WORK/dst/appsettings.Production.json" ] && echo yes || echo no)"
cb1_bak="$(cat "$WORK/rp-prod.bak" 2>/dev/null)"
echo "C-B1: config_created=$cb1_present  stale_bak_untouched=$cb1_bak"
[ "$cb1_present" = "no" ] && [ "$cb1_bak" = "STALE-FROM-ANOTHER-RUN" ]
check "C-B1" $? "(no stale content installed; the dance that would have installed it is gone)"

# C-B2: read-only /tmp stand-in. The fixed chain never touches either path.
build_fixture
build_fixed_archive
mkdir -p "$WORK/ro"
echo 'STALE-FROM-ANOTHER-RUN' > "$WORK/ro/rp-prod.bak"
chmod 0555 "$WORK/ro"
cb2_before="$(sha256sum "$WORK/dst/appsettings.Production.json" | cut -d' ' -f1)"
sh -c "tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/dst' 2>/dev/null; chmod +x '$WORK/dst/RotaryPhoneController.Server'"
cb2_after="$(sha256sum "$WORK/dst/appsettings.Production.json" | cut -d' ' -f1)"
cb2_cfg="$(cat "$WORK/dst/appsettings.Production.json")"
chmod 0755 "$WORK/ro"
echo "C-B2: config=$cb2_cfg  sha_unchanged=$([ "$cb2_before" = "$cb2_after" ] && echo yes || echo no)"
[ "$cb2_before" = "$cb2_after" ] && [ "$cb2_cfg" = "BOX-AUTHORITATIVE" ]
check "C-B2" $? "(the fixture that clobbers the old chain cannot reach the file at all)"
echo ""

echo "=== Case D: the chain can now report its own failure (Defect 4) ==="
# Cases A-C are about WHAT lands on the box. This one is about whether the deploy can
# TELL. The old remote compound ended in chmod, so it reported chmod's status -- 0 --
# while tar had exited 2, on every single run. Two properties, and the negative
# control is the one that matters: a check never seen to fail is not known to work.
build_fixture
build_fixed_archive

# D1: archive shape. No './' member and no directory members is what lets
# --unlink-first stop failing; the config and .playwright must still be absent.
d_dirs=$(tar -tzf "$WORK/fixed.tgz" | grep -c '/$')
d_dot=$(tar -tzf "$WORK/fixed.tgz" | grep -cx '\./')
d_prod=$(tar -tzf "$WORK/fixed.tgz" | grep -c 'appsettings.Production.json')
d_pw=$(tar -tzf "$WORK/fixed.tgz" | grep -c '\.playwright')
echo "D1: dir_members=$d_dirs  dot_member=$d_dot  prod_config=$d_prod  playwright=$d_pw"
[ "$d_dirs" -eq 0 ] && [ "$d_dot" -eq 0 ] && [ "$d_prod" -eq 0 ] && [ "$d_pw" -eq 0 ]
check "D1" $? "(files-only; nothing for --unlink-first to fail on, prune and exclude both applied)"

# D2: a GOOD extract exits 0 and prints NOTHING. The four "Cannot unlink" lines the
# old shape printed on every successful deploy are what trained the operator to scroll
# past a failing deploy.
mkdir -p "$WORK/dst"
d_err="$(sh -c "set -e; tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/dst'; chmod +x '$WORK/dst/RotaryPhoneController.Server'" 2>&1 >/dev/null)"
d_ok=$?
echo "D2: chain_exit=$d_ok  stderr='${d_err}'"
[ "$d_ok" -eq 0 ] && [ -z "$d_err" ]
check "D2" $? "(exit 0 and silent -- no Cannot unlink noise on a successful deploy)"

# D3: ⭐ THE NEGATIVE CONTROL. Point the extract at a directory that does not exist.
# The old chain reports 0 here because chmod runs last and succeeds; the new one must
# report non-zero, which is what makes the PowerShell throw in Deploy-ToLinux.ps1 fire
# and leaves the service on its prior binary.
sh -c "tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/nonexistent' 2>/dev/null; chmod +x '$WORK/dst/RotaryPhoneController.Server'"
d_old=$?
sh -c "set -e; tar -xzf '$WORK/fixed.tgz' --unlink-first -C '$WORK/nonexistent' 2>/dev/null; chmod +x '$WORK/dst/RotaryPhoneController.Server'"
d_new=$?
echo "D3: old_chain_exit=$d_old (ends in chmod, no set -e)   new_chain_exit=$d_new"
[ "$d_old" -eq 0 ] && [ "$d_new" -ne 0 ]
check "D3" $? "(the criterion the old chain fails: a failed extract is now visible)"

# D4: the known, accepted limitation. A files-only member list cannot carry an EMPTY
# directory. Asserted rather than assumed so the day the publish output gains one that
# matters, this test says so instead of the box silently missing a directory.
d_empty=$(tar -tzf "$WORK/fixed.tgz" | grep -c 'emptydir')
echo "D4: empty_dir_members=$d_empty  (expected 0 -- documented limitation, not a bug)"
[ "$d_empty" -eq 0 ]
check "D4" $? "(empty directories are dropped; see the comment in Deploy-ToLinux.ps1)"
echo ""

if [ "$FAILED" -eq 0 ]; then
    echo "ALL CASES BEHAVED AS EXPECTED"
else
    echo "SOME CASES DID NOT BEHAVE AS EXPECTED -- see UNEXPECTED lines above"
fi
exit "$FAILED"
