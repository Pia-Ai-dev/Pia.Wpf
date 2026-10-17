#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Harness for the live run of sandboxed local MCP servers (docs/mcp_sandbox/2026-10-17-mcp-sandbox.md).

.DESCRIPTION
  Seeds the fixture tree (workspace rw / protected ro / secret not granted), stages Pia.TestMcpServer, launches
  the Debug client DE-ELEVATED against the throwaway profile and the e2e stack, and reports on demand: process
  integrity level, ACEs on the fixture, fixture hashes, orphan child processes and container profiles.

  Run it from an elevated shell: that is what makes the de-elevated launch meaningful and what lets it hand
  ownership of the staged files to the user. MXC (Tier 3) cannot grant a path the user cannot WRITE_DAC, and
  the server then dies on load, so everything the sandbox touches must be user-owned.

  Actions:
    Init           layout + profile (created once, never rewritten) + server staging + fixture seed
    Seed           wipe and re-create the fixture tree, record SHA-256s, give it to the user
    StageServer    copy Pia.TestMcpServer into <Root>\server (user-owned) and <Root>\server-admin (admin-owned)
    Launch         start the Debug exe through explorer.exe (medium IL) and print its integrity level
    Status         integrity level, fixture verdicts, ACEs, orphans, container profiles
    Stop           wait for a graceful exit done from the UI (TrayMenu_Exit); -Kill ends Pia hard, leaving ACEs
                   for the next start to sweep
    Verify         the real profile (settings.json, history.db) is untouched
    RemoveRuntime  delete the downloaded MXC runtime on the real local root (-Force)

  Never use https://localhost:8081: it holds the owner's real data. Never restore roaming\settings.json.

.EXAMPLE
  ./Invoke-MxcSandboxE2e.ps1 Init
  ./Invoke-MxcSandboxE2e.ps1 Launch
  ./Invoke-MxcSandboxE2e.ps1 Status
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Init', 'Seed', 'StageServer', 'Launch', 'Status', 'Stop', 'Verify', 'RemoveRuntime')]
    [string]$Action,

    # Where the throwaway profile, fixture, staged servers and launch files live. Default: <repo>\artifacts\mxc-sandbox-e2e.
    [string]$Root,

    [string]$ExePath,

    [string]$ServerSource,

    [string]$ServerUrl = 'https://localhost:18081',

    [switch]$Kill,

    [switch]$Force,

    [switch]$AllowElevated,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (-not $Root) { $Root = Join-Path $repoRoot 'artifacts/mxc-sandbox-e2e' }
$Root = [IO.Path]::GetFullPath($Root)
if (-not $ExePath) { $ExePath = Join-Path $repoRoot 'src/Pia.Wpf/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.exe' }
if (-not $ServerSource) { $ServerSource = Join-Path $repoRoot 'tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.TestMcpServer' }

$paths = [ordered]@{
    Roaming      = Join-Path $Root 'profile/roaming'
    Local        = Join-Path $Root 'profile/local'
    Fixture      = Join-Path $Root 'fixture'
    Workspace    = Join-Path $Root 'fixture/workspace'
    Protected    = Join-Path $Root 'fixture/protected'
    Secret       = Join-Path $Root 'fixture/secret'
    Server       = Join-Path $Root 'server/Pia.TestMcpServer'
    ServerAdmin  = Join-Path $Root 'server-admin/Pia.TestMcpServer'
    Launch       = Join-Path $Root 'launch'
    State        = Join-Path $Root 'state.json'
}
$serverExe = Join-Path $paths.Server 'Pia.TestMcpServer.exe'
$serverAdminExe = Join-Path $paths.ServerAdmin 'Pia.TestMcpServer.exe'
$realSettings = Join-Path $env:APPDATA 'Pia/settings.json'
$realHistoryDb = Join-Path $env:LOCALAPPDATA 'Pia/history.db'
$mxcRuntimeDir = Join-Path $env:LOCALAPPDATA 'Pia/Runtimes/Mxc'
$userName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$sidWellKnownAppPackages = @('S-1-15-2-1', 'S-1-15-2-2')
$sandboxHelperNames = @('plm', 'wxc-wslc-daemon')

function Fail($message, [int]$code = 2) {
    Write-Host "mxc-e2e: $message" -ForegroundColor Red
    exit $code
}

function Write-Section($title) { Write-Host "`n== $title" -ForegroundColor Cyan }

