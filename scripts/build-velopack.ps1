<#
.SYNOPSIS
    Build Pia as a Velopack installer for local development and testing.

.DESCRIPTION
    Publishes the WPF client as a self-contained single-file executable, then
    packages it with Velopack. Version is determined automatically by
    Nerdbank.GitVersioning (nbgv).

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER Runtime
    Target runtime identifier (default: win-x64).

.EXAMPLE
    .\build-velopack.ps1
    .\build-velopack.ps1 -Configuration Debug
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ProjectFile = Join-Path $RepoRoot "src\Pia.Wpf\Pia.Wpf.csproj"
$PublishDir = Join-Path $RepoRoot "artifacts\publish"
$ReleaseDir = Join-Path $RepoRoot "artifacts\velopack"

# --- Determine version via nbgv ---
Write-Host "Detecting version from Nerdbank.GitVersioning..." -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    dotnet tool restore 2>&1 | Out-Null
    $Version = & dotnet nbgv get-version -v SimpleVersion
    if ($LASTEXITCODE -ne 0) {
        Write-Error "nbgv failed. Ensure you are inside a git repository with version.json."
        exit 1
    }
} finally {
    Pop-Location
}
Write-Host "Version: $Version" -ForegroundColor Green

# --- Ensure vpk CLI is available ---
Write-Host "Checking for Velopack CLI (vpk)..." -ForegroundColor Cyan
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing vpk globally..." -ForegroundColor Yellow
    dotnet tool install -g vpk --allow-roll-forward
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to install vpk. Install manually: dotnet tool install -g vpk"
        exit 1
    }
}
Write-Host "vpk found." -ForegroundColor Green

# --- Clean previous artifacts ---
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
if (Test-Path $ReleaseDir) { Remove-Item $ReleaseDir -Recurse -Force }

# --- Publish self-contained single-file ---
Write-Host "`nPublishing self-contained build..." -ForegroundColor Cyan
dotnet publish $ProjectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# --- Pack with Velopack ---
Write-Host "`nCreating Velopack release..." -ForegroundColor Cyan
vpk pack `
    -u Pia.Wpf `
    -v $Version `
    -p $PublishDir `
    -o $ReleaseDir `
    --mainExe Pia.Wpf.exe `
    --icon "$RepoRoot\src\Pia.Wpf\Resources\Icons\Pia.ico" `
    --packTitle "Pia" `
    --packAuthors "Pia-Ai-dev" `
    --instLicense "$RepoRoot\src\Pia.Wpf\Resources\Installer\LICENSE.txt" `
    --msiBanner "$RepoRoot\src\Pia.Wpf\Resources\Installer\banner.bmp" `
    --msiLogo "$RepoRoot\src\Pia.Wpf\Resources\Installer\logo.bmp" `
    --msi

if ($LASTEXITCODE -ne 0) {
    Write-Error "vpk pack failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# --- Summary ---
Write-Host "`n========================================" -ForegroundColor Green
Write-Host " Velopack build complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Version:   $Version"
Write-Host "  Output:    $ReleaseDir"
Write-Host ""

$setup = Get-ChildItem -Path $ReleaseDir -Filter "*Setup*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($setup) {
    Write-Host "  Installer: $($setup.FullName)" -ForegroundColor White
}

$msi = Get-ChildItem -Path $ReleaseDir -Filter "*.msi" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($msi) {
    Write-Host "  MSI:       $($msi.FullName)" -ForegroundColor White
}

$nupkg = Get-ChildItem -Path $ReleaseDir -Filter "*.nupkg" -ErrorAction SilentlyContinue
foreach ($pkg in $nupkg) {
    Write-Host "  Package:   $($pkg.Name)" -ForegroundColor White
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
