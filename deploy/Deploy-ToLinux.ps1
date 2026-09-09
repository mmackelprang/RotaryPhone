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

.EXAMPLE
  .\deploy\Deploy-ToLinux.ps1
  .\deploy\Deploy-ToLinux.ps1 -TargetHost radio -Runtime linux-x64
  .\deploy\Deploy-ToLinux.ps1 -Logs
#>
[CmdletBinding()]
param(
  [switch]$NoRestart,
  [switch]$Logs,
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

Write-Host "=== Rotary Phone Deploy ===" -ForegroundColor Cyan
Write-Host "Target:  ${SshTarget}:${TargetPath}"
Write-Host "Runtime: $Runtime"
Write-Host ""

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
  # Its status is read at :102 -- that is the check, and it is deliberate rather than
  # missing.
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
  #   * An exit code says what a program CLAIMED. The size check after the sync asks the box what
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
    # what the rsync path at :89 has always done. The box's copy is authoritative
    # (docs/HT801-ADDRESS.md) and carries BluetoothAdapter: hci1, which crosses the
    # Radio Console audio boundary. The publish output ships the repo TEMPLATE (the
    # SDK's appsettings*.json Content glob), so while it was in the stream the file
    # was overwritten on every run and depended on a restore to put it back.
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
    # A genuine first deploy still gets its config: :142-148 scps the template in
    # when the box has no appsettings.Production.json, which is now the only path
    # that ever writes this file.
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

  bash $syncScriptPath
  $syncExit = $LASTEXITCODE
  Remove-Item $syncScriptPath -ErrorAction SilentlyContinue
  if ($syncExit -ne 0) { throw "tar-pipe deploy failed (exit $syncExit) -- aborting (service NOT restarted; still on prior binary)" }
  $synced = $true
}

# Independent of every exit status above: ask the box what it has. An exit code says
# what a program claimed; this says what is on disk. Runs on both sync paths, because
# a silently-truncated rsync is no better than a silently-failed tar.
$localBinSize = (Get-Item (Join-Path $PublishDir "RotaryPhoneController.Server")).Length
$remoteBinSize = (ssh $SshTarget "stat -c %s ${TargetPath}/RotaryPhoneController.Server 2>/dev/null")
$statExit = $LASTEXITCODE
if ($statExit -ne 0) {
  throw "sync verification FAILED: could not stat ${TargetPath}/RotaryPhoneController.Server on ${TargetHost} (exit $statExit). The service has NOT been restarted."
}
$remoteBinSize = "$remoteBinSize".Trim()
if ($remoteBinSize -ne "$localBinSize") {
  throw "sync verification FAILED: ${TargetPath}/RotaryPhoneController.Server is $remoteBinSize bytes on ${TargetHost}, expected $localBinSize. The service has NOT been restarted."
}
Write-Host "  Sync verified: binary is $localBinSize bytes on the box" -ForegroundColor Green

# Copy appsettings.Production.json only if it doesn't exist on target.
#
# ⚠ This `ssh` is deliberately NOT exit-checked. `test -f` returns non-zero when the
# file is absent, which is the normal first-deploy case -- the whole point of the
# probe. Its ANSWER is the signal, not its status.
$prodExists = ssh $SshTarget "test -f ${TargetPath}/appsettings.Production.json && echo EXISTS"
if ($prodExists -ne "EXISTS") {
  $prodConfig = Join-Path $RepoRoot "src\RotaryPhoneController.Server\appsettings.Production.json"
  if (Test-Path $prodConfig) {
    Write-Host "  Copying initial appsettings.Production.json..." -ForegroundColor Yellow
    scp $prodConfig "${SshTarget}:${TargetPath}/appsettings.Production.json"
    $prodScpExit = $LASTEXITCODE
    # ⛔ The one that matters most in this block. This is the FIRST-DEPLOY path for the
    # very file the rest of this change exists to protect. A silent failure here leaves
    # the box with no production config at all, and the service starts on defaults --
    # including the wrong BluetoothAdapter, which crosses the Radio Console boundary.
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

  # ⛔ Hard gate, and it runs BEFORE any scp so a bad file never reaches the box.
  #
  # Today setup-gvbridge.sh is shipped but never executed by the deploy, so
  # gv-bridge-ensure.sh sits on the box as an inert file and its contents do not
  # matter. The moment the deploy runs the installer (plan Task 10, not yet built)
  # that file becomes an executed one, and --password-store stops being inert. On a
  # profile already holding v11 cookies it makes the keyring-derived key
  # unobtainable and Chrome DISCARDS them: measured live at 45 v11 -> 16 v10,
  # destroying the Google Voice session. ~/.config/gv-bridge-chrome is exactly that
  # profile. The gate lands first, deliberately, so the hazard is closed before the
  # change that opens it.
  #
  # Scoped to this ONE file deliberately. A repo-wide search is NOT equivalent:
  # scripts/bin/Debug/net10.0/.playwright/.../chromiumSwitches.js carries
  # "--password-store=basic" as one of Playwright's own Chromium defaults, so a broad
  # grep fails on a clean tree (verified 2026-09-09).
  $ensureSrc = Join-Path $deployScripts "gv-bridge-ensure.sh"
  if (Test-Path $ensureSrc) {
    if (Select-String -Path $ensureSrc -Pattern 'password-store' -SimpleMatch -Quiet) {
      throw "REFUSING TO DEPLOY: deploy/gv-bridge-ensure.sh contains --password-store. On ~/.config/gv-bridge-chrome that discards the v11 cookies and destroys the Google Voice session. Remove the flag, then redeploy."
    }
  } else {
    throw "REFUSING TO DEPLOY: deploy/gv-bridge-ensure.sh is missing -- setup-gvbridge.sh would fail on the box after the binary sync had already landed."
  }

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
  ssh $SshTarget "sudo systemctl status rotary-phone.service --no-pager -l" 2>&1 | Write-Host
}

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
  ssh $SshTarget "sudo journalctl -u rotary-phone.service -f --no-pager"
}
