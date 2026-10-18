using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class RuntimeInstallGuidesTests
{
    [Fact]
    public void Node_IsTheUserScopeWingetInstall_WithTheZipOneLinerAsAlternative()
    {
        var guide = RuntimeInstallGuides.GuideFor(RuntimeFamily.Node);

        Assert.Equal(RuntimeFamily.Node, guide.Family);
        Assert.Equal(
            "winget install --id OpenJS.NodeJS.LTS --exact --scope user --accept-package-agreements --accept-source-agreements",
            guide.Command);
        Assert.Equal(
            """$ProgressPreference='SilentlyContinue';$a=@{ARM64='arm64'}[$env:PROCESSOR_ARCHITECTURE];if(!$a){$a='x64'};$v=((irm https://nodejs.org/dist/index.json)|?{$_.lts})[0].version;$d="$env:LOCALAPPDATA\Programs\nodejs";$z="$env:TEMP\node-$v.zip";iwr "https://nodejs.org/dist/$v/node-$v-win-$a.zip" -OutFile $z -UseBasicParsing;New-Item $d -ItemType Directory -Force|Out-Null;tar -xf $z -C $d --strip-components=1""",
            guide.AlternativeCommand);
    }

    [Fact]
    public void Uv_IsTheAstralInstaller_WithWingetAsAlternative()
    {
        var guide = RuntimeInstallGuides.GuideFor(RuntimeFamily.Uv);

        Assert.Equal(RuntimeFamily.Uv, guide.Family);
        Assert.Equal("powershell -ExecutionPolicy ByPass -c \"irm https://astral.sh/uv/install.ps1 | iex\"", guide.Command);
        Assert.Equal("winget install --id astral-sh.uv -e", guide.AlternativeCommand);
    }

    [Fact]
    public void Dotnet_IsTheDotnetInstallScriptForChannel10_WithNoAlternative()
    {
        var guide = RuntimeInstallGuides.GuideFor(RuntimeFamily.Dotnet);

        Assert.Equal(RuntimeFamily.Dotnet, guide.Family);
        Assert.Equal(
            "powershell -ExecutionPolicy ByPass -c \"& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0\"",
            guide.Command);
        Assert.Null(guide.AlternativeCommand);
    }

    [Fact]
    public void EveryFamily_HasAGuide()
    {
        foreach (var family in Enum.GetValues<RuntimeFamily>())
            Assert.False(string.IsNullOrWhiteSpace(RuntimeInstallGuides.GuideFor(family).Command));
    }

    private static RuntimeResolution MissingDll(params (string Name, string Version)[] frameworks) =>
        new(RunnerKind.DotnetDll, RuntimeOutcome.NotFound, RuntimeIssue.DotnetRuntimeMissing, null, [], null,
            [.. frameworks.Select(f => new DotnetRuntimeConfig.Framework(f.Name, f.Version))]);

    private static string RuntimeCommand(string runtime, string channel) =>
        $"powershell -ExecutionPolicy ByPass -c \"& ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Runtime {runtime} -Channel {channel}\"";

    [Theory]
    [InlineData("Microsoft.NETCore.App", "8.0.0", "dotnet", "8.0", ".NET 8.0")]
    [InlineData("Microsoft.NETCore.App", "9.0.4", "dotnet", "9.0", ".NET 9.0")]
    [InlineData("Microsoft.AspNetCore.App", "8.0.0", "aspnetcore", "8.0", "ASP.NET Core 8.0")]
    [InlineData("Microsoft.WindowsDesktop.App", "9.0.0", "windowsdesktop", "9.0", ".NET Desktop 9.0")]
    [InlineData("Contoso.Shared.App", "10.0.1", "dotnet", "10.0", ".NET 10.0")]
    public void ADll_GetsTheSharedRuntimeOfItsOwnMajor(string framework, string version, string runtime, string channel, string product)
    {
        var guide = RuntimeInstallGuides.GuideFor(MissingDll((framework, version)));

        Assert.Equal(RuntimeFamily.Dotnet, guide.Family);
        Assert.Equal(RuntimeCommand(runtime, channel), guide.Command);
        Assert.Null(guide.AlternativeCommand);
        Assert.Equal(product, guide.Product);
    }

    [Fact]
    public void ADllNeedingAspNetCore_GetsTheAspNetCoreRuntime_WhichBringsNetCoreAlong()
    {
        var guide = RuntimeInstallGuides.GuideFor(MissingDll(("Microsoft.NETCore.App", "8.0.0"), ("Microsoft.AspNetCore.App", "8.0.0")));

        Assert.Equal(RuntimeCommand("aspnetcore", "8.0"), guide.Command);
    }

    [Fact]
    public void ADllWithoutAReadableRuntimeConfig_FallsBackToTheSdk()
    {
        var sdk = RuntimeInstallGuides.GuideFor(RuntimeFamily.Dotnet);

        Assert.Equal(sdk, RuntimeInstallGuides.GuideFor(MissingDll()));
        Assert.Equal(sdk, RuntimeInstallGuides.GuideFor(MissingDll() with { Frameworks = null }));
        Assert.Equal(sdk, RuntimeInstallGuides.GuideFor(MissingDll(("Microsoft.NETCore.App", "latest"))));
        Assert.Null(sdk.Product);
    }

    [Fact]
    public void Dnx_KeepsTheSdkGuide_EvenWithFrameworks()
    {
        var dnx = new RuntimeResolution(RunnerKind.Dnx, RuntimeOutcome.NotFound, RuntimeIssue.DotnetSdkMissing, null, [], null,
            [new DotnetRuntimeConfig.Framework("Microsoft.NETCore.App", "8.0.0")]);

        Assert.Equal(RuntimeInstallGuides.GuideFor(RuntimeFamily.Dotnet), RuntimeInstallGuides.GuideFor(dnx));
    }

    [Theory]
    [InlineData(RunnerKind.Npx, RuntimeFamily.Node)]
    [InlineData(RunnerKind.NodeScript, RuntimeFamily.Node)]
    [InlineData(RunnerKind.Uvx, RuntimeFamily.Uv)]
    public void OtherRunners_GetTheirFamilysGuide(RunnerKind kind, RuntimeFamily family)
    {
        var resolution = new RuntimeResolution(kind, RuntimeOutcome.NotFound, RuntimeIssue.NodeNotFound, null, [], null);

        Assert.Equal(RuntimeInstallGuides.GuideFor(family), RuntimeInstallGuides.GuideFor(resolution));
    }
}
