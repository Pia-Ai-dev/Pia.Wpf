#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Harness for the live run of sandboxed local MCP servers (docs/mcp_sandbox/2026-10-17-mcp-sandbox.md) and of
  sandboxed npx, uvx and dnx servers (docs/mcp_sandbox/2026-10-18-sandbox-runtimes-plan.md).

.DESCRIPTION
  Seeds the fixture tree (workspace rw / protected ro / secret not granted), stages Pia.TestMcpServer and the
  Node and uv toolchains, launches the Debug client DE-ELEVATED against the throwaway profile and the e2e stack,
  and reports on demand: process integrity level, ACEs on the fixture, the toolchains and Pia's runtime folders,
  fixture hashes, orphan child processes and container profiles.

  Run it from an elevated shell: that is what makes the de-elevated launch meaningful and what lets it hand
  ownership of the staged files to the user. MXC (Tier 3) cannot grant a path the user cannot WRITE_DAC, and
  the server then dies on load, so everything the sandbox touches must be user-owned.

  Actions:
    Init           layout + profile (created once, never rewritten) + server staging + fixture seed
    Seed           wipe and re-create the fixture tree, record SHA-256s, give it to the user
    StageServer    copy Pia.TestMcpServer into <Root>\server (user-owned) and <Root>\server-admin (admin-owned)
    StageToolchain -Kind Node (default): the newest Node LTS zip into <Root>\toolchain\node-user (user-owned), a
                   copy at node-admin with the Node MSI's ACL (owner SYSTEM, protected DACL without
                   S-1-15-2-1; its SDDL goes into state.json), an empty user-owned node-empty, and a junction
                   node-link to node-user. -Kind Uv: the latest uv zip into <Root>\toolchain\uv
    UndoToolchainGrant  icacls node-admin /remove:g *S-1-15-2-1, then compare its SDDL with the recorded one
                   (exits 3 on a mismatch or on any S-1-15-2-1 entry left in the tree)
    Launch         start the Debug exe through explorer.exe (medium IL) and print its integrity level;
                   -RuntimeRoots <dirs> sets PIA_DEBUG_SANDBOX_RUNTIME_ROOTS (a bare name means
                   <Root>\toolchain\<name>); -Elevated starts it straight from this elevated shell against
                   <Root>\profile-elevated; -DryRun writes and prints the launch and starts nothing
    Status         integrity level, fixture verdicts, ownership, ACEs (fixture, servers, toolchain, McpSandbox
                   with its .runtime folders, McpSandboxSupport, both profiles), .runtime sizes, grant records,
                   orphans, container profiles
    EndSession     send WM_QUERYENDSESSION then WM_ENDSESSION (logoff) to Pia's own window, then check the log,
                   ACEs (the roots Status scans) and helper processes; exits 3 unless Pia exited and left
                   nothing behind
    ResetSandboxRoot  delete both profiles' local\McpSandbox (server folders and .runtime caches) so the
                   editor's "Pia creates this folder" hint shows (Pia stopped)
    Stop           wait for a graceful exit done from the UI (TrayMenu_Exit); -Kill ends Pia hard, leaving ACEs
                   for the next start to sweep
    Verify         the real profile (settings.json, history.db) is untouched; reports, never fails on, changes
                   to the real npm, NuGet and uv folders since Init
    RemoveRuntime  delete the downloaded MXC runtime on the real local root (-Force)
    TamperRuntime  flip one byte of the extracted mxc_ffi.dll so the signature check must refuse it; -Undo
                   restores the byte (Pia must be stopped: a loaded dll cannot be written)
    Baseline       take the pia-mcp-* container profiles and mappings that exist now as the "existed at Init" set,
                   and snapshot the real npm, NuGet and uv folders again
    MirrorLocal    point the Debug exe's Assets:MirrorBaseUrl at the e2e stack's storage (-Url overrides,
                   -NoMirror blanks it so downloads go upstream), through appsettings.Development.json
                   beside the exe; -Off puts the original back

  Never use https://localhost:8081: it holds the owner's real data. Never restore roaming\settings.json.
  Files an elevated Pia creates are read-only to the de-elevated user, so -Elevated never shares a profile.

.EXAMPLE
  ./Invoke-MxcSandboxE2e.ps1 Init
  ./Invoke-MxcSandboxE2e.ps1 StageToolchain
  ./Invoke-MxcSandboxE2e.ps1 Launch -RuntimeRoots node-user
  ./Invoke-MxcSandboxE2e.ps1 Status
  ./Invoke-MxcSandboxE2e.ps1 Launch -Elevated -RuntimeRoots node-admin
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Init', 'Seed', 'StageServer', 'StageToolchain', 'UndoToolchainGrant', 'Launch', 'Status', 'Stop', 'EndSession', 'ResetSandboxRoot', 'Verify', 'RemoveRuntime', 'TamperRuntime', 'MirrorLocal', 'Baseline')]
    [string]$Action,

    # Where the throwaway profile, fixture, staged servers and launch files live. Default: <repo>\artifacts\mxc-sandbox-e2e.
    [string]$Root,

    [string]$ExePath,

    [string]$ServerSource,

    [string]$ServerUrl = 'https://localhost:18081',

    [switch]$Kill,

    [switch]$Force,

    [switch]$AllowElevated,

    [switch]$Json,

    [switch]$Undo,

    [switch]$Off,

    [switch]$NoMirror,

    [string]$Url = 'http://localhost:18090/f/assets/',

    # Overrides the downloaded-runtime location (default: the real local root's Runtimes\Mxc).
    [string]$RuntimeDir,

    [ValidateSet('Node', 'Uv')]
    [string]$Kind = 'Node',

    # Folders detection looks in instead of the real ones; a bare name means <Root>\toolchain\<name>.
    [string[]]$RuntimeRoots,

    [switch]$Elevated,

    [switch]$DryRun
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
    Toolchain    = Join-Path $Root 'toolchain'
    Downloads    = Join-Path $Root 'toolchain/downloads'
    NodeUser     = Join-Path $Root 'toolchain/node-user'
    NodeAdmin    = Join-Path $Root 'toolchain/node-admin'
    NodeEmpty    = Join-Path $Root 'toolchain/node-empty'
    NodeLink     = Join-Path $Root 'toolchain/node-link'
    Uv           = Join-Path $Root 'toolchain/uv'
}
# Files an elevated Pia creates are read-only to the de-elevated user, so each launch mode keeps its own profile.
$piaProfiles = [ordered]@{
    default  = Join-Path $Root 'profile'
    elevated = Join-Path $Root 'profile-elevated'
}
$serverExe = Join-Path $paths.Server 'Pia.TestMcpServer.exe'
$serverAdminExe = Join-Path $paths.ServerAdmin 'Pia.TestMcpServer.exe'
$realSettings = Join-Path $env:APPDATA 'Pia/settings.json'
$realHistoryDb = Join-Path $env:LOCALAPPDATA 'Pia/history.db'
$mxcRuntimeDir = if ($RuntimeDir) { $RuntimeDir } else { Join-Path $env:LOCALAPPDATA 'Pia/Runtimes/Mxc' }
$tamperRecord = Join-Path $Root 'launch/tamper.json'
$devSettingsPath = Join-Path (Split-Path $ExePath -Parent) 'appsettings.Development.json'
$devSettingsBackup = "$devSettingsPath.mxc-e2e.bak"
$userName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$sidWellKnownAppPackages = @('S-1-15-2-1', 'S-1-15-2-2')
$sandboxHelperNames = @('plm', 'wxc-wslc-daemon')
$realRuntimeFolders = [ordered]@{
    npmCache      = Join-Path $env:LOCALAPPDATA 'npm-cache'
    npmGlobal     = Join-Path $env:APPDATA 'npm'
    nugetPackages = Join-Path $env:USERPROFILE '.nuget/packages'
    uvLocal       = Join-Path $env:LOCALAPPDATA 'uv'
    uvRoaming     = Join-Path $env:APPDATA 'uv'
}
$nodeIndexUrl = 'https://nodejs.org/dist/index.json'
$uvZipUrl = 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip'

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

