#requires -version 7
<#
.SYNOPSIS
  Pre-fetches the models and browser Pia.Wpf otherwise downloads lazily on first use.

.DESCRIPTION
  Every asset here is normally fetched the first time a feature needs it, behind a catch-and-log. That
  is fine on a fast link and useless on a metered or air-gapped one, where the first transcription
  silently does nothing for ten minutes. This script fetches them up front, into the exact paths the
  app checks, so the app finds them and skips its own download.

  The asset list lives in RuntimeAssetCatalogue.ps1, shared with Publish-RuntimeAssets.ps1. It mirrors
  docs/external_endpoints/2026-08-29-external-endpoint-inventory.md §3 and is pinned by
  ModelDownloadUrlTests and RuntimeAssetCatalogTests.

  **Truncation is the failure that matters.** The app's presence checks are weak — a bundle directory
  holding any .onnx, or a VAD file of non-zero length — so a download interrupted halfway leaves a
  cache the app will never re-fetch and never succeed with. Every file here is therefore written to a
  .tmp, checked against the Content-Length the server reported, and only then moved into place; and an
  existing file whose size does not match is re-fetched rather than skipped, which repairs a cache
  poisoned by an earlier Ctrl-C.

  **Not covered: TTS voices.** Piper voices are fetched by PiperSharp into a layout it owns, and
  TtsService gates on "the directory holds an .onnx" while loading needs PiperSharp's model.json
  beside it. Hand-placing the .onnx would satisfy the gate and then fail to load, permanently, with
  no self-heal — strictly worse than not pre-fetching. Download voices from the app's own TTS
  settings instead.

.PARAMETER Include
  Which asset groups to fetch. Defaults to what a normal install actually pulls: the VAD, the speaker
  embedding model, the three text-embedding files, and Whisper Base (the shipped default model).

.PARAMETER All
  Every group, including all five Whisper sizes, Parakeet and Chromium. Several gigabytes.

.PARAMETER DestinationRoot
  Where `Models\` and `Browsers\` live. Defaults to the real profile, matching PiaPaths — which
  deliberately ignores PIA_LOCAL_DATA_DIR for downloaded artifacts. Point it elsewhere to stage a
  bundle for an air-gapped machine, then copy the tree to that machine's %LOCALAPPDATA%\Pia.

  Pointing it at a publish payload (-Include Chromium -DestinationRoot publish) is how a release
  bundles the browser: the app prefers a Browsers\ folder beside its own exe over the download.

.PARAMETER Force
  Re-fetch even when the asset is already present and the right size.

.PARAMETER MirrorBaseUrl
  Try this mirror before each asset's upstream host, exactly as the app does. Defaults to the same
  base as appsettings.json's Assets:MirrorBaseUrl; pass an empty string to go straight upstream.

.PARAMETER ListOnly
  Print the plan and the total download size, fetch nothing.

.EXAMPLE
  pwsh scripts/Save-RuntimeAssets.ps1
  Fetches the default set (~720 MB).

.EXAMPLE
  pwsh scripts/Save-RuntimeAssets.ps1 -Include WhisperLarge,Parakeet,Chromium

.EXAMPLE
  pwsh scripts/Save-RuntimeAssets.ps1 -All -ListOnly
#>
[CmdletBinding()]
param(
    # ArgumentCompletions, not ValidateSet: `pwsh script.ps1 -Include A,B` hands the whole thing over
    # as one string, which a ValidateSet rejects. The names are normalised and checked below instead,
    # so the documented command lines actually work.
    [ArgumentCompletions('Vad', 'Speaker', 'Embeddings', 'WhisperTiny', 'WhisperBase', 'WhisperSmall',
                         'WhisperMedium', 'WhisperLarge', 'Parakeet', 'Chromium')]
    [string[]]$Include = @('Vad', 'Speaker', 'Embeddings', 'WhisperBase'),

    [switch]$All,
    [string]$DestinationRoot = (Join-Path $env:LOCALAPPDATA 'Pia'),
    [string]$MirrorBaseUrl = 'https://storage.pia-ai.de/f/assets/',
    [switch]$Force,
    [switch]$ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'RuntimeAssetCatalogue.ps1')

$catalogue = Get-RuntimeAssetCatalogue -DestinationRoot $DestinationRoot
$Include = Resolve-RuntimeAssetGroups -Catalogue $catalogue -Include $Include -All:$All

function Format-Size([Nullable[long]]$bytes) {
    if ($null -eq $bytes) { return '?' }
    if ($bytes -ge 1GB) { return '{0:N2} GB' -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return '{0:N0} MB' -f ($bytes / 1MB) }
    return '{0:N0} KB' -f ($bytes / 1KB)
}

function Get-RemoteLength([string]$Url) {
    try {
        $resp = Invoke-WebRequest -Uri $Url -Method Head -MaximumRedirection 10 -TimeoutSec 60 `
            -SkipHttpErrorCheck -ErrorAction Stop
        if ([int]$resp.StatusCode -ne 200) { return $null }
        $len = $resp.Headers['Content-Length'] | Select-Object -First 1
        if ($len) { return [long]$len }
    }
    catch { }
    return $null
}

