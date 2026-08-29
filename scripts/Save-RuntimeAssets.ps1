#requires -version 7
<#
.SYNOPSIS
  Pre-fetches the models and browser Pia.Wpf otherwise downloads lazily on first use.

.DESCRIPTION
  Every asset here is normally fetched the first time a feature needs it, behind a catch-and-log. That
  is fine on a fast link and useless on a metered or air-gapped one, where the first transcription
  silently does nothing for ten minutes. This script fetches them up front, into the exact paths the
  app checks, so the app finds them and skips its own download.

  URLs mirror docs/external_endpoints/2026-08-29-external-endpoint-inventory.md §3 and are pinned by
  ModelDownloadUrlTests. Change one and change it in all three places.

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

.PARAMETER Force
  Re-fetch even when the asset is already present and the right size.

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
    [switch]$Force,
    [switch]$ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sherpaAsr = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
# The "recongition" misspelling is the real release tag; the corrected spelling 404s.
$sherpaSpk = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models'
$hf = 'https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main'

$modelsDir = Join-Path $DestinationRoot 'Models'
$embeddingsDir = Join-Path $modelsDir 'Embeddings'
$browsersDir = Join-Path $DestinationRoot 'Browsers'

# SizeHint is the Content-Length measured 2026-08-29, used only for the up-front total. The download
# itself verifies against whatever the server reports now, so a republished asset is not a failure.
$catalogue = [ordered]@{
    Vad = @(
        @{ Kind = 'File'; Name = 'Silero VAD'
           Url = 'https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx'
           Target = Join-Path $modelsDir 'silero_vad.onnx'; SizeHint = 2327524 }
    )
    Speaker = @(
        @{ Kind = 'File'; Name = 'Speaker embedding (3D-Speaker CAM++)'
           Url = "$sherpaSpk/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
           Target = Join-Path $modelsDir '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
           SizeHint = 28281164 }
    )
    # The ONNX file is renamed on the way in: EmbeddingService looks for the model under the model's
    # own name, not the "model.onnx" the URL ends in.
    Embeddings = @(
        @{ Kind = 'File'; Name = 'Text embedding model'; Url = "$hf/onnx/model.onnx"
           Target = Join-Path $embeddingsDir 'paraphrase-multilingual-MiniLM-L12-v2.onnx'
           SizeHint = 470301610 }
        @{ Kind = 'File'; Name = 'Text embedding tokenizer'; Url = "$hf/tokenizer.json"
           Target = Join-Path $embeddingsDir 'tokenizer.json'; SizeHint = 9081518 }
        @{ Kind = 'File'; Name = 'Text embedding SentencePiece'; Url = "$hf/sentencepiece.bpe.model"
           Target = Join-Path $embeddingsDir 'sentencepiece.bpe.model'; SizeHint = 5069051 }
    )
    WhisperTiny = @(
        @{ Kind = 'Bundle'; Name = 'Whisper Tiny'; Url = "$sherpaAsr/sherpa-onnx-whisper-tiny.tar.bz2"
           Target = Join-Path $modelsDir 'sherpa-whisper-tiny'; SizeHint = 116204861 }
    )
    WhisperBase = @(
        @{ Kind = 'Bundle'; Name = 'Whisper Base'; Url = "$sherpaAsr/sherpa-onnx-whisper-base.tar.bz2"
           Target = Join-Path $modelsDir 'sherpa-whisper-base'; SizeHint = 207557382 }
    )
    WhisperSmall = @(
        @{ Kind = 'Bundle'; Name = 'Whisper Small'; Url = "$sherpaAsr/sherpa-onnx-whisper-small.tar.bz2"
           Target = Join-Path $modelsDir 'sherpa-whisper-small'; SizeHint = 639387718 }
    )
    WhisperMedium = @(
        @{ Kind = 'Bundle'; Name = 'Whisper Medium'; Url = "$sherpaAsr/sherpa-onnx-whisper-medium.tar.bz2"
           Target = Join-Path $modelsDir 'sherpa-whisper-medium'; SizeHint = 1931372882 }
    )
    # sherpa publishes large-v3-turbo under the bare "turbo" name; the spelled-out form 404s.
    WhisperLarge = @(
        @{ Kind = 'Bundle'; Name = 'Whisper Large v3 Turbo'; Url = "$sherpaAsr/sherpa-onnx-whisper-turbo.tar.bz2"
           Target = Join-Path $modelsDir 'sherpa-whisper-turbo'; SizeHint = 563790207 }
    )
    Parakeet = @(
        @{ Kind = 'Bundle'; Name = 'Parakeet TDT v3'
           Url = "$sherpaAsr/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"
           Target = Join-Path $modelsDir 'sherpa-parakeet-tdt-v3'; SizeHint = 487170055 }
    )
    # The installer also pulls the headless shell, ffmpeg and winldd alongside the browser, so the
    # real transfer is roughly twice the Chromium zip on its own.
    Chromium = @(
        @{ Kind = 'Playwright'; Name = 'Chromium (meeting attendee)'; Target = $browsersDir; SizeHint = 315000000 }
    )
}

