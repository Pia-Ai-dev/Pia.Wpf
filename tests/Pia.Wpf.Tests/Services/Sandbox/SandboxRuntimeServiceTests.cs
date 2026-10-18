using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxRuntimeServiceTests
{
    private const string NodeRoot = @"C:\Program Files\nodejs";

    private readonly IRuntimeDetector _detector = Substitute.For<IRuntimeDetector>();
    private readonly IToolchainAclService _acl = Substitute.For<IToolchainAclService>();
    private readonly IElevationService _elevation = Substitute.For<IElevationService>();
    private readonly FakeRuntimeHost _host = new();

    private SandboxRuntimeService Service() =>
        new(_detector, _acl, _elevation, _host, NullLogger<SandboxRuntimeService>.Instance);

    private static RuntimeResolution Resolution(RunnerKind kind) =>
        new(kind, RuntimeOutcome.Ready, RuntimeIssue.None, null, [], null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_QuotesTheFolderInBothCommands_AndReportsAnExistingEntry(bool existing)
    {
        _acl.HasAllAppPackagesAce(NodeRoot).Returns(existing);

        var preview = Service().PreviewMachineWideGrant(NodeRoot);

        Assert.Equal(NodeRoot, preview.Root);
        Assert.Equal(@"icacls ""C:\Program Files\nodejs"" /grant *S-1-15-2-1:(OI)(CI)(RX)", preview.GrantCommand);
        Assert.Equal(@"icacls ""C:\Program Files\nodejs"" /remove:g *S-1-15-2-1", preview.RemoveCommand);
        Assert.Equal(existing, preview.HasExistingAce);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanGrantMachineWide_MirrorsElevation(bool elevated)
    {
        _elevation.IsElevated.Returns(elevated);

        Assert.Equal(elevated, Service().CanGrantMachineWide);
    }

    [Fact]
    public async Task Grant_WhenNotElevated_IsRefused()
    {
        _elevation.IsElevated.Returns(false);

        var result = await Service().GrantMachineWideReadAsync(NodeRoot, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        _acl.DidNotReceive().GrantAllAppPackagesRead(Arg.Any<string>());
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\")]
    [InlineData(@"C:\windows\System32\nodejs")]
    public async Task Grant_UnderTheWindowsFolder_IsRefused(string root)
    {
        _elevation.IsElevated.Returns(true);

        var result = await Service().GrantMachineWideReadAsync(root, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        _acl.DidNotReceive().GrantAllAppPackagesRead(Arg.Any<string>());
    }

    [Fact]
    public async Task Grant_WithAnExistingEntry_IsRefused()
    {
        _elevation.IsElevated.Returns(true);
        _acl.HasAllAppPackagesAce(NodeRoot).Returns(true);

        var result = await Service().GrantMachineWideReadAsync(NodeRoot, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        _acl.DidNotReceive().GrantAllAppPackagesRead(Arg.Any<string>());
    }

    [Theory]
    [InlineData(NodeRoot)]
    [InlineData(@"C:\Windows Tools\nodejs")]
    public async Task Grant_WhenElevatedOutsideWindowsWithoutAnEntry_Runs(string root)
    {
        _elevation.IsElevated.Returns(true);

        var result = await Service().GrantMachineWideReadAsync(root, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        _acl.Received(1).GrantAllAppPackagesRead(root);
    }

    [Fact]
    public async Task Grant_ThatThrows_ReturnsTheExceptionMessage()
    {
        _elevation.IsElevated.Returns(true);
        _acl.When(acl => acl.GrantAllAppPackagesRead(NodeRoot))
            .Do(_ => throw new UnauthorizedAccessException("Access is denied."));

        var result = await Service().GrantMachineWideReadAsync(NodeRoot, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Access is denied.", result.Error);
    }

    [Fact]
    public async Task Detect_ForAPlainExe_ReturnsTheParseWithoutDetecting()
    {
        var detection = await Service().DetectAsync(@"C:\Tools\fs\fs.exe", ["--stdio"], null, "fs", CancellationToken.None);

        Assert.Equal(RunnerKind.None, detection.Parse.Kind);
        Assert.Equal(RunnerRefusal.None, detection.Parse.Refusal);
        Assert.Null(detection.Resolution);
        Assert.Null(detection.StateDirectory);
        Assert.False(detection.StateReadOnly);
        _detector.DidNotReceive().Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Detect_ForARefusedCommand_ReturnsTheRefusalWithoutDetecting()
    {
        var detection = await Service().DetectAsync("pnpm", ["dlx", "@x/y"], null, "x", CancellationToken.None);

        Assert.Equal(RunnerRefusal.NonGoalLauncher, detection.Parse.Refusal);
        Assert.Null(detection.Resolution);
        Assert.Null(detection.StateDirectory);
        Assert.False(detection.StateReadOnly);
        _detector.DidNotReceive().Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Detect_ForARunner_PassesTheTokenAndNamesTheStateFolderFromThePrefix()
    {
        using var cts = new CancellationTokenSource();
        var resolution = Resolution(RunnerKind.Npx);
        _detector.Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>()).Returns(resolution);

        var detection = await Service().DetectAsync("npx", ["-y", "@x/y"], null, "fs", cts.Token);

        Assert.Equal(RunnerKind.Npx, detection.Parse.Kind);
        Assert.Same(resolution, detection.Resolution);
        Assert.Equal(PiaPaths.McpRuntimeStateDirectory("fs"), detection.StateDirectory);
        Assert.False(detection.StateReadOnly);
        _detector.Received(1).Detect(
            Arg.Is<RunnerRequest>(runner => runner.Kind == RunnerKind.Npx),
            Arg.Is<CancellationToken>(token => token == cts.Token));
    }

    [Fact]
    public async Task Detect_ForUvx_MarksTheStateFolderReadOnly()
    {
        _detector.Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>()).Returns(Resolution(RunnerKind.Uvx));

        var detection = await Service().DetectAsync("uvx", ["mcp-server-time"], null, "time", CancellationToken.None);

        Assert.Equal(RunnerKind.Uvx, detection.Parse.Kind);
        Assert.Equal(PiaPaths.McpRuntimeStateDirectory("time"), detection.StateDirectory);
        Assert.True(detection.StateReadOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Detect_WithABlankPrefix_UsesTheEmptyNamePrefix(string prefix)
    {
        _detector.Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>()).Returns(Resolution(RunnerKind.Npx));

        var detection = await Service().DetectAsync("npx", ["-y", "@x/y"], null, prefix, CancellationToken.None);

        Assert.Equal(PiaPaths.McpRuntimeStateDirectory("mcp"), detection.StateDirectory);
    }

    [Theory]
    [InlineData(RuntimeFamily.Node)]
    [InlineData(RuntimeFamily.Uv)]
    [InlineData(RuntimeFamily.Dotnet)]
    public void GuideFor_DelegatesToTheGuides(RuntimeFamily family)
    {
        Assert.Equal(RuntimeInstallGuides.GuideFor(family), Service().GuideFor(family));
    }
}