if ($ServerUrl -match ':8081(/|$)') { Fail 'refusing https://localhost:8081: it holds the owner''s real data. Use the e2e stack (18081).' }

if (-not ('MxcE2eTokenProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MxcE2eTokenProbe
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returned);

    const int TokenElevation = 20;
    const int TokenIntegrityLevel = 25;

    static IntPtr OpenToken(int pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) throw new InvalidOperationException("OpenProcess failed: " + Marshal.GetLastWin32Error());
        try
        {
            IntPtr token;
            if (!OpenProcessToken(process, 0x8, out token)) throw new InvalidOperationException("OpenProcessToken failed: " + Marshal.GetLastWin32Error());
            return token;
        }
        finally { CloseHandle(process); }
    }

    static IntPtr Query(IntPtr token, int infoClass)
    {
        int needed;
        GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out needed);
        var buffer = Marshal.AllocHGlobal(needed);
        if (!GetTokenInformation(token, infoClass, buffer, needed, out needed)) throw new InvalidOperationException("GetTokenInformation failed: " + Marshal.GetLastWin32Error());
        return buffer;
    }

    public static int IntegrityRid(int pid)
    {
        var token = OpenToken(pid);
        try
        {
            var buffer = Query(token, TokenIntegrityLevel);
            try
            {
                var sid = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(sid, 1);
                return Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    public static bool IsElevated(int pid)
    {
        var token = OpenToken(pid);
        try
        {
            var buffer = Query(token, TokenElevation);
            try { return Marshal.ReadInt32(buffer) != 0; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }
}
'@
}

function Get-IntegrityText([int]$processId) {
    $rid = [MxcE2eTokenProbe]::IntegrityRid($processId)
    $name = switch ($rid) { 0x1000 { 'Low' } 0x2000 { 'Medium' } 0x2100 { 'MediumPlus' } 0x3000 { 'High' } 0x4000 { 'System' } default { "0x$($rid.ToString('x'))" } }
    [pscustomobject]@{ Name = $name; Sid = "S-1-16-$rid"; Elevated = [MxcE2eTokenProbe]::IsElevated($processId) }
}

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-HashOrNull($path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } catch { 'unreadable' }
}

function Read-State {
    if (-not (Test-Path -LiteralPath $paths.State)) { Fail "no state at $($paths.State). Run: Init" }
    Get-Content -LiteralPath $paths.State -Raw | ConvertFrom-Json -AsHashtable
}

function Write-State($state) {
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $paths.State -Encoding utf8
}

function Get-OwnerOf($path) { (Get-Acl -LiteralPath $path).Owner }

# What the non-elevated user is granted by the DACL (Administrators is deny-only in that token, so it is left out).
# The owner always holds WRITE_DAC.
function Get-UserRights($path) {
    $acl = Get-Acl -LiteralPath $path
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $sids = @($me.Value, 'S-1-5-32-545', 'S-1-5-11', 'S-1-1-0')
    $rights = [Security.AccessControl.FileSystemRights]0
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $sids -contains $rule.IdentityReference.Value) { $rights = $rights -bor $rule.FileSystemRights }
    }
    $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    [pscustomobject]@{
        Write    = [bool]($rights -band [Security.AccessControl.FileSystemRights]::WriteData)
        WriteDac = ($ownerSid -eq $me.Value) -or [bool]($rights -band [Security.AccessControl.FileSystemRights]::ChangePermissions)
    }
}

function Set-OwnerToUser($path) {
    & icacls $path /setowner $userName /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "icacls /setowner failed on $path (exit $LASTEXITCODE)" }
}

function Set-OwnerToAdministrators($path) {
    & icacls $path /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "icacls /setowner failed on $path (exit $LASTEXITCODE)" }
}

# A directory made from this elevated shell inherits the drive root's ACL, where the user only reads: the
# CREATOR OWNER entry resolves to Administrators. The user needs real write access, and WRITE_DAC with it.
function Set-UserFullAccessAcl($dir) {
    & icacls $dir /inheritance:r /grant:r "${userName}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "icacls /grant failed on $dir (exit $LASTEXITCODE)" }
}

# The shape of an admin-installed tool: the user can read and run it but holds no WRITE_DAC on it.
function Set-AdminOnlyWriteAcl($dir) {
    & icacls $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "icacls /grant failed on $dir (exit $LASTEXITCODE)" }
}