if (-not ('MxcE2eWindowProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MxcE2eWindowProbe
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    // The top-level windows of ONE process, visible ones first. Nothing here ever addresses another process.
    public static string[] TopLevelWindows(int pid)
    {
        var found = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != pid) return true;
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            found.Add((IsWindowVisible(hwnd) ? "1" : "0") + "|" + hwnd.ToInt64() + "|" + cls);
            return true;
        }, IntPtr.Zero);
        found.Sort((a, b) => string.CompareOrdinal(b, a));
        return found.ToArray();
    }

    // Returns the window procedure's result, or -1 when the window did not answer within the timeout.
    public static long Send(long hwnd, uint msg, long wParam, long lParam, uint timeoutMs)
    {
        IntPtr result;
        var ok = SendMessageTimeout(new IntPtr(hwnd), msg, new IntPtr(wParam), new IntPtr(lParam), 0x2, timeoutMs, out result);
        return ok == IntPtr.Zero ? -1 : result.ToInt64();
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

# A contained server can plant a junction in a folder it writes, and .NET's recursive enumeration follows
# junctions, so every .NET walk here skips reparse points.
$walkOptions = [IO.EnumerationOptions]::new()
$walkOptions.RecurseSubdirectories = $true
$walkOptions.AttributesToSkip = [IO.FileAttributes]::ReparsePoint
$walkOptions.IgnoreInaccessible = $true

# Get-ChildItem lists a junction itself but does not descend into it, so the link's own ACL is still checked.
function Get-TreeItems([string]$root) {
    $top = Get-Item -LiteralPath $root -Force
    $top
    if ($top -is [IO.DirectoryInfo] -and -not ($top.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-AppContainerAces([string[]]$roots) {
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($item in Get-TreeItems $root) {
            try { $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, [Security.AccessControl.AccessControlSections]::Access) }
            catch { continue }
            foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
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

# The profile of the last launch, which still holds its logs and sandbox folders after Pia exits.
function Get-ActiveProfileName($state) {
    $launch = $state['launch']
    if ($launch -and $launch.ContainsKey('profile') -and $launch['profile']) { return $launch['profile'] }
    'default'
}

function Get-ProfileLocal([string]$name) { Join-Path $piaProfiles[$name] 'local' }

# Both profiles, whichever launched last: a leak under either fails the run. McpSandbox holds the server
# folders and their .runtime siblings.
function Get-AceRoots {
    $roots = @($paths.Fixture, $paths.Server, $paths.ServerAdmin, $paths.Toolchain)
    foreach ($name in $piaProfiles.Keys) {
        $local = Get-ProfileLocal $name
        $roots += Join-Path $local 'McpSandbox'
        $roots += Join-Path $local 'McpSandboxSupport'
    }
    $roots
}

function Get-OwnerSid($path) { (Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier]).Value }

# Owner and DACL only: icacls leaves the group alone, and SDDL names principals by SID on any locale.
function Get-OwnerAndDaclSddl($path) {
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    [IO.FileSystemAclExtensions]::GetAccessControl((Get-Item -LiteralPath $path -Force), $sections).GetSecurityDescriptorSddlForm($sections)
}

function Get-AllAppPackagesState($path) {
    $rules = @((Get-Acl -LiteralPath $path).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
            Where-Object { $_.IdentityReference.Value -eq 'S-1-15-2-1' })
    if ($rules.Count -eq 0) { 'none' } elseif (@($rules | Where-Object { -not $_.IsInherited }).Count -gt 0) { 'explicit' } else { 'inherited' }
}

function Get-EntryStats([string]$dir) {
    $entries = 0
    $bytes = [long]0
    $newest = [IO.DirectoryInfo]::new($dir).LastWriteTimeUtc.Ticks
    foreach ($item in [IO.DirectoryInfo]::new($dir).EnumerateFileSystemInfos('*', $walkOptions)) {
        $entries++
        if ($item -is [IO.FileInfo]) { $bytes += $item.Length }
        if ($item.LastWriteTimeUtc.Ticks -gt $newest) { $newest = $item.LastWriteTimeUtc.Ticks }
    }
    [pscustomobject]@{ Entries = $entries; Bytes = $bytes; NewestTicksUtc = $newest }
}

function Get-RuntimeStateFolders {
    foreach ($name in $piaProfiles.Keys) {
        $sandbox = Join-Path (Get-ProfileLocal $name) 'McpSandbox'
        if (-not (Test-Path -LiteralPath $sandbox)) { continue }
        foreach ($dir in @(Get-ChildItem -LiteralPath $sandbox -Directory -Force | Where-Object { $_.Name -like '*.runtime' })) {
            $stats = Get-EntryStats $dir.FullName
            [pscustomobject]@{ Profile = $name; Name = $dir.Name; Path = $dir.FullName; Entries = $stats.Entries; Bytes = $stats.Bytes }
        }
    }
}

function Get-GrantRecords {
    foreach ($name in $piaProfiles.Keys) {
        $dir = Join-Path (Get-ProfileLocal $name) 'McpSandboxGrants'
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $dir -File -Force -Filter '*.json')) {
            $record = try { Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable } catch { $null }
            [pscustomobject]@{
                Profile       = $name
                File          = $file.Name
                Container     = if ($record) { $record['container'] } else { '(unreadable)' }
                StateReadOnly = if ($record) { $record['stateReadOnly'] } else { $null }
                Written       = if ($record) { $record['writtenUtc'] } else { $null }
                Roots         = if ($record -and $record['roots']) { @($record['roots']) } else { @() }
            }
        }
    }
}

function Get-RealRuntimeSnapshot {
    $snapshot = [ordered]@{}
    foreach ($entry in $realRuntimeFolders.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Value)) {
            $snapshot[$entry.Key] = @{ path = $entry.Value; exists = $false; entries = 0; newestTicksUtc = 0 }
            continue
        }
        $stats = Get-EntryStats $entry.Value
        $snapshot[$entry.Key] = @{ path = $entry.Value; exists = $true; entries = $stats.Entries; newestTicksUtc = $stats.NewestTicksUtc }
    }
    $snapshot
}

