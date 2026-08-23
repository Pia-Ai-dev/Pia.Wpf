#requires -version 7
<#
.SYNOPSIS
  Counts how many of the artifact declarations in Pia's history are file-shaped. Read-only, no app launch.

.DESCRIPTION
  When the agent plans a step it may declare an expected artifact, and the verifier's probe splits every
  declaration two ways before it touches the disk: file-shaped (at least one token looks like a file name,
  so a path is probed) or not a file reference (prose, a number, a bare extension). That split is a
  property of the declaration string alone, so it is recoverable from the persisted AgentSteps rows long
  after the run — which is what this script does, over the machine's whole history rather than the few days
  a log file covers.

  Only that half is recoverable. Three things bound what the numbers mean:

  - A replan DELETES every step row that is not Done or Skipped: keeping the done steps feeds a
    replace-steps write that removes the run's rows and re-inserts only the survivors. Declarations that
    were replanned away are gone, so the sample is biased toward steps that survived to the end.
  - found versus NOT FOUND is NOT recoverable this way. The filesystem has moved on since those runs, and
    a per-run workspace is torn down when the run settles. Only live probe lines answer that half.
  - Deleting a chat cascades AssistantChats -> AgentRuns -> AgentSteps, and foreign keys are enforced by
    default, so "nothing ever deletes a run row" holds for explicit deletes only. A deleted chat takes its
    declarations with it.

  A declaration is user content. Nothing here prints or writes one, on any switch: stdout is counts, and
  -OutputPath writes counts. -OutputPath also hard-refuses any path inside the repository, with no override
  and no carve-out for the gitignored folders, so a number reaches a doc by being retyped.

  The classifier is this script's own copy of the probe's rule, in another language, and it is replayed
  against the synthetic case table next to this script before every measurement. On any disagreement it
  prints the offending synthetic cases and throws rather than printing a ratio that has drifted.

  The database is always opened read-only, which is what makes this safe to run while Pia is open.

.EXAMPLE
  ./scripts/Measure-ArtifactDeclarations.ps1

.EXAMPLE
  ./scripts/Measure-ArtifactDeclarations.ps1 -SinceDays 30 -OutputPath ~/artifact-counts.json

.EXAMPLE
  ./scripts/Measure-ArtifactDeclarations.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'Measure')]
param(
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest,
    # 0 = all history.
    [Parameter(ParameterSetName = 'Measure')][ValidateRange(0, 36500)][int]$SinceDays = 0,
    [Parameter(ParameterSetName = 'Measure')][string]$OutputPath,
    [Parameter(ParameterSetName = 'Measure')][switch]$Force,
    [Parameter(ParameterSetName = 'Measure')][string]$DatabasePath,
    [Parameter(ParameterSetName = 'Measure')][string]$Sqlite3Path,
    [string]$CasesPath
)

$ErrorActionPreference = 'Stop'

$MaxDeclarationChars = 200
$TokenSeparators = [char[]]@(' ', "`t", "`r", "`n", ',', ';', '"', "'", '`', '(', ')', '[', ']', '{', '}', '<', '>', '|', '*', '?', '=')
$TrailingTrim = [char[]]@('.', ',', ';', ':', '!', '?', '"', "'", '`', '*', '-')
$ExtensionShape = '^\.\p{L}[\p{L}\p{Nd}]{1,4}$'
$StatusNames = @{ 0 = 'Pending'; 1 = 'Running'; 2 = 'Done'; 3 = 'Failed'; 4 = 'Skipped' }
$StatusOrder = @(2, 4, 0, 1, 3)

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
    throw "$Name not found. Put it on PATH, pass -${Name}Path, or run: winget install SQLite.SQLite"
}