# Mirrors the app's own presence check, tightened: for a single file the size must also match the
# server, so a truncated earlier attempt is repaired instead of being trusted forever.
function Test-AssetPresent($Asset, [Nullable[long]]$RemoteLength) {
    switch ($Asset.Kind) {
        'File' {
            if (-not (Test-Path -LiteralPath $Asset.Target)) { return $false }
            $len = (Get-Item -LiteralPath $Asset.Target).Length
            if ($len -eq 0) { return $false }
            if ($null -ne $RemoteLength -and $len -ne $RemoteLength) { return $false }
            return $true
        }
        'Bundle' {
            # The app's gate exactly: the directory holds at least one .onnx, non-recursive. Byte-level
            # verification is not possible after extraction, so -Force is the way to redo a bad one.
            return (Test-Path -LiteralPath $Asset.Target) -and
                   @(Get-ChildItem -LiteralPath $Asset.Target -Filter '*.onnx' -File -ErrorAction SilentlyContinue).Count -gt 0
        }
        'Playwright' {
            return @(Get-ChildItem -LiteralPath $Asset.Target -Filter 'chrome.exe' -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0
        }
    }
    return $false
}

# Mirror-first, the same order the app takes. The probe doubles as the size the plan reports, so the
# table can never name a host the download then does not use. Latched: against a mirror that is down
# every remaining asset would otherwise re-pay the same handshake timeout.
$script:MirrorDown = $false
function Resolve-AssetSource($Asset) {
    if (-not $script:MirrorDown -and $MirrorBaseUrl -and $Asset.Contains('MirrorKey')) {
        $url = $MirrorBaseUrl.TrimEnd('/') + '/' + $Asset.MirrorKey
        $len = Get-RemoteLength $url
        if ($null -ne $len) { return @{ Url = $url; Length = $len; Source = 'mirror' } }
        $script:MirrorDown = $true
    }
    return @{ Url = $Asset.Url; Length = (Get-RemoteLength $Asset.Url); Source = 'upstream' }
}

function Save-File($Asset, [string]$Url, [Nullable[long]]$RemoteLength) {
    $target = $Asset.Target
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $tmp = "$target.tmp"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }

    Invoke-WebRequest -Uri $Url -OutFile $tmp -MaximumRedirection 10 -ErrorAction Stop

    $got = (Get-Item -LiteralPath $tmp).Length
    if ($null -ne $RemoteLength -and $got -ne $RemoteLength) {
        Remove-Item -LiteralPath $tmp -Force
        throw "Truncated download: got $got bytes, server said $RemoteLength."
    }
    if ($got -eq 0) { Remove-Item -LiteralPath $tmp -Force; throw 'Empty download.' }

    Move-Item -LiteralPath $tmp -Destination $target -Force
    return $got
}

