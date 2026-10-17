#requires -version 7
<#
.SYNOPSIS
  Writes a decrypted copy of Pia's encrypted history database, for the sqlite3-based dev scripts.

.DESCRIPTION
  history.db is encrypted with SQLite3 Multiple Ciphers under a random key that DPAPI protects for the
  current Windows user (history.db.key beside it), so only that user on that machine can run this.

  The copy holds every chat in plaintext, so it is never written inside the repository; the default is a
  fresh folder under the system temp directory. Delete it when done. The engine comes from a local build
  of Pia (src/Pia.Wpf/bin), so build once first.

.EXAMPLE
  $copy = ./scripts/Export-DecryptedHistoryDb.ps1
  ./scripts/Export-CompactionCorpus.ps1 -List -DatabasePath $copy
#>
param(
    [string]$DatabasePath,
    [string]$OutputPath,
    [string]$BuildOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $DatabasePath) {
    $root = if ($env:PIA_LOCAL_DATA_DIR) { $env:PIA_LOCAL_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'Pia' }
    $DatabasePath = Join-Path $root 'history.db'
}
$DatabasePath = (Resolve-Path -LiteralPath $DatabasePath).Path
$keyPath = "$DatabasePath.key"
if (-not (Test-Path -LiteralPath $keyPath)) { throw "No key file at $keyPath; this database was never encrypted." }

if (-not $OutputPath) {
    $OutputPath = Join-Path ([IO.Path]::GetTempPath()) ("pia-history-" + [guid]::NewGuid().ToString('N')) 'history.db'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ($OutputPath.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write a decrypted history database inside the repository."
}

if (-not $BuildOutput) {
    $BuildOutput = Get-ChildItem (Join-Path $repoRoot 'src/Pia.Wpf/bin') -Recurse -Filter 'SQLite3MC.PCLRaw.provider.dll' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object DirectoryName
    if (-not $BuildOutput) { throw "No Pia build found under src/Pia.Wpf/bin. Run 'dotnet build' first." }
}

Add-Type -AssemblyName System.Security
$protected = [IO.File]::ReadAllBytes($keyPath)
$entropy = [Text.Encoding]::UTF8.GetBytes('Pia.HistoryDb.Key')
$key = [Security.Cryptography.ProtectedData]::Unprotect($protected, $entropy, 'CurrentUser')
$password = "x'" + [Convert]::ToHexString($key) + "'"

[Runtime.InteropServices.NativeLibrary]::Load((Join-Path $BuildOutput 'runtimes/win-x64/native/sqlite3mc.dll')) | Out-Null
foreach ($name in 'SQLitePCLRaw.core', 'SQLite3MC.PCLRaw.provider', 'SQLitePCLRaw.batteries_v2', 'Microsoft.Data.Sqlite') {
    Add-Type -LiteralPath (Join-Path $BuildOutput "$name.dll")
}
[SQLitePCL.Batteries_V2]::Init()

# A copy, so a running Pia keeps its file; the WAL travels with it so the newest rows are not lost.
New-Item -ItemType Directory -Force (Split-Path -Parent $OutputPath) | Out-Null
foreach ($suffix in '', '-wal') {
    $source = "$DatabasePath$suffix"
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $in = [IO.FileStream]::new($source, 'Open', 'Read', 'ReadWrite, Delete')
    try { $out = [IO.File]::Create("$OutputPath$suffix"); try { $in.CopyTo($out) } finally { $out.Dispose() } }
    finally { $in.Dispose() }
}

$builder = [Microsoft.Data.Sqlite.SqliteConnectionStringBuilder]::new()
$builder.DataSource = $OutputPath
$builder.Password = $password
$builder.Pooling = $false
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($builder.ToString())
try {
    $connection.Open()
    foreach ($sql in 'PRAGMA wal_checkpoint(TRUNCATE);', 'PRAGMA journal_mode=DELETE;', "PRAGMA rekey = '';") {
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        [void]$command.ExecuteNonQuery()
        $command.Dispose()
    }
} finally {
    $connection.Dispose()
}

$OutputPath
