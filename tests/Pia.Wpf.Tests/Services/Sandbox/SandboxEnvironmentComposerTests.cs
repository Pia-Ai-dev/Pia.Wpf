using Pia.Services.Plugins.Sandbox.Runtimes;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxEnvironmentComposerTests
{
    private static readonly IReadOnlyDictionary<string, string> NoPlan = new Dictionary<string, string>();

    [Theory]
    [InlineData("Path")]
    [InlineData("PATH")]
    [InlineData("TEMP")]
    [InlineData("tmp")]
    [InlineData("OneDrive")]
    [InlineData("OneDriveCommercial")]
    [InlineData("onedriveconsumer")]
    public void TheAllowlist_IsNeverBlanked(string name)
    {
        var environment = SandboxEnvironmentComposer.Compose([name], NoPlan, null);

        Assert.Empty(environment);
    }

    [Fact]
    public void EveryOtherUserVariable_IsBlanked_Once()
    {
        var environment = SandboxEnvironmentComposer.Compose(["GH_TOKEN", "gh_token", "AZURE_DEVOPS_PAT", "TEMP", ""], NoPlan, null);

        Assert.Equal(2, environment.Count);
        Assert.Equal("", environment["GH_TOKEN"]);
        Assert.Equal("", environment["azure_devops_pat"]);
    }

    [Fact]
    public void ThePlan_OverridesABlank_AndTheUser_OverridesThePlan()
    {
        var plan = new Dictionary<string, string> { ["APPDATA"] = @"C:\state\home\AppData\Roaming", ["DOTNET_NOLOGO"] = "1" };
        var user = new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "0", ["API_KEY"] = "mine" };

        var environment = SandboxEnvironmentComposer.Compose(["APPDATA", "API_KEY", "GH_TOKEN"], plan, user);

        Assert.Equal(@"C:\state\home\AppData\Roaming", environment["APPDATA"]);
        Assert.Equal("0", environment["DOTNET_NOLOGO"]);
        Assert.Equal("mine", environment["API_KEY"]);
        Assert.Equal("", environment["GH_TOKEN"]);
    }

    [Fact]
    public void PathAndNodeOptions_FromTheUser_AreSkipped_WhenThePlanAlreadyMergedThem()
    {
        var plan = new Dictionary<string, string> { ["PATH"] = @"C:\node;C:\mine", ["NODE_OPTIONS"] = "--require x --max-old-space-size=512" };
        var user = new Dictionary<string, string> { ["Path"] = @"C:\mine", ["node_options"] = "--max-old-space-size=512" };

        var environment = SandboxEnvironmentComposer.Compose([], plan, user);

        Assert.Equal(@"C:\node;C:\mine", environment["PATH"]);
        Assert.Equal("--require x --max-old-space-size=512", environment["NODE_OPTIONS"]);
    }

    [Fact]
    public void PathAndNodeOptions_FromTheUser_Apply_WhenThePlanSetNeither()
    {
        var user = new Dictionary<string, string> { ["PATH"] = @"C:\mine", ["NODE_OPTIONS"] = "--inspect" };

        var environment = SandboxEnvironmentComposer.Compose([], NoPlan, user);

        Assert.Equal(@"C:\mine", environment["PATH"]);
        Assert.Equal("--inspect", environment["NODE_OPTIONS"]);
    }

    [Fact]
    public void TheUser_CanNeverSetTheRuntimeDirectory()
    {
        var user = new Dictionary<string, string> { ["mxc_ffi_dir"] = @"C:\evil" };

        var environment = SandboxEnvironmentComposer.Compose([], NoPlan, user);

        Assert.Empty(environment);
    }

    [Fact]
    public void TheResult_IgnoresCase()
    {
        var environment = SandboxEnvironmentComposer.Compose(["GH_TOKEN"], NoPlan, null);

        Assert.True(environment.ContainsKey("gh_token"));
    }
}
