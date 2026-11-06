[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  SpaceView Build" -ForegroundColor Cyan
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

# Default output folder: <repo>\artifacts\publish
# (resolved here because $PSScriptRoot is empty in param() defaults under Windows PowerShell 5.1 with [CmdletBinding()])
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\publish"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir).TrimEnd('\')

# 1. Optionally clean the output folder (only ever inside a SpaceView "artifacts" folder)
if ($Clean) {
    # Safe rule: the path must be a strict descendant of a folder named "artifacts"
    # whose parent folder is a SpaceView repo root (contains SpaceView.sln).
    $safe = $false
    $candidate = $OutputDir
    while ($true) {
        $parent = Split-Path -Parent $candidate
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $candidate) { break }
        if ((Split-Path -Leaf $parent) -eq "artifacts" -and
            (Test-Path (Join-Path (Split-Path -Parent $parent) "SpaceView.sln"))) {
            $safe = $true
            break
        }
        $candidate = $parent
    }
    if (-not $safe) {
        throw "Refusing to clean '$OutputDir': -Clean only removes folders inside the 'artifacts' folder of a SpaceView repo."
    }
    if (Test-Path $OutputDir) {
        Write-Host "[1/3] Cleaning output folder: $OutputDir" -ForegroundColor Yellow
        Remove-Item -Path $OutputDir -Recurse -Force
    } else {
        Write-Host "[1/3] Output folder does not exist, nothing to clean." -ForegroundColor Gray
    }
} else {
    Write-Host "[1/3] Clean skipped (use -Clean to remove the output folder first)." -ForegroundColor Gray
}

# 2. Publish binaries to the output folder
Write-Host "[2/3] Publishing $Configuration binaries to: $OutputDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

dotnet publish $projectFile -c $Configuration -o $OutputDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $OutputDir "SpaceView.exe"
$icoSource = Join-Path $repoRoot "SpaceView\Assets\SpaceView.ico"
$icoDest = Join-Path $OutputDir "SpaceView.ico"
if (Test-Path $icoSource) {
    Copy-Item -Path $icoSource -Destination $icoDest -Force
}

if (-not (Test-Path $exePath)) {
    throw "Publish finished but SpaceView.exe was not found at: $exePath"
}

# 3. Summary
Write-Host "[3/3] Build complete." -ForegroundColor Cyan

$fileVersion = (Get-Item $exePath).VersionInfo.FileVersion
$totalBytes = (Get-ChildItem -Path $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalSize = "{0:N1} MB" -f ($totalBytes / 1MB)

$commit = "unknown"
try {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $hash = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and $hash) {
            $commit = "$hash"
            $dirty = (& git -C $repoRoot status --porcelain 2>$null)
            if ($dirty) { $commit += "-dirty" }
        }
    }
} catch {
    # Non-fatal if git is unavailable
}

Write-Host ""
Write-Host "SpaceView has been built successfully!" -ForegroundColor Green
Write-Host "  Output Folder : $OutputDir"
Write-Host "  Executable    : $exePath"
Write-Host "  File Version  : $fileVersion"
Write-Host "  Total Size    : $totalSize"
Write-Host "  Git Commit    : $commit"
