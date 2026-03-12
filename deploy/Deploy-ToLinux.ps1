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

ssh $SshTarget "sudo mkdir -p ${TargetPath}/{data,logs} && sudo chown -R ${TargetUser}:${TargetUser} ${TargetPath}"

# --- Step 3: Sync files ---
Write-Host "[3/4] Syncing files..." -ForegroundColor Yellow

$rsyncAvailable = Get-Command rsync -ErrorAction SilentlyContinue
if ($rsyncAvailable) {
  # Convert Windows path to rsync-compatible path
  $rsyncSource = ($PublishDir -replace '\\', '/' -replace '^([A-Za-z]):', '/$1').ToLower() + "/"
  rsync -az --delete `
    --exclude 'appsettings.Production.json' `
    --exclude 'data/' `
    --exclude 'logs/' `
    -e ssh `
    $rsyncSource `
    "${SshTarget}:${TargetPath}/"

  if ($LASTEXITCODE -ne 0) {
    Write-Host "  rsync failed, falling back to scp..." -ForegroundColor Yellow
    scp -r "${PublishDir}\*" "${SshTarget}:${TargetPath}/"
  }
} else {
  Write-Host "  rsync not found, using scp..." -ForegroundColor Yellow
  scp -r "${PublishDir}\*" "${SshTarget}:${TargetPath}/"
}

# Copy appsettings.Production.json only if it doesn't exist on target
$prodExists = ssh $SshTarget "test -f ${TargetPath}/appsettings.Production.json && echo EXISTS"
if ($prodExists -ne "EXISTS") {
  $prodConfig = Join-Path $RepoRoot "src\RotaryPhoneController.Server\appsettings.Production.json"
  if (Test-Path $prodConfig) {
    Write-Host "  Copying initial appsettings.Production.json..." -ForegroundColor Yellow
    scp $prodConfig "${SshTarget}:${TargetPath}/appsettings.Production.json"
  }
}

# Ensure binary is executable
ssh $SshTarget "chmod +x ${TargetPath}/RotaryPhoneController.Server"

Write-Host "  Files synced" -ForegroundColor Green

# --- Step 4: Install service & restart ---
Write-Host "[4/4] Installing service..." -ForegroundColor Yellow

$serviceFile = Join-Path $RepoRoot "deploy\rotary-phone.service"
scp $serviceFile "${SshTarget}:/tmp/rotary-phone.service"
ssh $SshTarget "sudo mv /tmp/rotary-phone.service /etc/systemd/system/rotary-phone.service && sudo systemctl daemon-reload && sudo systemctl enable rotary-phone.service"

if (-not $NoRestart) {
  Write-Host "  Restarting service..." -ForegroundColor Yellow
  ssh $SshTarget "sudo systemctl restart rotary-phone.service"
  Start-Sleep -Seconds 2
  ssh $SshTarget "sudo systemctl status rotary-phone.service --no-pager -l" 2>&1 | Write-Host
}

Write-Host ""
Write-Host "=== Deploy Complete ===" -ForegroundColor Green
Write-Host "  API: http://${TargetHost}:5004"
Write-Host "  Swagger: http://${TargetHost}:5004/swagger"
Write-Host ""

if ($Logs) {
  Write-Host "Tailing logs..." -ForegroundColor Yellow
  ssh $SshTarget "sudo journalctl -u rotary-phone.service -f --no-pager"
}
