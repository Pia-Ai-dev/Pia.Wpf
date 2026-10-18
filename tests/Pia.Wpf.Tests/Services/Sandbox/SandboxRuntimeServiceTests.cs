using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.Plugins.Sandbox;
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

    private static readonly SandboxGuardEnvironment Guard = new(
        WindowsDirectory: @"C:\Windows",
        ProgramFilesDirectories: [@"C:\Program Files", @"C:\Program Files (x86)"],
        ProfileRoot: @"C:\Users\u",
        PiaDataRoots: [@"C:\Users\u\AppData\Roaming\Pia", @"C:\Users\u\AppData\Local\Pia"],
        SandboxRoot: @"C:\Users\u\AppData\Local\Pia\McpSandbox",
        WideFolders: [@"C:\Users\u\Desktop", @"C:\Users\u\Documents", @"C:\Users\u\Downloads"],
        PathExists: _ => true,
        CountEntries: (_, limit) => limit);

    private SandboxRuntimeService Service() =>
        new(_detector, _acl, _elevation, _host, new RuntimeRootVerifier(() => Guard), NullLogger<SandboxRuntimeService>.Instance);

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

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData("D:/")]
    [InlineData(@"\\fileserver\share")]
    public async Task Grant_OnADriveRoot_IsRefused(string root)
    {
        _elevation.IsElevated.Returns(true);

        var result = await Service().GrantMachineWideReadAsync(root, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SandboxRuntimeMessages.ToolchainTooWide, result.Error);
        PathFreeAssert.IsPathFree(new InvalidOperationException(result.Error));
        _acl.DidNotReceive().GrantAllAppPackagesRead(Arg.Any<string>());
    }

    [Theory]
    [InlineData(@"C:\Users\u")]
    [InlineData(@"C:\Users\u\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\u\Downloads")]
    [InlineData(@"C:\Users\u\AppData\Local")]
    [InlineData(@"C:\Users\u\AppData\Local\Pia")]
    public async Task Grant_OnAProfileAncestor_IsRefused(string root)
    {
        _elevation.IsElevated.Returns(true);

        var result = await Service().GrantMachineWideReadAsync(root, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SandboxRuntimeMessages.ToolchainTooWide, result.Error);
        PathFreeAssert.IsPathFree(new InvalidOperationException(result.Error));
        _acl.DidNotReceive().GrantAllAppPackagesRead(Arg.Any<string>());
        _acl.DidNotReceive().HasAllAppPackagesAce(Arg.Any<string>());
    }

    /// <summary>The too-wide refusal also fires for a drive or share root, so its text must cover them.</summary>
    [Fact]
    public void TheTooWideText_CoversADriveAndAShare_WithoutNamingAPath()
    {
        Assert.Contains("on a drive", SandboxRuntimeMessages.ToolchainTooWide);
        Assert.Contains("network share", SandboxRuntimeMessages.ToolchainTooWide);
        PathFreeAssert.IsPathFree(new InvalidOperationException(SandboxRuntimeMessages.ToolchainTooWide));
    }

    /// <summary>The cap keeps per-start grants fast; this entry is written once, so a large runtime folder may take it.</summary>
    [Fact]
    public async Task Grant_OnAFolderTooLargeForAPerStartGrant_StillRuns()
    {
        _elevation.IsElevated.Returns(true);
        var root = @"D:\tools\node";
        Assert.Equal(RuntimeIssue.ToolchainTooLarge, new RuntimeRootVerifier(() => Guard).Check(root, false));

        var result = await Service().GrantMachineWideReadAsync(root, CancellationToken.None);

        Assert.True(result.Ok);
        _acl.Received(1).GrantAllAppPackagesRead(root);
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
    public async Task Detect_TellsTheDetectorThatTheSandboxRootIsWritable()
    {
        var sandboxRoot = PiaPaths.McpSandboxRoot;
        _detector.Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>()).Returns(Resolution(RunnerKind.Uvx));

        await Service().DetectAsync("uvx", ["mcp-server-time"], null, "time", CancellationToken.None);

        _detector.Received(1).Detect(
            Arg.Is<RunnerRequest>(runner => runner.WritableFolders != null && runner.WritableFolders.SequenceEqual(new[] { sandboxRoot })),
            Arg.Any<CancellationToken>());
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

    [Fact]
    public void GuideForAResolution_DelegatesToTheGuides()
    {
        var resolution = Resolution(RunnerKind.DotnetDll) with
        {
            Outcome = RuntimeOutcome.NotFound,
            Issue = RuntimeIssue.DotnetRuntimeMissing,
            Frameworks = [new DotnetRuntimeConfig.Framework("Microsoft.AspNetCore.App", "8.0.0")]
        };

        Assert.Equal(RuntimeInstallGuides.GuideFor(resolution), Service().GuideFor(resolution));
        Assert.Equal("ASP.NET Core 8.0", Service().GuideFor(resolution).Product);
    }
}
