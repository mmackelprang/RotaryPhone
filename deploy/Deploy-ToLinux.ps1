<#
.SYNOPSIS
  Deploy RotaryPhone Controller to a Linux target from Windows.

.DESCRIPTION
  Cross-compiles for the specified Linux runtime, syncs to the target
  via SCP/SSH, installs systemd service, and restarts.

.PARAMETER TargetHost
  Target hostname or IP. Default: radio.

.PARAMETER TargetUser
  SSH user. Default: mmack.

.PARAMETER Runtime
  .NET runtime identifier. Default: linux-x64.

.PARAMETER NoRestart
  Deploy without restarting the service.

.PARAMETER Logs
  Tail journalctl after restart.

.PARAMETER PreflightOnly
  Run the pre-flight gate (interpreter + ssh/scp transport + sudo) and stop
  before the build. Touches nothing on the target beyond one probe file in /tmp,
  which it removes. Use it to answer "would a deploy work from this shell?"
  without deploying.

.EXAMPLE
  .\deploy\Deploy-ToLinux.ps1
  .\deploy\Deploy-ToLinux.ps1 -TargetHost radio -Runtime linux-x64
  .\deploy\Deploy-ToLinux.ps1 -Logs
  .\deploy\Deploy-ToLinux.ps1 -PreflightOnly
