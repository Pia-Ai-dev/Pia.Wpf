using Microsoft.Mxc.Sdk.V1;
using Pia.Services.Plugins.Sandbox;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class MxcMcpLauncherTests
{
    private static SandboxLaunchSpec Spec(
        bool network = false,
        string? grantedCommandDirectory = @"C:\Tools\fs",
        IReadOnlyList<string>? ro = null,
        IReadOnlyDictionary<string, string>? env = null) =>
        new("pia-mcp-fs", @"C:\Tools\fs\fs.exe", ["--root", @"C:\a b"],
            env ?? new Dictionary<string, string> { ["TOKEN"] = "t" }, @"C:\box\rw",
            [@"C:\box\rw"], ro ?? [@"C:\box\ro"], network, grantedCommandDirectory);

    [Fact]
    public void Request_CarriesTheCommandLineAndTheContainerIdentity()
    {
        var request = MxcMcpLauncher.BuildRequest(Spec());

        Assert.Equal("\"C:\\Tools\\fs\\fs.exe\" --root \"C:\\a b\"", request.Command);
        Assert.Equal("pia-mcp-fs", request.ContainerName);
        Assert.Equal(@"C:\box\rw", request.WorkingDirectory);
        Assert.Null(request.TimeoutMs);
    }

    [Fact]
    public void Request_GrantsReadwriteAndReadonlyPaths_PlusTheCommandDirectoryReadOnly()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(Spec()).Filesystem!;

        Assert.Equal([@"C:\box\rw"], filesystem.ReadwritePaths);
        Assert.Equal([@"C:\box\ro", @"C:\Tools\fs"], filesystem.ReadonlyPaths);
    }

    [Fact]
    public void Request_WithNoCommandDirectoryGrant_GrantsOnlyThePolicyPaths()
    {
        var filesystem = MxcMcpLauncher.BuildRequest(Spec(grantedCommandDirectory: null)).Filesystem!;

        Assert.Equal([@"C:\box\ro"], filesystem.ReadonlyPaths);
    }

    [Theory]
    [InlineData(false, NetworkAction.Deny)]
    [InlineData(true, NetworkAction.Allow)]
    public void Request_MapsNetworkToTheEgressDefault(bool allow, NetworkAction expected)
    {
        var request = MxcMcpLauncher.BuildRequest(Spec(network: allow));

        Assert.Equal(expected, request.Network!.Egress!.Default);
    }

    [Fact]
    public void Request_KeepsTheUiOn_BecauseDotNetChildrenFailWithoutIt()
    {
        Assert.False(MxcMcpLauncher.BuildRequest(Spec()).Ui!.Disable);
    }

    [Fact]
    public void Request_LayersTheEnvironmentOnTheDefaultOne()
    {
        var request = MxcMcpLauncher.BuildRequest(Spec());

        Assert.True(request.InheritDefaultEnvironment);
        Assert.Equal("t", request.Environment!["TOKEN"]);
    }

    [Fact]
    public void Request_DoesNotLetTheCallerMutateTheSpec()
    {
        var env = new Dictionary<string, string> { ["A"] = "1" };
        var request = MxcMcpLauncher.BuildRequest(Spec(env: env));

        request.Environment!["B"] = "2";

        Assert.DoesNotContain("B", env.Keys);
    }
}
