#requires -version 7
<#
.SYNOPSIS
  Deletes old GitHub releases, keeping the newest few plus everything inside a retention window.

.DESCRIPTION
  Every push to main cuts a release, so the list grows by several entries a day and each one carries
  ~1.1 GB of assets (two MSIs, a full nupkg, the previous full nupkg, a delta). This trims it.

  Safe because the auto-updater never reads an old release: Velopack's GithubSource resolves the
  newest non-prerelease release and downloads the assets its releases.win.json names, and that feed
  lists only the current full, the current delta and the previous full. The pia-ai.de mirror is a
  copy on Hetzner storage, not a link to a GitHub asset, so it survives too.

  Tags are never deleted (no --cleanup-tag). changelog.yml regenerates the whole CHANGELOG.md with
  git-cliff on every publish, and build-and-release.yml derives PREV_TAG / the release-notes diff
  base from `git describe --tags`, so dropping a tag would quietly rewrite history on the next
  release. Tags cost nothing to keep.

  Nothing is deleted unless you pass -Apply.

.PARAMETER Repo
  OWNER/NAME to prune.

.PARAMETER KeepMinimum
  How many of the newest stable releases to keep regardless of age. Counted by tag semver, not by
  date: two releases can share a publish timestamp to the second.

.PARAMETER MaxAgeDays
  Releases published within this many days are kept whatever their position.

.PARAMETER Apply
  Actually delete. Without it the script only prints the plan.

.EXAMPLE
  pwsh scripts/Prune-Releases.ps1
  Prints what a prune would do, changing nothing.

.EXAMPLE
  pwsh scripts/Prune-Releases.ps1 -Apply
  Deletes the releases the plan lists, keeping their tags.
#>
[CmdletBinding()]
param(
    [string]$Repo = 'Pia-Ai-dev/Pia.Wpf',
    [ValidateRange(1, 100)]
    [int]$KeepMinimum = 5,
    [ValidateRange(1, 3650)]
    [int]$MaxAgeDays = 90,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TagVersion {
    param([string]$Tag)
    if ($Tag -match '^v?(\d+)\.(\d+)\.(\d+)') {
        return [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
    }
    return $null
}

$json = gh release list --repo $Repo --limit 1000 --json 'tagName,isDraft,isPrerelease,isLatest,publishedAt,createdAt'
if ($LASTEXITCODE -ne 0) { throw "gh release list failed for $Repo (exit $LASTEXITCODE)" }

$releases = @($json | ConvertFrom-Json)
if ($releases.Count -eq 0) {
    Write-Host "$Repo has no releases; nothing to prune."
    return
}

$now = [datetimeoffset]::UtcNow
$cutoff = $now.AddDays(-$MaxAgeDays)

$candidates = @(
    foreach ($r in $releases) {
        if ($r.isDraft) { continue }
        $stamp = if ($r.publishedAt) { [datetimeoffset]$r.publishedAt } else { [datetimeoffset]$r.createdAt }
        [pscustomobject]@{
            Tag          = $r.tagName
            Version      = Get-TagVersion $r.tagName
            Published    = $stamp
            AgeDays      = [int]($now - $stamp).TotalDays
            IsLatest     = [bool]$r.isLatest
            IsPrerelease = [bool]$r.isPrerelease
            Keep         = $true
            Reason       = ''
        }
    }
)

$newestStable = @(
    $candidates |
        Where-Object { -not $_.IsPrerelease -and $null -ne $_.Version } |
        Sort-Object -Property Version -Descending |
        Select-Object -First $KeepMinimum |
        ForEach-Object { $_.Tag }
)

foreach ($c in $candidates) {
    if ($c.IsLatest) {
        $c.Reason = 'latest - the update feed reads it'
    }
    elseif ($newestStable -contains $c.Tag) {
        $c.Reason = "newest $KeepMinimum"
    }
    elseif ($null -eq $c.Version) {
        $c.Reason = 'tag is not vMAJOR.MINOR.PATCH'
    }
    elseif ($c.Published -ge $cutoff) {
        $c.Reason = "$($c.AgeDays)d old"
    }
    else {
        $c.Keep = $false
        $c.Reason = "$($c.AgeDays)d old"
    }
}

$ordered = @($candidates | Sort-Object -Property Published -Descending)
$toDelete = @($ordered | Where-Object { -not $_.Keep })
$mode = if ($Apply) { 'apply' } else { 'dry run' }

Write-Host ""
Write-Host "$Repo - keep newest $KeepMinimum and anything under $MaxAgeDays days ($mode)"
Write-Host ""
$ordered |
    Format-Table -AutoSize @{ Label = ' '; Expression = { if ($_.Keep) { 'keep' } else { 'DEL ' } } },
    @{ Label = 'Tag'; Expression = 'Tag' },
    @{ Label = 'Published'; Expression = { $_.Published.ToString('yyyy-MM-dd') } },
    @{ Label = 'Why'; Expression = 'Reason' } |
    Out-String -Width 100 |
    Write-Host

$kept = $ordered.Count - $toDelete.Count
Write-Host "$kept kept, $($toDelete.Count) to delete."

if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @(
        "### Prune releases - $mode",
        "",
        "Keeping the newest $KeepMinimum stable releases and anything published in the last $MaxAgeDays days. Tags are kept in every case.",
        "",
        "| | Tag | Published | Why |",
        "|---|---|---|---|"
    ) + @($ordered | ForEach-Object {
            $flag = if ($_.Keep) { 'keep' } else { '**delete**' }
            "| $flag | $($_.Tag) | $($_.Published.ToString('yyyy-MM-dd')) | $($_.Reason) |"
        })
    $lines -join "`n" | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

if ($toDelete.Count -eq 0) { return }

if (-not $Apply) {
    Write-Host ""
    Write-Host "Dry run - nothing deleted. Re-run with -Apply to delete these $($toDelete.Count) releases."
    return
}

$failed = @()
foreach ($r in $toDelete) {
    Write-Host "deleting $($r.Tag)"
    gh release delete $r.Tag --repo $Repo --yes
    if ($LASTEXITCODE -ne 0) { $failed += $r.Tag }
}

if ($failed.Count -gt 0) {
    throw "Failed to delete: $($failed -join ', ')"
}

Write-Host "Deleted $($toDelete.Count) releases; their tags are untouched."
