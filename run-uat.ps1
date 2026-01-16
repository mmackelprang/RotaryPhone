[CmdletBinding()]
param(
    [int]$ApiPort = 5555,
    [int]$UiPort = 5173
)

$ErrorActionPreference = 'Stop'

function Get-PwshExecutable {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwsh) { return $pwsh.Source }
    $winPs = Get-Command powershell -ErrorAction Stop
    return $winPs.Source
}

function Get-NpmExecutable {
    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if ($npm) { return $npm.Source }
    throw 'npm is required but was not found on PATH.'
}

$pwshExe = Get-PwshExecutable
$npmExe = Get-NpmExecutable

$repoRoot = Split-Path -Parent $PSCommandPath
Set-Location $repoRoot

Write-Host "[UAT] Restoring .NET dependencies..." -ForegroundColor Cyan
& dotnet restore

Write-Host "[UAT] Restoring UI dependencies (npm ci)..." -ForegroundColor Cyan
pushd "$repoRoot/src/RotaryPhoneController.Client" | Out-Null
& $npmExe ci
popd | Out-Null

$apiProject = Join-Path $repoRoot 'src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj'
$apiCmd = "dotnet run --project `"$apiProject`" --urls http://0.0.0.0:$ApiPort"
$uiCmd = "npm run dev -- --host --port $UiPort"

Write-Host "[UAT] Starting backend on port $ApiPort..." -ForegroundColor Green
$apiProcess = Start-Process -FilePath $pwshExe -WorkingDirectory $repoRoot -ArgumentList '-NoLogo','-NoExit','-Command', $apiCmd -PassThru

Write-Host "[UAT] Starting UI on port $UiPort..." -ForegroundColor Green
$uiProcess = Start-Process -FilePath $pwshExe -WorkingDirectory (Join-Path $repoRoot 'src/RotaryPhoneController.Client') -ArgumentList '-NoLogo','-NoExit','-Command', $uiCmd -PassThru

Write-Host "[UAT] Backend PID: $($apiProcess.Id)" -ForegroundColor Yellow
Write-Host "[UAT] UI PID: $($uiProcess.Id)" -ForegroundColor Yellow

Start-Sleep -Seconds 8

function Test-Port([int]$Port, [string]$Name) {
    $result = Test-NetConnection -ComputerName '127.0.0.1' -Port $Port -WarningAction SilentlyContinue -InformationLevel Quiet
    $status = $result ? 'LISTENING' : 'NOT LISTENING'
    $color = $result ? 'Green' : 'Red'
    Write-Host "[UAT] Port $Port ($Name): $status" -ForegroundColor $color
    return $result
}

Write-Host "[UAT] Verifying ports..." -ForegroundColor Cyan
for ($i = 0; $i -lt 3; $i++) {
    $apiOk = Test-Port -Port $ApiPort -Name 'Backend'
    $uiOk = Test-Port -Port $UiPort -Name 'UI'
    if ($apiOk -and $uiOk) { break }
    if ($i -lt 2) { Start-Sleep -Seconds 2 }
}

Write-Host "[UAT] To stop processes, run: Stop-Process -Id $($apiProcess.Id), $($uiProcess.Id)" -ForegroundColor Magenta
