$ErrorActionPreference = "Stop"

$protected = @("main", "develop")
$writeCommands = @("commit", "merge", "cherry-pick", "revert", "am", "rebase")
$branchesFromMain = @("hotfix/", "back-merge/")

$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = [string]$payload.tool_input.command
if ($command -notmatch '\bgit\b') { exit 0 }
$cwd = if ($payload.cwd) { [string]$payload.cwd } else { (Get-Location).Path }

function Block([string]$reason) {
    [Console]::Error.WriteLine("Blocked by .claude/hooks/guard-protected-branches.ps1: $reason Work on a feature/, release/, hotfix/ or back-merge/ branch and reach main/develop through a GitHub pull request (see the release-flow skill).")
    exit 2
}

function Get-CurrentBranch([string]$dir) {
    $branch = & git -C $dir branch --show-current 2>$null
    if ($LASTEXITCODE -eq 0) { [string]$branch } else { "" }
}

function Get-BranchName([string]$ref) {
    ($ref.TrimStart('+') -replace '^refs/heads/', '' -replace '^origin/', '')
}

foreach ($segment in $command -split '&&|\|\||;|\||\r?\n') {
    $tokens = @([regex]::Matches($segment, '"[^"]*"|''[^'']*''|\S+') | ForEach-Object { $_.Value.Trim('"', "'") })
    if ($tokens.Count -ge 2 -and @("cd", "Set-Location", "Push-Location", "sl", "pushd") -contains $tokens[0]) {
        $target = $tokens[-1]
        $cwd = if ([System.IO.Path]::IsPathRooted($target)) { $target } else { Join-Path $cwd $target }
        continue
    }
    $gitAt = [array]::IndexOf($tokens, "git")
    if ($gitAt -lt 0) { continue }

    $dir = $cwd
    $i = $gitAt + 1
    while ($i -lt $tokens.Count -and $tokens[$i].StartsWith("-")) {
        if ($tokens[$i] -eq "-C" -and $i + 1 -lt $tokens.Count) { $dir = $tokens[$i + 1]; $i += 2; continue }
        if ($tokens[$i] -eq "-c") { $i += 2; continue }
        $i++
    }
    if ($i -ge $tokens.Count) { continue }
    $sub = $tokens[$i]
    $argv = @($tokens | Select-Object -Skip ($i + 1))
    $positional = @($argv | Where-Object { -not $_.StartsWith("-") })
    $current = Get-CurrentBranch $dir

    if ($writeCommands -contains $sub -and $protected -contains $current) {
        if ($argv -contains "--abort" -or $argv -contains "--quit") { continue }
        Block "git $sub on $current."
    }

    if ($sub -eq "push") {
        if ($argv -contains "--all" -or $argv -contains "--mirror") { Block "git push --all/--mirror includes main and develop." }
        if ($argv -contains "--tags" -and $positional.Count -le 1) { continue }
        $refspecs = @($positional | Select-Object -Skip 1)
        if ($refspecs.Count -eq 0 -and $protected -contains $current) { Block "git push from $current." }
        foreach ($spec in $refspecs) {
            $destination = if ($spec.Contains(":")) { $spec.Split(":")[-1] } else { $spec }
            if ($destination -eq "HEAD") { $destination = $current }
            if ($protected -contains (Get-BranchName $destination)) { Block "git push to $(Get-BranchName $destination)." }
        }
    }

    $newBranch = $null
    $startPoint = $null
    if (($sub -eq "checkout" -and $argv -contains "-b") -or ($sub -eq "switch" -and ($argv -contains "-c" -or $argv -contains "--create"))) {
        $newBranch = $positional | Select-Object -First 1
        $startPoint = $positional | Select-Object -Skip 1 -First 1
    }
    if ($newBranch) {
        $base = if ($startPoint) { Get-BranchName $startPoint } else { $current }
        $fromMainAllowed = @($branchesFromMain | Where-Object { $newBranch.StartsWith($_) }).Count -gt 0
        if ($base -eq "main" -and -not $fromMainAllowed) {
            Block "new branch $newBranch starts from main. Branch from origin/develop (hotfix/* and back-merge/* from origin/main)."
        }
    }
}

exit 0
