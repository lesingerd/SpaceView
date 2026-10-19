[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\SpaceView")
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  SpaceView Uninstaller" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# 1. Stop any running SpaceView instance
$running = Get-Process -Name "SpaceView" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[1/3] Stopping running SpaceView instance(s)..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 600
} else {
    Write-Host "[1/3] No running SpaceView instance detected." -ForegroundColor Gray
}

# 2. Remove shortcuts
Write-Host "[2/3] Removing shortcuts..." -ForegroundColor Cyan
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "SpaceView.lnk"
if (Test-Path $startMenuShortcut) {
    Remove-Item -Path $startMenuShortcut -Force
    Write-Host "      Removed Start Menu shortcut: $startMenuShortcut" -ForegroundColor Green
}

$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "SpaceView.lnk"
if (Test-Path $desktopShortcut) {
    Remove-Item -Path $desktopShortcut -Force
    Write-Host "      Removed Desktop shortcut:    $desktopShortcut" -ForegroundColor Green
}

# 3. Remove installation folder
Write-Host "[3/3] Removing installation directory..." -ForegroundColor Cyan
if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "      Removed directory: $InstallDir" -ForegroundColor Green
} else {
    Write-Host "      Install directory not found ($InstallDir)." -ForegroundColor Gray
}

Write-Host ""
Write-Host "SpaceView has been uninstalled." -ForegroundColor Green