# StageToolchain and UndoToolchainGrant rewrite owners and DACLs; that must only ever hit the scratch copies.
function Assert-ScratchToolchain {
    $guarded = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:windir) | Where-Object { $_ }
    foreach ($dir in $guarded) {
        $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($dir)) + '\'
        if ($paths.Toolchain.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "refusing a toolchain under $dir`: the harness rewrites owners and ACLs there. Pick a -Root of your own."
        }
    }
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
    if (Test-Path -LiteralPath $paths.State) {
        # A state from before the runtime work lacks the snapshot; never retake an existing one here.
        $state = Read-State
        if (-not $state.ContainsKey('realRuntimeFolders')) {
            $state['realRuntimeFolders'] = Get-RealRuntimeSnapshot
            Write-State $state
            Write-Host 'state:     added the snapshot of the real npm, NuGet and uv folders'
        }
        return
    }
    Write-State @{
        root               = $Root
        exe                = $ExePath
        serverUrl          = $ServerUrl
        realProfile        = @{ settings = Get-HashOrNull $realSettings; history = Get-HashOrNull $realHistoryDb }
        realRuntimeFolders = Get-RealRuntimeSnapshot
        baselinePackages   = @(Get-ContainerProfiles)
        baselineMappings   = @(Get-ContainerMappings)
        fixtureHashes      = @{}
        launch             = $null
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

# A junction is removed as a link, never followed into its target.
function Remove-ToolchainFolder([string]$dir) {
    if (-not (Test-Path -LiteralPath $dir)) { return }
    $item = Get-Item -LiteralPath $dir -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($dir, $false); return }
    Remove-Item -LiteralPath $dir -Recurse -Force
}

# Downloads stay under artifacts\: the TEMP cleaner deletes build inputs mid-run.
function Save-Download([string]$uri, [string]$target, [switch]$Fresh) {
    if (-not $Fresh -and (Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -gt 0) {
        Write-Host "download:  reusing $target"
        return
    }
    $partial = "$target.part"
    $savedProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $uri -OutFile $partial -UseBasicParsing }
    finally { $ProgressPreference = $savedProgress }
    Move-Item -LiteralPath $partial -Destination $target -Force
    Write-Host "download:  $uri"
}

function Assert-Sha256([string]$file, [string]$expected) {
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) {
        Remove-Item -LiteralPath $file -Force
        Fail "SHA-256 mismatch for $file (expected $expected, got $actual); the download was deleted." 3
    }
}

# The Windows tar (bsdtar) reads zips; a Git tar earlier on PATH does not.
function Expand-Zip([string]$zip, [string]$destination, [int]$strip = 0) {
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $tar = Join-Path $env:windir 'System32/tar.exe'
    $arguments = @('-xf', $zip, '-C', $destination)
    if ($strip -gt 0) { $arguments += "--strip-components=$strip" }
    & $tar @arguments
    if ($LASTEXITCODE -ne 0) { Fail "tar failed on $zip (exit $LASTEXITCODE)" }
}

# The Node MSI's shape: owner SYSTEM, a protected DACL with no ALL APPLICATION PACKAGES entry, so the user can
# run Node but cannot WRITE_DAC it. .NET cannot set a foreign owner without SeRestorePrivilege; icacls can.
function Set-NodeMsiAcl([string]$dir) {
    & icacls $dir /setowner '*S-1-5-18' /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "icacls /setowner failed on $dir (exit $LASTEXITCODE)" }

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($grant in @(
            @('S-1-5-32-545', 'ReadAndExecute'), @('S-1-5-11', 'ReadAndExecute'),
            @('S-1-5-32-544', 'FullControl'), @('S-1-5-18', 'FullControl'))) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($grant[0]), [Security.AccessControl.FileSystemRights]$grant[1],
                $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    # A fresh descriptor carries no owner, so only the DACL is written; Set-Acl would try to write SYSTEM back.
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($dir), $security)
}

function Get-TreeAclSummary([string]$dir) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $items = 0; $notSystem = 0; $userAces = 0; $appPackageAces = 0
    foreach ($item in Get-TreeItems $dir) {
        $items++
        $acl = [IO.FileSystemAclExtensions]::GetAccessControl($item, $sections)
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18') { $notSystem++ }
        $sids = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object { $_.IdentityReference.Value })
        if ($sids -contains $me) { $userAces++ }
        if (@($sids | Where-Object { $_ -like 'S-1-15-2-*' }).Count -gt 0) { $appPackageAces++ }
    }
    [pscustomobject]@{ Items = $items; NotSystemOwned = $notSystem; WithUserAce = $userAces; WithAppPackageAce = $appPackageAces }
}

function Invoke-StageNode($state) {
    $release = @((Invoke-RestMethod -Uri $nodeIndexUrl) | Where-Object { $_.lts -and ($_.files -contains 'win-x64-zip') })[0]
    if (-not $release) { Fail "no LTS release with a win-x64 zip in $nodeIndexUrl" }
    $version = $release.version
    $zipName = "node-$version-win-x64.zip"
    $zip = Join-Path $paths.Downloads $zipName
    Save-Download "https://nodejs.org/dist/$version/$zipName" $zip
    $sums = Invoke-RestMethod -Uri "https://nodejs.org/dist/$version/SHASUMS256.txt"
    $line = @($sums -split "`n" | Where-Object { $_ -match "\s$([regex]::Escape($zipName))\s*$" })[0]
    if (-not $line) { Fail "no checksum for $zipName in SHASUMS256.txt" }
    Assert-Sha256 $zip ($line -split '\s+')[0]

    foreach ($dir in $paths.NodeLink, $paths.NodeUser, $paths.NodeAdmin, $paths.NodeEmpty) { Remove-ToolchainFolder $dir }

    Expand-Zip $zip $paths.NodeUser 1
    if (-not (Test-Path -LiteralPath (Join-Path $paths.NodeUser 'node.exe'))) { Fail "no node.exe in $($paths.NodeUser) after extracting $zipName" }
    Set-OwnerToUser $paths.NodeUser

    Copy-Item -LiteralPath $paths.NodeUser -Destination $paths.NodeAdmin -Recurse
    Set-NodeMsiAcl $paths.NodeAdmin
    $sddl = Get-OwnerAndDaclSddl $paths.NodeAdmin
    $summary = Get-TreeAclSummary $paths.NodeAdmin
    if ($summary.NotSystemOwned -gt 0 -or $summary.WithUserAce -gt 0 -or $summary.WithAppPackageAce -gt 0) {
        Fail ("node-admin is not in the MSI's shape: of $($summary.Items) items, $($summary.NotSystemOwned) not owned by SYSTEM, " +
            "$($summary.WithUserAce) with an entry for you, $($summary.WithAppPackageAce) with an S-1-15-2-* entry") 3
    }

    New-Item -ItemType Directory -Force -Path $paths.NodeEmpty | Out-Null
    Set-OwnerToUser $paths.NodeEmpty
    New-Item -ItemType Junction -Path $paths.NodeLink -Target $paths.NodeUser | Out-Null

    $state['toolchain']['node'] = @{
        version   = $version
        user      = $paths.NodeUser
        admin     = $paths.NodeAdmin
        adminSddl = $sddl
        empty     = $paths.NodeEmpty
        link      = $paths.NodeLink
    }
    Write-Host "node:      $version  lts $($release.lts)"
    Write-Host "node-user: $($paths.NodeUser)  owner $(Get-OwnerSid $paths.NodeUser)  user-write-dac=$((Get-UserRights $paths.NodeUser).WriteDac)"
    Write-Host "node-admin: $($paths.NodeAdmin)  $($summary.Items) items owned by SYSTEM, no entry for you, no S-1-15-2-*"
    Write-Host "           sddl $sddl (recorded)"
    Write-Host "node-empty: $($paths.NodeEmpty)  (scenario 5: copy node-user's files in, then Detect again)"
    Write-Host "node-link: $($paths.NodeLink) -> $((Get-Item -LiteralPath $paths.NodeLink -Force).Target)"
}

