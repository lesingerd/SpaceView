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
$iconGenScript = Join-Path $repoRoot "tools\generate-icon.ps1"

if (-not (Test-Path $projectFile)) {
    throw "Could not find project file at: $projectFile"
}

# Ensure multi-resolution classic DIB + PNG icon is generated before building
if (Test-Path $iconGenScript) {
    & $iconGenScript
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

function New-AppShortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetExe,
        [string]$WorkDir,
        [string]$IconPath
    )

    if (Test-Path $ShortcutPath) {
        Remove-Item -Path $ShortcutPath -Force
    }

    $parentDir = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }

    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetExe
    $shortcut.WorkingDirectory = $WorkDir
    $shortcut.IconLocation = "$IconPath,0"
    $shortcut.WindowStyle = 1
    $shortcut.Description = "SpaceView - Disk Consumption & Live NTFS USN Monitor"
    $shortcut.Save()
}

$userProgramsDir = [Environment]::GetFolderPath("Programs")

# Clean up any duplicate subfolder if present
$oldSubfolder = Join-Path $userProgramsDir "SpaceView"
if (Test-Path $oldSubfolder) {
    Remove-Item -Path $oldSubfolder -Recurse -Force -ErrorAction SilentlyContinue
}

$startMenuShortcutPath = Join-Path $userProgramsDir "SpaceView.lnk"
New-AppShortcut -ShortcutPath $startMenuShortcutPath -TargetExe $exePath -WorkDir $InstallDir -IconPath $icoDest
Write-Host "      Start Menu shortcut created: $startMenuShortcutPath" -ForegroundColor Green

if ($CreateDesktopShortcut) {
    $desktopDir = [Environment]::GetFolderPath("Desktop")
    $desktopShortcutPath = Join-Path $desktopDir "SpaceView.lnk"
    New-AppShortcut -ShortcutPath $desktopShortcutPath -TargetExe $exePath -WorkDir $InstallDir -IconPath $icoDest
    Write-Host "      Desktop shortcut created:    $desktopShortcutPath" -ForegroundColor Green
}

# Notify Windows Shell & Start Menu indexer that shortcuts and icons changed
try {
    if (-not ("ShellNotify" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ShellNotify {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);
}
"@
    }
    # SHCNE_CREATE (0x0002) + SHCNE_UPDATEDIR (0x1000) + SHCNE_ASSOCCHANGED (0x08000000)
    [ShellNotify]::SHChangeNotify(0x00000002, 0x0005, $startMenuShortcutPath, [IntPtr]::Zero)
    [ShellNotify]::SHChangeNotify(0x00001000, 0x0005, $userProgramsDir, [IntPtr]::Zero)
    [ShellNotify]::SHChangeNotify(0x08000000, 0x0000, $null, [IntPtr]::Zero)
} catch {
    # Non-fatal if shell notification fails
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
