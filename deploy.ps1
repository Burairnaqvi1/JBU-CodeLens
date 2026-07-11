# CodeLensAI Deployment Script
# Run from repo root: .\deploy.ps1

Write-Host "Building CodeLensAI for deployment..." -ForegroundColor Cyan

$publishDir = ".\publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish src\CodeLensAI.UI\CodeLensAI.UI.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    --output $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}

# Create models folder in publish output
$modelsDir = "$publishDir\models"
if (-not (Test-Path $modelsDir)) { New-Item -ItemType Directory -Path $modelsDir }

Write-Host ""
Write-Host "Deployment build complete." -ForegroundColor Green
Write-Host "Output folder: $publishDir"
Write-Host ""
Write-Host "BEFORE COPYING TO TARGET MACHINE:" -ForegroundColor Yellow
Write-Host "  Copy your .gguf model file into: $modelsDir"
Write-Host "  The app will find it automatically on startup."
Write-Host ""
Write-Host "NO .NET RUNTIME INSTALLATION REQUIRED on target machine." -ForegroundColor Green