if ($All) {
    $Include = @($catalogue.Keys)
}
else {
    $Include = @($Include | ForEach-Object { $_ -split '[,;]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $known = @($catalogue.Keys)
    $unknown = @($Include | Where-Object { $_ -notin $known })
    if ($unknown.Count -gt 0) {
        throw "Unknown asset group(s): $($unknown -join ', '). Valid: $($known -join ', ')."
    }
    $Include = @($known | Where-Object { $_ -in $Include })
}

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

function Save-File($Asset, [Nullable[long]]$RemoteLength) {
    $target = $Asset.Target
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $tmp = "$target.tmp"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }

    Invoke-WebRequest -Uri $Asset.Url -OutFile $tmp -MaximumRedirection 10 -ErrorAction Stop

    $got = (Get-Item -LiteralPath $tmp).Length
    if ($null -ne $RemoteLength -and $got -ne $RemoteLength) {
        Remove-Item -LiteralPath $tmp -Force
        throw "Truncated download: got $got bytes, server said $RemoteLength."
    }
    if ($got -eq 0) { Remove-Item -LiteralPath $tmp -Force; throw 'Empty download.' }

    Move-Item -LiteralPath $tmp -Destination $target -Force
    return $got
}

function Save-Bundle($Asset, [Nullable[long]]$RemoteLength) {
    $target = $Asset.Target
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $archive = "$target.tar.bz2.tmp"
    $extract = "$target.extract.tmp"

    try {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Invoke-WebRequest -Uri $Asset.Url -OutFile $archive -MaximumRedirection 10 -ErrorAction Stop

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
        & $installer install chromium
        if ($LASTEXITCODE -ne 0) { throw "Playwright installer failed with exit code $LASTEXITCODE." }
    }
    finally {
        $env:PLAYWRIGHT_BROWSERS_PATH = $previous
    }
    return $null
}

# --- plan -------------------------------------------------------------------

$assets = foreach ($group in $Include) { $catalogue[$group] }

Write-Host "Destination: $DestinationRoot"
Write-Host ''

$plan = foreach ($a in $assets) {
    $remote = if ($a.Kind -eq 'Playwright') { $null } else { Get-RemoteLength $a.Url }
    $present = Test-AssetPresent $a $remote
    [pscustomobject]@{
        Name    = $a.Name
        Size    = Format-Size ($remote ?? $a.SizeHint)
        Action  = if ($present -and -not $Force) { 'skip (present)' } else { 'download' }
        Asset   = $a
        Remote  = $remote
        Fetch   = (-not $present) -or $Force
    }
}

$plan | Format-Table Name, Size, Action -AutoSize

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
    Write-Host ("==> {0} ({1})" -f $a.Name, $row.Size)
    try {
        switch ($a.Kind) {
            'File'       { Save-File $a $row.Remote | Out-Null }
            'Bundle'     { Save-Bundle $a $row.Remote | Out-Null }
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
