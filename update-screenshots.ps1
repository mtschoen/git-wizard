#!/usr/bin/env pwsh
# Script to automatically update UI screenshots for documentation

$ErrorActionPreference = "Stop"

Write-Host "GitWizard MAUI Screenshot Updater" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "WARNING: Not running as Administrator. Tests may fail to start WinAppDriver." -ForegroundColor Yellow
    Write-Host "Restart PowerShell as Administrator for best results." -ForegroundColor Yellow
    Write-Host ""
}

try {
    # Build the MAUI project
    Write-Host ""
    Write-Host "Building MAUI project..." -ForegroundColor Yellow
    dotnet build "GitWizardUI\GitWizardUI.csproj" -c Debug -f net10.0-windows10.0.19041.0

    if ($LASTEXITCODE -ne 0) {
        throw "Build failed"
    }
    Write-Host "Build successful!" -ForegroundColor Green

    # Run screenshot capture test
    Write-Host ""
    Write-Host "Capturing screenshots..." -ForegroundColor Yellow
    dotnet test "GitWizardUI.UITests\GitWizardUI.UITests.csproj" `
        --filter "FullyQualifiedName~CaptureMainWindowScreenshot" `
        --logger "console;verbosity=normal"

    if ($LASTEXITCODE -ne 0) {
        throw "Screenshot capture failed"
    }

    # List generated screenshots
    Write-Host ""
    Write-Host "Screenshots updated:" -ForegroundColor Green
    Get-ChildItem -Path "." -Filter "maui-ui*.png" | ForEach-Object {
        $size = [math]::Round($_.Length / 1KB, 2)
        Write-Host "  - $($_.Name) (${size} KB)" -ForegroundColor Cyan
    }
}
catch {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Done! Screenshots are ready for documentation." -ForegroundColor Green
