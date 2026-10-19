[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\SpaceView"),
    [switch]$CreateDesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  SpaceView Installer" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

$repoRoot = $PSScriptRoot
$projectFile = Join-Path $repoRoot "SpaceView\SpaceView.csproj"

if (-not (Test-Path $projectFile)) {
    throw "Could not find project file at: $projectFile"
}

# 1. Stop any running SpaceView process so files are not locked
$running = Get-Process -Name "SpaceView" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[1/4] Stopping running SpaceView instance(s)..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 600
} else {
    Write-Host "[1/4] No running SpaceView instance detected." -ForegroundColor Gray
}

# 2. Publish Release binaries to the target install folder
Write-Host "[2/4] Publishing Release binaries to: $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

dotnet publish $projectFile -c Release -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $InstallDir "SpaceView.exe"
$icoSource = Join-Path $repoRoot "SpaceView\Assets\SpaceView.ico"
$icoDest = Join-Path $InstallDir "SpaceView.ico"
if (Test-Path $icoSource) {
    Copy-Item -Path $icoSource -Destination $icoDest -Force
}

# 3. Create Start Menu Shortcut (and optional Desktop Shortcut)
Write-Host "[3/4] Creating Start Menu shortcut..." -ForegroundColor Cyan
$startMenuDir = [Environment]::GetFolderPath("Programs")
$startMenuShortcutPath = Join-Path $startMenuDir "SpaceView.lnk"

$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($startMenuShortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = "SpaceView - Disk Consumption & Live NTFS USN Monitor"
$shortcut.Save()

Write-Host "      Start Menu shortcut created: $startMenuShortcutPath" -ForegroundColor Green

if ($CreateDesktopShortcut) {
    $desktopDir = [Environment]::GetFolderPath("Desktop")
    $desktopShortcutPath = Join-Path $desktopDir "SpaceView.lnk"
    $deskShortcut = $wshShell.CreateShortcut($desktopShortcutPath)
    $deskShortcut.TargetPath = $exePath
    $deskShortcut.WorkingDirectory = $InstallDir
    $deskShortcut.IconLocation = "$exePath,0"
    $deskShortcut.Description = "SpaceView - Disk Consumption & Live NTFS USN Monitor"
    $deskShortcut.Save()
    Write-Host "      Desktop shortcut created:    $desktopShortcutPath" -ForegroundColor Green
}

# 4. Launch installed application
if (-not $NoLaunch) {
    Write-Host "[4/4] Launching SpaceView from $exePath..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
} else {
    Write-Host "[4/4] Installation complete (launch skipped)." -ForegroundColor Cyan
}

Write-Host ""
Write-Host "SpaceView has been installed successfully!" -ForegroundColor Green
Write-Host "  Install Folder : $InstallDir"
Write-Host "  Start Menu     : $startMenuShortcutPath"
