#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Replays the recorded WinWright UI scripts in this folder against the Pia desktop client.

.DESCRIPTION
  Each script under scripts/ is a ww_record export. By default the harness points the app at a throwaway
  data directory (PIA_DATA_DIR / PIA_LOCAL_DATA_DIR), seeds the fixture there, replays every script through
  `winwright run`, and verifies by hash that the real profile was never written. Nothing in
  %APPDATA%\Pia or %LOCALAPPDATA%\Pia is read, seeded or restored, so a replay can run while your own Pia
  instance is open.

  Exit codes: 0 = all scripts passed, 1 = at least one script failed, 2 = harness/setup problem.

.EXAMPLE
  ./Invoke-UiScripts.ps1
  Replays every script against the Debug build in a fresh temp profile.

.EXAMPLE
  ./Invoke-UiScripts.ps1 -DataDir C:\temp\pia-ui -KeepDataDir
  Replays into a named profile and keeps it afterwards (settings.json, history.db and the logs).

.EXAMPLE
  ./Invoke-UiScripts.ps1 -KeepProfile
  Drives your real profile instead. Close Pia first: the harness seeds settings.json and restores it after.

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

    # Roots the app's data directories here instead of a fresh temp directory.
    [string]$DataDir,

    # Keep the throwaway data directory after a passing run (a failing run always keeps it).
    [switch]$KeepDataDir,

    # Capture screenshots at assertion points.
    [switch]$Screenshots,

    # Probe selectors (winwright heal) instead of replaying.
    [switch]$Heal,

    # Drive the REAL profile: seed and restore %APPDATA%\Pia\settings.json the pre-override way.
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

function Get-HashOrNull($path) {
    if (-not (Test-Path $path)) { return $null }
    try { return (Get-FileHash $path).Hash }
    catch { return 'unreadable' }
}

$hermetic = -not $KeepProfile
if ($DataDir -and $KeepProfile) { Fail '-DataDir and -KeepProfile are mutually exclusive.' }

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