#>
[CmdletBinding()]
param(
  [switch]$NoRestart,
  [switch]$Logs,
  [switch]$PreflightOnly,
  [string]$TargetHost = "radio",
  [string]$TargetUser = "mmack",
  [string]$TargetPath = "/opt/rotary-phone",
  [ValidateSet("linux-arm64", "linux-x64")]
  [string]$Runtime = "linux-x64"
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
$PublishDir = Join-Path $RepoRoot "publish\$Runtime"
$SshTarget = "${TargetUser}@${TargetHost}"

# ⛔ Hard gate, and it runs BEFORE ANYTHING TOUCHES THE BOX -- before the build, the
# sudo mkdir, the binary sync and every scp. It needs nothing but the local repo, so
# there is no reason to discover a bad file halfway through a deploy: aborting at that
# point would leave the new binary on disk with the old service still running.
#
# Today setup-gvbridge.sh is shipped but never executed by the deploy, so
# gv-bridge-ensure.sh sits on the box as an inert file and its contents do not matter.
# The moment the deploy runs the installer (plan Task 10, not yet built) that file
# becomes an executed one, and --password-store stops being inert. On a profile
# already holding v11 cookies it makes the keyring-derived key unobtainable and Chrome
# DISCARDS them: measured live at 45 v11 -> 16 v10, destroying the Google Voice
# session. ~/.config/gv-bridge-chrome is exactly that profile. The gate lands first,
# deliberately, so the hazard is closed before the change that opens it.
#
# Scoped to this ONE file deliberately. A repo-wide search is NOT equivalent:
# scripts/bin/Debug/net10.0/.playwright/.../chromiumSwitches.js carries
# "--password-store=basic" as one of Playwright's own Chromium defaults, and this very
# file now contains the literal too, so a broad grep fails on a clean tree.
$ensureSrc = Join-Path $RepoRoot "deploy\gv-bridge-ensure.sh"
if (Test-Path $ensureSrc) {
  if (Select-String -Path $ensureSrc -Pattern 'password-store' -SimpleMatch -Quiet) {
    throw "REFUSING TO DEPLOY: deploy/gv-bridge-ensure.sh contains --password-store. On ~/.config/gv-bridge-chrome that discards the v11 cookies and destroys the Google Voice session. Remove the flag, then redeploy."
  }
} else {
  throw "REFUSING TO DEPLOY: deploy/gv-bridge-ensure.sh is missing -- setup-gvbridge.sh would fail on the box after the binary sync had already landed."
}

Write-Host "=== Rotary Phone Deploy ===" -ForegroundColor Cyan
Write-Host "Target:  ${SshTarget}:${TargetPath}"
Write-Host "Runtime: $Runtime"
Write-Host ""

# =============================================================================
# PRE-FLIGHT -- prove the INTERPRETER and the TRANSPORT before anything touches
# the box.
#
# ⛔ WHY IT RUNS HERE AND NOT SOMEWHERE MORE CONVENIENT
#
# [2/4] runs `sudo mkdir -p` and `chown -R` on ${TargetPath}. That is a WRITE to
# a live, shared box, and until 2026-09-10 it ran before anything had established
# that this machine could transfer a single byte. The sibling project on this same
# box ran exactly that shape on 2026-09-09: it STOPPED SERVICES at its step 2 and
# only discovered at its step 3 that it could not transfer. Every sync error was
# therefore an outage rather than a failed deploy. Proving capability first is the
# entire point, and it costs about four seconds.
#
# It runs before the BUILD as well, which is free -- the build takes ~a minute and
# nothing asked below depends on its output. Failing after a successful build is
# merely wasteful; failing after [2/4] is an incident.
# =============================================================================

function Get-RpDeployBashCandidates {
  # Order matters: first usable wins. An explicit override comes first so an
  # operator can pin an interpreter without editing this file or touching PATH.
  $c = @()
  if ($env:RP_DEPLOY_BASH) { $c += $env:RP_DEPLOY_BASH }
  if ($env:ProgramFiles)        { $c += (Join-Path $env:ProgramFiles        'Git\bin\bash.exe') }
  if (${env:ProgramFiles(x86)}) { $c += (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe') }
  if ($env:LOCALAPPDATA)        { $c += (Join-Path $env:LOCALAPPDATA        'Programs\Git\bin\bash.exe') }
  $c += 'C:\msys64\usr\bin\bash.exe'
  # De-duplicate while preserving order; a repeated candidate would be probed twice
  # and reported twice in the failure message for no benefit.
  $seen = @{}
  $c | Where-Object { $_ -and -not $seen.ContainsKey($_) -and ($seen[$_] = $true) }
}

function Test-RpDeployBash {
  <#
    Probe ONE candidate against what the sync script actually requires. Returns
    @{ ok = <bool>; why = <string> }.

    ⚠ THIS RUNS THE REAL PIPE, NOT `command -v`, and that is the whole design.
    Measured on this workstation 2026-09-10: inside C:\msys64\usr\bin\bash.exe,
    `command -v find` SUCCEEDS -- it inherits the Windows PATH and resolves
    C:\Windows\System32\FIND.EXE. The pipe then dies with
    "FIND: Parameter format not correct". An existence probe SELECTS that
    interpreter; only running the pipe rejects it. This repo has a name for the
    difference: a truthful instrument pointed at the wrong quantity.

    ⚠ AND IT RUNS THE INTERPRETER'S OWN ssh. The tar-pipe's remote half is
    `... | ssh '<target>' 'set -e; tar -xzf - ...'` -- an ssh resolved INSIDE
    bash, which is NOT the Windows ssh.exe the rest of this script uses. Different
    binary, different HOME, different known_hosts. Measured 2026-09-10: with
    C:\msys64\usr\bin prepended so its own GNU tools win, msys64 bash PASSES the
    find|tar probe and then its ssh fails "Host key verification failed" (255).
    A probe that stopped at find|tar would have selected it, and the deploy would
    have died at the remote half -- after [2/4] had already written to the box.
    Proving `ssh.exe` works proves nothing about this; they are different programs.
  #>
  param([string]$Exe, [string]$RepoMsys, [string]$Target)

  if ([string]::IsNullOrWhiteSpace($Exe)) { return @{ ok = $false; why = 'empty path' } }
  if (-not (Test-Path -LiteralPath $Exe)) { return @{ ok = $false; why = 'not present on this machine' } }

  $body = @"
set -e -o pipefail
cd '$RepoMsys' || { echo RPPF_FAIL_CD; exit 10; }
find . -maxdepth 1 -type f -print0 | tar --null -czf - -T - > /dev/null || { echo RPPF_FAIL_PIPE; exit 11; }
ssh -o BatchMode=yes -o ConnectTimeout=10 '$Target' 'echo RPPF_REMOTE_OK' > /dev/null || { echo RPPF_FAIL_SSH; exit 12; }
echo RPPF_OK
"@

  # LF-only, no BOM -- the same requirement the sync script itself has.
  $probePath = Join-Path ([System.IO.Path]::GetTempPath()) ("rp-preflight-" + [guid]::NewGuid().ToString('N') + ".sh")
  [System.IO.File]::WriteAllText($probePath, ($body -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))

  # ⛔ $ErrorActionPreference MUST be relaxed around this call, for the reason this
  # file already documents at the `systemctl status` display near the end: `2>&1` on
  # a NATIVE command surfaces its stderr as ErrorRecords, and under
  # $ErrorActionPreference = "Stop" the first one becomes a terminating
  # NativeCommandError. Every REJECTED candidate writes to stderr by definition, so
  # without this the first bad candidate would ABORT THE DEPLOY instead of being
  # rejected and passed over -- turning a working fallback chain into a hard stop.
  $eapPrev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $out  = & $Exe $probePath 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
  } finally {
    $ErrorActionPreference = $eapPrev
    Remove-Item $probePath -ErrorAction SilentlyContinue
  }

  $joined = ($out -join ' ')
  if ($code -eq 0 -and $joined -match 'RPPF_OK') { return @{ ok = $true; why = 'ok' } }

  $why =
    if     ($joined -match 'RPPF_FAIL_CD')   { "cannot resolve the msys-style path '$RepoMsys' -- this is what the WSL launcher does" }
    elseif ($joined -match 'RPPF_FAIL_PIPE') { "'find -print0 | tar --null -T -' failed -- its PATH resolves a non-GNU find/tar (msys64 without its own /usr/bin first hits Windows FIND.EXE)" }
    elseif ($joined -match 'RPPF_FAIL_SSH')  { "its OWN ssh cannot reach ${Target} -- a different binary, HOME and known_hosts from ssh.exe" }
    else   { "exit ${code}: " + (($out | Select-Object -First 3) -join ' / ') }
  return @{ ok = $false; why = $why }
}

Write-Host "[pre-flight] proving the interpreter and the transport..." -ForegroundColor Yellow

# --- 1. The Windows ssh transport ------------------------------------------
#
# Checked FIRST so that "the box is unreachable" is never misreported as "no
# usable bash". The interpreter probe below also opens an ssh connection, so
# without this a network outage would blame every candidate in turn and print a
# confident, wrong diagnosis.
#
# ssh.exe/scp.exe are the one thing this script may take from PATH: they live in
# C:\WINDOWS\System32\OpenSSH on the MACHINE path, which every PowerShell
# inherits regardless of how it was launched. Only `bash` is unsafe.
foreach ($req in @('ssh', 'scp')) {
  if (-not (Get-Command $req -ErrorAction SilentlyContinue)) {
    throw "PRE-FLIGHT FAILED: '$req' is not on PATH. Expected C:\WINDOWS\System32\OpenSSH\$req.exe (machine PATH). Nothing has been done to ${TargetHost}."
  }
}

$sshProbe = ssh -o BatchMode=yes -o ConnectTimeout=10 $SshTarget "echo RPPF_REMOTE_OK"
$sshProbeExit = $LASTEXITCODE
if ($sshProbeExit -ne 0 -or "$sshProbe".Trim() -ne 'RPPF_REMOTE_OK') {
  throw "PRE-FLIGHT FAILED: cannot reach ${SshTarget} over ssh (exit $sshProbeExit, said '$("$sshProbe".Trim())'). Nothing has been done to ${TargetHost}."
}

# --- 2. Non-interactive sudo -----------------------------------------------
#
# [2/4] runs `sudo mkdir -p` + `sudo chown -R`, and [4/4] runs `sudo mv`,
# `daemon-reload` and `enable`. ssh with no tty cannot answer a password prompt,
# so a box whose sudoers changed fails at [2/4] -- i.e. mid-write. Ask now.
$sudoProbe = ssh $SshTarget "sudo -n true >/dev/null 2>&1 && echo RPPF_SUDO_OK || echo RPPF_SUDO_PROMPT"
$sudoProbeExit = $LASTEXITCODE
if ($sudoProbeExit -ne 0 -or "$sudoProbe".Trim() -ne 'RPPF_SUDO_OK') {
  throw "PRE-FLIGHT FAILED: passwordless sudo is not available for ${TargetUser} on ${TargetHost} (exit $sudoProbeExit, said '$("$sudoProbe".Trim())'). [2/4] and [4/4] both need it and cannot answer a prompt. Nothing has been done to ${TargetHost}."
}

# --- 3. A REAL transfer, verified by content -------------------------------
#
# ⛔ The owner's requirement is "prove we can transfer", and an exit code is not
# that proof -- it is what a program CLAIMED. This ships a token to the box and
# reads it back, so the assertion is about a byte that made the round trip. It is
# the same discipline as the post-sync sha256 further down, applied BEFORE the
# first write rather than after the last one.
#
# /tmp, not ${TargetPath}: the pre-flight must not create the very directory
# [2/4] exists to create, or it would mask a failure there.
$probeToken  = "RPPF-" + [guid]::NewGuid().ToString('N')
$probeLocal  = Join-Path ([System.IO.Path]::GetTempPath()) ("rp-preflight-probe-" + $PID + ".txt")
$probeRemote = "/tmp/rp-preflight-probe-$PID.txt"
[System.IO.File]::WriteAllText($probeLocal, $probeToken, (New-Object System.Text.UTF8Encoding($false)))
try {
  # Slash-converted like every other scp in this file: passed raw, a Windows path
  # reaches scp as "C:\..." and scp reads the leading "C:" as a REMOTE HOST.
  scp ($probeLocal -replace '\\', '/') "${SshTarget}:${probeRemote}" | Out-Null
  $probeScpExit = $LASTEXITCODE
  if ($probeScpExit -ne 0) {
    throw "PRE-FLIGHT FAILED: scp to ${SshTarget} failed (exit $probeScpExit). The transport cannot carry a file; NOT proceeding to [2/4], which would write to ${TargetPath} first. Nothing has been done to ${TargetHost}."
  }
  $probeEcho = ssh $SshTarget "cat '$probeRemote'; rm -f '$probeRemote'"
  $probeEchoExit = $LASTEXITCODE
  if ($probeEchoExit -ne 0 -or "$probeEcho".Trim() -ne $probeToken) {
    throw "PRE-FLIGHT FAILED: the file scp'd to ${SshTarget}:${probeRemote} did not read back intact (exit $probeEchoExit, got '$("$probeEcho".Trim())', expected '$probeToken'). Nothing has been done to ${TargetHost}."
  }
} finally {
  Remove-Item $probeLocal -ErrorAction SilentlyContinue
}

# --- 4. The interpreter -----------------------------------------------------
#
# ⛔ `bash` is NEVER taken from PATH, and putting a directory ON the PATH is NOT
# an acceptable alternative fix. Measured on this workstation 2026-09-10 against
# the PERSISTENT machine+user PATH read from the registry -- which is exactly what
# a freshly-launched PowerShell gets, 54 entries:
#
#     HIT  C:\WINDOWS\system32\bash.exe                                (machine PATH)
#     HIT  C:\Users\<u>\AppData\Local\Microsoft\WindowsApps\bash.exe   (user PATH)
#     ---  BOTH ARE THE WSL LAUNCHER
#     Git\bin  is NOT on the persistent PATH. Git\cmd IS, and holds no bash.exe.
#
# WSL bash reports uname=Linux, cannot resolve the msys-style /d/prj/... paths
# this script passes, and cannot even OPEN a script named by a Windows path:
# handed C:\Users\...\rp-deploy-sync.sh it strips the backslashes and exits 127
# with "No such file or directory". Deploys have only ever worked from a
# PowerShell LAUNCHED FROM GIT BASH, which inherits Git's paths -- a property of
# that shell's ancestry, not of this machine. Running from a clean PowerShell is
# a requirement, so the interpreter is resolved explicitly instead.
#
# ⛔ PATH is deliberately NOT mutated to fix this: that is a workstation-global,
# cross-repo change, and it is precisely the property that let a sibling project's
# rsync shim reach into this repo's transport selection and cause an outage on
# 2026-09-09.
#
# ⚠ Resolved UNCONDITIONALLY, even when rsync is present, and that is deliberate.
# The tar path is the FALLBACK that fires when rsync fails at runtime -- so it must
# be proven BEFORE the primary is attempted, or it is not a fallback. Gating this
# on `Get-Command rsync` would repeat the exact 2026-09-09 mistake: that call
# truthfully reports rsync EXISTS and was read as "rsync works here".
#
# ⚠ A normal deploy stops at the first candidate that passes -- probing the rest
# would cost ssh round trips for no decision. -PreflightOnly probes ALL of them and
# reports each verdict, because that mode exists to answer "what does this machine
# actually have, and why was each thing rejected?" A diagnostic that shows only the
# winner cannot tell you that a candidate you believed in is quietly unusable.
$repoMsys = ($RepoRoot -replace '\\', '/' -replace '^([A-Za-z]):', '/$1').ToLower()
$DeployBash = $null
$bashTried  = @()
foreach ($cand in (Get-RpDeployBashCandidates)) {
  if ($DeployBash -and -not $PreflightOnly) { break }
  $r = Test-RpDeployBash -Exe $cand -RepoMsys $repoMsys -Target $SshTarget
  if ($r.ok) {
    if (-not $DeployBash) { $DeployBash = $cand }
    if ($PreflightOnly) { Write-Host "  [candidate] USABLE   $cand" -ForegroundColor Green }
  } else {
    $bashTried += "    $cand`n        -> $($r.why)"
    if ($PreflightOnly) { Write-Host "  [candidate] rejected $cand`n                   -> $($r.why)" -ForegroundColor DarkGray }
  }
}

if (-not $DeployBash) {
  throw @"
PRE-FLIGHT FAILED: no usable bash interpreter for the tar-pipe sync path.

Candidates probed, in order:
$($bashTried -join "`n")

A candidate must (a) resolve the msys-style path '$repoMsys', (b) run
'find -print0 | tar --null -T -' with GNU tools, and (c) reach ${SshTarget} with
its OWN ssh. `bash` from PATH is NOT considered: on this machine it resolves to
the WSL launcher, which fails (a).

Fix by INSTALLING a usable interpreter (Git for Windows provides one at
'C:\Program Files\Git\bin\bash.exe'), or point at one explicitly:
    `$env:RP_DEPLOY_BASH = 'C:\path\to\bash.exe'

⛔ Do NOT "fix" this by adding a directory to PATH -- that is a machine-global,
cross-repo mutation.

Nothing has been done to ${TargetHost}.
"@
}

Write-Host "  interpreter: $DeployBash" -ForegroundColor Green
Write-Host "  transport:   ssh + scp round trip to ${SshTarget} verified by content, sudo -n OK" -ForegroundColor Green

if ($PreflightOnly) {
  Write-Host ""
  Write-Host "=== Pre-flight OK (-PreflightOnly: stopping before the build) ===" -ForegroundColor Green
  return
}

# --- Step 1: Build ---
Write-Host "[1/4] Building for $Runtime..." -ForegroundColor Yellow

$publishArgs = @(
  "publish",
  "src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj",
  "--configuration", "Release",
  "--runtime", $Runtime,
  "-f", "net10.0",
  "--self-contained",
  "--output", $PublishDir,
  "-v", "quiet"
)

Push-Location $RepoRoot
try {
  & dotnet @publishArgs
  if ($LASTEXITCODE -ne 0) { throw "Build failed" }
} finally {
  Pop-Location
}

Write-Host "  Build complete" -ForegroundColor Green

# --- Step 2: Create target directories ---
Write-Host "[2/4] Preparing target directories..." -ForegroundColor Yellow

# Native commands do NOT honour $ErrorActionPreference -- see the note above Step 3.
# Every native call from here on captures its own status on the very next line: one
# capture per call, never a single test after a sequence, because the value would then
# belong to whichever call ran last rather than to the one that failed.
ssh $SshTarget "sudo mkdir -p ${TargetPath}/{data,logs} && sudo chown -R ${TargetUser}:${TargetUser} ${TargetPath}"
$mkdirExit = $LASTEXITCODE
if ($mkdirExit -ne 0) { throw "failed to prepare ${TargetPath} on ${SshTarget} (exit $mkdirExit) -- every later copy would land somewhere unintended, or not at all" }

# --- Step 3: Sync files ---
Write-Host "[3/4] Syncing files..." -ForegroundColor Yellow

$synced = $false
$rsyncAvailable = Get-Command rsync -ErrorAction SilentlyContinue
if ($rsyncAvailable) {
  # Convert Windows path to rsync-compatible path
  $rsyncSource = ($PublishDir -replace '\\', '/' -replace '^([A-Za-z]):', '/$1').ToLower() + "/"
  # ⚠ This rsync must be allowed to FAIL SOFTLY, and it is the one native call in the
  # script that must never be made to throw. Its non-zero exit is the BRANCH CONDITION
  # for the tar fallback below: a `throw` here would delete the fallback outright.
  # Its status is read by the `if ($LASTEXITCODE -eq 0)` just below -- that IS the
  # check, and it is deliberate rather than missing.
  rsync -az --delete `
    --exclude 'appsettings.Production.json' `
    --exclude 'data/' `
    --exclude 'logs/' `
    -e ssh `
    $rsyncSource `
    "${SshTarget}:${TargetPath}/"

  if ($LASTEXITCODE -eq 0) {
    $synced = $true
  } else {
    Write-Host "  rsync failed (exit $LASTEXITCODE), falling back to tar-over-scp..." -ForegroundColor Yellow
  }
}

if (-not $synced) {
  # Robust fallback when rsync is unavailable: run the proven tar-pipe-over-ssh inside BASH.
  # Why bash and not a PowerShell scp/tar:
  #   * PowerShell's pipeline corrupts binary streams, so 'tar -czf - | ssh' must not run in PS.
  #   * msys GNU tar treats a Windows 'C:\...' archive path as a remote host ('C:'), so we use
  #     msys-style '/d/...' paths instead.
  #   * --unlink-first avoids ETXTBSY when overwriting the running binary (old inode survives for
  #     the live process; the Step-4 restart picks up the new file).
  #   * The box's data/ (cookies) is untouched (publish has no data/); appsettings.Production.json
  #     is EXCLUDED FROM THE ARCHIVE, so the customized prod config is never overwritten and never
  #     needs restoring. It used to be backed up + restored around the extract, and that dance
  #     clobbered the config two different ways when either of its best-effort ends failed --
  #     see deploy/tests/repro-tar-clobber.sh cases B1 and B2, and the exclude comment below.
  #   * The remote chain now runs under its own `set -e`. It used to end in `chmod`, so the chain
  #     reported CHMOD's status -- 0 -- while tar had exited 2. The $LASTEXITCODE check below was
  #     real but structurally blind, and the deploy restarted the service on a half-extracted tree
  #     while printing success. Measured 2026-09-09; the old comment claiming this was already
  #     handled was wrong. See docs/plans/deploy-tooling-honest-deploy-plan.md Defect 4.
  #   * An exit code says what a program CLAIMED. The sha256 check after the sync asks the box what
  #     it actually has, which is the only statement here that does not depend on a status being
  #     reported honestly.
  if (-not $rsyncAvailable) {
    Write-Host "  rsync not found, using tar-pipe over ssh (bash)..." -ForegroundColor Yellow
  }

  # Windows publish path -> msys path (D:\prj\..\linux-x64 -> /d/prj/.../linux-x64) for GNU tar -C.
  $publishMsys = ($PublishDir -replace '\\', '/' -replace '^([A-Za-z]):', '/$1').ToLower()

  # Write the tar-pipe to a temp .sh and run THAT (avoids PowerShell 5.1's unreliable native-arg
  # quoting of a 'bash -c "<string with quotes>"'). Must be LF-only with no BOM for bash.
  $syncScript =
    "set -e -o pipefail`n" +
    # --exclude=./appsettings.Production.json is the load-bearing line, and it mirrors
    # the rsync path's own --exclude in Step 3 above. The box's copy is authoritative
    # (docs/HT801-ADDRESS.md). The publish output ships the repo TEMPLATE (the SDK's
    # appsettings*.json Content glob), so while it was in the stream the file was
    # overwritten on every run and depended on a restore to put it back.
    #
    # ⚠ WHAT A CLOBBER ACTUALLY COSTS, measured 2026-09-09 against this tree -- the
    # widely-repeated "it resets BluetoothAdapter and breaks Radio Console's audio" is
    # NO LONGER TRUE and should not be repeated. The template currently carries
    # UseActualBluetoothHfp: true and BluetoothAdapter: hci1, identical to what the box
    # needs, so a clobber does not touch the adapter at all. It was true when written
    # (f222613 set the template to hci0); 1b56224 set it back to hci1 and quietly
    # falsified it. What a clobber DOES lose is GvPhoneNumber, EnableMarkRead and the
    # box's real HT801 address -- a silent GV/SMS outage that the deploy reports as
    # success. The reason this file must stay box-owned is that the template CAN drift
    # back, not that it currently has.
    #
    # The backup/restore dance is GONE rather than repaired, and that is the point.
    # Both of its ends were best-effort (`2>/dev/null || true`), so EITHER end could
    # fail silently, and each produced a different clobber -- both reproduced in
    # deploy/tests/repro-tar-clobber.sh:
    #
    #   B1  the BACKUP cp fails (first deploy, no config on the box yet) but a
    #       stale /tmp/rp-prod.bak from an earlier run makes `[ -f ]` true, so the
    #       restore installs that stale content. Worst case: not the repo template,
    #       but arbitrary config from a previous deploy, and the chain exits 0.
    #   B2  the backup SUCCEEDS and the RESTORE mv fails (a /tmp this uid cannot
    #       unlink from). The template stays on the box and the backup is stranded.
    #       This is the state PR #72 UAT found, finding L3.
    #
    # Excluding the member removes the state instead of protecting it: there is
    # nothing to restore because nothing is overwritten, and the property holds
    # whichever end would have failed. Making the restore "unconditional", which
    # KNOWN-ISSUES proposed, fixes neither -- in B2 the mv runs and fails, and in B1
    # it runs and installs the wrong file.
    #
    # A genuine first deploy still gets its config: the RP_CFG_MISSING probe further
    # down scps the template in when the box has none, and that is now the only path
    # in this script that ever writes this file.
    "cd '$publishMsys'`n" +
    # Files-only member list, and no './' member. GNU tar creates missing parent
    # directories on extract (verified: a 'sub/deep' that did not exist beforehand
    # is created), so directory members buy nothing here -- and --unlink-first calls
    # unlink() on every one of them, which cannot succeed. `tar -czf - .` therefore
    # made tar exit 2 on EVERY run (measured 2026-09-09,
    # deploy/tests/repro-tar-clobber.sh case A; seen live on the box the same day).
    # Dropping the directory members is what lets the exit status below be honest
    # instead of decorative -- and stops four `Cannot unlink` lines printing on every
    # successful deploy, which is what trained us to scroll past them.
    #
    # -type l is included so symlinks ship as symlinks. --exclude works against the
    # names read from -T -, verified against the real publish output: 396 members,
    # 0 directory members, 0 appsettings.Production.json.
    #
    # ⚠ Two known consequences of a files-only list, both measured, neither live today:
    #
    #   * EMPTY directories cannot be carried by \( -type f -o -type l \) and are
    #     silently dropped. Harmless right now -- all 24 empty directories in
    #     publish/linux-x64 are under .playwright, which is pruned anyway, and there
    #     are 0 outside it. It becomes a silent-loss mode the day the publish output
    #     gains one. deploy/tests/repro-tar-clobber.sh case D asserts THE LIMITATION IS
    #     REAL (an empty dir does not survive the archive). It does NOT assert the
    #     safety condition -- it uses its own fixture, not the publish tree, so it
    #     would keep passing on the day publish/linux-x64 gains a load-bearing empty
    #     directory. Guarding that needs an assertion against the real publish output.
    #   * Directories tar AUTO-CREATES take their mode from the remote umask rather
    #     than from the archive (measured: source 700 extracted as 775 under umask
    #     0002, which is what this box runs -- see setup-gvbridge.sh). No live impact,
    #     because every directory in the publish tree already exists on the box. Left
    #     as-is deliberately rather than pinned with `umask 022`: that would also
    #     tighten every FILE mode the extract writes, which is a permissions change on
    #     a production box and belongs to the owner, not to this PR. Tracked in the
    #     plan's follow-ups.
    "find . -mindepth 1 -path ./.playwright -prune -o \( -type f -o -type l \) -print0 |" +
      " tar --null --exclude=./appsettings.Production.json -czf - -T - |" +
      # `set -e` in the REMOTE shell. Without it the compound's status is the LAST
      # command's -- chmod's -- so a failed tar reported 0. The remote shell does not
      # inherit the local `set -e -o pipefail` above: that one governs this script,
      # and by the time it can act the remote work has already finished.
      " ssh '$SshTarget' '" +
      "set -e; " +
      "tar -xzf - --unlink-first -C $TargetPath; " +
      "chmod +x $TargetPath/RotaryPhoneController.Server'`n"
  $syncScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "rp-deploy-sync.sh"
  [System.IO.File]::WriteAllText($syncScriptPath, $syncScript, (New-Object System.Text.UTF8Encoding($false)))

  # ⛔ $DeployBash, NEVER bare `bash`. On this workstation's persistent PATH `bash`
  # is the WSL launcher, which cannot resolve '$publishMsys' and cannot even open a
  # script named by a Windows path (exit 127, "No such file or directory"). The
  # pre-flight resolved and PROVED this interpreter before anything touched the box;
  # see the comment block above [1/4]. It is guaranteed non-null here -- the
  # pre-flight throws rather than falling through.
  & $DeployBash $syncScriptPath
  $syncExit = $LASTEXITCODE
  Remove-Item $syncScriptPath -ErrorAction SilentlyContinue
  if ($syncExit -ne 0) { throw "tar-pipe deploy failed (exit $syncExit) -- aborting (service NOT restarted; still on prior binary)" }
  $synced = $true
}

# Independent of every exit status above: ask the box what it has. An exit code says
# what a program claimed; this says what is on disk. Runs on both sync paths, because
# a silently-truncated rsync is no better than a silently-failed tar.
#
# ⚠ sha256, NOT size. A size comparison cannot fail in the most common case this is
# meant to catch: rebuilding unchanged source produces a byte-identical apphost, so
# `stat -c %s` matches whether or not the sync did anything at all. A sync that
# silently no-ops -- precisely the defect this script is being fixed for -- passes a
# size check on every redeploy without a source change. Same round trip, real answer.
#
# No 2>/dev/null on the remote side: swallowing the error would blind the diagnostic
# this throws on.
#
# ⛔ AND IT MUST HASH THE .dll, NOT ONLY THE APPHOST. Measured 2026-09-10, and this
# is the same defect one level deeper than the note above:
#
#   commit 1c8a22c  (no browserRefreshOutcome)  ->  apphost f500cf157697de69  .dll b89b31d80eaa717f
#   this branch     (has browserRefreshOutcome) ->  apphost f500cf157697de69  .dll a723b269258689df
#
# `RotaryPhoneController.Server` is the SDK's generic apphost -- a 78KB native
# launcher patched with the app name. The application is the 115KB
# `RotaryPhoneController.Server.dll` beside it. The apphost is therefore
# BYTE-IDENTICAL ACROSS TWO BUILDS WHOSE C# DIFFERS, so hashing it answers
# "did a launcher arrive?" and gets read as "did this build arrive?".
#
# That is exactly the class the comment above was written to close -- the fix went
# from `stat -c %s` to sha256 and kept hashing the file that cannot change. The
# upgrade was real and it did not move the check onto the right quantity.
#
# Both are verified: the apphost still catches a missing or truncated launcher, and
# the .dll is the one that proves THIS BUILD landed. One ssh round trip for both.
$verifyRel = @("RotaryPhoneController.Server", "RotaryPhoneController.Server.dll")
$expected  = [ordered]@{}
foreach ($rel in $verifyRel) {
  $localPath = Join-Path $PublishDir $rel
  if (-not (Test-Path -LiteralPath $localPath)) {
    throw "sync verification FAILED: ${rel} is not in the local publish output at ${PublishDir}. The build did not produce what the deploy expects; the service has NOT been restarted."
  }
  $expected[$rel] = (Get-FileHash $localPath -Algorithm SHA256).Hash.ToLower()
}
$remoteHashOut = ssh $SshTarget ("cd '${TargetPath}' && sha256sum " + (($verifyRel | ForEach-Object { "'$_'" }) -join ' '))
$hashExit = $LASTEXITCODE
if ($hashExit -ne 0) {
  throw "sync verification FAILED: could not hash $($verifyRel -join ', ') under ${TargetPath} on ${TargetHost} (exit $hashExit). The service has NOT been restarted."
}
# Parse into name -> hash rather than trusting sha256sum's output ORDER. It happens to
# echo the order it was given, but a check whose correctness rests on that would pass
# for the wrong reason the day it does not.
#
# ⚠ Iterate the ARRAY; do NOT stringify it first. A native command's multi-line output
# arrives as string[], and "$array" joins its elements with a SPACE, not a newline -- so
# `"$remoteHashOut" -split "\r?\n"` yields ONE line holding both records and every
# lookup misses. Measured here 2026-09-10: the two hashes were correct and matching, and
# the check still threw. It failed CLOSED, which is the right direction, but a
# verification that cannot pass is not a verification.
$remoteHashes = @{}
$remoteHashLines = @($remoteHashOut) | ForEach-Object { "$_" -split "`r?`n" } | Where-Object { $_.Trim() }
foreach ($line in $remoteHashLines) {
  $parts = $line.Trim() -split '\s+', 2
  if ($parts.Count -eq 2) { $remoteHashes[$parts[1].Trim()] = $parts[0].Trim().ToLower() }
}
foreach ($rel in $verifyRel) {
  if (-not $remoteHashes.ContainsKey($rel)) {
    throw "sync verification FAILED: ${TargetHost} did not report a hash for ${rel} (got: '$($remoteHashLines -join ' | ')'). Refusing to read an unanswered question as an answer. The service has NOT been restarted."
  }
  if ($remoteHashes[$rel] -ne $expected[$rel]) {
    throw "sync verification FAILED: ${TargetPath}/${rel} hashes $($remoteHashes[$rel]) on ${TargetHost}, expected $($expected[$rel]). The service has NOT been restarted."
  }
}
Write-Host "  Sync verified: apphost $($expected['RotaryPhoneController.Server'].Substring(0,16)) and app $($expected['RotaryPhoneController.Server.dll'].Substring(0,16)) match on the box" -ForegroundColor Green

# Copy appsettings.Production.json only if it doesn't exist on target.
#
# ⛔ This probe is now the ONLY thing in the deploy that can write this file, so it has
# to be unambiguous. The old form was `test -f … && echo EXISTS` compared with
# `-ne "EXISTS"`, which silently treats "the probe did not answer" as "the file is
# absent" and overwrites the box's authoritative config with the repo template. Two
# ways that fired:
#
#   * ssh connects but the command does not run cleanly -> $prodExists is $null,
#     $null -ne "EXISTS" is TRUE, and the template is copied over a live config.
#   * the remote emits anything besides EXISTS -> $prodExists is a String[], and
#     PowerShell's -ne on a collection FILTERS rather than compares: it returns the
#     non-matching elements, and a non-empty array is truthy. Same outcome.
#
# So the probe answers both cases explicitly, its transport status IS checked, and
# only an explicit RP_CFG_MISSING may write. Anything else aborts rather than guesses
# -- which is the whole point of this change: do not let an unanswered question look
# like an answer.
$prodProbe = ssh $SshTarget "if [ -f '${TargetPath}/appsettings.Production.json' ]; then echo RP_CFG_EXISTS; else echo RP_CFG_MISSING; fi"
$probeExit = $LASTEXITCODE
if ($probeExit -ne 0) { throw "could not probe ${TargetPath}/appsettings.Production.json on ${TargetHost} (exit $probeExit) -- refusing to guess whether the box has a config; NOT writing the template" }
$probeAnswers = @("$prodProbe" -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($probeAnswers.Count -ne 1 -or $probeAnswers[0] -notin @('RP_CFG_EXISTS', 'RP_CFG_MISSING')) {
  throw "unexpected output probing ${TargetPath}/appsettings.Production.json: '$($probeAnswers -join '|')' -- refusing to guess; NOT writing the template"
}
if ($probeAnswers[0] -eq 'RP_CFG_MISSING') {
  $prodConfig = Join-Path $RepoRoot "src\RotaryPhoneController.Server\appsettings.Production.json"
  if (Test-Path $prodConfig) {
    Write-Host "  Copying initial appsettings.Production.json..." -ForegroundColor Yellow
    scp $prodConfig "${SshTarget}:${TargetPath}/appsettings.Production.json"
    $prodScpExit = $LASTEXITCODE
    # ⛔ The one that matters most in this block. This is the FIRST-DEPLOY path for the
    # very file the rest of this change exists to protect.
    #
    # What a missing config actually costs, measured rather than assumed: the app falls
    # back to appsettings.json, which sets UseActualBluetoothHfp: false. That
    # short-circuits BluetoothAdapterFactory.Create before any adapter is chosen, so
    # hci0 is never touched and the Radio Console boundary is NOT crossed. Instead the
    # service starts, `systemctl status` shows active, and the phone runs silently on
    # MockBluetoothHfpAdapter with the template's HT801 address -- a dead phone
    # reported as a healthy deploy, which is worse to diagnose, not better.
    if ($prodScpExit -ne 0) { throw "failed to copy the initial appsettings.Production.json to ${SshTarget} (exit $prodScpExit) -- the box has NO production config; aborting before the service is restarted" }
  }
}

# Copy scripts directory (HFP monitor, etc.)
$scriptsDir = Join-Path $RepoRoot "scripts"
if (Test-Path $scriptsDir) {
  Write-Host "  Copying scripts..." -ForegroundColor Yellow
  ssh $SshTarget "mkdir -p ${TargetPath}/scripts"
  $scriptsMkdirExit = $LASTEXITCODE
  if ($scriptsMkdirExit -ne 0) { throw "failed to create ${TargetPath}/scripts on ${SshTarget} (exit $scriptsMkdirExit)" }
  scp -r ($scriptsDir -replace '\\', '/') "${SshTarget}:${TargetPath}/"
  $scriptsScpExit = $LASTEXITCODE
  if ($scriptsScpExit -ne 0) { throw "failed to copy scripts/ to ${SshTarget} (exit $scriptsScpExit) -- the box would be left with stale or absent HFP monitor scripts" }
}

# Copy Chrome extension (GV Bridge) to both deploy path and snap-accessible path
$extensionDir = Join-Path $RepoRoot "ChromeExtension"
if (Test-Path $extensionDir) {
  Write-Host "  Copying Chrome extension..." -ForegroundColor Yellow
  ssh $SshTarget "mkdir -p ${TargetPath}/ChromeExtension"
  $extMkdirExit = $LASTEXITCODE
  if ($extMkdirExit -ne 0) { throw "failed to create ${TargetPath}/ChromeExtension on ${SshTarget} (exit $extMkdirExit)" }
  scp -r ($extensionDir -replace '\\', '/') "${SshTarget}:${TargetPath}/"
  $extScpExit = $LASTEXITCODE
  if ($extScpExit -ne 0) { throw "failed to copy ChromeExtension/ to ${SshTarget} (exit $extScpExit) -- the box would be left with a stale extension" }
  # Also update the snap-accessible copy if it exists (for running Chromium)
  #
  # ⚠ Deliberately NOT exit-checked, and it must stay that way. The remote side is
  # already guarded by `if [ -d … ]`, the snap profile belongs to the SUPERSEDED
  # legacy configuration (see setup-gvbridge.sh step 7), and its absence is the
  # normal state on this box. Making this throw would fail every deploy.
  ssh $SshTarget "if [ -d ~/snap/chromium/common/gv-bridge-profile/Extension ]; then cp -r ${TargetPath}/ChromeExtension/* ~/snap/chromium/common/gv-bridge-profile/Extension/ && echo '  Extension updated in snap profile'; fi"
}

# Copy deploy shell scripts (setup-gvbridge.sh, gv-bridge-ensure.sh, gv-bridge-restart.sh)
# and the systemd unit files setup-gvbridge.sh installs from deploy/systemd.
#
# Every scp is exit-code checked and throws, for the same reason the binary sync
# above is: $ErrorActionPreference = "Stop" does NOT turn a non-zero exit from a
# native executable into a terminating error, so an unchecked scp would let a
# half-shipped deploy print "Deploy Complete". That is the silent-stale-deploy bug,
# and it would land here as a stale or missing gv-bridge-ensure.sh on the box.
$deployScripts = Join-Path $RepoRoot "deploy"
$systemdDir = Join-Path $deployScripts "systemd"
$shellScripts = @(Get-ChildItem -Path $deployScripts -Filter "*.sh" -File -ErrorAction SilentlyContinue)
$unitFiles = @(if (Test-Path $systemdDir) { Get-ChildItem -Path $systemdDir -File })

if ($shellScripts.Count -gt 0 -or $unitFiles.Count -gt 0) {
  Write-Host "  Copying deploy scripts..." -ForegroundColor Yellow
  ssh $SshTarget "mkdir -p ${TargetPath}/deploy/systemd"
  if ($LASTEXITCODE -ne 0) { throw "failed to create ${TargetPath}/deploy on ${SshTarget} (exit $LASTEXITCODE)" }

  foreach ($script in $shellScripts) {
    scp ($script.FullName -replace '\\', '/') "${SshTarget}:${TargetPath}/deploy/"
    if ($LASTEXITCODE -ne 0) { throw "failed to copy $($script.Name) (exit $LASTEXITCODE) -- aborting before the box is left with a stale copy" }
  }

  # setup-gvbridge.sh reads these from ${TargetPath}/deploy/systemd and installs
  # them into ~/.config/systemd/user, so they have to ship alongside it. Shipped
  # independently of the .sh files so a future reorg of one cannot silently stop
  # shipping the other.
  foreach ($unit in $unitFiles) {
    scp ($unit.FullName -replace '\\', '/') "${SshTarget}:${TargetPath}/deploy/systemd/"
    if ($LASTEXITCODE -ne 0) { throw "failed to copy systemd unit $($unit.Name) (exit $LASTEXITCODE)" }
  }

  # Explicit modes rather than chmod +x: NTFS carries no permission bits, so the
  # mode on arrival is whatever the umask made it. 755 keeps the scripts runnable
  # without making them group-writable.
  #
  # ⛔ This used to be `chmod …/*.sh 2>/dev/null; chmod …/systemd/* 2>/dev/null`, whose
  # status was the SECOND chmod's -- the same last-command-wins masking as the tar
  # chain. The globs are now built only for groups that actually have files, so a
  # non-zero status means a real failure rather than an unmatched glob, and the remote
  # runs under `set -e`. It matters because setup-gvbridge.sh has to be executable for
  # the deploy to be able to run it.
  $chmodCmds = @()
  if ($shellScripts.Count -gt 0) { $chmodCmds += "chmod 755 ${TargetPath}/deploy/*.sh" }
  if ($unitFiles.Count -gt 0)    { $chmodCmds += "chmod 644 ${TargetPath}/deploy/systemd/*" }
  if ($chmodCmds.Count -gt 0) {
    ssh $SshTarget ("set -e; " + ($chmodCmds -join "; "))
    $chmodDeployExit = $LASTEXITCODE
    if ($chmodDeployExit -ne 0) { throw "failed to set modes on ${TargetPath}/deploy (exit $chmodDeployExit) -- setup-gvbridge.sh would not be executable on the box" }
  }

  # Record what the REPO holds for every file we just shipped, and carry it to the box.
  #
  # check-installed-drift.sh needs the REPO end of the chain. Comparing only shipped-vs-installed
  # is a check that runs, passes, and answers a different question: it truthfully reports "the two
  # copies match" and gets read as "the box has the current file". On a transfer that silently did
  # nothing, two stale copies match. See docs/plans/gv-session-alarm.md §0.9.
  #
  # ⚠ PowerShell-native by requirement, not by taste. Get-FileHash and WriteAllText, then scp as a
  # native command -- NO local bash/sh/wsl. `bash` on this machine's persistent PATH resolves to
  # the WSL launcher (C:\WINDOWS\system32\bash.exe), which cannot resolve the msys-style /d/prj/...
  # paths this script uses; Git\bin and msys64\usr\bin are not on the persistent PATH. ssh.exe and
  # scp.exe are safe -- they live in C:\WINDOWS\System32\OpenSSH on the MACHINE path.
  $manifestPath = Join-Path ([System.IO.Path]::GetTempPath()) "rp-shipped-manifest.sha256"
  $manifestLines = foreach ($f in ($shellScripts + $unitFiles)) {
    $rel = if ($f.Directory.Name -eq "systemd") { "systemd/$($f.Name)" } else { $f.Name }
    "$((Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower())  $rel"
  }
  [System.IO.File]::WriteAllText($manifestPath, (($manifestLines -join "`n") + "`n"),
                                 (New-Object System.Text.UTF8Encoding($false)))
  # ⛔ The path MUST be slash-converted, exactly like every other scp in this file. Passed raw, a
  # Windows path reaches scp as "C:\Users\...\rp-shipped-manifest.sha256" and scp reads the leading
  # "C:" as a REMOTE HOST. The plan's literal code omitted this.
  scp ($manifestPath -replace '\\', '/') "${SshTarget}:${TargetPath}/deploy/.shipped-manifest.sha256"
  $manifestScpExit = $LASTEXITCODE
  if ($manifestScpExit -ne 0) { throw "failed to ship the drift manifest (exit $manifestScpExit) -- the post-deploy state check would silently have nothing to compare against" }
  Remove-Item $manifestPath -ErrorAction SilentlyContinue
}

# Ensure binary is executable.
#
# ⛔ The Server chmod must succeed: without it the service cannot start, and the
# restart below would run anyway. The scripts/*.py chmod is best-effort on purpose --
# the glob legitimately matches nothing when scripts/ carries no Python -- so it keeps
# its own `|| true` rather than being allowed to abort the deploy.
ssh $SshTarget "set -e; chmod +x ${TargetPath}/RotaryPhoneController.Server; chmod +x ${TargetPath}/scripts/*.py 2>/dev/null || true"
$chmodBinExit = $LASTEXITCODE
if ($chmodBinExit -ne 0) { throw "failed to make ${TargetPath}/RotaryPhoneController.Server executable (exit $chmodBinExit) -- the service would fail to start; NOT restarting" }

Write-Host "  Files synced" -ForegroundColor Green

# --- Step 4: Install service & restart ---
Write-Host "[4/4] Installing service..." -ForegroundColor Yellow

$serviceFile = Join-Path $RepoRoot "deploy\rotary-phone.service"
scp $serviceFile "${SshTarget}:/tmp/rotary-phone.service"
$svcScpExit = $LASTEXITCODE
# ⛔ Both of these were unchecked. A failed copy or a failed install left systemd on a
# STALE unit, and the restart below then restarted into it -- while the script printed
# "=== Deploy Complete ===". The `&&` chain already stops at the first failure; what was
# missing was anyone reading its status.
if ($svcScpExit -ne 0) { throw "failed to copy rotary-phone.service to ${SshTarget} (exit $svcScpExit) -- systemd would be left on the previous unit; NOT restarting" }
ssh $SshTarget "sudo mv /tmp/rotary-phone.service /etc/systemd/system/rotary-phone.service && sudo systemctl daemon-reload && sudo systemctl enable rotary-phone.service"
$svcInstallExit = $LASTEXITCODE
if ($svcInstallExit -ne 0) { throw "failed to install rotary-phone.service on ${TargetHost} (exit $svcInstallExit) -- systemd is on a stale unit; NOT restarting" }

if (-not $NoRestart) {
  Write-Host "  Restarting service..." -ForegroundColor Yellow
  ssh $SshTarget "sudo systemctl restart rotary-phone.service"
  $restartExit = $LASTEXITCODE
  # ⛔ The last silent failure in the script, and the loudest one to get wrong: an
  # unchecked restart meant a service that failed to come up was reported as a
  # successful deploy.
  if ($restartExit -ne 0) { throw "systemctl restart rotary-phone.service FAILED on ${TargetHost} (exit $restartExit) -- the new binary is on the box but the service is not running it" }
  Start-Sleep -Seconds 2
  # ⚠ Not exit-checked on purpose: this is a DISPLAY of the unit's state, and
  # `systemctl status` returns non-zero for a unit that is merely inactive. The restart
  # above is the gate; this is the operator's read of what happened.
  #
  # ⛔ $ErrorActionPreference is restored around it for a non-obvious reason. `2>&1` on
  # a NATIVE command merges its stderr into the pipeline as ErrorRecords, and under
  # $ErrorActionPreference = "Stop" (set at the top of this file) PowerShell turns the
  # first of those into a terminating NativeCommandError. So anything ssh or sudo
  # writes to stderr here -- a host-key notice, a sudo lecture -- would abort the
  # deploy AFTER a successful restart and stop it printing "=== Deploy Complete ===".
  # A status display must not be able to fail a deploy that already worked.
  $eapPrev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  ssh $SshTarget "sudo systemctl status rotary-phone.service --no-pager -l" 2>&1 | Write-Host
  $ErrorActionPreference = $eapPrev
}

# --- Post-deploy: install the alarm, and report the installed state of both groups ---
#
# The alarm's installer is narrow and safe to run every deploy; it does NOT touch
# gv-bridge-ensure.sh. That matters: the shipped copy of gv-bridge-ensure.sh adds
# `flock ... || exit 0`, a THIRD outcome arriving as exit 0 on a cross-repo contract that
# already cannot express two. See docs/plans/gv-session-alarm.md §0.2.
#
# ⚠ Every shell construct below is on the FAR side of ssh, interpreted by the box's own
# bash. Nothing here invokes a local interpreter.
ssh $SshTarget "bash ${TargetPath}/deploy/install-gv-session-alarm.sh"
$alarmInstallExit = $LASTEXITCODE

# ⛔ DO NOT throw here. The drift check below is the ONLY thing that states what is
# actually on the box, and a failed install is exactly when the operator needs that
# statement most. The installer runs under `set -euo pipefail` and installs three files
# in sequence, so a failure on the second or third leaves a PARTIAL install -- and
# throwing first would report "the installer failed" while saying nothing about what is
# now installed. That is this repo's own failure class, arrived at from the tidy end.
if ($alarmInstallExit -ne 0) {
  Write-Host "  the GV session alarm installer FAILED (exit $alarmInstallExit) -- reading the installed state before aborting:" -ForegroundColor Red
}

# Conditional by construction: these print one quiet line when everything matches, and a
# ⚠ block naming the action only when it does not. Never an unconditional banner -- a
# warning that fires on a healthy deploy trains the operator to scroll past the one run
# where it means something, which is exactly what the old "Cannot unlink" line did here.
#
# ⚠ --ship-dir is passed explicitly. The script's default happens to equal the default
# $TargetPath, so omitting it agreed only by coincidence of two defaults -- and a
# -TargetPath override would have had the check read a DIFFERENT tree's manifest and
# report confidently about the wrong box directory.
ssh $SshTarget "bash ${TargetPath}/deploy/check-installed-drift.sh --group alarm --ship-dir '${TargetPath}/deploy'"
$alarmDriftExit = $LASTEXITCODE
if ($alarmInstallExit -ne 0) { throw "the GV session alarm installer failed (exit $alarmInstallExit) -- the drift report above states what is actually on the box" }
if ($alarmDriftExit -ne 0) { throw "the alarm's installed state does not match what was shipped (exit $alarmDriftExit) -- see the drift report above" }

ssh $SshTarget "bash ${TargetPath}/deploy/check-installed-drift.sh --group bridge --ship-dir '${TargetPath}/deploy'"
$bridgeDriftExit = $LASTEXITCODE
# ⛔ NOT a throw. ~/bin/gv-bridge-ensure.sh's staleness is real -- measured 2026-09-09, the
# installed copy is from Aug 18 -- but it is not this deploy's to fix, and fixing it is
# blocked on a cross-repo exit-code decision (spec §8, §11 decision 4). Aborting here would
# block every deploy on that decision. It must be LOUD and it must not be fatal.
if ($bridgeDriftExit -ne 0) { Write-Host "  (bridge tooling is not in sync -- see above. Not fatal; blocked on the gv-bridge-ensure.sh exit-code decision, spec §11 decision 4.)" -ForegroundColor Yellow }

Write-Host ""
Write-Host "=== Deploy Complete ===" -ForegroundColor Green
Write-Host "  API: http://${TargetHost}:5004"
Write-Host "  Swagger: http://${TargetHost}:5004/swagger"
Write-Host ""

if ($Logs) {
  Write-Host "Tailing logs..." -ForegroundColor Yellow
  # ⚠ Not exit-checked: an interactive follow that the operator ends with Ctrl-C, which
  # is a non-zero exit and the normal way to leave it. The deploy is already complete
  # and nothing runs after this.
  #
  # ⛔ But do NOT read this comment as an endorsement of `-f` on this box.
  # docs/plans/deploy-tooling-honest-deploy-plan.md is explicit: "Bounded reads only on
  # this box -- never journalctl -f or tail -f. It is an N100 shared with Radio Console
  # and journald churn correlates with audible audio distortion there." This call
  # predates that policy and survives only because -Logs is opt-in and the operator is
  # sitting in front of it. Prefer `-n 200 --no-pager`. Do not add another follow.
  ssh $SshTarget "sudo journalctl -u rotary-phone.service -f --no-pager"
}