function Invoke-StageUv($state) {
    $zip = Join-Path $paths.Downloads 'uv-x86_64-pc-windows-msvc.zip'
    # "latest" keeps one file name for every release, so a cached zip could be any version.
    Save-Download $uvZipUrl $zip -Fresh
    $sidecar = "$zip.sha256"
    Save-Download "$uvZipUrl.sha256" $sidecar -Fresh
    Assert-Sha256 $zip ((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0]

    Remove-ToolchainFolder $paths.Uv
    Expand-Zip $zip $paths.Uv
    $uvExe = Join-Path $paths.Uv 'uv.exe'
    if (-not (Test-Path -LiteralPath $uvExe)) { Fail "no uv.exe in $($paths.Uv) after extracting the zip" }
    Set-OwnerToUser $paths.Uv
    $version = (& $uvExe --version) -join ' '
    $state['toolchain']['uv'] = @{ path = $paths.Uv; version = $version }
    Write-Host "uv:        $($paths.Uv)  $version  owner $(Get-OwnerSid $paths.Uv)"
}

function Invoke-StageToolchain {
    if (-not (Test-Elevated)) { Fail 'run from an elevated shell: the harness assigns file ownership.' }
    Assert-ScratchToolchain
    $state = Read-State
    if (Get-OurPiaProcess $state) { Fail 'Pia from this harness is running. Stop it first: the toolchain may be in use or granted.' }
    New-Item -ItemType Directory -Force -Path $paths.Toolchain, $paths.Downloads | Out-Null
    if (-not $state.ContainsKey('toolchain') -or $state['toolchain'] -isnot [hashtable]) { $state['toolchain'] = @{} }
    if ($Kind -eq 'Uv') { Invoke-StageUv $state } else { Invoke-StageNode $state }
    Write-State $state
}

function Invoke-UndoToolchainGrant {
    if (-not (Test-Elevated)) { Fail 'run from an elevated shell: node-admin is writable by administrators only.' }
    Assert-ScratchToolchain
    $state = Read-State
    $node = if ($state.ContainsKey('toolchain') -and $state['toolchain'] -and $state['toolchain'].ContainsKey('node')) { $state['toolchain']['node'] } else { $null }
    if (-not $node -or -not $node['adminSddl']) { Fail 'no recorded SDDL for node-admin. Run: StageToolchain' }
    if (-not (Test-Path -LiteralPath $paths.NodeAdmin)) { Fail "no $($paths.NodeAdmin). Run: StageToolchain" }

    $before = Get-OwnerAndDaclSddl $paths.NodeAdmin
    & icacls $paths.NodeAdmin /remove:g '*S-1-15-2-1' /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "icacls /remove:g failed on $($paths.NodeAdmin) (exit $LASTEXITCODE)" }
    $after = Get-OwnerAndDaclSddl $paths.NodeAdmin
    $left = @(Get-AppContainerAces @($paths.NodeAdmin) | Where-Object { $_.Sid -eq 'S-1-15-2-1' } | ForEach-Object Path | Sort-Object -Unique)

    Write-Host "recorded:  $($node['adminSddl'])"
    Write-Host "before:    $before"
    Write-Host "after:     $after"
    Write-Host "S-1-15-2-1 entries left in the tree: $($left.Count)"
    foreach ($path in ($left | Select-Object -First 5)) { Write-Host "  $path" }
    if ($after -ne $node['adminSddl'] -or $left.Count -gt 0) {
        Write-Host 'result:    NOT restored' -ForegroundColor Red
        exit 3
    }
    Write-Host 'result:    restored (the SDDL matches the recorded one)'
}

function Get-RecordedAdminSddl($state) {
    if ($state.ContainsKey('toolchain') -and $state['toolchain'] -and $state['toolchain'].ContainsKey('node')) { return $state['toolchain']['node']['adminSddl'] }
    $null
}

# Owner SID, user rights and the S-1-15-2-1 entry are the evidence for scenarios 6, 7 and 15.
function Write-ToolchainStatus($state) {
    $rows = [ordered]@{}
    foreach ($entry in @(@('node-user', $paths.NodeUser), @('node-admin', $paths.NodeAdmin), @('node-empty', $paths.NodeEmpty), @('node-link', $paths.NodeLink), @('uv', $paths.Uv))) {
        $name = $entry[0]
        $dir = $entry[1]
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        $item = Get-Item -LiteralPath $dir -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Host ("{0,-11} junction -> {1}" -f $name, $item.Target)
            $rows[$name] = @{ junctionTarget = [string]$item.Target }
            continue
        }
        $rights = Get-UserRights $dir
        $appPackages = Get-AllAppPackagesState $dir
        $entries = (Get-EntryStats $dir).Entries
        $protected = (Get-Acl -LiteralPath $dir).AreAccessRulesProtected
        Write-Host ("{0,-11} owner {1}  protected={2}  user-write={3} user-write-dac={4}  S-1-15-2-1={5}  entries {6:N0}" -f $name, (Get-OwnerSid $dir), $protected, $rights.Write, $rights.WriteDac, $appPackages, $entries)
        $rows[$name] = @{ owner = Get-OwnerSid $dir; userWriteDac = $rights.WriteDac; allAppPackages = $appPackages; entries = $entries }
    }
    $recorded = Get-RecordedAdminSddl $state
    if ($recorded -and (Test-Path -LiteralPath $paths.NodeAdmin)) {
        $sddlMatches = (Get-OwnerAndDaclSddl $paths.NodeAdmin) -eq $recorded
        Write-Host "node-admin SDDL matches the one StageToolchain recorded: $sddlMatches"
        $rows['nodeAdminSddlMatches'] = $sddlMatches
    }
    if ($rows.Count -eq 0) { Write-Host 'not staged (run: StageToolchain, StageToolchain -Kind Uv)' }
    $rows
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
    Write-Host "runtime scenarios: StageToolchain, StageToolchain -Kind Uv, then Launch -RuntimeRoots node-user"
}

