[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  SpaceView MSI Installer Build" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

$repoRoot = $PSScriptRoot
$projectFile = Join-Path $repoRoot "SpaceView\SpaceView.csproj"
$wixProject = Join-Path $repoRoot "installer\SpaceView.Installer.wixproj"
$iconGenScript = Join-Path $repoRoot "tools\generate-icon.ps1"

if (-not (Test-Path $projectFile)) {
    throw "Could not find project file at: $projectFile"
}
if (-not (Test-Path $wixProject)) {
    throw "Could not find installer project at: $wixProject"
}

# Ensure multi-resolution classic DIB + PNG icon is generated before building
if (Test-Path $iconGenScript) {
    & $iconGenScript
}

# Default output folder: <repo>\artifacts\installer
# (resolved here because $PSScriptRoot is empty in param() defaults under Windows PowerShell 5.1 with [CmdletBinding()])
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\installer"
}
# Resolve against the PowerShell location (not the .NET process directory) so relative paths behave as typed
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir).TrimEnd('\')
$payloadDir = Join-Path $OutputDir "payload"

# Version: 1.0.<commit count> (fallback 1.0.0)
$version = "1.0.0"
$commit = "unknown"
try {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $count = (& git -C $repoRoot rev-list --count HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and $count -match '^\d+$') { $version = "1.0.$count" }
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
Write-Host "Product version: $version" -ForegroundColor Gray

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
        Write-Host "[1/4] Cleaning output folder: $OutputDir" -ForegroundColor Yellow
        Remove-Item -Path $OutputDir -Recurse -Force
    } else {
        Write-Host "[1/4] Output folder does not exist, nothing to clean." -ForegroundColor Gray
    }
} else {
    Write-Host "[1/4] Clean skipped (use -Clean to remove the output folder first)." -ForegroundColor Gray
}

# The payload is always rebuilt from scratch so stale files never end up in the MSI.
# Same safety rule: only delete a 'payload' folder that lives inside an 'artifacts' folder of a SpaceView repo.
if (Test-Path $payloadDir) {
    $artifactsRoot = Join-Path $repoRoot "artifacts"
    if (-not $payloadDir.StartsWith($artifactsRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset payload folder '$payloadDir': it is not inside '$artifactsRoot'."
    }
    Remove-Item -Path $payloadDir -Recurse -Force
}

# 2. Publish a self-contained win-x64 build (bundles the .NET runtime)
Write-Host "[2/4] Publishing self-contained win-x64 payload to: $payloadDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null

dotnet publish $projectFile -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:Version=$version -p:FileVersion=$version `
    -o $payloadDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path (Join-Path $payloadDir "SpaceView.exe"))) {
    throw "Publish finished but SpaceView.exe was not found in: $payloadDir"
}

# 3. Build the MSI with WiX Toolset v5 (restored from nuget.org by the build, nothing installed globally)
Write-Host "[3/4] Building MSI with WiX..." -ForegroundColor Cyan
dotnet build $wixProject -c $Configuration -p:ProductVersion=$version `
    "-p:PayloadDir=$payloadDir" "-p:OutDir=$OutputDir/wix/" --nologo
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE"
}

$builtMsi = Get-ChildItem -Path (Join-Path $OutputDir "wix") -Filter "*.msi" -Recurse | Select-Object -First 1
if (-not $builtMsi) {
    throw "WiX build finished but no .msi was found under: $OutputDir\wix"
}
$msiPath = Join-Path $OutputDir "SpaceView-$version-x64.msi"
Copy-Item -Path $builtMsi.FullName -Destination $msiPath -Force

# 4. Summary
Write-Host "[4/4] Installer build complete." -ForegroundColor Cyan

$msiSize = "{0:N1} MB" -f ((Get-Item $msiPath).Length / 1MB)

Write-Host ""
Write-Host "SpaceView installer has been built successfully!" -ForegroundColor Green
Write-Host "  MSI           : $msiPath"
Write-Host "  Version       : $version"
Write-Host "  MSI Size      : $msiSize"
Write-Host "  Git Commit    : $commit"