# Mirrors PiaPaths.LocalDataDirectory and SqliteContext.DefaultDbPath(), so a throwaway profile is read
# rather than the developer's.
function Resolve-Database([string]$Explicit) {
    if ($Explicit) {
        $candidate = $Explicit
    } else {
        $root = if ($env:PIA_LOCAL_DATA_DIR) {
            $env:PIA_LOCAL_DATA_DIR
        } elseif ($env:LOCALAPPDATA) {
            Join-Path $env:LOCALAPPDATA 'Pia'
        } else {
            throw "This machine has no LOCALAPPDATA, so there is no default profile to read. Pass -DatabasePath, or set PIA_LOCAL_DATA_DIR — the corpus worth measuring lives on the Windows box."
        }
        $candidate = Join-Path $root 'history.db'
    }
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "History database not found at $candidate. Pass -DatabasePath, or set PIA_LOCAL_DATA_DIR to the profile you want to read."
    }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Invoke-Sqlite([string]$Script) {
    $out = $Script | & $sqlite3 -readonly $database
    if ($LASTEXITCODE -ne 0) { throw "sqlite3 failed with exit code $LASTEXITCODE" }
    return ($out -join [Environment]::NewLine)
}

# Separators and the extension shape are hand-rolled rather than taken from [System.IO.Path], whose
# separator set follows the host OS: the same declaration must classify the same way wherever this runs.
function Get-TokenExtension([string]$Token) {
    for ($i = $Token.Length - 1; $i -ge 0; $i--) {
        $ch = $Token[$i]
        if ($ch -eq '.') {
            if ($i -ne $Token.Length - 1) { return $Token.Substring($i) }
            return ''
        }
        if ($ch -eq '/' -or $ch -eq '\') { break }
    }
    return ''
}

function Get-TokenStem([string]$Token) {
    $start = 0
    for ($i = $Token.Length - 1; $i -ge 0; $i--) {
        if ($Token[$i] -eq '/' -or $Token[$i] -eq '\') {
            $start = $i + 1
            break
        }
    }
    $name = $Token.Substring($start)
    $extension = Get-TokenExtension $name
    if ($extension.Length -gt 0) { return $name.Substring(0, $name.Length - $extension.Length) }
    return $name
}

function Split-DeclarationToken([string]$Declaration) {
    $tokens = [System.Collections.Generic.List[string]]::new()
    $current = ''
    foreach ($ch in $Declaration.ToCharArray()) {
        if ($TokenSeparators -contains $ch) {
            if ($current.Length -gt 0) {
                $tokens.Add($current)
                $current = ''
            }
        } else {
            $current += $ch
        }
    }
    if ($current.Length -gt 0) { $tokens.Add($current) }
    return $tokens
}

function Test-FileShaped([string]$Declaration) {
    $flat = $Declaration.Trim().Replace("`r", ' ').Replace("`n", ' ').Replace("`t", ' ')
    if ($flat.Length -gt $MaxDeclarationChars) {
        $flat = $flat.Substring(0, $MaxDeclarationChars) + [char]0x2026
    }
    foreach ($raw in @(Split-DeclarationToken $flat)) {
        $token = $raw.Trim().TrimEnd($TrailingTrim)
        if ($token.Length -eq 0) { continue }
        if ((Get-TokenExtension $token) -notmatch $ExtensionShape) { continue }
        if ((Get-TokenStem $token).Length -eq 0) { continue }
        return $true
    }
    return $false
}

function Get-ParityCase([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Parity case table not found at $Path. Pass -CasesPath, or restore artifact-declaration-cases.json next to this script."
    }
    $table = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    $cases = @($table.cases)
    if ($cases.Count -eq 0) { throw "Parity case table at $Path holds no cases." }
    return $cases
}

function Confirm-ClassifierParity($Cases) {
    $mismatches = [System.Collections.Generic.List[string]]::new()
    foreach ($case in $Cases) {
        $expected = [bool]$case.fileShaped
        $actual = Test-FileShaped $case.declaration
        if ($actual -ne $expected) {
            $mismatches.Add("$($case.declaration) → expected $expected, got $actual")
        }
    }
    if ($mismatches.Count -eq 0) { return }
    foreach ($line in $mismatches) { Write-Host $line }
    throw "The classifier disagrees with $($mismatches.Count) of $($Cases.Count) parity case(s), so it no longer mirrors the app's probe. No ratio was printed."
}

function Get-StatusName([int]$Status) {
    if ($StatusNames.ContainsKey($Status)) { return $StatusNames[$Status] }
    return "Status $Status"
}

function Format-Share([int]$Numerator, [int]$Denominator) {
    if ($Denominator -le 0) { return 'n/a' }
    return ('{0:P1}' -f ($Numerator / $Denominator))
}

$casesFile = if ($CasesPath) { $CasesPath } else { Join-Path $PSScriptRoot 'artifact-declaration-cases.json' }
$cases = Get-ParityCase $casesFile
Confirm-ClassifierParity $cases

if ($SelfTest) {
    Write-Host ("{0}/{1} parity cases agree" -f $cases.Count, $cases.Count)
    return
}

$resolvedOutput = $null
if ($OutputPath) {
    $requested = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $resolvedOutput = [System.IO.Path]::GetFullPath($requested)
    $repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    if ($resolvedOutput -eq $repoRoot -or
        $resolvedOutput.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write $resolvedOutput: it is inside the repository at $repoRoot. There is no override — a measured number reaches a doc by being retyped. Pass -OutputPath somewhere outside the repo."
    }
    if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
        throw "$resolvedOutput already exists. Pass -Force to overwrite it."
    }
}

