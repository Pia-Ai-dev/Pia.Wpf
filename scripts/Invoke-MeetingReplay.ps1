#requires -version 7
<#
.SYNOPSIS
  Replays a recorded meeting through the real Meeting Attendee pipeline, end to end, unattended.

.DESCRIPTION
  Sets PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE (plus the roster override, without which the replay
  measures expected=0 and the roster ceiling is off), launches Pia against a throwaway profile,
  drives the join form through UI Automation, waits for the recording to play out, then stops and
  saves the transcript. The log it leaves behind is the input to Measure-SpeakerAttribution.ps1.

  Why UI Automation and not WinWright: a WinWright session owns the process it launched and kills it
  when the session expires, which a 20–50 minute replay outlives.

  The throwaway profile is seeded from the real %APPDATA%\Pia so the app starts configured and
  signed in (the cloud tokens live inside settings.json). Sync is switched off in the copy and
  pending-sync-deletes.json is not copied, so a throwaway run cannot write to the live account.
  %LOCALAPPDATA% is left empty, which is what isolates the log.

.EXAMPLE
  ./scripts/Invoke-MeetingReplay.ps1 -AudioPath 'artifacts/meeting_recording/x.mp4' -RosterSize 10 `
      -RunName workshop-head
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AudioPath,
    # Humans on the roster, excluding Pia itself. Feeds the diarizer's speaker-count ceiling.
    [Parameter(Mandatory)][int]$RosterSize,
    [Parameter(Mandatory)][string]$RunName,
    [string]$AppDir,
    [string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'pia-meeting-replay'),
    # Grace period after the recording plays out, for the transcription queue to drain.
    [int]$DrainSeconds = 180,
    [int]$JoinTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
$audio = (Resolve-Path -LiteralPath $AudioPath).Path
if (-not $AppDir) {
    $AppDir = Join-Path $repoRoot 'src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0'
}
$exe = Join-Path $AppDir 'Pia.Wpf.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Pia.Wpf.exe not found at $exe — build first." }

$runDir = Join-Path $WorkRoot $RunName
$roaming = Join-Path $runDir 'roaming'
$local = Join-Path $runDir 'local'
if (Test-Path -LiteralPath $runDir) { Remove-Item -LiteralPath $runDir -Recurse -Force }
New-Item -ItemType Directory -Path $roaming, $local -Force | Out-Null

$realRoaming = Join-Path $env:APPDATA 'Pia'
foreach ($name in 'settings.json', 'providers.json', 'templates.json') {
    $src = Join-Path $realRoaming $name
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $roaming }
}
$settingsPath = Join-Path $roaming 'settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    (Get-Content -LiteralPath $settingsPath -Raw) -replace '"syncEnabled"\s*:\s*true', '"syncEnabled": false' |
        Set-Content -LiteralPath $settingsPath -Encoding utf8NoBOM
}

$roster = (1..$RosterSize | ForEach-Object { "P$_" }) -join ';'
$env:PIA_DATA_DIR = $roaming
$env:PIA_LOCAL_DATA_DIR = $local
$env:PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE = $audio
$env:PIA_DEBUG_MEETING_ATTENDEE_ROSTER = $roster

Write-Host "Replay '$RunName'"
Write-Host "  audio   : $audio"
Write-Host "  roster  : $RosterSize participants"
Write-Host "  profile : $runDir"

$proc = Start-Process -FilePath $exe -WorkingDirectory $AppDir -PassThru
Write-Host "  pid     : $($proc.Id)"

function Wait-Element([System.Windows.Automation.AutomationElement]$root, [string]$automationId, [int]$timeoutSeconds) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    $deadline = [datetime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 400
    }
    throw "Timed out waiting for element '$automationId'"
}

function Get-MainWindow([int]$processId, [int]$timeoutSeconds) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $deadline = [datetime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        foreach ($w in $windows) {
            # The first-run wizard is a separate window; the shell is the one with the nav list.
            $nav = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavigationItems')
            if ($w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nav)) { return $w }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Timed out waiting for the main window'
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

try {
    $window = Get-MainWindow $proc.Id $JoinTimeoutSeconds
    Write-Host "  window  : $($window.Current.Name)"

    # The composer's overlay toggle carries no AutomationId, only a name.
    $toggle = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Join a meeting and transcribe'))
    if (-not $toggle) { throw 'Meeting-attendee toggle not found in the composer' }
    Invoke-Element $toggle

    $url = Wait-Element $window 'MeetingAttendee_Url' 30
    $urlValue = $url.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    # The URL only has to pass the Teams-host check: DebugNoOpMeetingSession never dials it.
    $urlValue.SetValue("https://teams.microsoft.com/l/meetup-join/replay-$RunName")

    $consent = Wait-Element $window 'MeetingAttendee_Consent' 10
    $toggleState = $consent.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggleState.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggleState.Toggle() }

    Invoke-Element (Wait-Element $window 'MeetingAttendee_Join' 10)
    Write-Host '  joined, waiting for the recording to play out...'

    $logDir = Join-Path $local 'Logs'
    $deadline = [datetime]::UtcNow.AddSeconds(7200)
    $done = $false
    while ([datetime]::UtcNow -lt $deadline) {
        if ($proc.HasExited) { throw "Pia exited unexpectedly (code $($proc.ExitCode))" }
        $log = Get-ChildItem -LiteralPath $logDir -Filter 'pia-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log -and (Select-String -LiteralPath $log.FullName -Pattern 'finished playing' -Quiet)) {
            $done = $true
            break
        }
        Start-Sleep -Seconds 15
    }
    if (-not $done) { throw 'The recording never reached EOF' }

    Write-Host "  playback done, draining the transcription queue for ${DrainSeconds}s"
    Start-Sleep -Seconds $DrainSeconds

    Invoke-Element (Wait-Element $window 'MeetingAttendee_Stop' 30)
    Start-Sleep -Seconds 10
    Write-Host '  stopped'
} finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(20000)) { $proc.Kill() }
    }
    $log = Get-ChildItem -Path (Join-Path $local 'Logs') -Filter 'pia-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) { Write-Host "  log     : $($log.FullName)" }
}