# A bare name means <Root>\toolchain\<name>, so scenarios can say node-user, node-admin, node-link or uv.
function Resolve-RuntimeRoots {
    @($RuntimeRoots | ForEach-Object { $_ -split ';' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object {
            $full = if ([IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $paths.Toolchain $_ }
            [IO.Path]::GetFullPath($full)
        })
}

# Written once and never again: roaming\settings.json carries the rotating sign-in tokens.
function Initialize-ElevatedProfile {
    $roaming = Join-Path $piaProfiles.elevated 'roaming'
    New-Item -ItemType Directory -Force -Path $roaming, (Join-Path $piaProfiles.elevated 'local') | Out-Null
    $settings = Join-Path $roaming 'settings.json'
    if (Test-Path -LiteralPath $settings) { return }
    Copy-Item (Join-Path $repoRoot 'tests/ui-scripts/fixtures/settings.ui-test-seed.json') $settings
    Write-Host "profile:   seeded $settings (first run only; sign in once more in the elevated Pia)"
}

function Start-ElevatedPia([System.Collections.Specialized.OrderedDictionary]$environment, [string]$exeDir) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($ExePath)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $exeDir
    foreach ($entry in $environment.GetEnumerator()) {
        if ($entry.Value) { $startInfo.Environment[$entry.Key] = $entry.Value }
        else { [void]$startInfo.Environment.Remove($entry.Key) }
    }
    $started = [Diagnostics.Process]::Start($startInfo)
    # A second instance hands over to a running Pia and exits at once.
    Start-Sleep -Seconds 3
    if ($started.HasExited) { Fail "Pia.Wpf.exe exited at once (code $($started.ExitCode)); is another Pia running?" 3 }
    Get-Process -Id $started.Id
}

function Start-DeElevatedPia([string]$cmdPath) {
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
    $proc
}

function Invoke-Launch {
    if (-not (Test-Elevated)) { Fail 'run from an elevated shell (the point of the launch is to drop out of it, or with -Elevated to stay in it).' }
    $state = Read-State
    if (-not (Test-Path -LiteralPath $ExePath)) { Fail "app exe not found: $ExePath. Run: dotnet build -c Debug" }
    if (Get-OurPiaProcess $state) { Fail "Pia from this harness is already running (pid $($state['launch']['pid'])). Stop first." }

    $profileName = if ($Elevated) { 'elevated' } else { 'default' }
    $runtimeRoots = @(Resolve-RuntimeRoots)
    foreach ($dir in $runtimeRoots) {
        if ($dir.Contains(';')) { Fail "a runtime root cannot contain ';': $dir" }
        Write-Host ("runtime root: {0}  {1}" -f $dir, $(if (Test-Path -LiteralPath $dir) { 'exists' } else { 'MISSING' }))
    }
    # An empty value clears a variable the shell may still carry from an earlier run.
    $environment = [ordered]@{
        PIA_CLOUD_SERVER_URL             = $ServerUrl
        PIA_DATA_DIR                     = Join-Path $piaProfiles[$profileName] 'roaming'
        PIA_LOCAL_DATA_DIR               = Get-ProfileLocal $profileName
        PIA_DEBUG_SANDBOX_RUNTIME_ROOTS  = $runtimeRoots -join ';'
    }

    $exeDir = Split-Path $ExePath -Parent
    $cmdPath = Join-Path $paths.Launch 'launch.cmd'
    $groupsFile = Join-Path $paths.Launch 'whoami-groups.csv'
    if ($Elevated) {
        Write-Host "launch:    elevated, straight from this shell, profile $($piaProfiles.elevated)"
        foreach ($entry in $environment.GetEnumerator()) { Write-Host ("env:       {0}={1}" -f $entry.Key, $(if ($entry.Value) { $entry.Value } else { '(removed)' })) }
        if ($DryRun) { Write-Host "dry run:   would start $ExePath in $exeDir; nothing started"; return }
        Initialize-ElevatedProfile
        $proc = Start-ElevatedPia $environment $exeDir
    }
    else {
        Remove-Item -LiteralPath $groupsFile -ErrorAction SilentlyContinue
        $lines = @('@echo off')
        $lines += @($environment.GetEnumerator() | ForEach-Object { "set `"$($_.Key)=$($_.Value)`"" })
        $lines += @(
            "whoami /groups /fo csv /nh > `"$groupsFile`""
            "cd /d `"$exeDir`""
            "start `"`" `"$ExePath`""
        )
        $lines | Set-Content -LiteralPath $cmdPath -Encoding ascii
        if ($DryRun) {
            Write-Host "dry run:   wrote $cmdPath; explorer.exe not started"
            Get-Content -LiteralPath $cmdPath | ForEach-Object { Write-Host "  $_" }
            return
        }
        $proc = Start-DeElevatedPia $cmdPath
    }

    $il = Get-IntegrityText $proc.Id
    $state['launch'] = @{
        pid           = $proc.Id
        startTicksUtc = $proc.StartTime.ToUniversalTime().Ticks
        exe           = $ExePath
        elevated      = [bool]$Elevated
        profile       = $profileName
        runtimeRoots  = $runtimeRoots
    }
    Write-State $state

    Write-Host "launched:  pid $($proc.Id)  server $ServerUrl  profile $profileName"
    Write-Host "integrity: $($il.Name) ($($il.Sid))  elevated=$($il.Elevated)"
    if ($Elevated) {
        Write-Host 'medium-IL check skipped for this elevated launch (scenario 7).'
        return
    }
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
    $profileName = Get-ActiveProfileName $state
    $elevatedLaunch = $profileName -eq 'elevated'
    if ($pia) {
        $il = Get-IntegrityText $pia.Id
        $report.pia = @{ pid = $pia.Id; integrity = $il.Sid; elevated = $il.Elevated; profile = $profileName }
        Write-Host "pid $($pia.Id)  integrity $($il.Name) ($($il.Sid))  elevated=$($il.Elevated)  profile $profileName"
        if ($elevatedLaunch) { Write-Host 'elevated launch: the medium-IL check is skipped for it' }
        elseif ($il.Sid -ne 'S-1-16-8192' -or $il.Elevated) { Write-Host "warning: not at medium IL; this launch proves nothing about the non-elevated spawn" -ForegroundColor Yellow }
    }
    else {
        $report.pia = $null
        Write-Host "not running (last launch: profile $profileName)"
    }
    $launch = $state['launch']
    if ($launch -and $launch.ContainsKey('runtimeRoots') -and @($launch['runtimeRoots']).Count -gt 0) {
        Write-Host "runtime roots of the last launch: $(@($launch['runtimeRoots']) -join ';')"
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

    Write-Section "Toolchain ($($paths.Toolchain))"
    $report.toolchain = Write-ToolchainStatus $state

    # A running sandbox legitimately holds explicit ACEs on its policy paths; once nothing runs they are leaks.
    $procs = @(Get-SandboxProcesses)
    $sandboxRunning = $null -ne $pia -and $procs.Count -gt 0

    Write-Section 'AppContainer ACEs (S-1-15-2-*; explicit ones are active while a sandbox runs, leaks after)'
    $aces = @(Get-AppContainerAces (Get-AceRoots))
    $explicit = @($aces | Where-Object { $_.Explicit -and -not $_.WellKnown })
    foreach ($ace in $explicit) {
        Write-Host ("{0,-8} {1}  {2}" -f $(if ($sandboxRunning) { 'active' } else { 'LEAK' }), $ace.Sid, $ace.Path)
    }
    # The two well-known SIDs are never leaks; a step 2 grant on a toolchain shows here.
    foreach ($ace in @($aces | Where-Object { $_.Explicit -and $_.WellKnown })) {
        Write-Host ("{0,-8} {1}  {2}" -f 'known', $ace.Sid, $ace.Path)
    }
    $inherited = @($aces | Where-Object { -not $_.Explicit -and -not $_.WellKnown })
    if ($inherited.Count -gt 0) { Write-Host "inherited: $($inherited.Count) item(s) below the explicit ones carry the same SID" }
    if (-not $aces) { Write-Host 'none' }
    $leakedCount = if ($sandboxRunning) { 0 } else { $explicit.Count }
    Write-Host "explicit ACEs: $($explicit.Count)  leaked: $leakedCount"
    $report.leakedAces = $leakedCount
    $report.activeAces = if ($sandboxRunning) { $explicit.Count } else { 0 }

    Write-Section 'Runtime state folders (<profile>\local\McpSandbox\*.runtime)'
    $stateFolders = @(Get-RuntimeStateFolders)
    foreach ($folder in $stateFolders) {
        Write-Host ("{0,-9} {1}  entries {2:N0}  size {3:N1} MiB" -f $folder.Profile, $folder.Name, $folder.Entries, ($folder.Bytes / 1MB))
    }
    if ($stateFolders.Count -eq 0) { Write-Host 'none' }
    $report.runtimeStateFolders = @($stateFolders | ForEach-Object { @{ profile = $_.Profile; name = $_.Name; entries = $_.Entries; bytes = $_.Bytes } })

    Write-Section 'Grant records (<profile>\local\McpSandboxGrants)'
    $records = @(Get-GrantRecords)
    foreach ($record in $records) {
        Write-Host ("{0,-9} {1}  stateReadOnly={2}  written {3}  {4} root(s)" -f $record.Profile, $record.Container, $record.StateReadOnly, $record.Written, $record.Roots.Count)
        foreach ($root in $record.Roots) { Write-Host "            $root" }
    }
    if ($records.Count -eq 0) { Write-Host 'none' }
    $report.grantRecords = @($records | ForEach-Object { @{ profile = $_.Profile; container = $_.Container; stateReadOnly = $_.StateReadOnly; roots = $_.Roots } })

    Write-Section 'Sandbox helper processes'
    foreach ($p in $procs) { Write-Host ("pid {0,-6} parent {1,-6} {2}  {3}" -f $p.Pid, $p.Parent, $p.Name, $p.Path) }
    $orphans = @(if ($pia) { @() } else { $procs })
    Write-Host "helpers: $($procs.Count)  orphans (Pia not running): $($orphans.Count)"
    $report.orphans = $orphans.Count

    Write-Section 'Container profiles (%LOCALAPPDATA%\Packages\pia-mcp-*)'
    $profiles = Get-ContainerProfiles
    $baseline = @($state['baselinePackages'])
    foreach ($name in $profiles) { Write-Host ("{0}  {1}" -f $name, (Get-ContainerLabel $name $baseline)) }
    if (-not $profiles) { Write-Host 'none' }
    $report.containerProfiles = @($profiles)

    Write-Section 'Container mappings (HKCU AppContainer\Mappings, pia-mcp-* monikers)'
    $mappings = Get-ContainerMappings
    $baselineMappings = if ($state.ContainsKey('baselineMappings')) { @($state['baselineMappings']) } else { @() }
    foreach ($name in $mappings) { Write-Host ("{0}  {1}" -f $name, (Get-ContainerLabel $name $baselineMappings)) }
    if (-not $mappings) { Write-Host 'none' }
    $report.containerMappings = @($mappings)

    Write-LeftoverWarnings $state

    if ($Json) { $report | ConvertTo-Json -Depth 6 }
}

# The logoff path: what Windows sends every top-level window when a user signs out with Pia running. Sent to
# Pia's own window only, by process id, never broadcast.
function Invoke-EndSession {
    $state = Read-State
    $pia = Get-OurPiaProcess $state
    if (-not $pia) { Fail 'Pia from this harness is not running.' }

    # Windows asks every top-level window of a process, and WPF listens on its own hidden HwndWrapper windows,
    # not only on the visible one. Only windows of this one process are addressed, by process id.
    $windows = @([MxcE2eWindowProbe]::TopLevelWindows($pia.Id) | Where-Object { $_.Split('|')[2] -like 'HwndWrapper[[]*' })
    if ($windows.Count -eq 0) { Fail "pid $($pia.Id) has no WPF top-level window to send the session messages to." 3 }
    $targets = @($windows | ForEach-Object { $parts = $_.Split('|'); [pscustomobject]@{ Hwnd = [long]$parts[1]; Class = $parts[2]; Visible = $parts[0] -eq '1' } })
    Write-Host "targets:   pid $($pia.Id), $($targets.Count) window(s) of its own: $(($targets | ForEach-Object { '0x' + $_.Hwnd.ToString('x') }) -join ', ')"
    $helpersBefore = @(Get-SandboxProcesses).Count
    Write-Host "helpers:   $helpersBefore running before"
    $logDir = Join-Path (Get-ProfileLocal (Get-ActiveProfileName $state)) 'Logs'

    $logoff = [long]2147483648
    $sentAt = Get-Date
    # One window at a time, as the system does, and no further once Pia has gone: its SessionEnding handler runs
    # during the query, so a window asked after that point answers nothing useful.
    $shutdownLogged = {
        $current = Get-ChildItem $logDir -Filter 'pia-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $current) { return $false }
        $null -ne (Get-Content -LiteralPath $current.FullName | Where-Object {
                $parsed = [datetimeoffset]::MinValue
                [datetimeoffset]::TryParse(($_ -split "`t", 2)[0], [ref]$parsed) -and $parsed.LocalDateTime -ge $sentAt -and $_ -match 'PluginShutdownService'
            } | Select-Object -First 1)
    }
    $queries = New-Object System.Collections.Generic.List[long]
    $shutdownStarted = $false
    foreach ($target in $targets) {
        $answer = [MxcE2eWindowProbe]::Send($target.Hwnd, 0x11, 0, $logoff, 10000)
        $queries.Add($answer)
        if ($answer -ne 0 -and $answer -ne -1) { continue }

        # A window that answers nothing is only a refusal while Pia is not already shutting down: its
        # SessionEnding handler runs during the query and tears the windows down behind it.
        Start-Sleep -Milliseconds 800
        if ($pia.HasExited -or (& $shutdownLogged)) { $shutdownStarted = $true; break }
        Write-Host "WM_QUERYENDSESSION (logoff) -> $($queries -join ',')"
        Fail "Pia refused the session ending (window 0x$($target.Hwnd.ToString('x')) answered $answer). WM_ENDSESSION was not sent." 3
    }
    if (-not $shutdownStarted) { $shutdownStarted = $pia.HasExited -or (& $shutdownLogged) }
    Write-Host "WM_QUERYENDSESSION (logoff) -> $($queries -join ',')  (Pia began shutting down during the query: $shutdownStarted)"
    if (-not $shutdownStarted) {
        $ends = @($targets | ForEach-Object { [MxcE2eWindowProbe]::Send($_.Hwnd, 0x16, 1, $logoff, 10000) })
        Write-Host "WM_ENDSESSION (TRUE, logoff) -> $($ends -join ',')"
    }

    $exited = $pia.WaitForExit(30000)
    $took = [int]((Get-Date) - $sentAt).TotalMilliseconds
    Write-Host "process:   exited=$exited after $took ms"

    Write-Host 'log lines since the message (shutdown and container disposal):'
    $log = Get-ChildItem $logDir -Filter 'pia-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        $lines = Get-Content -LiteralPath $log.FullName | Where-Object {
            $stamp = ($_ -split "`t", 2)[0]
            $parsed = [datetimeoffset]::MinValue
            [datetimeoffset]::TryParse($stamp, [ref]$parsed) -and $parsed.LocalDateTime -ge $sentAt -and
                $_ -match 'pia-mcp-|Exited|exit code|ispos|hutdown|hutting|stopp|Removed|Application is'
        }
        foreach ($line in ($lines | Select-Object -Last 12)) { Write-Host ("  " + ($line.Length -gt 200 ? $line.Substring(0, 200) : $line)) }
        if (-not $lines) { Write-Host '  (none)' }
    }

    $explicit = @(Get-AppContainerAces (Get-AceRoots) | Where-Object { $_.Explicit -and -not $_.WellKnown })
    $helpersAfter = @(Get-SandboxProcesses).Count
    Write-Host "after:     explicit S-1-15-2-* ACEs=$($explicit.Count)  helper processes=$helpersAfter"
    foreach ($ace in $explicit) { Write-Host "  ACE on $($ace.Path)" }

    if ($exited -and $explicit.Count -eq 0 -and $helpersAfter -eq 0) { Write-Host 'result:    clean'; return }
    Write-Host 'result:    NOT clean' -ForegroundColor Red
    exit 3
}

function Invoke-ResetSandboxRoot {
    $state = Read-State
    if (Get-OurPiaProcess $state) { Fail 'Pia from this harness is running. Stop it first: its sandbox folders are in use.' }
    foreach ($name in $piaProfiles.Keys) {
        $root = Join-Path (Get-ProfileLocal $name) 'McpSandbox'
        if (-not (Test-Path -LiteralPath $root)) { Write-Host "nothing at $root"; continue }
        $runtimeFolders = @(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object { $_.Name -like '*.runtime' }).Count
        Remove-Item -LiteralPath $root -Recurse -Force
        Write-Host "removed $root, $runtimeFolders .runtime cache(s) with it (the next start or probe creates a server's folder again, so the editor's hint shows first)"
    }
    Write-Host 'McpSandboxGrants is kept: the records name the folders to sweep, and Pia deletes them with the server.'
}

function Invoke-Stop {
    $state = Read-State
    $pia = Get-OurPiaProcess $state
    Write-LeftoverWarnings $state
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
    Write-RealRuntimeComparison $state
    Write-LeftoverWarnings $state
    if ($leak) { exit 1 }
    Write-Host 'real profile untouched'
}

# A report, never a verdict: npm, NuGet and uv on this host write these folders for other work too.
function Write-RealRuntimeComparison($state) {
    if (-not $state.ContainsKey('realRuntimeFolders') -or -not $state['realRuntimeFolders']) {
        Write-Host 'note:  no snapshot of the real npm, NuGet and uv folders. Run: Init (adds it) or Baseline (retakes it)' -ForegroundColor Yellow
        return
    }
    $then = $state['realRuntimeFolders']
    $now = Get-RealRuntimeSnapshot
    foreach ($key in $now.Keys) {
        $current = $now[$key]
        if (-not $then.ContainsKey($key)) { Write-Host ("note:  {0,-13} {1}  not in the snapshot" -f $key, $current['path']); continue }
        $before = $then[$key]
        $verdict = if ([bool]$before['exists'] -ne [bool]$current['exists']) {
            if ($current['exists']) { 'CREATED since the snapshot' } else { 'DELETED since the snapshot' }
        }
        elseif ([long]$before['entries'] -ne [long]$current['entries'] -or [long]$before['newestTicksUtc'] -ne [long]$current['newestTicksUtc']) {
            "CHANGED: entries $($before['entries']) -> $($current['entries']), newest write $(Format-Ticks $before['newestTicksUtc']) -> $(Format-Ticks $current['newestTicksUtc'])"
        }
        else { $null }
        if ($verdict) { Write-Host ("look:  {0,-13} {1}  {2} (other tools write here too)" -f $key, $current['path'], $verdict) -ForegroundColor Yellow }
        elseif (-not $current['exists']) { Write-Host ("ok:    {0,-13} {1}  absent, as at the snapshot" -f $key, $current['path']) }
        else { Write-Host ("ok:    {0,-13} {1}  unchanged ({2} entries)" -f $key, $current['path'], $current['entries']) }
    }
}

function Format-Ticks($ticks) {
    if (-not $ticks -or [long]$ticks -le 0) { return '-' }
    [datetime]::new([long]$ticks, [DateTimeKind]::Utc).ToString('yyyy-MM-dd HH:mm:ss') + 'Z'
}

function Invoke-RemoveRuntime {
    if (-not $Force) { Fail "RemoveRuntime deletes $mxcRuntimeDir (a downloaded artifact on the real local root). Pass -Force." }
    Remove-Item -LiteralPath $tamperRecord -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $mxcRuntimeDir)) { Write-Host "nothing at $mxcRuntimeDir"; return }
    Remove-Item -LiteralPath $mxcRuntimeDir -Recurse -Force
    Write-Host "removed $mxcRuntimeDir"
}