# A live instance only matters when we are driving the real profile: the app rewrites settings.json on every
# property change, so it would eat the seed and fight the restore. A hermetic run shares nothing with it.
$preExistingPids = @(Get-Process -Name 'Pia.Wpf' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$otherInstanceRunning = $preExistingPids.Count -gt 0
if (-not $hermetic -and $otherInstanceRunning) {
    Fail 'Pia is running. Close it (including the tray icon), re-run, or drop -KeepProfile.'
}

# Waits for the instance THIS run launched, ignoring one the developer already had open.
function Wait-ForReplayedAppExit([int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $ours = @(Get-Process -Name 'Pia.Wpf' -ErrorAction SilentlyContinue |
            Where-Object { $preExistingPids -notcontains $_.Id })
        if ($ours.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "app:       $ExePath"
Write-Host "winwright: $WinWrightPath"
Write-Host "scripts:   $($selectedNames -join ', ')"

# --- profile ----------------------------------------------------------------

$realSettings = Join-Path $env:APPDATA 'Pia/settings.json'
$realHistoryDb = Join-Path $env:LOCALAPPDATA 'Pia/history.db'
$backupPath = $null
$hadSettings = Test-Path $realSettings
$realHashesBefore = $null
$previousDataDirEnv = $env:PIA_DATA_DIR
$previousLocalDataDirEnv = $env:PIA_LOCAL_DATA_DIR

if ($hermetic) {
    if (-not (Test-Path $fixturePath)) { Fail "fixture not found: $fixturePath" }
    if (-not $DataDir) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $DataDir = Join-Path ([IO.Path]::GetTempPath()) "pia-ui-profile-$stamp-$PID"
    }
    $roamingDir = Join-Path $DataDir 'roaming'
    $localDir = Join-Path $DataDir 'local'
    New-Item -ItemType Directory -Force -Path $roamingDir, $localDir | Out-Null
    Copy-Item $fixturePath (Join-Path $roamingDir 'settings.json') -Force

    # Set on this process so the app inherits it: `winwright run` has no --env flag, but the app it launches
    # inherits the harness environment.
    $env:PIA_DATA_DIR = $roamingDir
    $env:PIA_LOCAL_DATA_DIR = $localDir

    $realHashesBefore = @{ settings = Get-HashOrNull $realSettings; history = Get-HashOrNull $realHistoryDb }

    Write-Host "profile:   throwaway, seeded from fixture" -ForegroundColor Yellow
    Write-Host "           $DataDir" -ForegroundColor Yellow
    if ($otherInstanceRunning) {
        Write-Host 'note:      your own Pia is running; it shares nothing with this run' -ForegroundColor Yellow
    }
}
else {
    if (-not (Test-Path $fixturePath)) { Fail "fixture not found: $fixturePath" }
    $backupDir = Join-Path $OutputDir 'profile-backup'
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    if ($hadSettings) {
        $backupPath = Join-Path $backupDir ("settings.{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Copy-Item $realSettings $backupPath -Force
        Write-Host "profile:   -KeepProfile, seeded from fixture, your settings.json backed up to" -ForegroundColor Yellow
        Write-Host "           $backupPath" -ForegroundColor Yellow
    }
    else {
        Write-Host "profile:   -KeepProfile, no existing settings.json, seeding fixture" -ForegroundColor Yellow
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $realSettings) | Out-Null
    Copy-Item $fixturePath $realSettings -Force
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
    $env:PIA_DATA_DIR = $previousDataDirEnv
    $env:PIA_LOCAL_DATA_DIR = $previousLocalDataDirEnv

    if (-not $hermetic) {
        # `run` closes the app, but the app writes settings on shutdown — restoring before that
        # write lands would leave the fixture in place.
        if (-not (Wait-ForReplayedAppExit)) {
            Write-Host 'warn: Pia is still running; restoring settings.json anyway' -ForegroundColor Yellow
        }

        if ($backupPath) {
            Copy-Item $backupPath $realSettings -Force
            $restored = (Get-FileHash $realSettings).Hash -eq (Get-FileHash $backupPath).Hash
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
            Remove-Item $realSettings -Force -ErrorAction SilentlyContinue
            Write-Host 'profile:   seeded settings.json removed' -ForegroundColor Green
        }
    }
}

# --- summary ----------------------------------------------------------------

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host

$failed = @($results | Where-Object { $_.ExitCode -ne 0 })
$profileLeak = $false

if ($hermetic) {
    # The replay must not have touched the real profile. An instance the developer already had open writes to
    # it for reasons of its own, so a change is only attributable to this run when nothing else was alive.
    Wait-ForReplayedAppExit | Out-Null
    $realHashesAfter = @{ settings = Get-HashOrNull $realSettings; history = Get-HashOrNull $realHistoryDb }
    $changed = @('settings', 'history' | Where-Object { $realHashesBefore[$_] -ne $realHashesAfter[$_] })

    if ($changed.Count -eq 0) {
        Write-Host 'profile:   real profile untouched (settings.json and history.db hash-identical)' -ForegroundColor Green
    }
    elseif ($otherInstanceRunning) {
        Write-Host "profile:   real $($changed -join ' + ') changed, but your own Pia was running — not attributable" -ForegroundColor Yellow
    }
    else {
        Write-Host "profile:   LEAK — real $($changed -join ' + ') changed during a hermetic run" -ForegroundColor Red
        $profileLeak = $true
    }

    if ($KeepDataDir -or $failed.Count -gt 0 -or $profileLeak) {
        Write-Host "profile:   kept at $DataDir" -ForegroundColor Yellow
    }
    else {
        Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($Heal) { if ($profileLeak) { exit 2 } else { exit 0 } }

if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) of $($results.Count) script(s) failed" -ForegroundColor Red
    exit 1
}
if ($profileLeak) {
    Write-Host "$($results.Count) script(s) passed but the real profile was written" -ForegroundColor Red
    exit 1
}
Write-Host "$($results.Count) script(s) passed" -ForegroundColor Green
exit 0