function Get-RelativeHashes($dir) {
    $result = [ordered]@{}
    if (-not (Test-Path -LiteralPath $dir)) { return $result }
    Get-ChildItem -LiteralPath $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
        $result[$_.FullName.Substring($dir.Length).TrimStart('\', '/')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $result
}

function Get-AppContainerAces([string[]]$roots) {
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $items = @(Get-Item -LiteralPath $root) + @(Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue)
        foreach ($item in $items) {
            $rules = (Get-Acl -LiteralPath $item.FullName).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
            foreach ($rule in $rules) {
                $sid = $rule.IdentityReference.Value
                if ($sid -like 'S-1-15-2-*') {
                    [pscustomobject]@{
                        Path      = $item.FullName
                        Sid       = $sid
                        Explicit  = -not $rule.IsInherited
                        WellKnown = $sidWellKnownAppPackages -contains $sid
                    }
                }
            }
        }
    }
}

function Get-SandboxProcesses {
    $candidates = Get-CimInstance Win32_Process | Where-Object {
        $name = [IO.Path]::GetFileNameWithoutExtension($_.Name)
        ($sandboxHelperNames -contains $name) -or
        ($_.ExecutablePath -and ($_.ExecutablePath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)))
    }
    $candidates | ForEach-Object {
        [pscustomobject]@{ Pid = $_.ProcessId; Parent = $_.ParentProcessId; Name = $_.Name; Path = $_.ExecutablePath }
    }
}

function Get-OurPiaProcess($state) {
    $launch = $state['launch']
    if (-not $launch) { return $null }
    $proc = Get-Process -Id $launch['pid'] -ErrorAction SilentlyContinue
    if ($proc -and $proc.StartTime.ToUniversalTime().Ticks -eq [long]$launch['startTicksUtc']) { return $proc }
    $null
}

function Get-ContainerProfiles {
    $packages = Join-Path $env:LOCALAPPDATA 'Packages'
    @(Get-ChildItem -LiteralPath $packages -Directory -Filter 'pia-mcp-*' -ErrorAction SilentlyContinue | ForEach-Object Name)
}

function Get-ContainerMappings {
    $key = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings'
    if (-not (Test-Path -LiteralPath $key)) { return @() }
    @(Get-ChildItem -LiteralPath $key | ForEach-Object { (Get-ItemProperty -LiteralPath $_.PSPath).Moniker } |
        Where-Object { $_ -like 'pia-mcp-*' } | Sort-Object)
}

function Initialize-Layout {
    if (-not (Test-Elevated)) { Fail 'run from an elevated shell: the harness assigns file ownership and de-elevates the launch.' }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-UserFullAccessAcl $Root
    New-Item -ItemType Directory -Force -Path $paths.Roaming, $paths.Local, $paths.Launch | Out-Null

    # Written once and never again: roaming\settings.json carries the rotating sign-in tokens.
    $settings = Join-Path $paths.Roaming 'settings.json'
    if (-not (Test-Path -LiteralPath $settings)) {
        Copy-Item (Join-Path $repoRoot 'tests/ui-scripts/fixtures/settings.ui-test-seed.json') $settings
        Write-Host "profile:   seeded $settings (first run only)"
    }
    else {
        Write-Host "profile:   kept $settings (never rewritten)"
    }
    Set-OwnerToUser (Join-Path $Root 'profile')
    Set-OwnerToUser $paths.Launch
}

function Initialize-State {
    if (Test-Path -LiteralPath $paths.State) { return }
    Write-State @{
        root             = $Root
        exe              = $ExePath
        serverUrl        = $ServerUrl
        realProfile      = @{ settings = Get-HashOrNull $realSettings; history = Get-HashOrNull $realHistoryDb }
        baselinePackages = @(Get-ContainerProfiles)
        baselineMappings = @(Get-ContainerMappings)
        fixtureHashes    = @{}
        launch           = $null
    }
}

function Invoke-Seed {
    foreach ($dir in $paths.Workspace, $paths.Protected, $paths.Secret) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $paths.Workspace 'notes') | Out-Null

    $files = @{
        (Join-Path $paths.Workspace 'readme.txt')       = "Pia sandbox e2e workspace.`r`nThis folder is read-write for the sandboxed server.`r`n"
        (Join-Path $paths.Workspace 'notes/todo.txt')   = "1. read the readme`r`n2. write a note`r`n"
        (Join-Path $paths.Protected 'important.txt')    = "IMPORTANT: this file must survive every test.`r`n"
        (Join-Path $paths.Protected 'policy.txt')       = "Read-only reference material.`r`n"
        (Join-Path $paths.Secret 'passwords.txt')       = "admin:fixture-not-a-real-password`r`n"
    }
    foreach ($entry in $files.GetEnumerator()) {
        [IO.File]::WriteAllText($entry.Key, $entry.Value, [Text.UTF8Encoding]::new($false))
    }
    Set-OwnerToUser $paths.Fixture

    $state = Read-State
    $state['fixtureHashes'] = @{
        protected = Get-RelativeHashes $paths.Protected
        secret    = Get-RelativeHashes $paths.Secret
        workspace = Get-RelativeHashes $paths.Workspace
    }
    Write-State $state
    Write-Host "fixture:   seeded $($paths.Fixture) ($($files.Count) files, owner $(Get-OwnerOf $paths.Workspace))"
}

