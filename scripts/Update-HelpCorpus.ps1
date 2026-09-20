#requires -version 7
<#
.SYNOPSIS
  Refreshes the help corpus that the assistant's pia_help tool searches.

.DESCRIPTION
  Runs the Pia.Docs KB builder in the sibling docs repo, takes the English wpf/** pages it emits,
  and writes them into this repo as one gzipped JSON resource embedded in Pia.Wpf.

  The docs builder already flattens Starlight MDX components, strips front matter and rewrites
  every internal link to an absolute docs.pia-ai.de URL, so nothing here parses MDX.

  CI cannot run this: the docs repo is a separate checkout that is not present on the build agent.
  Refresh the corpus by hand before a release, and use -Check to see whether it has drifted.

.PARAMETER DocsRepo
  The Pia.Docs project directory. Defaults to the sibling checkout.

.PARAMETER Check
  Regenerate to a temporary file and report whether the committed corpus differs. Writes nothing.
#>
[CmdletBinding()]
param(
    [string]$DocsRepo = (Join-Path $PSScriptRoot '..\..\Pia\src\Pia.Docs'),
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$outputPath = Join-Path $PSScriptRoot '..\src\Pia.Wpf\Resources\Help\help-corpus.json.gz'
$docsRepoPath = (Resolve-Path $DocsRepo).Path

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "node is required to run the docs KB builder but was not found on PATH."
}

Write-Host "Building the KB preset from $docsRepoPath ..."
Push-Location $docsRepoPath
try {
    & node 'scripts/build-kb.mjs' '--no-zip'
    if ($LASTEXITCODE -ne 0) { throw "build-kb.mjs failed with exit code $LASTEXITCODE." }

    $sourceDirty = [bool](& git status --porcelain -- 'src/content/docs')
}
finally {
    Pop-Location
}

$presetDir = Join-Path $docsRepoPath 'dist\kb-preset'
$manifest = Get-Content (Join-Path $presetDir 'manifest.json') -Raw | ConvertFrom-Json

# 'wpf' is the desktop-client product; server/** admin docs are deliberately out of scope.
$wpfDocs = @($manifest.documents | Where-Object { $_.product -eq 'wpf' } | Sort-Object path)
if ($wpfDocs.Count -eq 0) { throw "The KB preset contains no wpf documents." }

$pages = foreach ($doc in $wpfDocs) {
    $body = (Get-Content (Join-Path $presetDir $doc.path) -Raw) -replace "`r`n", "`n"

    # The builder's own header shape: "# Title\n\nSource: <url>\n\n<body>". Drop it — title and url
    # are already manifest fields, and leaving them in would rank every page against every query.
    $withoutHeader = $body -replace '(?s)^#\s[^\n]*\n\nSource:\s[^\n]*\n\n', ''

    $firstParagraph = ($withoutHeader -split "`n`n" | Where-Object { $_ -notmatch '^\s*$' -and $_ -notmatch '^#' } | Select-Object -First 1)
    $description = if ($firstParagraph) { ($firstParagraph -replace "`n", ' ').Trim() } else { '' }
    if ($description.Length -gt 200) { $description = $description.Substring(0, 197).TrimEnd() + '...' }

    [ordered]@{
        # 'docs/wpf/guides/speech.md' -> 'guides/speech', the ref shape the tool takes and returns.
        path        = ($doc.path -replace '^docs/wpf/', '' -replace '\.md$', '')
        title       = $doc.title
        description = $description
        url         = $doc.sourceUri
        body        = $withoutHeader.Trim()
    }
}

$corpus = [ordered]@{
    sourceCommit = $manifest.sourceCommit
    sourceDirty  = $sourceDirty
    pageCount    = $pages.Count
    pages        = @($pages)
}

$json = ($corpus | ConvertTo-Json -Depth 6 -Compress)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

$compressed = [System.IO.MemoryStream]::new()
$gzip = [System.IO.Compression.GZipStream]::new($compressed, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$gzip.Write($bytes, 0, $bytes.Length)
$gzip.Dispose()
$newBytes = $compressed.ToArray()
$compressed.Dispose()

if ($Check) {
    if (-not (Test-Path $outputPath)) {
        Write-Host "DRIFT: no corpus is committed yet."
        exit 1
    }
    $oldJson = [System.IO.File]::ReadAllBytes($outputPath)
    $reader = [System.IO.Compression.GZipStream]::new([System.IO.MemoryStream]::new($oldJson), [System.IO.Compression.CompressionMode]::Decompress)
    $buffer = [System.IO.MemoryStream]::new()
    $reader.CopyTo($buffer)
    $reader.Dispose()
    $oldText = [System.Text.Encoding]::UTF8.GetString($buffer.ToArray())
    $buffer.Dispose()

    # Compare the PAGES, not the whole file: sourceCommit moves on every unrelated docs commit, and a
    # check that cries drift over a server/** edit is a check nobody runs.
    $oldPages = ($oldText | ConvertFrom-Json).pages | ConvertTo-Json -Depth 6 -Compress
    $newPages = $pages | ConvertTo-Json -Depth 6 -Compress

    if ($oldPages -eq $newPages) {
        Write-Host "Up to date: $($pages.Count) pages. Snapshot taken at $(($oldText | ConvertFrom-Json).sourceCommit); docs are now at $($manifest.sourceCommit)."
        exit 0
    }
    Write-Host "DRIFT: the desktop guide has changed since the snapshot. Re-run without -Check."
    Write-Host "  snapshot: $(($oldText | ConvertFrom-Json).sourceCommit)"
    Write-Host "  docs now: $($manifest.sourceCommit)"
    exit 1
}

[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\src\Pia.Wpf\Resources\Help\help-corpus.json.gz'), $newBytes)

Write-Host "Wrote $($pages.Count) pages ($([math]::Round($bytes.Length / 1KB)) KB raw, $([math]::Round($newBytes.Length / 1KB)) KB gzipped) to Resources\Help\help-corpus.json.gz"
Write-Host "Docs commit: $($manifest.sourceCommit)$(if ($sourceDirty) { ' (working tree had uncommitted doc edits)' })"
