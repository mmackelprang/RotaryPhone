Write-Host "----------------------------------------------------------------"
Write-Host "Rotary Phone Controller - Build & Publish Script"
Write-Host "----------------------------------------------------------------"

# 1. Build Frontend
Write-Host "Step 1: Building React Frontend..." -ForegroundColor Cyan
Push-Location src/RotaryPhoneController.Client
try {
    npm install
    if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
    
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm build failed" }
}
finally {
    Pop-Location
}

# 2. Build Backend
Write-Host "Step 2: Publishing .NET Backend..." -ForegroundColor Cyan
$publishDir = Join-Path $PWD "publish"
dotnet publish src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "----------------------------------------------------------------"
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "Artifacts are in: $publishDir"
Write-Host "To run: cd publish; ./RotaryPhoneController.Server.exe"
Write-Host "----------------------------------------------------------------"
