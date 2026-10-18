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
}
