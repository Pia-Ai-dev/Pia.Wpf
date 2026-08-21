#requires -version 7
<#
.SYNOPSIS
  Scores one replayed meeting against its reference speaker timeline.

.DESCRIPTION
  Joins a Pia DEBUG log from a PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE replay against the reference
  produced by Get-SpeakerReference.ps1, and prints the numbers the plan asks for: how many distinct
  labels the run minted, how many survived, and how often the label on a segment matched the person
  the recording says was speaking.

  Alignment. Replay is paced by Task.Delay, so wall-clock elapsed is not stream time. The two
  anchors the log already carries ("playing" / "finished playing") plus the reference's known
  duration give the rate; a small offset sweep over the speech masks absorbs the pipeline latency.
  The residual is printed — an alignment that did not converge invalidates the attribution number,
  so it must not be silent.

  Attribution, not DER. The burned-in indicator attacks late and lingers, so interval boundaries
  are the indicator's error, not Pia's. Each segment is scored at its midpoint. Segments whose
  midpoint lands in overlapped speech, in no speech at all, or in a range where the tile grid had
  moved are reported in their own buckets so they can neither flatter nor damn the result.

.EXAMPLE
  ./scripts/Measure-SpeakerAttribution.ps1 -LogPath $env:LOCALAPPDATA\Pia\Logs\pia-2026-08-21.log `
      -ReferencePath scripts/speaker-reference/workshop.reference.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LogPath,
    [Parameter(Mandatory)][string]$ReferencePath,
    # Which replay in the log to score. -1 is the last.
    [int]$RunIndex = -1,
    [double]$OffsetSearchSeconds = 4.0,
    [double]$OffsetStepSeconds = 0.05,
    [string]$NameMapPath
)

$ErrorActionPreference = 'Stop'

$reference = Get-Content -LiteralPath $ReferencePath -Raw | ConvertFrom-Json
$names = if ($NameMapPath) { (Get-Content -LiteralPath $NameMapPath -Raw | ConvertFrom-Json).names } else { $null }

# ---- Parse the log ------------------------------------------------------------------------------

$lines = [System.IO.File]::ReadAllLines($LogPath)
$starts = @()
for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match 'Debug file audio source playing ') { $starts += $i }
}
if ($starts.Count -eq 0) { throw "No replay found in $LogPath (no 'Debug file audio source playing')" }
$from = if ($RunIndex -lt 0) { $starts[$starts.Count + $RunIndex] } else { $starts[$RunIndex] }
$to = $lines.Length - 1
foreach ($s in $starts) { if ($s -gt $from) { $to = $s - 1; break } }

function Get-Stamp([string]$line) {
    $tab = $line.IndexOf("`t")
    if ($tab -lt 1) { return $null }
    [datetimeoffset]::Parse($line.Substring(0, $tab), [cultureinfo]::InvariantCulture)
}

$t0 = Get-Stamp $lines[$from]
$tEnd = $null
$queued = [System.Collections.Generic.List[object]]::new()   # emitted VAD segments, in order
$engineStarts = [System.Collections.Generic.List[object]]::new()
$identified = [System.Collections.Generic.List[object]]::new()
$outcomes = [System.Collections.Generic.List[string]]::new() # 'text' | 'empty', in engine order
$passes = [System.Collections.Generic.List[object]]::new()
$pendingBatches = [System.Collections.Generic.Queue[object]]::new()
$labelSets = [System.Collections.Generic.List[string[]]]::new()
$appliedTotal = 0
$unjournaledTotal = 0
$corrections = [System.Collections.Generic.List[object]]::new() # (segId, label, applied)

