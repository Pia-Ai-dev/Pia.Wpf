#requires -version 7
<#
.SYNOPSIS
  Mirrors the models Pia.Wpf downloads lazily onto storage.pia-ai.de, so clients fetch them from us.

.DESCRIPTION
  Run by hand, not by CI: the assets change only when an upstream project publishes a new release, and
  the credential grants write AND delete on a public file service.

  Each asset is staged from its upstream host, verified against the Content-Length that host reported,
  then PUT to `{StorageBase}/upload/{Prefix}/{MirrorKey}` and read back from `{StorageBase}/f/{Prefix}/`.
  The keys come from RuntimeAssetCatalogue.ps1, shared with Save-RuntimeAssets.ps1 and pinned against
  the client's own table by RuntimeAssetCatalogTests — the client asks for exactly these paths and
  falls back to the upstream host when one is missing, so a wrong key costs a silent mirror miss.

  **Sherpa bundles are mirrored as the .tar.bz2 archive, not as the extracted tree.** The client
  extracts identically on both paths, which is what keeps the mirror from needing its own code path.

  **Not mirrored, and neither is an oversight.** Piper's engine and voices are fetched by PiperSharp
  from URLs held inside that package, with no override hook. Chromium comes from Playwright's own
  installer at a revision pinned to the package; mirroring it means reproducing its CDN layout per
  revision, which is a separate job (`ChromiumProvisioner.DownloadHostOverride` is the hook if it is
  ever wanted).

.PARAMETER Include
  Which asset groups to publish. Defaults to everything the client can download — a mirror that holds
  only the popular half still serves the rest from upstream, which is the outcome this exists to avoid.

.PARAMETER StorageBase
  Origin of the storage service. Self-hosting deployments point this and the client's
  `Assets:MirrorBaseUrl` at their own.

.PARAMETER Prefix
  Path under the served root. Must agree with the client's `Assets:MirrorBaseUrl`.

.PARAMETER StagingRoot
  Where upstream copies are kept between runs, so a re-run does not re-download several GB.

.PARAMETER Overwrite
  Send `X-Pia-Overwrite: true`. Off by default because it is destructive in a way that is invisible:
  the served ETag is mtime + length, so rewriting a blob turns every in-flight resume into a full
  re-download. A 409 means the mirror holds DIFFERENT bytes under that name — investigate first.

.PARAMETER ListOnly
  Print the plan, stage and upload nothing.

.EXAMPLE
  $env:PIA_STORAGE_UPLOAD_SECRET = '<secret>'
  pwsh scripts/Publish-RuntimeAssets.ps1 -ListOnly

.EXAMPLE
  pwsh scripts/Publish-RuntimeAssets.ps1 -Include Vad,Speaker,Embeddings
#>
[CmdletBinding()]
param(
    [ArgumentCompletions('Vad', 'Speaker', 'Embeddings', 'WhisperTiny', 'WhisperBase', 'WhisperSmall',
                         'WhisperMedium', 'WhisperLarge', 'Parakeet')]
    [string[]]$Include = @('Vad', 'Speaker', 'Embeddings', 'WhisperTiny', 'WhisperBase', 'WhisperSmall',
                           'WhisperMedium', 'WhisperLarge', 'Parakeet'),

    [string]$StorageBase = 'https://storage.pia-ai.de',
    [string]$Prefix = 'assets',
    [string]$StagingRoot = (Join-Path $env:TEMP 'pia-asset-staging'),

    # PUT /upload and GET/DELETE /manage share ONE sliding window per IP (30 per 60 s) and nothing
    # retries a 429. Spacing bounds how many calls land inside one window, so adding assets to the
    # publish set does not move the worst case. Same reason the release workflow carries WRITE_SPACING.
    [int]$WriteSpacing = 3,

    [switch]$Overwrite,
    [switch]$ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'RuntimeAssetCatalogue.ps1')

# DestinationRoot only shapes the cache paths in the catalogue, which this script does not use; the
# staging layout is derived from MirrorKey so the local tree mirrors the served one.
$catalogue = Get-RuntimeAssetCatalogue -DestinationRoot $StagingRoot
$groups = Resolve-RuntimeAssetGroups -Catalogue $catalogue -Include $Include

$assets = @(foreach ($g in $groups) { $catalogue[$g] } ) |
    Where-Object { $_.Contains('MirrorKey') }

if ($assets.Count -eq 0) { throw 'Nothing to publish — every requested group is mirror-exempt.' }

$uploadBase = "$($StorageBase.TrimEnd('/'))/upload/$Prefix"
$publicBase = "$($StorageBase.TrimEnd('/'))/f/$Prefix"

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

function Get-StagingPath($Asset) {
    Join-Path $StagingRoot ($Asset.MirrorKey -replace '/', [IO.Path]::DirectorySeparatorChar)
}

# Truncation is the failure that matters twice over here: a short file uploaded once is served forever,
# and the client's own presence check would never re-fetch it. So the staged copy is written to a .tmp,
# checked against the length the upstream host reported, and only then moved into place.
function Save-Staged($Asset, [Nullable[long]]$RemoteLength) {
    $target = Get-StagingPath $Asset
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $tmp = "$target.tmp"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }

    Invoke-WebRequest -Uri $Asset.Url -OutFile $tmp -MaximumRedirection 10 -ErrorAction Stop

    $got = (Get-Item -LiteralPath $tmp).Length
    if ($got -eq 0) { Remove-Item -LiteralPath $tmp -Force; throw 'Empty download.' }
    if ($null -ne $RemoteLength -and $got -ne $RemoteLength) {
        Remove-Item -LiteralPath $tmp -Force
        throw "Truncated download: got $got bytes, upstream said $RemoteLength."
    }

    Move-Item -LiteralPath $tmp -Destination $target -Force
    return $got
}

