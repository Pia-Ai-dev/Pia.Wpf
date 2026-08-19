#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Replays the recorded WinWright UI scripts in this folder against the Pia desktop client.

.DESCRIPTION
  Each script under scripts/ is a ww_record export. This harness resolves the app exe and the
  WinWright binary, seeds a known settings profile so a script can run more than once, replays
  every script through `winwright run`, then restores the profile it replaced.

  Exit codes: 0 = all scripts passed, 1 = at least one script failed, 2 = harness/setup problem.

.EXAMPLE
  ./Invoke-UiScripts.ps1
  Replays every script against the Debug build.

.EXAMPLE
  ./Invoke-UiScripts.ps1 -Name settings-general -Format junit
  Replays one script and writes a junit report into artifacts/.

.EXAMPLE
  ./Invoke-UiScripts.ps1 -Heal
  Probes every selector against the app's start screen instead of replaying. Expect false alarms
  for steps whose target is not on the first screen — see README.
#>
[CmdletBinding()]
param(
    # Script names to run (with or without .json). Default: every script in scripts/.
    [string[]]$Name = @(),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # Overrides the app exe discovery.
    [string]$ExePath,

    # Overrides the WinWright binary location.
    [string]$WinWrightPath,

    [ValidateSet('text', 'junit')]
    [string]$Format = 'text',

    # Where junit reports, healed scripts and the profile backup land.
    [string]$OutputDir,

    # Capture screenshots at assertion points.
    [switch]$Screenshots,

    # Probe selectors (winwright heal) instead of replaying.
    [switch]$Heal,

    # Run against the profile as it is — do not seed or restore settings.json.
    [switch]$KeepProfile,

    [switch]$ListScripts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$harnessRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $harnessRoot '../..')).Path
$scriptDir = Join-Path $harnessRoot 'scripts'
$fixturePath = Join-Path $harnessRoot 'fixtures/settings.ui-test-seed.json'
if (-not $OutputDir) { $OutputDir = Join-Path $harnessRoot 'artifacts' }

function Fail($message) {
    Write-Host "harness: $message" -ForegroundColor Red
    exit 2
}

# --- script selection -------------------------------------------------------

$available = @(Get-ChildItem -Path $scriptDir -Filter '*.json' | Sort-Object Name)
if ($available.Count -eq 0) { Fail "no scripts found in $scriptDir" }

if ($ListScripts) {
    $available | ForEach-Object { Write-Host "  $($_.BaseName)" }
    exit 0
}

$selected = $available
if ($Name.Count -gt 0) {
    $wanted = @($Name | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) })
    $selected = @($available | Where-Object { $wanted -contains $_.BaseName })
    $found = @($selected | ForEach-Object { $_.BaseName })
    $missing = @($wanted | Where-Object { $found -notcontains $_ })
    if ($missing.Count -gt 0) { Fail "unknown script(s): $($missing -join ', ')" }
}
$selectedNames = @($selected | ForEach-Object { $_.BaseName })

# --- tool + app discovery ---------------------------------------------------

if (-not $WinWrightPath) {
    $WinWrightPath = Join-Path $env:LOCALAPPDATA 'WinWright/Civyk.WinWright.Mcp.exe'
}
if (-not (Test-Path $WinWrightPath)) {
    Fail "WinWright not found at $WinWrightPath. Install it or pass -WinWrightPath."
}

if (-not $ExePath) {
    $binDir = Join-Path $repoRoot "src/Pia.Wpf/bin/$Configuration"
    $candidate = Get-ChildItem -Path $binDir -Filter 'Pia.Wpf.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { Fail "no Pia.Wpf.exe under $binDir. Run: dotnet build -c $Configuration" }
    $ExePath = $candidate.FullName
}
if (-not (Test-Path $ExePath)) { Fail "app exe not found: $ExePath" }