for ($i = $from; $i -le $to; $i++) {
    $line = $lines[$i]
    if ($line -match 'Debug file audio source finished playing') { $tEnd = Get-Stamp $line; continue }
    if ($line -match 'Segment queued for \w+: (\d+) samples') {
        $queued.Add(@{ Stamp = Get-Stamp $line; Samples = [int]$Matches[1] }); continue
    }
    if ($line -match 'Engine start: \w+ (\d+) samples') {
        $engineStarts.Add(@{ Stamp = Get-Stamp $line; Samples = [int]$Matches[1] }); continue
    }
    if ($line -match 'Segment identified: seg=(\d+) label=(.*) samples=(\d+)$') {
        $identified.Add(@{ SegId = [long]$Matches[1]; Label = $Matches[2]; Samples = [int]$Matches[3] }); continue
    }
    if ($line -match 'Engine done: \w+ \d+ms') { $outcomes.Add('text'); continue }
    if ($line -match 'Engine produced empty result') { $outcomes.Add('empty'); continue }
    if ($line -match 'Adaptive pass: (\d+)/(\d+) segments .* (\d+) clusters cut=([\d.]+) expected=(\d+) changed=(\d+)') {
        $passes.Add(@{
            Eligible = [int]$Matches[1]; Total = [int]$Matches[2]; Clusters = [int]$Matches[3]
            Cut = [double]$Matches[4]; Expected = [int]$Matches[5]; Changed = [int]$Matches[6]
        }); continue
    }
    if ($line -match 'Adaptive pass labels: \[(.*)\]$') {
        $labelSets.Add(@(($Matches[1] -split ', ') | Where-Object { $_ })); continue
    }
    if ($line -match 'Adaptive pass reassigned: \[(.*)\]$') {
        $batch = @()
        foreach ($pair in ($Matches[1] -split ', ')) {
            $eq = $pair.IndexOf('=')
            if ($eq -lt 1) { continue }
            # A bare "123=" is the pass clearing a label it can no longer stand behind, not an empty one.
            $label = $pair.Substring($eq + 1)
            $batch += @{ SegId = [long]$pair.Substring(0, $eq); Label = if ($label) { $label } else { $null } }
        }
        $pendingBatches.Enqueue($batch); continue
    }
    if ($line -match 'Reassignments: applied=(\d+) unjournaled=\[(.*)\]$') {
        $appliedTotal += [int]$Matches[1]
        $lost = @(($Matches[2] -split ',') | Where-Object { $_ } | ForEach-Object { [long]$_ })
        $unjournaledTotal += $lost.Count
        if ($pendingBatches.Count -gt 0) {
            foreach ($c in $pendingBatches.Dequeue()) {
                $corrections.Add(@{ SegId = $c.SegId; Label = $c.Label; Applied = ($lost -notcontains $c.SegId) })
            }
        }
        continue
    }
}

if (-not $t0) { throw 'Could not read the replay start timestamp' }
if ($queued.Count -ne $engineStarts.Count) {
    Write-Warning "queued=$($queued.Count) engineStarts=$($engineStarts.Count) — the run was cut short mid-queue"
}

# ---- Stitch each emitted segment to its identity and outcome ------------------------------------
# The engine's segment loop is serial, so VAD-close order == engine order == identify order.

$segments = [System.Collections.Generic.List[object]]::new()
$idIdx = 0
for ($i = 0; $i -lt $engineStarts.Count; $i++) {
    $samples = $engineStarts[$i].Samples
    $label = $null; $segId = $null
    if ($idIdx -lt $identified.Count -and $identified[$idIdx].Samples -eq $samples) {
        $label = $identified[$idIdx].Label; $segId = $identified[$idIdx].SegId; $idIdx++
    }
    $stamp = if ($i -lt $queued.Count) { $queued[$i].Stamp } else { $engineStarts[$i].Stamp }
    $segments.Add(@{
        Index    = $i
        SegId    = $segId
        Label    = $label
        Final    = $label
        Samples  = $samples
        Duration = $samples / 16000.0
        Closed   = ($stamp - $t0).TotalSeconds
        Outcome  = if ($i -lt $outcomes.Count) { $outcomes[$i] } else { 'pending' }
    })
}

$bySegId = @{}
foreach ($s in $segments) { if ($null -ne $s.SegId) { $bySegId[$s.SegId] = $s } }
foreach ($c in $corrections) {
    if (-not $c.Applied) { continue }
    if ($bySegId.ContainsKey($c.SegId)) { $bySegId[$c.SegId].Final = $c.Label }
}

# ---- Align replay wall-clock to recording time --------------------------------------------------

$refDuration = [double]$reference.durationSeconds
$elapsed = if ($tEnd) { ($tEnd - $t0).TotalSeconds } else { $null }
$rate = if ($elapsed -and $elapsed -gt 0) { $refDuration / $elapsed } else { 1.0 }

function Get-RefMask([object]$reference, [double]$step) {
    $n = [int][Math]::Ceiling([double]$reference.durationSeconds / $step) + 1
    $mask = [bool[]]::new($n)
    foreach ($iv in $reference.intervals) {
        $a = [int][Math]::Floor([double]$iv.start / $step)
        $b = [int][Math]::Ceiling([double]$iv.end / $step)
        for ($k = $a; $k -lt [Math]::Min($b, $n); $k++) { $mask[$k] = $true }
    }
    $mask
}