function Invoke-TamperRuntime {
    $match = @(Get-ChildItem -LiteralPath $mxcRuntimeDir -Recurse -Filter 'mxc_ffi.dll' -ErrorAction SilentlyContinue)
    if ($Undo) {
        if (-not (Test-Path -LiteralPath $tamperRecord)) { Write-Host 'nothing tampered'; return }
        $record = Get-Content -LiteralPath $tamperRecord -Raw | ConvertFrom-Json
        # A reinstall replaces the file, so flipping the byte again would tamper it instead of restoring it.
        if ((Test-Path -LiteralPath $record.path) -and (Get-AuthenticodeSignature -LiteralPath $record.path).Status -eq 'Valid') {
            Remove-Item -LiteralPath $tamperRecord
            Write-Host 'the runtime verifies again (it was reinstalled); cleared the stale tamper record'
            return
        }
        $stream = [IO.File]::Open($record.path, 'Open', 'ReadWrite', 'None')
        try { $stream.Position = $record.offset; $stream.WriteByte([byte]$record.original) } finally { $stream.Dispose() }
        Remove-Item -LiteralPath $tamperRecord
        Write-Host "restored byte at offset $($record.offset) of $($record.path)"
        return
    }
    if ($match.Count -eq 0) { Fail "no mxc_ffi.dll under $mxcRuntimeDir. Download the runtime first (scenario 0)." }
    if (Test-Path -LiteralPath $tamperRecord) { Fail 'the runtime is already tampered. Use -Undo or RemoveRuntime -Force.' }
    $path = $match[0].FullName
    try { $stream = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None') }
    catch { Fail "cannot open $path for writing ($($_.Exception.Message)). Stop Pia first: a loaded dll is locked." }
    try {
        # A quarter in lands inside .text, away from the PE header checksum and the certificate table.
        $offset = [long]($stream.Length / 4)
        $stream.Position = $offset
        $original = $stream.ReadByte()
        $stream.Position = $offset
        $stream.WriteByte([byte]($original -bxor 0xFF))
    }
    finally { $stream.Dispose() }
    @{ path = $path; offset = $offset; original = $original } | ConvertTo-Json | Set-Content -LiteralPath $tamperRecord -Encoding utf8
    Write-Host "flipped byte at offset $offset of $path (was 0x$($original.ToString('x2')))"
    $sig = (Get-AuthenticodeSignature -LiteralPath $path).Status
    Write-Host "Authenticode status now: $sig (must not be Valid)"
}

function Invoke-MirrorLocal {
    $state = Read-State
    if ($Off) {
        if (-not $state.ContainsKey('mirror') -or -not $state['mirror']) { Write-Host 'no mirror override active'; return }
        $mirror = $state['mirror']
        if ($mirror['hadOriginal']) { Move-Item -LiteralPath $devSettingsBackup -Destination $devSettingsPath -Force }
        else { Remove-Item -LiteralPath $devSettingsPath -ErrorAction SilentlyContinue }
        $state['mirror'] = $null
        Write-State $state
        Write-Host "mirror override removed from $devSettingsPath$(if ($mirror['hadOriginal']) { ' (original restored)' })"
        return
    }

    $mirrorUrl = if ($NoMirror) { '' } else { $Url }
    if ($mirrorUrl -match 'pia-ai\.de') { Fail 'refusing storage.pia-ai.de: that mirror is the owner''s. Use the e2e stack''s storage (localhost:18090).' }
    $active = $state.ContainsKey('mirror') -and $state['mirror']
    $original = @{}
    $hadOriginal = $false
    if ($active) { $hadOriginal = [bool]$state['mirror']['hadOriginal'] }
    if (Test-Path -LiteralPath $devSettingsPath) {
        if (-not $active) {
            Copy-Item -LiteralPath $devSettingsPath -Destination $devSettingsBackup -Force
            $hadOriginal = $true
        }
        $original = Get-Content -LiteralPath $devSettingsPath -Raw | ConvertFrom-Json -AsHashtable
    }
    if (-not $original.ContainsKey('Assets') -or $original['Assets'] -isnot [hashtable]) { $original['Assets'] = @{} }
    $original['Assets']['MirrorBaseUrl'] = $mirrorUrl
    $original | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $devSettingsPath -Encoding utf8
    $state['mirror'] = @{ path = $devSettingsPath; hadOriginal = $hadOriginal }
    Write-State $state
    $shown = if ($mirrorUrl) { $mirrorUrl } else { '(blank: downloads go upstream)' }
    Write-Host "mirror:    $shown  via $devSettingsPath"
    if ($mirrorUrl) {
        $probe = $mirrorUrl.TrimEnd('/') + '/runtimes/microsoft.mxc.sdk.1.0.1.nupkg'
        try { $status = [int](Invoke-WebRequest -Uri $probe -Method Head -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode }
        catch { $status = "unreachable ($($_.Exception.Message))" }
        Write-Host "mirror object: HTTP $status  ($probe)"
    }
    Write-Host 'applies to the next Launch (appsettings is read once at startup).'
}

# The real-container unit tests name theirs pia-mcp-test-*; those are not the app's.
function Get-ContainerLabel([string]$name, $baseline) {
    if ($baseline -contains $name) { return '(existed at Init)' }
    if ($name -like 'pia-mcp-test*' -or $name -like 'pia-mcp-t1-*') { return '(known test artefact)' }
    '(new)'
}

function Invoke-Baseline {
    $state = Read-State
    $state['baselinePackages'] = @(Get-ContainerProfiles)
    $state['baselineMappings'] = @(Get-ContainerMappings)
    $state['realRuntimeFolders'] = Get-RealRuntimeSnapshot
    Write-State $state
    Write-Host "baseline:  $($state['baselinePackages'].Count) container profile(s), $($state['baselineMappings'].Count) mapping(s), real npm/NuGet/uv folders snapshotted"
}

function Write-LeftoverWarnings($state) {
    if ($state.ContainsKey('mirror') -and $state['mirror']) {
        Write-Host "warning: Assets:MirrorBaseUrl is still overridden in $devSettingsPath. Run: MirrorLocal -Off" -ForegroundColor Yellow
    }
    if (Test-Path -LiteralPath $tamperRecord) {
        $record = Get-Content -LiteralPath $tamperRecord -Raw | ConvertFrom-Json
        $stillBroken = -not (Test-Path -LiteralPath $record.path) -or (Get-AuthenticodeSignature -LiteralPath $record.path).Status -ne 'Valid'
        $text = if ($stillBroken) { 'the MXC runtime is still tampered' } else { 'a tamper record is left over, but the runtime verifies again (it was reinstalled)' }
        Write-Host "warning: $text. Run: TamperRuntime -Undo, or RemoveRuntime -Force" -ForegroundColor Yellow
    }
    if ((Test-Path -LiteralPath $paths.NodeAdmin) -and (Get-AllAppPackagesState $paths.NodeAdmin) -ne 'none') {
        Write-Host "warning: $($paths.NodeAdmin) still carries the step 2 grant (S-1-15-2-1). Run: UndoToolchainGrant" -ForegroundColor Yellow
    }
}

switch ($Action) {
    'Init'          { Invoke-Init }
    'Seed'          { Invoke-Seed }
    'StageServer'   { Invoke-StageServer }
    'StageToolchain' { Invoke-StageToolchain }
    'UndoToolchainGrant' { Invoke-UndoToolchainGrant }
    'Launch'        { Invoke-Launch }
    'Status'        { Invoke-Status }
    'Stop'          { Invoke-Stop }
    'EndSession'    { Invoke-EndSession }
    'ResetSandboxRoot' { Invoke-ResetSandboxRoot }
    'Verify'        { Invoke-Verify }
    'RemoveRuntime' { Invoke-RemoveRuntime }
    'TamperRuntime' { Invoke-TamperRuntime }
    'MirrorLocal'   { Invoke-MirrorLocal }
    'Baseline'      { Invoke-Baseline }
}