# curl.exe rather than Invoke-WebRequest: it streams --upload-file from disk and sets Content-Length
# itself, which is what makes a 1.8 GB PUT work, and -w gives the status without an exception to
# unwrap. The secret goes in via a config file on STDIN, so it never reaches a command line another
# process can read, nor a file on disk.
function Invoke-Upload([string]$Path, [string]$Key, [string]$Secret) {
    $curlArgs = @('--config', '-', '--silent', '--show-error', '--upload-file', $Path,
                  '--output', $script:CurlOut, '--write-out', '%{http_code}',
                  "$uploadBase/$Key")

    $config = "header = `"X-Pia-Upload-Secret: $Secret`""
    if ($Overwrite) { $config += "`nheader = `"X-Pia-Overwrite: true`"" }

    return ($config | & curl.exe @curlArgs)
}

# --- plan -------------------------------------------------------------------

Write-Host "Mirror:  $publicBase"
Write-Host "Staging: $StagingRoot"
Write-Host ''

$plan = foreach ($a in $assets) {
    $upstream = Get-RemoteLength $a.Url
    $staged = Get-StagingPath $a
    $have = (Test-Path -LiteralPath $staged) -and
            ($null -eq $upstream -or (Get-Item -LiteralPath $staged).Length -eq $upstream)
    [pscustomobject]@{
        Name     = $a.Name
        Key      = $a.MirrorKey
        Size     = Format-Size ($upstream ?? $a.SizeHint)
        Staging  = if ($have) { 'staged' } else { 'fetch' }
        Served   = Format-Size (Get-RemoteLength "$publicBase/$($a.MirrorKey)")
        Asset    = $a
        Upstream = $upstream
        Staged   = $have
    }
}

$plan | Format-Table Key, Size, Staging, Served -AutoSize

$toFetch = @($plan | Where-Object { -not $_.Staged })
$fetchBytes = ($toFetch | ForEach-Object { $_.Upstream ?? $_.Asset.SizeHint } | Measure-Object -Sum).Sum
$uploadBytes = ($plan | ForEach-Object { $_.Upstream ?? $_.Asset.SizeHint } | Measure-Object -Sum).Sum
Write-Host ("To stage:  {0} item(s), {1}" -f $toFetch.Count, (Format-Size $fetchBytes))
Write-Host ("To upload: {0} item(s), {1}" -f $plan.Count, (Format-Size $uploadBytes))

if ($ListOnly) { exit 0 }

$secret = $env:PIA_STORAGE_UPLOAD_SECRET
if ([string]::IsNullOrWhiteSpace($secret)) {
    throw 'PIA_STORAGE_UPLOAD_SECRET is not set. It is the only write credential on the storage service.'
}

# --- stage ------------------------------------------------------------------

Write-Host ''
foreach ($row in $toFetch) {
    Write-Host ("==> staging {0} ({1})" -f $row.Key, $row.Size)
    Save-Staged $row.Asset $row.Upstream | Out-Null
}

# --- upload -----------------------------------------------------------------

Write-Host ''
$script:CurlOut = Join-Path ([IO.Path]::GetTempPath()) "pia-upload-$PID.out"
$failed = @()
try {
    foreach ($row in $plan) {
        $path = Get-StagingPath $row.Asset
        Start-Sleep -Seconds $WriteSpacing
        # curl exits non-zero only on a transport failure — an HTTP status is a successful run, which is
        # what lets the case analysis below distinguish the four outcomes that mean different things.
        try { $code = Invoke-Upload $path $row.Key $secret }
        catch { $failed += "$($row.Key): $($_.Exception.Message -replace 's+', ' ')"; continue }

        switch ($code) {
            '201' { Write-Host "uploaded  $($row.Key)" }
            '204' { Write-Host "identical $($row.Key)" }
            '200' { Write-Host "replaced  $($row.Key)" }
            '409' {
                # Not something to force past with -Overwrite: the mirror already serves these bytes to
                # clients, and republishing changes the ETag (mtime + length), so every resume in flight
                # restarts from zero. A differing digest means upstream republished under the same name.
                $failed += "$($row.Key): already mirrored with DIFFERENT bytes (409). Compare the staged copy against what is served before deciding; do not re-run with -Overwrite blind."
            }
            default {
                $body = if (Test-Path -LiteralPath $script:CurlOut) { (Get-Content -Raw $script:CurlOut).Trim() } else { '' }
                $failed += "$($row.Key): PUT returned $code. $body"
            }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $script:CurlOut) { Remove-Item -LiteralPath $script:CurlOut -Force }
}

# --- verify -----------------------------------------------------------------
#
# Ask for it exactly as the client will. A PUT that answered 201 and a file the public route does not
# serve is the one shape that would otherwise ship as "mirrored" and silently fall back forever.

Write-Host ''
foreach ($row in $plan) {
    $served = Get-RemoteLength "$publicBase/$($row.Key)"
    $local = (Get-Item -LiteralPath (Get-StagingPath $row.Asset)).Length
    if ($null -eq $served) {
        $failed += "$($row.Key): not served under $publicBase/"
    }
    elseif ($served -ne $local) {
        $failed += "$($row.Key): $served bytes served but $local staged"
    }
    else {
        Write-Host ("ok {0} ({1} bytes)" -f $row.Key, $served)
    }
}

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host "All $($plan.Count) asset(s) mirrored at $publicBase/."
    exit 0
}

Write-Host "$($failed.Count) problem(s):"
foreach ($f in $failed) { Write-Host "  $f" }
exit 1
