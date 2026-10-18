using System.Globalization;

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

    private const string DotnetRuntimeCommand =
        "powershell -ExecutionPolicy ByPass -c \"& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Runtime {0} -Channel {1}.0\"";

    public static RuntimeGuide GuideFor(RuntimeFamily family) => family switch
    {
        RuntimeFamily.Node => new RuntimeGuide(family, NodeCommand, NodeZipCommand),
        RuntimeFamily.Uv => new RuntimeGuide(family, UvCommand, UvWingetCommand),
        RuntimeFamily.Dotnet => new RuntimeGuide(family, DotnetCommand, null),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null),
    };

    public static RuntimeGuide GuideFor(RuntimeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (resolution.Kind == RunnerKind.DotnetDll && SharedRuntimeFor(resolution.Frameworks) is { } runtime)
        {
            var command = string.Format(CultureInfo.InvariantCulture, DotnetRuntimeCommand, runtime.Runtime, runtime.Major);
            return new RuntimeGuide(RuntimeFamily.Dotnet, command, null, runtime.Product);
        }

        return GuideFor(resolution.Install?.Family ?? RuntimeDetector.FamilyOf(resolution.Kind));
    }

    /// <returns>The shared runtime a dll asks for; null when no framework names a version, so the SDK covers it.</returns>
    public static DotnetSharedRuntime? SharedRuntimeFor(IReadOnlyList<DotnetRuntimeConfig.Framework>? frameworks)
    {
        if (frameworks is null)
            return null;

        // The ASP.NET Core and desktop runtimes each bring Microsoft.NETCore.App along.
        return Pick(frameworks, "Microsoft.AspNetCore.App", "aspnetcore", "ASP.NET Core")
            ?? Pick(frameworks, "Microsoft.WindowsDesktop.App", "windowsdesktop", ".NET Desktop")
            ?? Pick(frameworks, "Microsoft.NETCore.App", "dotnet", ".NET")
            ?? Pick(frameworks, null, "dotnet", ".NET");
    }

    private static DotnetSharedRuntime? Pick(
        IReadOnlyList<DotnetRuntimeConfig.Framework> frameworks, string? name, string runtime, string product)
    {
        foreach (var framework in frameworks)
        {
            if ((name is null || string.Equals(framework.Name, name, StringComparison.OrdinalIgnoreCase))
                && SdkVersion.MajorOf(framework.Version) is { } major)
                return new DotnetSharedRuntime(runtime, major, $"{product} {major}.0");
        }

        return null;
    }
}
