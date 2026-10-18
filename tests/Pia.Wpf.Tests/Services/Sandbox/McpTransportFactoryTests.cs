using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using NSubstitute;
using Pia.Services.Plugins;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class McpTransportFactoryTests
{
    private static McpTransportRequest Request(McpSandboxPolicy? sandbox, string command = @"C:\Tools\fs\fs.exe", bool probe = false) =>
        new("Files", command, ["--x"], new Dictionary<string, string> { ["TOKEN"] = "t" }, null, sandbox, "fs", probe);

    private static readonly SandboxGuardEnvironment EverythingExists =
        SandboxGuardEnvironment.Current() with { PathExists = _ => true };

    private static McpTransportFactory WithLauncher(FakeMxcProcessLauncher? launcher = null) =>
        new(launcher ?? new FakeMxcProcessLauncher(), Substitute.For<ISandboxGrantVerifier>(),
            NullLoggerFactory.Instance, fileExists: _ => true, guardEnvironment: EverythingExists);

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"\\server\share")]
    public void ASandboxedRequest_WithAPathTheGuardRefuses_IsRefused_NotRunWithIt(string readwrite)
    {
        var ex = Assert.Throws<SandboxStartException>(
            () => WithLauncher().Create(Request(new McpSandboxPolicy([readwrite], [], false))));

        Assert.StartsWith("Pia can't sandbox this server:", ex.Message);
        Assert.DoesNotContain(readwrite, ex.Message);
    }

    [Fact]
    public void ASandboxedRequest_WithAProgramInAWideFolder_IsRefused()
    {
        var downloads = Path.Combine(SandboxGuardEnvironment.Current().ProfileRoot, "Downloads");

        var ex = Assert.Throws<SandboxStartException>(
            () => WithLauncher().Create(Request(McpSandboxPolicy.Empty, Path.Combine(downloads, "server.exe"))));

        Assert.Contains("Put the server in its own folder", ex.Message);
    }

    [Fact]
    public void ASandboxedRequest_WithNoFolders_IsAccepted()
    {
        Assert.IsType<MxcClientTransport>(WithLauncher().Create(Request(McpSandboxPolicy.Empty)));
    }

    [Fact]
    public void WithoutASandbox_ItIsAPlainStdioTransport()
    {
        var transport = new McpTransportFactory().Create(Request(sandbox: null));

        Assert.IsType<StdioClientTransport>(transport);
    }

    [Fact]
    public void WithASandbox_ItIsAContainerTransport()
    {
        var transport = WithLauncher().Create(Request(new McpSandboxPolicy([@"C:\box"], [], false)));

        var mxc = Assert.IsType<MxcClientTransport>(transport);
        Assert.Equal("mxc:pia-mcp-fs", mxc.Name);
    }

    [Fact]
    public void AProbeOfASandboxedServer_GetsItsOwnContainerName()
    {
        var transport = WithLauncher().Create(Request(new McpSandboxPolicy([], [], false), probe: true));

        Assert.Equal("mxc:pia-mcp-fs-probe", Assert.IsType<MxcClientTransport>(transport).Name);
    }

    /// <summary>The fail-closed rule: whatever is missing, a sandboxed request never degrades to a bare process.</summary>
    [Fact]
    public void ASandboxedRequest_WithNoLauncher_IsRefused_NotRunUncontained()
    {
        var factory = new McpTransportFactory(launcher: null, verifier: null, NullLoggerFactory.Instance, fileExists: _ => true);

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(Request(McpSandboxPolicy.Empty)));

        Assert.Contains("not available", ex.Message);
    }

    [Theory]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\run.bat")]
    public void ASandboxedScriptLauncher_IsRefused_NotRunUncontained(string command)
    {
        var ex = Assert.Throws<SandboxStartException>(() => WithLauncher().Create(Request(McpSandboxPolicy.Empty, command)));

        Assert.Equal(SandboxRuntimeMessages.ScriptFile, ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    private const string NodeRoot = @"C:\Users\u\node";

    private static readonly string Downloads = Path.Combine(SandboxGuardEnvironment.Current().ProfileRoot, "Downloads");

    private static McpTransportRequest NpxRequest(string command = "npx", McpSandboxPolicy? sandbox = null) =>
        new("Files", command, ["-y", "@x/y"], new Dictionary<string, string> { ["TOKEN"] = "t" }, null,
            sandbox ?? McpSandboxPolicy.Empty, "fs", false);

    private static IRuntimeDetector Detector(RuntimeOutcome outcome = RuntimeOutcome.Ready, RuntimeIssue issue = RuntimeIssue.None)
    {
        var install = new RuntimeInstall(RuntimeFamily.Node, NodeRoot, Path.Combine(NodeRoot, "node.exe"), "24.1.0", "11.4.0",
            Path.Combine(NodeRoot, "node_modules", "npm", "bin", "npx-cli.js"), ReadableAlready: false, AdminOwned: false);
        var detector = Substitute.For<IRuntimeDetector>();
        detector.Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new RuntimeResolution(call.Arg<RunnerRequest>().Kind, outcome, issue,
                outcome == RuntimeOutcome.Ready ? install : null, outcome == RuntimeOutcome.Ready ? [NodeRoot] : [], null));
        return detector;
    }

    private static (ISandboxGrantVerifier Verifier, List<SandboxLaunchSpec> Seen) RefusingVerifier()
    {
        var seen = new List<SandboxLaunchSpec>();
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Do<SandboxLaunchSpec>(spec => seen.Add(spec))).Returns(new SandboxGrantResult(false, 1, "stop"));
        return (verifier, seen);
    }

    private static McpTransportFactory Factory(
        ISandboxGrantVerifier? verifier = null,
        IRuntimeDetector? detector = null,
        IRuntimeHostService? host = null,
        IRuntimeStateProvisioner? provisioner = null,
        Func<IReadOnlyList<string>>? names = null) =>
        new(new FakeMxcProcessLauncher(), verifier ?? Substitute.For<ISandboxGrantVerifier>(), NullLoggerFactory.Instance,
            fileExists: _ => true, guardEnvironment: EverythingExists, detector, host, provisioner, names);

    private static async Task<SandboxLaunchSpec> SpecReachingTheVerifier(
        IClientTransport transport, List<SandboxLaunchSpec> seen)
    {
        await Assert.ThrowsAsync<SandboxStartException>(() => transport.ConnectAsync(TestContext.Current.CancellationToken));
        return Assert.Single(seen);
    }

    [Theory]
    [InlineData("uvx", new[] { @"C:\w\x.whl" }, SandboxRuntimeMessages.UvxValue)]
    [InlineData("uvx", new[] { "x-1.0-py3-none-any.whl" }, SandboxRuntimeMessages.UvxValue)]
    [InlineData("cmd", new[] { "/c", "npx -y @x/y & calc" }, SandboxRuntimeMessages.CmdMetacharacters)]
    [InlineData("pnpm", new[] { "dlx", "x" },
        "Pia can't sandbox servers started with pnpm. npx, npm exec, uvx and dnx work; otherwise point the command at the server program itself.")]
    public void ARunnerRefusal_IsThrownWithItsText_AndNamesNoPath(string command, string[] args, string expected)
    {
        var request = Request(McpSandboxPolicy.Empty, command) with { Args = args };

        var ex = Assert.Throws<SandboxStartException>(
            () => Factory(detector: Detector(), host: new FakeRuntimeHost(), provisioner: new RecordingProvisioner()).Create(request));

        Assert.Equal(expected, ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    [Fact]
    public void ARuntimeThatIsNotReady_IsThrownWithItsIssueText()
    {
        var factory = Factory(detector: Detector(RuntimeOutcome.NotFound, RuntimeIssue.NodeNotFound),
            host: new FakeRuntimeHost(), provisioner: new RecordingProvisioner());

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(NpxRequest()));

        Assert.Equal(SandboxRuntimeMessages.NodeNotFound, ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    [Fact]
    public void ARunner_WithoutAProvisioner_IsRefused()
    {
        var factory = Factory(detector: Detector(), host: new FakeRuntimeHost());

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(NpxRequest()));

        Assert.Equal("Pia can't sandbox this server: runtime detection is not available.", ex.Message);
    }

    [Fact]
    public void TheGuard_JudgesTheProgramCmdUnwrapped()
    {
        var request = Request(McpSandboxPolicy.Empty, Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            with { Args = ["/c", Path.Combine(Downloads, "server.exe")] };

        var ex = Assert.Throws<SandboxStartException>(() => Factory().Create(request));

        Assert.Contains("Put the server in its own folder", ex.Message);
    }

    [Fact]
    public void TheGuard_LeavesARunnersFoldersToTheDetector()
    {
        var factory = Factory(detector: Detector(), host: new FakeRuntimeHost(), provisioner: new RecordingProvisioner());

        Assert.IsType<MxcClientTransport>(factory.Create(NpxRequest(Path.Combine(Downloads, "npx.cmd"))));
    }

    [Fact]
    public async Task TheProvisioner_ReachesTheTransport_WithTheRunnersSpec()
    {
        var provisioner = new RecordingProvisioner();
        var (verifier, seen) = RefusingVerifier();
        var factory = Factory(verifier, Detector(), new FakeRuntimeHost(), provisioner);

        var spec = await SpecReachingTheVerifier(factory.Create(NpxRequest()), seen);

        Assert.Same(spec, Assert.Single(provisioner.Prepared));
        Assert.Equal(RunnerKind.Npx, spec.RunnerKind);
        Assert.Equal(Path.Combine(NodeRoot, "node.exe"), spec.Command);
    }

    [Fact]
    public async Task AnExeServer_IsBlanked_WithTheNamesFromTheSeam()
    {
        var (verifier, seen) = RefusingVerifier();
        var factory = Factory(verifier, names: () => ["GH_TOKEN", "Path", "TOKEN"]);

        var spec = await SpecReachingTheVerifier(factory.Create(Request(McpSandboxPolicy.Empty)), seen);

        Assert.Equal("", spec.Environment["GH_TOKEN"]);
        Assert.Equal("t", spec.Environment["TOKEN"]);
        Assert.False(spec.Environment.ContainsKey("Path"));
    }

    [Fact]
    public async Task TheHostsNames_WinOverTheSeam()
    {
        var (verifier, seen) = RefusingVerifier();
        var host = new FakeRuntimeHost { UserNames = { "FROM_HOST" } };
        var factory = Factory(verifier, host: host, names: () => ["FROM_SEAM"]);

        var spec = await SpecReachingTheVerifier(factory.Create(Request(McpSandboxPolicy.Empty)), seen);

        Assert.Equal("", spec.Environment["FROM_HOST"]);
        Assert.False(spec.Environment.ContainsKey("FROM_SEAM"));
    }

    [Fact]
    public async Task WithoutAHostOrASeam_TheRegistryNamesAreStillBlanked()
    {
        var (verifier, seen) = RefusingVerifier();
        var factory = Factory(verifier);

        var spec = await SpecReachingTheVerifier(factory.Create(Request(McpSandboxPolicy.Empty)), seen);

        var expected = SandboxEnvironmentComposer.NamesToBlank(RuntimeHostService.ReadUserEnvironmentNames())
            .Where(name => !string.Equals(name, "TOKEN", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.SkipWhen(expected.Count == 0, "no HKCU Environment name outside the allowlist on this host");
        Assert.All(expected, name => Assert.Equal("", spec.Environment[name]));
    }

    /// <summary>Without the names nothing would be blanked, so the start fails closed rather than leaking the user's variables.</summary>
    [Fact]
    public void AUserEnvironmentTheSeamCannotRead_RefusesTheStart_AndStartsNothing()
    {
        var launcher = new FakeMxcProcessLauncher();
        var logger = new CapturingLogger<McpTransportFactory>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        var factory = new McpTransportFactory(launcher, Substitute.For<ISandboxGrantVerifier>(), loggerFactory,
            fileExists: _ => true, guardEnvironment: EverythingExists,
            userEnvironmentNames: () => throw new UnauthorizedAccessException(@"Access to C:\Users\jane\secret is denied."));

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(Request(McpSandboxPolicy.Empty)));

        Assert.Equal(SandboxRuntimeMessages.UserEnvironmentUnreadable, ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.Empty(launcher.Launched);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(nameof(UnauthorizedAccessException), warning.Message);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Information && entry.Message.Contains("secret"));
    }

    [Fact]
    public void AUserEnvironmentTheHostCannotRead_RefusesARunner_BeforeDetectionOrProvisioning()
    {
        var launcher = new FakeMxcProcessLauncher();
        var detector = Detector();
        var provisioner = new RecordingProvisioner();
        var host = new FakeRuntimeHost { UserNamesFailure = new IOException("registry unavailable") };
        var factory = new McpTransportFactory(launcher, Substitute.For<ISandboxGrantVerifier>(), NullLoggerFactory.Instance,
            fileExists: _ => true, guardEnvironment: EverythingExists, detector, host, provisioner,
            userEnvironmentNames: () => ["FROM_SEAM"]);

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(NpxRequest()));

        Assert.Equal(SandboxRuntimeMessages.UserEnvironmentUnreadable, ex.Message);
        PathFreeAssert.IsPathFree(ex);
        detector.DidNotReceive().Detect(Arg.Any<RunnerRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(provisioner.Prepared);
        Assert.Empty(launcher.Launched);
    }

    private sealed class RecordingProvisioner : IRuntimeStateProvisioner
    {
        public List<SandboxLaunchSpec> Prepared { get; } = [];

        public Task<SandboxLaunchSpec> PrepareAsync(SandboxLaunchSpec spec, Action<string> stderrLine, CancellationToken cancellationToken)
        {
            Prepared.Add(spec);
            return Task.FromResult(spec);
        }
    }

    [Fact]
    public void ASandboxedCommandThatIsNotThere_IsRefused()
    {
        var factory = new McpTransportFactory(new FakeMxcProcessLauncher(), Substitute.For<ISandboxGrantVerifier>(),
            NullLoggerFactory.Instance, fileExists: _ => false);

        var ex = Assert.Throws<SandboxStartException>(() => factory.Create(Request(McpSandboxPolicy.Empty)));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StdioOptions_NeverPassTheRuntimeDirectoryToTheChild()
    {
        var options = McpTransportFactory.BuildStdioOptions(Request(null));

        Assert.True(options.EnvironmentVariables!.ContainsKey("MXC_FFI_DIR"));
        Assert.Null(options.EnvironmentVariables["MXC_FFI_DIR"]);
        Assert.Equal("t", options.EnvironmentVariables["TOKEN"]);
    }

    [Fact]
    public void StdioOptions_DropAUserSuppliedRuntimeDirectoryToo()
    {
        var request = Request(null) with { Env = new Dictionary<string, string> { ["mxc_ffi_dir"] = @"C:\evil" } };

        var options = McpTransportFactory.BuildStdioOptions(request);

        Assert.All(options.EnvironmentVariables!.Where(e => e.Key.Equals("MXC_FFI_DIR", StringComparison.OrdinalIgnoreCase)),
            e => Assert.Null(e.Value));
    }

    [Fact]
    public void StdioOptions_KeepTheCommandNameAndArguments()
    {
        var options = McpTransportFactory.BuildStdioOptions(Request(null, command: "node"));

        Assert.Equal("Files", options.Name);
        Assert.Equal("node", options.Command);
        Assert.Equal(["--x"], options.Arguments);
    }
}
