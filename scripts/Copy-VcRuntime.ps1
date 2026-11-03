#requires -version 7
<#
.SYNOPSIS
  Copies the Visual C++ runtime DLLs ONNX Runtime imports into a publish directory.

.DESCRIPTION
  A clean Windows lacks these DLLs. Shipped next to Pia.Wpf.exe, they reach both MSIs and every update.

  Pinned to Microsoft.VC143.CRT (VS 2022): VC145 (VS 2026) needs a newer Windows than Pia's 10.0.17763.
  When ONNX Runtime or sherpa move to a 14.5x toolset, the raised -MinimumVersion fails here; re-check
  the OS floor then.

.PARAMETER Destination
  The publish directory that holds Pia.Wpf.exe.

.PARAMETER MinimumVersion
  The MSVC version ONNX Runtime and sherpa-onnx were linked with. Every copied DLL must be at least this.
#>
param(
    [Parameter(Mandatory)] [string]$Destination,
    [version]$MinimumVersion = '14.44'
)

$ErrorActionPreference = 'Stop'

$runtimeFiles = 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'
$component = 'the component Microsoft.VisualStudio.Component.VC.Redist.14.Latest ("C++ 2022 Redistributable Update")'

function Fail([string]$message) {
    Write-Error $message -ErrorAction Continue
    exit 1
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Fail "vswhere.exe not found at $vswhere. Install Visual Studio 2022 with $component."
}

$found = & $vswhere -all -products * -find 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT\vcruntime140_1.dll'
$candidates = @($found | Where-Object { $_ } | ForEach-Object {
    $crt = Split-Path $_ -Parent
    $msvc = Split-Path (Split-Path $crt -Parent) -Parent
    [pscustomobject]@{ Folder = $crt; Version = [version](Split-Path $msvc -Leaf) }
})
if ($candidates.Count -eq 0) {
    Fail "No Visual C++ x64 runtime under any Visual Studio's VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT. Add $component to Visual Studio 2022."
}

$source = ($candidates | Sort-Object Version -Descending | Select-Object -First 1).Folder
"VC++ runtime source: $source"

New-Item -ItemType Directory -Force $Destination | Out-Null
foreach ($name in $runtimeFiles) {
    $file = Join-Path $source $name
    if (-not (Test-Path $file)) {
        Fail "$name is missing from $source."
    }

    $info = (Get-Item $file).VersionInfo
    $version = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    if ($version -lt $MinimumVersion) {
        Fail "$name is $version, below the $MinimumVersion floor ONNX Runtime was linked with. Update Visual Studio 2022."
    }

    # Same signer parse as the release workflow's package check, so the two agree.
    $signature = Get-AuthenticodeSignature $file
    $org = if ($signature.SignerCertificate) {
        [regex]::Match($signature.SignerCertificate.Subject, '(?:^|,\s*)O=("[^"]*"|[^,]*)').Groups[1].Value.Trim('"')
    }
    if ($signature.Status -ne 'Valid' -or $org -ne 'Microsoft Corporation') {
        Fail "$name is not validly signed by Microsoft Corporation: $($signature.Status), signer O=$org."
    }

    Copy-Item $file -Destination $Destination -Force
    "  $name $version"
}