$step = $OffsetStepSeconds
$refMask = Get-RefMask $reference $step
$best = @{ Offset = 0.0; Score = -1 }
$sweep = [int]($OffsetSearchSeconds / $step)
for ($o = -$sweep; $o -le $sweep; $o++) {
    $offset = $o * $step
    $score = 0
    foreach ($s in $segments) {
        $end = $s.Closed * $rate + $offset
        $start = $end - $s.Duration
        $a = [int][Math]::Floor($start / $step); $b = [int][Math]::Ceiling($end / $step)
        for ($k = [Math]::Max($a, 0); $k -lt [Math]::Min($b, $refMask.Length); $k++) {
            if ($refMask[$k]) { $score++ } else { $score-- }
        }
    }
    if ($score -gt $best.Score) { $best = @{ Offset = $offset; Score = $score } }
}
$offset = $best.Offset
$speechSamples = 0
foreach ($s in $segments) { $speechSamples += [int][Math]::Ceiling($s.Duration / $step) }
$agreement = if ($speechSamples -gt 0) { ($best.Score + $speechSamples) / (2.0 * $speechSamples) } else { 0 }

# ---- Score --------------------------------------------------------------------------------------

function Get-SpeakersAt([object]$reference, [double]$t) {
    foreach ($iv in $reference.intervals) {
        if ($t -ge [double]$iv.start -and $t -lt [double]$iv.end) { return @($iv.speakers) }
    }
    @()
}
function Test-Invalid([object]$reference, [double]$t) {
    foreach ($r in $reference.invalidRanges) {
        if ($t -ge [double]$r.start -and $t -lt [double]$r.end) { return $true }
    }
    $false
}

$confusion = @{}
$scored = 0; $scoredSeconds = 0.0
$ambiguous = 0; $ambiguousSeconds = 0.0
$noRef = 0; $noRefSeconds = 0.0
$unusable = 0; $unusableSeconds = 0.0
$unlabelled = 0
$everLabels = [System.Collections.Generic.HashSet[string]]::new()
$finalLabels = [System.Collections.Generic.HashSet[string]]::new()

foreach ($s in $segments) {
    if ($s.Label) { [void]$everLabels.Add($s.Label) }
    if ($s.Final) { [void]$everLabels.Add($s.Final) }
    if ($s.Outcome -ne 'text') { continue }              # no utterance → no bubble to score
    if (-not $s.Final) { $unlabelled++; continue }
    [void]$finalLabels.Add($s.Final)

    $mid = $s.Closed * $rate + $offset - $s.Duration / 2
    if (Test-Invalid $reference $mid) { $unusable++; $unusableSeconds += $s.Duration; continue }
    $who = Get-SpeakersAt $reference $mid
    if ($who.Count -eq 0) { $noRef++; $noRefSeconds += $s.Duration; continue }
    if ($who.Count -gt 1) { $ambiguous++; $ambiguousSeconds += $s.Duration; continue }

    $key = "$($s.Final)|$($who[0])"
    if (-not $confusion.ContainsKey($key)) { $confusion[$key] = @{ Count = 0; Seconds = 0.0 } }
    $confusion[$key].Count++
    $confusion[$key].Seconds += $s.Duration
    $scored++; $scoredSeconds += $s.Duration
}
foreach ($set in $labelSets) { foreach ($l in $set) { [void]$everLabels.Add($l) } }

# Greedy one-to-one label→speaker assignment, largest cell first: the best case for the run, so a
# low number cannot be an artefact of a bad pairing.
$cells = $confusion.GetEnumerator() | ForEach-Object {
    $parts = $_.Key -split '\|'
    [pscustomobject]@{ Label = $parts[0]; Speaker = $parts[1]; Count = $_.Value.Count; Seconds = $_.Value.Seconds }
} | Sort-Object -Property Seconds -Descending
$takenLabel = @{}; $takenSpeaker = @{}; $mapping = @{}
$matched = 0; $matchedSeconds = 0.0
foreach ($c in $cells) {
    if ($takenLabel.ContainsKey($c.Label) -or $takenSpeaker.ContainsKey($c.Speaker)) { continue }
    $takenLabel[$c.Label] = $true; $takenSpeaker[$c.Speaker] = $true
    $mapping[$c.Label] = $c.Speaker
    $matched += $c.Count; $matchedSeconds += $c.Seconds
}

