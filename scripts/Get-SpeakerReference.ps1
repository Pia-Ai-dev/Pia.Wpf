#requires -version 7
<#
.SYNOPSIS
  Extracts a reference speaker timeline from a Teams cloud recording.

.DESCRIPTION
  Teams burns a per-participant active-speaker highlight into the recording: an indigo pill behind
  the speaking participant's name label. This samples each tile's name-label rect, classifies the
  pill by mean colour, and emits {start, end, speakers[]} intervals — ground truth for scoring
  Pia's diarization without hand-labelling.

  Speakers are identified by tile position only (A, B, C, ...). The tile-to-real-name map is a
  one-time hand-read per recording and belongs in a local-only sidecar, never in the output.

  Three things the output records rather than hides:

  * Simultaneous highlights are kept as overlap ground truth, not collapsed to one speaker.
  * A frame whose non-highlighted tiles are not dark means the tile grid moved (Teams reflows on
    join/leave, and both recordings open in a different layout). Those frames land in
    invalidRanges instead of being silently mislabelled.
  * The indicator attacks late and lingers, so boundaries are approximate by construction. Score
    per-segment attribution against an interval's midpoint, not strict DER.

.EXAMPLE
  ./scripts/Get-SpeakerReference.ps1 -VideoPath 'artifacts/meeting_recording/x.mp4' `
      -LayoutPath scripts/speaker-reference/workshop.layout.json `
      -OutputPath scripts/speaker-reference/workshop.reference.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$VideoPath,
    [Parameter(Mandatory)][string]$LayoutPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [double]$Fps = 4,
    # Gaps shorter than this inside one speaker's run are closed: the pill flickers on repaint.
    [double]$MergeGapSeconds = 0.25,
    # Pill test: blue must lead red/green by this much, at this brightness. Measured margin on both
    # recordings is ~56 against ~4 for an idle label, so the threshold is not delicate.
    [int]$PillBlueDelta = 25,
    [int]$PillMinBlue = 80,
    # Layout test: an idle label rect is near-black. Anything brighter is not a name label.
    [int]$IdleMaxChannel = 80,
    [string]$FfmpegPath,
    [string]$FfprobePath
)

$ErrorActionPreference = 'Stop'

function Resolve-Tool([string]$Explicit, [string]$Name) {
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) { throw "$Name not found at $Explicit" }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }
    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    if (Test-Path -LiteralPath $winget) {
        $found = Get-ChildItem -LiteralPath $winget -Recurse -Filter "$Name.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    throw "$Name not found. Put it on PATH or pass -${Name}Path."
}

$ffmpeg = Resolve-Tool $FfmpegPath 'ffmpeg'
$ffprobe = Resolve-Tool $FfprobePath 'ffprobe'
$video = (Resolve-Path -LiteralPath $VideoPath).Path
$layout = Get-Content -LiteralPath $LayoutPath -Raw | ConvertFrom-Json
$tiles = @($layout.tiles)
if ($tiles.Count -lt 1) { throw "Layout $LayoutPath declares no tiles" }

$duration = [double](& $ffprobe -v error -show_entries format=duration -of csv=p=0 $video)
Write-Host "$($layout.name): $video"
Write-Host ("  {0:N1} s, {1} tiles, sampling at {2} fps" -f $duration, $tiles.Count, $Fps)

# One ffmpeg pass: crop each tile's label rect, area-average it to a single pixel, and stack the
# tiles into one row. Output is 3 bytes per tile per sampled frame.
$chain = [System.Collections.Generic.List[string]]::new()
$split = ($tiles | ForEach-Object -Begin { $i = 0 } -Process { "[t$($script:i)]"; $script:i++ }) -join ''
$chain.Add("[0:v]fps=$Fps,split=$($tiles.Count)$split")
for ($i = 0; $i -lt $tiles.Count; $i++) {
    $t = $tiles[$i]
    $chain.Add("[t$i]crop=$($t.w):$($t.h):$($t.x):$($t.y),scale=1:1:flags=area[o$i]")
}
$stack = ($tiles | ForEach-Object -Begin { $i = 0 } -Process { "[o$($script:i)]"; $script:i++ }) -join ''
$chain.Add("$stack" + "hstack=inputs=$($tiles.Count)[out]")