$sqlite3 = Resolve-Tool $Sqlite3Path 'sqlite3'
$database = Resolve-Database $DatabasePath

# Timestamps are persisted round-trip formatted, so comparing them as text compares them chronologically.
$cutoff = if ($SinceDays -gt 0) { (Get-Date).ToUniversalTime().AddDays(-$SinceDays).ToString('o') } else { $null }
$shapeWindow = if ($cutoff) { "WHERE CreatedAt >= '$cutoff'" } else { '' }
$rowsWindow = if ($cutoff) { " AND CreatedAt >= '$cutoff'" } else { '' }

$shapeSql = @"
.mode json
SELECT COUNT(*) AS steps, COUNT(DISTINCT RunId) AS runs, MIN(CreatedAt) AS firstStep, MAX(CreatedAt) AS lastStep
FROM AgentSteps $shapeWindow;
"@

$shape = @(Invoke-Sqlite $shapeSql | ConvertFrom-Json)[0]
$stepRows = [int]$shape.steps
$runs = [int]$shape.runs
if ($stepRows -eq 0) {
    $scope = if ($cutoff) { " created on or after $cutoff" } else { '' }
    throw "No AgentSteps rows$scope in $database. Either this is the wrong database or the window is too narrow."
}

$rowsSql = @"
.mode json
SELECT ExpectedArtifact AS declaration, Status AS status
FROM AgentSteps
WHERE ExpectedArtifact IS NOT NULL AND ExpectedArtifact <> ''$rowsWindow
ORDER BY CreatedAt;
"@

$rowsJson = Invoke-Sqlite $rowsSql
$rows = if ([string]::IsNullOrWhiteSpace($rowsJson)) { @() } else { @($rowsJson | ConvertFrom-Json) }

$declarations = 0
$blankAfterTrim = 0
$fileShaped = 0
$notFileShaped = 0
$distinctAll = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$distinctShaped = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$distinctUnshaped = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$byStatus = @{}

foreach ($row in $rows) {
    # SQLite's TRIM leaves tabs and newlines, so the blank filter has to match the app's here.
    if ([string]::IsNullOrWhiteSpace($row.declaration)) {
        $blankAfterTrim++
        continue
    }
    $declarations++
    $shaped = Test-FileShaped $row.declaration
    [void]$distinctAll.Add($row.declaration)
    if ($shaped) {
        $fileShaped++
        [void]$distinctShaped.Add($row.declaration)
    } else {
        $notFileShaped++
        [void]$distinctUnshaped.Add($row.declaration)
    }

    $status = [int]$row.status
    if (-not $byStatus.ContainsKey($status)) { $byStatus[$status] = @{ declarations = 0; fileShaped = 0 } }
    $entry = $byStatus[$status]
    $entry.declarations++
    if ($shaped) { $entry.fileShaped++ }
}