# The app rewrites settings.json on every property change, so a live instance would eat the seed
# and fight the restore.
if (Get-Process -Name 'Pia.Wpf' -ErrorAction SilentlyContinue) {
    Fail 'Pia is running. Close it (including the tray icon) and re-run.'
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "app:       $ExePath"
Write-Host "winwright: $WinWrightPath"
Write-Host "scripts:   $($selectedNames -join ', ')"

# --- profile seeding --------------------------------------------------------

$settingsPath = Join-Path $env:APPDATA 'Pia/settings.json'
$backupPath = $null
$hadSettings = Test-Path $settingsPath

if (-not $KeepProfile) {
    if (-not (Test-Path $fixturePath)) { Fail "fixture not found: $fixturePath" }
    $backupDir = Join-Path $OutputDir 'profile-backup'
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    if ($hadSettings) {
        $backupPath = Join-Path $backupDir ("settings.{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Copy-Item $settingsPath $backupPath -Force
        Write-Host "profile:   seeded from fixture, your settings.json backed up to" -ForegroundColor Yellow
        Write-Host "           $backupPath" -ForegroundColor Yellow
    }
    else {
        Write-Host "profile:   no existing settings.json, seeding fixture" -ForegroundColor Yellow
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
    Copy-Item $fixturePath $settingsPath -Force
}
else {
    Write-Host 'profile:   -KeepProfile, running against current settings.json' -ForegroundColor Yellow
}

# --- replay -----------------------------------------------------------------

$results = [System.Collections.Generic.List[object]]::new()
$exeJson = ConvertTo-Json $ExePath

try {
    foreach ($script in $selected) {
        $raw = Get-Content -Raw -Path $script.FullName
        if ($raw -notmatch '\{\{APP_EXE\}\}') {
            Write-Host "warn: $($script.BaseName) has no {{APP_EXE}} placeholder, its own launchPath is used" -ForegroundColor Yellow
        }
        $prepared = $raw.Replace('"{{APP_EXE}}"', $exeJson)
        $tempScript = Join-Path ([IO.Path]::GetTempPath()) ("pia-ui-{0}-{1}.json" -f $script.BaseName, $PID)
        Set-Content -Path $tempScript -Value $prepared -Encoding utf8NoBOM

        $wwArgs = @()
        if ($Heal) {
            $wwArgs = @('heal', $tempScript, '--output', (Join-Path $OutputDir "$($script.BaseName).healed.json"))
        }
        else {
            $wwArgs = @('run', $tempScript, '--format', $Format)
            if ($Format -eq 'junit') {
                $wwArgs += @('--output', (Join-Path $OutputDir "$($script.BaseName).junit.xml"))
            }
            if ($Screenshots) {
                $shotDir = Join-Path $OutputDir "screenshots/$($script.BaseName)"
                New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
                $wwArgs += @('--screenshots', '--screenshots-dir', $shotDir)
            }
        }

        Write-Host ''
        Write-Host "--- $($script.BaseName) ---" -ForegroundColor Cyan
        & $WinWrightPath @wwArgs
        $code = $LASTEXITCODE
        Remove-Item $tempScript -Force -ErrorAction SilentlyContinue

        $verdict = if ($Heal) {
            switch ($code) { 0 { 'clean' } 2 { 'findings' } default { "error ($code)" } }
        }
        else {
            switch ($code) { 0 { 'pass' } 1 { 'FAIL' } default { "error ($code)" } }
        }
        $results.Add([pscustomobject]@{ Script = $script.BaseName; Verdict = $verdict; ExitCode = $code })
    }
}
finally {
    if (-not $KeepProfile) {
        # `run` closes the app, but the app writes settings on shutdown — restoring before that
        # write lands would leave the fixture in place.
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Process -Name 'Pia.Wpf' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Name 'Pia.Wpf' -ErrorAction SilentlyContinue) {
            Write-Host 'warn: Pia is still running; restoring settings.json anyway' -ForegroundColor Yellow
        }

        if ($backupPath) {
            Copy-Item $backupPath $settingsPath -Force
            $restored = (Get-FileHash $settingsPath).Hash -eq (Get-FileHash $backupPath).Hash
            if ($restored) {
                Write-Host ''
                Write-Host 'profile:   settings.json restored' -ForegroundColor Green
            }
            else {
                Write-Host ''
                Write-Host "profile:   RESTORE MISMATCH — recover by hand from $backupPath" -ForegroundColor Red
            }
        }
        elseif (-not $hadSettings) {
            Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue
            Write-Host 'profile:   seeded settings.json removed' -ForegroundColor Green
        }
    }
}

# --- summary ----------------------------------------------------------------

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host

if ($Heal) { exit 0 }

$failed = @($results | Where-Object { $_.ExitCode -ne 0 })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) of $($results.Count) script(s) failed" -ForegroundColor Red
    exit 1
}
Write-Host "$($results.Count) script(s) passed" -ForegroundColor Green
exit 0