$raw = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "pia-speaker-ref-$([guid]::NewGuid()).rgb")
try {
    & $ffmpeg -y -v error -i $video -filter_complex ($chain -join ';') -map '[out]' `
        -f rawvideo -pix_fmt rgb24 $raw
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed with exit code $LASTEXITCODE" }
    $bytes = [System.IO.File]::ReadAllBytes($raw)
} finally {
    Remove-Item -LiteralPath $raw -ErrorAction SilentlyContinue
}

$stride = $tiles.Count * 3
$frames = [int]($bytes.Length / $stride)
if ($frames -lt 1) { throw 'ffmpeg produced no frames' }

# Per frame: which tiles carry a pill, and whether the grid still looks like a grid.
$perFrame = [object[]]::new($frames)
for ($f = 0; $f -lt $frames; $f++) {
    $base = $f * $stride
    $lit = [System.Collections.Generic.List[string]]::new()
    $valid = $true
    for ($i = 0; $i -lt $tiles.Count; $i++) {
        $r = $bytes[$base + $i * 3]; $g = $bytes[$base + $i * 3 + 1]; $b = $bytes[$base + $i * 3 + 2]
        if (($b - ($r + $g) / 2) -ge $PillBlueDelta -and $b -ge $PillMinBlue) {
            $lit.Add($tiles[$i].id)
        } elseif ([Math]::Max([Math]::Max($r, $g), $b) -gt $IdleMaxChannel) {
            $valid = $false
        }
    }
    $perFrame[$f] = @{ Valid = $valid; Key = ($lit -join '+') }
}

# Collapse equal consecutive frames into runs, then close sub-MergeGapSeconds gaps inside a run of
# the same speaker set (a repaint drops the pill for a frame or two).
$step = 1.0 / $Fps
$runs = [System.Collections.Generic.List[hashtable]]::new()
for ($f = 0; $f -lt $frames; $f++) {
    $state = if (-not $perFrame[$f].Valid) { '!invalid' } else { $perFrame[$f].Key }
    if ($runs.Count -gt 0 -and $runs[$runs.Count - 1].State -eq $state) {
        $runs[$runs.Count - 1].End = ($f + 1) * $step
    } else {
        $runs.Add(@{ State = $state; Start = $f * $step; End = ($f + 1) * $step })
    }
}
for ($i = $runs.Count - 2; $i -ge 1; $i--) {
    $gap = $runs[$i]
    if ($gap.State -ne '' -or ($gap.End - $gap.Start) -gt $MergeGapSeconds) { continue }
    if ($runs[$i - 1].State -ne $runs[$i + 1].State -or $runs[$i - 1].State -eq '!invalid') { continue }
    $runs[$i - 1].End = $runs[$i + 1].End
    $runs.RemoveRange($i, 2)
}

$intervals = [System.Collections.Generic.List[object]]::new()
$invalid = [System.Collections.Generic.List[object]]::new()
$talk = @{}
$silence = 0.0
foreach ($run in $runs) {
    $span = $run.End - $run.Start
    if ($run.State -eq '!invalid') {
        $invalid.Add([ordered]@{ start = [Math]::Round($run.Start, 2); end = [Math]::Round($run.End, 2) })
        continue
    }
    if ($run.State -eq '') { $silence += $span; continue }
    $speakers = $run.State -split '\+'
    foreach ($s in $speakers) { $talk[$s] = [double]$talk[$s] + $span }
    $intervals.Add([ordered]@{
        start    = [Math]::Round($run.Start, 2)
        end      = [Math]::Round($run.End, 2)
        speakers = @($speakers)
    })
}

$overlap = ($intervals | Where-Object { $_.speakers.Count -gt 1 } |
    Measure-Object -Property { $_.end - $_.start } -Sum).Sum
$invalidTotal = ($invalid | Measure-Object -Property { $_.end - $_.start } -Sum).Sum

$reference = [ordered]@{
    layout           = $layout.name
    sampleFps        = $Fps
    durationSeconds  = [Math]::Round($duration, 2)
    sampledFrames    = $frames
    speakers         = @($tiles | Where-Object { $talk.ContainsKey($_.id) } | ForEach-Object { $_.id })
    talkSeconds      = [ordered]@{}
    silenceSeconds   = [Math]::Round($silence, 2)
    overlapSeconds   = [Math]::Round([double]$overlap, 2)
    invalidSeconds   = [Math]::Round([double]$invalidTotal, 2)
    invalidRanges    = @($invalid)
    intervals        = @($intervals)
}
foreach ($t in $tiles) {
    if ($talk.ContainsKey($t.id)) { $reference.talkSeconds[$t.id] = [Math]::Round($talk[$t.id], 2) }
}

$json = $reference | ConvertTo-Json -Depth 6
$dir = Split-Path -Parent $OutputPath
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Set-Content -LiteralPath $OutputPath -Value $json -Encoding utf8NoBOM

Write-Host ''
Write-Host "  talkers          : $($reference.speakers.Count) of $($tiles.Count) on the roster"
foreach ($k in $reference.talkSeconds.Keys) {
    Write-Host ("    {0}  {1,8:N1} s" -f $k, $reference.talkSeconds[$k])
}
Write-Host ("  no highlight     : {0,8:N1} s" -f $reference.silenceSeconds)
Write-Host ("  overlap          : {0,8:N1} s" -f $reference.overlapSeconds)
Write-Host ("  unusable layout  : {0,8:N1} s in {1} range(s)" -f $reference.invalidSeconds, $invalid.Count)
Write-Host ("  intervals        : {0}" -f $intervals.Count)
Write-Host "  written to $OutputPath"