$orderedStatuses = @($StatusOrder | Where-Object { $byStatus.ContainsKey($_) }) +
    @($byStatus.Keys | Where-Object { $StatusOrder -notcontains $_ } | Sort-Object)

$windowLabel = if ($cutoff) { "last $SinceDays day(s) (steps created >= $cutoff)" } else { 'all history' }

Write-Host "Database    : $database"
Write-Host "Window      : $windowLabel"
Write-Host ("Classifier  : {0}/{1} parity cases agree" -f $cases.Count, $cases.Count)
Write-Host ("Corpus      : {0} step row(s) in {1} run(s), {2} .. {3}" -f $stepRows, $runs, $shape.firstStep, $shape.lastStep)
Write-Host ''
Write-Host 'DECLARATIONS'
Write-Host ("  {0,-34}: {1}  = {2} of step rows" -f 'with a non-blank expectedArtifact', $declarations, (Format-Share $declarations $stepRows))
Write-Host ("  {0,-34}: {1}" -f 'blank after trimming (not probed)', $blankAfterTrim)
Write-Host ("  {0,-34}: {1}" -f 'distinct declaration strings', $distinctAll.Count)
Write-Host ''
Write-Host "FILE-SHAPEDNESS  (per declaration, the split the verifier's probe reports)"
Write-Host ("  {0,-34}: {1}  = {2}   (distinct: {3} = {4})" -f 'file-shaped', $fileShaped,
    (Format-Share $fileShaped $declarations), $distinctShaped.Count, (Format-Share $distinctShaped.Count $distinctAll.Count))
Write-Host ("  {0,-34}: {1}  = {2}   (distinct: {3} = {4})" -f 'not a file reference', $notFileShaped,
    (Format-Share $notFileShaped $declarations), $distinctUnshaped.Count, (Format-Share $distinctUnshaped.Count $distinctAll.Count))
Write-Host ''
Write-Host 'BY STEP STATUS  (a replan deletes every row that is not Done or Skipped)'
foreach ($status in $orderedStatuses) {
    Write-Host ("  {0,-10}: {1} declaration(s), {2} file-shaped" -f (Get-StatusName $status),
        $byStatus[$status].declarations, (Format-Share $byStatus[$status].fileShaped $byStatus[$status].declarations))
}
Write-Host ''
Write-Host 'Counts only — no declaration string was printed or written. This is not the found / NOT FOUND split.'

if ($resolvedOutput) {
    $payload = [ordered]@{
        database                  = $database
        measuredAtUtc             = (Get-Date).ToUniversalTime().ToString('o')
        windowDays                = $SinceDays
        cutoffUtc                 = $cutoff
        stepRows                  = $stepRows
        runs                      = $runs
        firstStepUtc              = $shape.firstStep
        lastStepUtc               = $shape.lastStep
        declarations              = $declarations
        blankAfterTrim            = $blankAfterTrim
        distinctDeclarations      = $distinctAll.Count
        fileShaped                = $fileShaped
        notAFileReference         = $notFileShaped
        distinctFileShaped        = $distinctShaped.Count
        distinctNotAFileReference = $distinctUnshaped.Count
        byStatus                  = @($orderedStatuses | ForEach-Object {
            [ordered]@{
                status       = Get-StatusName $_
                declarations = $byStatus[$_].declarations
                fileShaped   = $byStatus[$_].fileShaped
            }
        })
        parityCases               = $cases.Count
    }

    $directory = Split-Path -Parent $resolvedOutput
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-Content -LiteralPath $resolvedOutput -Value ($payload | ConvertTo-Json -Depth 5) -Encoding utf8NoBOM
    Write-Host "Counts      : $resolvedOutput"
}