function Invoke-StageServer {
    if (-not (Test-Path -LiteralPath (Join-Path $ServerSource 'Pia.TestMcpServer.exe'))) {
        Fail "no Pia.TestMcpServer.exe under $ServerSource. Run: dotnet build tests/Pia.Wpf.Tests"
    }
    $serverRoot = Split-Path $paths.Server -Parent
    $adminRoot = Split-Path $paths.ServerAdmin -Parent
    foreach ($dir in $serverRoot, $adminRoot) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    Set-UserFullAccessAcl $serverRoot
    Set-AdminOnlyWriteAcl $adminRoot
    foreach ($target in $paths.Server, $paths.ServerAdmin) {
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Path (Join-Path $ServerSource '*') -Destination $target -Recurse -Force
    }
    Set-OwnerToUser $serverRoot
    Set-OwnerToAdministrators $adminRoot
    Write-Host "server:    $serverExe (owner $(Get-OwnerOf $serverExe))"
    Write-Host "server:    $serverAdminExe (owner $(Get-OwnerOf $serverAdminExe), scenario 12)"
}

function Invoke-Init {
    Initialize-Layout
    Initialize-State
    Invoke-StageServer
    Invoke-Seed
    Write-Host "`nready. Next: Launch, then sign in with the e2e account and add the server:"
    Write-Host "  command:   $serverExe"
    Write-Host "  rw:        $($paths.Workspace)"
    Write-Host "  ro:        $($paths.Protected)"
}