function Save-Bundle($Asset, [string]$Url, [Nullable[long]]$RemoteLength) {
    $target = $Asset.Target
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $archive = "$target.tar.bz2.tmp"
    $extract = "$target.extract.tmp"

    try {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Invoke-WebRequest -Uri $Url -OutFile $archive -MaximumRedirection 10 -ErrorAction Stop

        $got = (Get-Item -LiteralPath $archive).Length
        if ($null -ne $RemoteLength -and $got -ne $RemoteLength) {
            throw "Truncated download: got $got bytes, server said $RemoteLength."
        }

        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $extract | Out-Null

        # --strip-components=1 reproduces what the app does: sherpa wraps everything in a folder named
        # after the bundle, and the app drops that component so the files land flat. Keep them
        # identical or the app will not find the model where it looks. Windows tar is bsdtar and
        # auto-detects bzip2, so no -j.
        Write-Host "    extracting..."
        & tar -x -f $archive -C $extract --strip-components=1
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }

        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Move-Item -LiteralPath $extract -Destination $target -Force
        return $got
    }
    finally {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# Delegated to Playwright's own installer rather than a hard-coded CDN URL: the browser revision is
# tied to the pinned Microsoft.Playwright version, and the installer already knows which one.
function Save-Chromium($Asset) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        # A publish payload first, so staging a release bundle needs no separate repo build.
        Join-Path $DestinationRoot 'playwright.ps1'
        Join-Path $repoRoot 'src/Pia.Wpf/bin/Release/net10.0-windows10.0.17763.0/playwright.ps1'
        Join-Path $repoRoot 'src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/playwright.ps1'
    )
    $installer = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $installer) {
        throw "playwright.ps1 not found in the build output. Run 'dotnet build' first, or drop Chromium from -Include."
    }

    New-Item -ItemType Directory -Force -Path $Asset.Target | Out-Null
    $previous = $env:PLAYWRIGHT_BROWSERS_PATH
    try {
        $env:PLAYWRIGHT_BROWSERS_PATH = $Asset.Target
        # --no-shell: the app launches the full headed build for a real audio render session, so the
        # headless shell would be 260 MB of binary nothing ever executes.
        & $installer install chromium --no-shell
        if ($LASTEXITCODE -ne 0) { throw "Playwright installer failed with exit code $LASTEXITCODE." }
    }
    finally {
        $env:PLAYWRIGHT_BROWSERS_PATH = $previous
    }

    # The installer records THIS machine's driver path in .links. Carried onto another machine that
    # link is dead, and a registry whose links are all dead is one install away from having every
    # browser directory in it deleted as unreferenced.
    $links = Join-Path $Asset.Target '.links'
    if (Test-Path -LiteralPath $links) { Remove-Item -LiteralPath $links -Recurse -Force }

    return $null
}

# --- plan -------------------------------------------------------------------

$assets = foreach ($group in $Include) { $catalogue[$group] }

Write-Host "Destination: $DestinationRoot"
Write-Host ''

$plan = foreach ($a in $assets) {
    $source = if ($a.Kind -eq 'Playwright') { @{ Url = $null; Length = $null; Source = 'playwright' } }
              else { Resolve-AssetSource $a }
    $present = Test-AssetPresent $a $source.Length
    [pscustomobject]@{
        Name    = $a.Name
        Size    = Format-Size ($source.Length ?? $a.SizeHint)
        From    = $source.Source
        Action  = if ($present -and -not $Force) { 'skip (present)' } else { 'download' }
        Asset   = $a
        Url     = $source.Url
        Remote  = $source.Length
        Fetch   = (-not $present) -or $Force
    }
}

$plan | Format-Table Name, Size, From, Action -AutoSize

$todo = @($plan | Where-Object Fetch)
$total = ($todo | ForEach-Object { $_.Remote ?? $_.Asset.SizeHint } | Measure-Object -Sum).Sum
if ($todo.Count -eq 0) {
    Write-Host 'Everything requested is already in place.'
    exit 0
}
Write-Host ("To download: {0} item(s), {1}" -f $todo.Count, (Format-Size $total))

if ($ListOnly) { exit 0 }
Write-Host ''

# --- fetch ------------------------------------------------------------------

$failed = @()
foreach ($row in $todo) {
    $a = $row.Asset
    Write-Host ("==> {0} ({1}, {2})" -f $a.Name, $row.Size, $row.From)
    try {
        switch ($a.Kind) {
            'File'       { Save-File $a $row.Url $row.Remote | Out-Null }
            'Bundle'     { Save-Bundle $a $row.Url $row.Remote | Out-Null }
            'Playwright' { Save-Chromium $a | Out-Null }
        }
        Write-Host "    done -> $($a.Target)"
    }
    catch {
        $msg = ($_.Exception.Message -replace '\s+', ' ')
        Write-Host "    FAILED: $msg"
        $failed += [pscustomobject]@{ Name = $a.Name; Error = $msg }
    }
}

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "All $($todo.Count) asset(s) in place."
    exit 0
}

Write-Host "$($failed.Count) of $($todo.Count) failed:"
foreach ($f in $failed) { Write-Host ("  {0}: {1}" -f $f.Name, $f.Error) }
exit 1