# ---- Report -------------------------------------------------------------------------------------

$lastSet = if ($labelSets.Count -gt 0) { $labelSets[$labelSets.Count - 1] } else { @() }
$expected = @($passes | ForEach-Object { $_.Expected } | Sort-Object -Unique)

Write-Host ''
Write-Host "Replay  : $($reference.layout)  ($LogPath, run $(if ($RunIndex -lt 0) { 'last' } else { $RunIndex }))"
Write-Host ("Pacing  : {0:N1} s of audio in {1} s wall clock → rate {2:N3}x{3}" -f `
    $refDuration, $(if ($elapsed) { [Math]::Round($elapsed, 1) } else { 'n/a' }), $rate,
    $(if ($tEnd) { '' } else { '  (no EOF marker — rate assumed 1.0)' }))
Write-Host ("Align   : offset {0:N2} s, speech-mask agreement {1:P1}" -f $offset, $agreement)
Write-Host ''
Write-Host "Segments: $($segments.Count) emitted, $(($segments | Where-Object { $_.Outcome -eq 'text' }).Count) transcribed, $(($segments | Where-Object { $null -eq $_.SegId }).Count) below the diarization floor"
Write-Host "Passes  : $($passes.Count), expected=$($expected -join '/'), clusters $(($passes | ForEach-Object { $_.Clusters }) -join ',')"
Write-Host ''
Write-Host "LABEL COUNT"
Write-Host "  distinct labels ever registered : $($everLabels.Count)"
Write-Host "  distinct labels in the final pass: $($lastSet.Count)   [$($lastSet -join ', ')]"
Write-Host "  distinct labels in the transcript: $($finalLabels.Count)   [$(($finalLabels | Sort-Object) -join ', ')]"
Write-Host "  true talkers in the recording   : $($reference.speakers.Count)"
Write-Host ''
Write-Host "RETRO-CORRECTIONS"
Write-Host "  emitted            : $($corrections.Count)"
Write-Host "  reached a bubble   : $(($corrections | Where-Object { $_.Applied }).Count)"
Write-Host "  lost (not journaled yet): $(($corrections | Where-Object { -not $_.Applied }).Count)"
Write-Host ''
Write-Host "ATTRIBUTION  (segment midpoint vs the recording's active-speaker indicator)"
if ($scored -gt 0) {
    Write-Host ("  scored segments    : {0}  ({1:N1} s)" -f $scored, $scoredSeconds)
    Write-Host ("  correct            : {0}  = {1:P1} by segment, {2:P1} by duration" -f `
        $matched, ($matched / $scored), ($matchedSeconds / $scoredSeconds))
} else {
    Write-Host '  scored segments    : 0 — nothing to score'
}
Write-Host ("  overlapped speech  : {0}  ({1:N1} s) — excluded, ambiguous reference" -f $ambiguous, $ambiguousSeconds)
Write-Host ("  no speaker lit     : {0}  ({1:N1} s) — excluded, indicator off" -f $noRef, $noRefSeconds)
Write-Host ("  unusable layout    : {0}  ({1:N1} s) — excluded, tile grid had moved" -f $unusable, $unusableSeconds)
if ($unlabelled -gt 0) { Write-Host "  no label at all    : $unlabelled" }

if ($cells) {
    Write-Host ''
    Write-Host 'CONFUSION  (label → reference speaker, seconds)'
    $speakers = @($reference.speakers)
    $header = '  {0,-12}' -f 'label'
    foreach ($sp in $speakers) {
        $tag = if ($names -and $names.$sp) { "$sp" } else { $sp }
        $header += ('{0,9}' -f $tag)
    }
    Write-Host $header
    foreach ($label in ($cells | ForEach-Object { $_.Label } | Sort-Object -Unique)) {
        $row = '  {0,-12}' -f $label
        foreach ($sp in $speakers) {
            $cell = $cells | Where-Object { $_.Label -eq $label -and $_.Speaker -eq $sp }
            $row += ('{0,9}' -f $(if ($cell) { [Math]::Round($cell.Seconds, 1) } else { '.' }))
        }
        if ($mapping[$label]) { $row += "   → $($mapping[$label])" }
        Write-Host $row
    }
    if ($names) {
        Write-Host ''
        foreach ($sp in $speakers) { if ($names.$sp) { Write-Host "  $sp = $($names.$sp)" } }
    }
}
Write-Host ''
