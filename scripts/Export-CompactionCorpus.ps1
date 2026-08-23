#requires -version 7
<#
.SYNOPSIS
  Exports one chat from Pia's history database to a JSON fixture for the compaction-recall harness.

.DESCRIPTION
  The harness needs transcripts long enough that compaction actually fires. Real ones live in the
  app's own SQLite database; this reads AssistantChatMessages for a single chat and writes
  {id, messages[{ordinal, role, content}]}.

  The extract contains real conversation content, so it is never committed. The script hard-refuses
  any output path inside the repository and defaults under the system temp directory; there is no
  override switch. Only the fixture path, a message count and an approximate token count are printed
  — never message text.

  The database is always opened read-only, which is also what makes it safe to run while Pia is open.

  -List prints candidate chats to the console only: a chat title is user content and never reaches a
  file.

.EXAMPLE
  ./scripts/Export-CompactionCorpus.ps1 -List

.EXAMPLE
  ./scripts/Export-CompactionCorpus.ps1 -ChatId 8f1c0a2e-... -Id chat-toolheavy
#>
[CmdletBinding(DefaultParameterSetName = 'Export')]
param(
    [Parameter(Mandatory, ParameterSetName = 'List')][switch]$List,
    # A chat below this is never long enough for compaction to fire against a realistic budget.
    [Parameter(ParameterSetName = 'List')][int]$MinMessages = 20,
    [Parameter(Mandatory, ParameterSetName = 'Export')][string]$ChatId,
    # Scorecard row label; also the fixture file name.
    [Parameter(ParameterSetName = 'Export')][string]$Id,
    [Parameter(ParameterSetName = 'Export')][string]$OutputPath,
    # Off by default: the compactor only ever sees Content.
    [Parameter(ParameterSetName = 'Export')][switch]$IncludeThinking,
    [Parameter(ParameterSetName = 'Export')][switch]$Force,
    [string]$DatabasePath,
    [string]$Sqlite3Path
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
    throw "$Name not found. Put it on PATH, pass -${Name}Path, or run: winget install SQLite.SQLite"
}

# Mirrors PiaPaths.LocalDataDirectory and SqliteContext.DefaultDbPath(), so a throwaway profile is read
# rather than the developer's.
function Resolve-Database([string]$Explicit) {
    if ($Explicit) {
        $candidate = $Explicit
    } else {
        $root = if ($env:PIA_LOCAL_DATA_DIR) { $env:PIA_LOCAL_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'Pia' }
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

$sqlite3 = Resolve-Tool $Sqlite3Path 'sqlite3'
$database = Resolve-Database $DatabasePath

if ($List) {
    $listSql = @"
.mode json
SELECT c.Id AS id,
       COUNT(m.Id) AS messages,
       SUM(LENGTH(m.Content)) AS chars,
       c.UpdatedAt AS updatedAt,
       c.Title AS title
FROM AssistantChats c
JOIN AssistantChatMessages m ON m.ChatId = c.Id
GROUP BY c.Id
HAVING COUNT(m.Id) >= $MinMessages
ORDER BY chars DESC
LIMIT 25;
"@

    $listed = Invoke-Sqlite $listSql
    if (-not $listed) { throw "No chat has at least $MinMessages messages in $database." }

    Write-Host "$database"
    Write-Host ''
    foreach ($row in @($listed | ConvertFrom-Json)) {
        Write-Host ("  {0}  {1,5} msg  ~{2,8:N0} tok  {3}" -f $row.id, $row.messages, ($row.chars / 4), $row.updatedAt)
        Write-Host ("      {0}" -f $row.title)
    }
    return
}

# Parsed rather than interpolated raw: a GUID cannot carry a quote, which removes the injection risk
# without depending on sqlite3's .parameter support.
$guid = ([guid]::Parse($ChatId)).ToString()
$label = if ($Id) { $Id } else { 'chat-' + $guid.Substring(0, 8) }

$defaultDirectory = if ($env:PIA_COMPACTION_CORPUS_DIR) {
    $env:PIA_COMPACTION_CORPUS_DIR
} else {
    Join-Path ([System.IO.Path]::GetTempPath()) 'pia-compaction-corpus'
}

$target = if ($OutputPath) { $OutputPath } else { Join-Path $defaultDirectory "$label.corpus.json" }
$resolved = [System.IO.Path]::GetFullPath($target)
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

if ($resolved -eq $repoRoot -or
    $resolved.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write ${resolved}: it is inside the repository at $repoRoot. An extracted transcript holds real conversation content and is never committed. Set PIA_COMPACTION_CORPUS_DIR, or pass -OutputPath somewhere outside the repo."
}

if ((Test-Path -LiteralPath $resolved) -and -not $Force) {
    throw "$resolved already exists. Pass -Force to overwrite it."
}

$columns = 'Ordinal AS ordinal, Role AS role, Content AS content'
if ($IncludeThinking) { $columns += ', ThinkingContent AS thinking' }

$exportSql = @"
.mode json
SELECT $columns FROM AssistantChatMessages WHERE ChatId = '$guid' ORDER BY Ordinal;
"@

$exported = Invoke-Sqlite $exportSql
if (-not $exported) { throw "No messages found for chat $guid in $database." }
$rows = @($exported | ConvertFrom-Json)
if ($rows.Count -lt 1) { throw "No messages found for chat $guid in $database." }

# ordinal rides along with role/content on purpose: the harness diffs the removed set by message
# identity rather than by position, and the ordinal is the stable handle for that.
$fixture = [ordered]@{
    id       = $label
    messages = @($rows | ForEach-Object {
        $message = [ordered]@{ ordinal = $_.ordinal; role = $_.role; content = $_.content }
        if ($IncludeThinking) { $message.thinking = $_.thinking }
        $message
    })
}

$directory = Split-Path -Parent $resolved
if ($directory -and -not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Set-Content -LiteralPath $resolved -Value ($fixture | ConvertTo-Json -Depth 5) -Encoding utf8NoBOM

$characters = ($rows | Measure-Object -Property { $_.content.Length } -Sum).Sum

Write-Host "  fixture   : $resolved"
Write-Host ("  messages  : {0}" -f $rows.Count)
Write-Host ("  tokens    : ~{0:N0}" -f ($characters / 4))
Write-Host '  This file holds real conversation content. Keep it out of the repo and off any share.'