function Invoke-Launch {
    if (-not (Test-Elevated)) { Fail 'run from an elevated shell (the point of the launch is to drop out of it).' }
    $state = Read-State
    if (-not (Test-Path -LiteralPath $ExePath)) { Fail "app exe not found: $ExePath. Run: dotnet build -c Debug" }
    if (Get-OurPiaProcess $state) { Fail "Pia from this harness is already running (pid $($state['launch']['pid'])). Stop first." }

    $exeDir = Split-Path $ExePath -Parent
    $cmdPath = Join-Path $paths.Launch 'launch.cmd'
    $groupsFile = Join-Path $paths.Launch 'whoami-groups.csv'
    Remove-Item -LiteralPath $groupsFile -ErrorAction SilentlyContinue
    @(
        '@echo off'
        "set `"PIA_CLOUD_SERVER_URL=$ServerUrl`""
        "set `"PIA_DATA_DIR=$($paths.Roaming)`""
        "set `"PIA_LOCAL_DATA_DIR=$($paths.Local)`""
        "whoami /groups /fo csv /nh > `"$groupsFile`""
        "cd /d `"$exeDir`""
        "start `"`" `"$ExePath`""
    ) | Set-Content -LiteralPath $cmdPath -Encoding ascii

    # explorer.exe hands the file to the user's medium-IL shell, so the process tree starts de-elevated.
    $before = Get-Date
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$cmdPath`""

    $proc = $null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $proc) {
        Start-Sleep -Milliseconds 500
        $found = Get-CimInstance Win32_Process -Filter "Name = 'Pia.Wpf.exe'" |
            Where-Object { $_.ExecutablePath -and ($_.ExecutablePath -ieq $ExePath) -and ($_.CreationDate -ge $before.AddSeconds(-2)) }
        if ($found) { $proc = Get-Process -Id @($found)[0].ProcessId -ErrorAction SilentlyContinue }
    }
    if (-not $proc) { Fail 'Pia.Wpf.exe did not start within 60 s (check launch\launch.cmd and the explorer shell).' 3 }

    $il = Get-IntegrityText $proc.Id
    $state['launch'] = @{ pid = $proc.Id; startTicksUtc = $proc.StartTime.ToUniversalTime().Ticks; exe = $ExePath }
    Write-State $state

    Write-Host "launched:  pid $($proc.Id)  server $ServerUrl"
    Write-Host "integrity: $($il.Name) ($($il.Sid))  elevated=$($il.Elevated)"
    if (Test-Path -LiteralPath $groupsFile) {
        $label = (Get-Content -LiteralPath $groupsFile | Where-Object { $_ -match 'S-1-16-' }) -join ' '
        Write-Host "wrapper:   $label"
    }
    if (($il.Sid -ne 'S-1-16-8192' -or $il.Elevated) -and -not $AllowElevated) {
        Fail "Pia is not at medium IL (got $($il.Sid), elevated=$($il.Elevated)); a result from it proves nothing about the non-elevated spawn." 3
    }
}

function Invoke-Status {
    $state = Read-State
    $report = [ordered]@{}

    Write-Section 'Pia process'
    $pia = Get-OurPiaProcess $state
    if ($pia) {
        $il = Get-IntegrityText $pia.Id
        $report.pia = @{ pid = $pia.Id; integrity = $il.Sid; elevated = $il.Elevated }
        Write-Host "pid $($pia.Id)  integrity $($il.Name) ($($il.Sid))  elevated=$($il.Elevated)"
    }
    else {
        $report.pia = $null
        Write-Host 'not running'
    }

    Write-Section 'Fixture (protected and secret must be unchanged)'
    $fixtureOk = $true
    foreach ($areaEntry in @(@('protected', $paths.Protected), @('secret', $paths.Secret))) {
        $area = $areaEntry[0]
        $expected = $state['fixtureHashes'][$area]
        $actual = Get-RelativeHashes $areaEntry[1]
        foreach ($rel in $expected.Keys) {
            $verdict = if (-not $actual.Contains($rel)) { 'MISSING' } elseif ($actual[$rel] -ne $expected[$rel]) { 'CHANGED' } else { 'ok' }
            if ($verdict -ne 'ok') { $fixtureOk = $false }
            Write-Host ("{0,-8} {1}\{2}  {3}" -f $verdict, $area, $rel, $expected[$rel].Substring(0, 12))
        }
        foreach ($rel in $actual.Keys | Where-Object { -not $expected.Contains($_) }) {
            $fixtureOk = $false
            Write-Host ("EXTRA    {0}\{1}" -f $area, $rel)
        }
    }
    $expectedWorkspace = $state['fixtureHashes']['workspace']
    $actualWorkspace = Get-RelativeHashes $paths.Workspace
    foreach ($rel in $actualWorkspace.Keys) {
        $note = if (-not $expectedWorkspace.Contains($rel)) { 'new' } elseif ($actualWorkspace[$rel] -ne $expectedWorkspace[$rel]) { 'modified' } else { 'unchanged' }
        Write-Host ("info     workspace\{0}  {1}  {2}" -f $rel, $note, $actualWorkspace[$rel].Substring(0, 12))
    }
    foreach ($rel in $expectedWorkspace.Keys | Where-Object { -not $actualWorkspace.Contains($_) }) {
        Write-Host ("info     workspace\{0}  deleted" -f $rel)
    }
    $report.fixtureOk = $fixtureOk

    Write-Section 'Ownership (the user must own what the sandbox touches)'
    foreach ($entry in @(
            @('fixture', $paths.Workspace), @('fixture', $paths.Protected), @('server', $paths.Server),
            @('server-admin', $paths.ServerAdmin))) {
        if (Test-Path -LiteralPath $entry[1]) {
            $rights = Get-UserRights $entry[1]
            Write-Host ("{0,-13} {1}  owner {2}  user-write={3} user-write-dac={4}" -f $entry[0], $entry[1], (Get-OwnerOf $entry[1]), $rights.Write, $rights.WriteDac)
        }
    }

    Write-Section 'AppContainer ACEs (S-1-15-2-*; explicit ones are leaks)'
    $aces = @(Get-AppContainerAces @($paths.Fixture, $paths.Server, $paths.ServerAdmin, (Join-Path $paths.Local 'McpSandbox')))
    $leaked = @($aces | Where-Object { $_.Explicit -and -not $_.WellKnown })
    foreach ($ace in $aces) {
        $kind = if ($ace.Explicit -and -not $ace.WellKnown) { 'LEAK' } elseif ($ace.Explicit) { 'explicit-wellknown' } else { 'inherited' }
        Write-Host ("{0,-18} {1}  {2}" -f $kind, $ace.Sid, $ace.Path)
    }
    if (-not $aces) { Write-Host 'none' }
    Write-Host "leaked explicit ACEs: $($leaked.Count)"
    $report.leakedAces = $leaked.Count

    Write-Section 'Sandbox helper processes'
    $procs = @(Get-SandboxProcesses)
    foreach ($p in $procs) { Write-Host ("pid {0,-6} parent {1,-6} {2}  {3}" -f $p.Pid, $p.Parent, $p.Name, $p.Path) }
    $orphans = @(if ($pia) { @() } else { $procs })
    Write-Host "helpers: $($procs.Count)  orphans (Pia not running): $($orphans.Count)"
    $report.orphans = $orphans.Count

    Write-Section 'Container profiles (%LOCALAPPDATA%\Packages\pia-mcp-*)'
    $profiles = Get-ContainerProfiles
    $baseline = @($state['baselinePackages'])
    foreach ($name in $profiles) { Write-Host ("{0}{1}" -f $name, $(if ($baseline -contains $name) { '  (existed at Init)' } else { '  (new)' })) }
    if (-not $profiles) { Write-Host 'none' }
    $report.containerProfiles = @($profiles)

    Write-Section 'Container mappings (HKCU AppContainer\Mappings, pia-mcp-* monikers)'
    $mappings = Get-ContainerMappings
    $baselineMappings = if ($state.ContainsKey('baselineMappings')) { @($state['baselineMappings']) } else { @() }
    foreach ($name in $mappings) { Write-Host ("{0}{1}" -f $name, $(if ($baselineMappings -contains $name) { '  (existed at Init)' } else { '  (new)' })) }
    if (-not $mappings) { Write-Host 'none' }
    $report.containerMappings = @($mappings)

    if ($Json) { $report | ConvertTo-Json -Depth 6 }
}

function Invoke-Stop {
    $state = Read-State
    $pia = Get-OurPiaProcess $state
    if (-not $pia) { Write-Host 'Pia from this harness is not running.'; return }
    if ($Kill) {
        & taskkill /F /PID $pia.Id | Out-Null
        Write-Host "killed pid $($pia.Id) (ACEs from a running sandbox stay until the next start sweeps them)"
        return
    }
    # Closing the window only hides Pia to the tray (explicit shutdown), so the exit itself is the UI's job.
    Write-Host "waiting for pid $($pia.Id) to exit; end it from the UI (tray menu, AutomationId TrayMenu_Exit)..."
    if (-not $pia.WaitForExit(60000)) { Fail "pid $($pia.Id) is still running after 60 s. Use TrayMenu_Exit, or -Kill for the hard kill." 3 }
    Write-Host "pid $($pia.Id) exited gracefully"
}

function Invoke-Verify {
    $state = Read-State
    $now = @{ settings = Get-HashOrNull $realSettings; history = Get-HashOrNull $realHistoryDb }
    $otherPia = @(Get-Process -Name 'Pia.Wpf' -ErrorAction SilentlyContinue | Where-Object {
            $ours = Get-OurPiaProcess $state
            -not $ours -or $_.Id -ne $ours.Id }).Count -gt 0
    $leak = $false
    foreach ($key in 'settings', 'history') {
        if ($now[$key] -ne $state['realProfile'][$key]) {
            if ($otherPia) { Write-Host "note:  real $key changed, but another Pia is running and may own that write" -ForegroundColor Yellow }
            else { Write-Host "LEAK:  real $key changed with no other Pia running" -ForegroundColor Red; $leak = $true }
        }
    }
    if ($leak) { exit 1 }
    Write-Host 'real profile untouched'
}

function Invoke-RemoveRuntime {
    if (-not $Force) { Fail "RemoveRuntime deletes $mxcRuntimeDir (a downloaded artifact on the real local root). Pass -Force." }
    if (-not (Test-Path -LiteralPath $mxcRuntimeDir)) { Write-Host "nothing at $mxcRuntimeDir"; return }
    Remove-Item -LiteralPath $mxcRuntimeDir -Recurse -Force
    Write-Host "removed $mxcRuntimeDir"
}

switch ($Action) {
    'Init'          { Invoke-Init }
    'Seed'          { Invoke-Seed }
    'StageServer'   { Invoke-StageServer }
    'Launch'        { Invoke-Launch }
    'Status'        { Invoke-Status }
    'Stop'          { Invoke-Stop }
    'Verify'        { Invoke-Verify }
    'RemoveRuntime' { Invoke-RemoveRuntime }
}
