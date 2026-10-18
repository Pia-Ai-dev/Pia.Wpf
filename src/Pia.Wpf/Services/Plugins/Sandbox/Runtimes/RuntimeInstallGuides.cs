namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>User-scope installs that need no administrator; Pia looks in each target folder.</summary>
public static class RuntimeInstallGuides
{
    // --scope user is required: without it winget picks the machine MSI and UAC.
    private const string NodeCommand =
        "winget install --id OpenJS.NodeJS.LTS --exact --scope user --accept-package-agreements --accept-source-agreements";

    private const string NodeZipCommand =
        """$ProgressPreference='SilentlyContinue';$a=@{ARM64='arm64'}[$env:PROCESSOR_ARCHITECTURE];if(!$a){$a='x64'};$v=((irm https://nodejs.org/dist/index.json)|?{$_.lts})[0].version;$d="$env:LOCALAPPDATA\Programs\nodejs";$z="$env:TEMP\node-$v.zip";iwr "https://nodejs.org/dist/$v/node-$v-win-$a.zip" -OutFile $z -UseBasicParsing;New-Item $d -ItemType Directory -Force|Out-Null;tar -xf $z -C $d --strip-components=1""";

    private const string UvCommand =
        "powershell -ExecutionPolicy ByPass -c \"irm https://astral.sh/uv/install.ps1 | iex\"";

    private const string UvWingetCommand = "winget install --id astral-sh.uv -e";

    // winget has no user-scope SDK.
    private const string DotnetCommand =
        "powershell -ExecutionPolicy ByPass -c \"& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0\"";

    public static RuntimeGuide GuideFor(RuntimeFamily family) => family switch
    {
        RuntimeFamily.Node => new RuntimeGuide(family, NodeCommand, NodeZipCommand),
        RuntimeFamily.Uv => new RuntimeGuide(family, UvCommand, UvWingetCommand),
        RuntimeFamily.Dotnet => new RuntimeGuide(family, DotnetCommand, null),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };
}
