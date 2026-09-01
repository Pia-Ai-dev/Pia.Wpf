#requires -version 7
<#
.SYNOPSIS
  Measures how many topic pages ingest produces from a set of source documents.

.DESCRIPTION
  Builds an isolated Pia profile, seeds its vault with copies of the sources you point at, launches
  the real app against it, waits until every source has been ingested, and reports the topic pages
  produced plus the per-source discovery counts from the log.

  The point is A/B: run it once per build (or once per charter) over the same sources and compare.
  It is what produced the 85 -> 13 numbers in
  docs/vault_topic_proliferation/2026-09-01-topic-proliferation-plan.md, and it is how to re-check
  the open question there — whether the substance bar is too strict on short meeting transcripts.

  Isolation. The live profile and the live vault are only ever READ: providers.json and settings.json
  are copied, and assistantFilesFolder is repointed at a throwaway workdir. Note that PIA_DATA_DIR
  and PIA_LOCAL_DATA_DIR name the Pia directory ITSELF, not a parent containing one — point them at
  a parent and the app silently boots on the real profile and ingests into the real vault.

  Cost. Every run spends real API calls: one discovery call per source plus one synthesis call per
  topic. Six sources cost roughly 20-90 calls depending on the build being measured.

.PARAMETER Label
  Names the run. Profiles live side by side under -WorkRoot\<Label>, so before/after runs coexist.

.PARAMETER SourcesPath
  Folder whose files are copied into the throwaway vault's sources/. Subfolders are ignored.

.PARAMETER Exe
  The Pia.Wpf.exe to measure. Point at a worktree build to measure an older commit.

.PARAMETER ProviderId
  Provider GUID to pin both modes to, from the copied providers.json. Defaults to whatever the live
  profile already resolves, which is usually Pia Cloud and usually not what you want.

.PARAMETER Charter
  Optional charter text seeded as memory/charter.md before the run, to measure its effect.

.EXAMPLE
  ./scripts/Measure-TopicYield.ps1 -Label before -SourcesPath "$HOME\Documents\Pia Assistant\Vault\sources" `
    -Exe .\src\Pia.Wpf\bin\Debug\net10.0-windows10.0.17763.0\Pia.Wpf.exe -ProviderId 819d7d72-...
#>
param(
  [Parameter(Mandatory = $true)][string]$Label,
  [Parameter(Mandatory = $true)][string]$SourcesPath,
  [Parameter(Mandatory = $true)][string]$Exe,
  [string]$ProviderId,
  [string]$Charter,
  [string]$WorkRoot = (Join-Path ([System.IO.Path]::GetTempPath()) 'pia-topic-yield'),
  [int]$MaxMinutes = 40
)

$ErrorActionPreference = 'Stop'

$root    = Join-Path $WorkRoot $Label
$roaming = Join-Path $root 'roaming'
$local   = Join-Path $root 'local'
$workdir = Join-Path $root 'workdir'
$vault   = Join-Path $workdir 'Vault'
$topics  = Join-Path $vault 'memory\topics'
$logDir  = Join-Path $local 'Logs'

if (Test-Path $root) { Remove-Item $root -Recurse -Force }
foreach ($d in @($roaming, $local, (Join-Path $vault 'sources'), $topics)) {
  New-Item -ItemType Directory -Force -Path $d | Out-Null
}

$liveRoaming = Join-Path $env:APPDATA 'Pia'
Copy-Item (Join-Path $liveRoaming 'providers.json') (Join-Path $roaming 'providers.json')

$settings = Get-Content (Join-Path $liveRoaming 'settings.json') -Raw | ConvertFrom-Json
$settings.assistantFilesFolder = $workdir
$settings.autoIngestSources = $true
$settings.assistantFolderLayoutVersion = 1
if ($ProviderId) {
  $settings.modeProviderDefaults = [pscustomobject]@{ Optimize = $ProviderId; Assistant = $ProviderId }
}
# Everything else that would spend the provider stays off: the measurement is of ingest alone.
$settings.chatHistoryEnabled = $false
$settings.assistantSuggestionsEnabled = $false
$settings.chatAutoTitleEnabled = $false
$settings.directTranscriptionEnabled = $false
$settings.autoUpdateEnabled = $false
$settings | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $roaming 'settings.json')

Get-ChildItem $SourcesPath -File | ForEach-Object {
  Copy-Item $_.FullName (Join-Path $vault "sources\$($_.Name)")
}
$expected = @(Get-ChildItem (Join-Path $vault 'sources') -File).Count
if ($expected -eq 0) { throw "No files under $SourcesPath" }

if ($Charter) {
  $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  @(
    '---', 'pia: managed', "id: $([guid]::NewGuid())", 'type: note', 'title: Charter',
    "created: $now", "updated: $now", 'schemaVersion: 1', '---', '', $Charter
  ) | Set-Content (Join-Path $vault 'memory\charter.md')
}

$env:PIA_DATA_DIR       = $roaming
$env:PIA_LOCAL_DATA_DIR = $local

$proc = Start-Process -FilePath $Exe -PassThru
Write-Host "[$Label] pid=$($proc.Id), $expected source(s), charter=$([bool]$Charter)"

function Get-Settled {
  if (-not (Test-Path $logDir)) { return 0 }
  # Failures count as settled: a source that cannot be ingested will never raise the topic count,
  # and waiting for it would just burn the timeout.
  @(Select-String -Path (Join-Path $logDir '*.log') `
      -Pattern 'Auto-ingest completed|Auto-ingest failed to process a source' -ErrorAction SilentlyContinue).Count
}

$deadline = (Get-Date).AddMinutes($MaxMinutes)
$lastSeen = -1
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 10
  $settled = Get-Settled
  if ($settled -ne $lastSeen) {
    $count = @(Get-ChildItem $topics -Filter *.md -ErrorAction SilentlyContinue).Count
    Write-Host "[$Label] settled=$settled/$expected topics=$count"
    $lastSeen = $settled
  }
  if ($settled -ge $expected) { break }
}

Start-Sleep -Seconds 10   # let the last page write and index upsert flush
try { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 5 } catch { }
try { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } } catch { }

$log = Get-Content (Join-Path $logDir '*.log') -Raw
# The source ref can contain spaces, so this is deliberately not \S+.
$perSource = [regex]::Matches($log, 'Ingest (sources/.+?) discovered (\d+) topics')
$pages = Get-ChildItem $topics -Filter *.md | Sort-Object Name

Write-Host ''
Write-Host "=== $Label ==="
Write-Host "topic pages       : $($pages.Count)"
Write-Host "topics discovered : $((($perSource | ForEach-Object { [int]$_.Groups[2].Value }) | Measure-Object -Sum).Sum)"
Write-Host "unparseable titles: $(@(Select-String -Path (Join-Path $topics '*.md') -Pattern '^title:\s*[\{\[]' -ErrorAction SilentlyContinue).Count)"
Write-Host "alias collapses   : $(@(Select-String -Path (Join-Path $logDir '*.log') -Pattern 'Ingest collapsed' -ErrorAction SilentlyContinue).Count)"
Write-Host "cap hits          : $(@(Select-String -Path (Join-Path $logDir '*.log') -Pattern 'keeping the first' -ErrorAction SilentlyContinue).Count)"
Write-Host ''
Write-Host 'per source:'
$perSource | ForEach-Object { Write-Host ("  {0,3}  {1}" -f $_.Groups[2].Value, $_.Groups[1].Value) }
Write-Host ''
Write-Host 'pages:'
$pages | ForEach-Object { Write-Host "  $($_.Name)" }
