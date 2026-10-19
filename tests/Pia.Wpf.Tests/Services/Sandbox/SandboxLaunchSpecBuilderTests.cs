using System.IO;
using Pia.Paths;
using Pia.Services.Exceptions;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class SandboxLaunchSpecBuilderTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static McpTransportRequest Request(
        string command,
        McpSandboxPolicy? policy = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? prefix = "fs",
        bool probe = false,
        IReadOnlyList<string>? args = null) =>
        new("Files", command, args ?? [], env, workingDirectory, policy ?? new McpSandboxPolicy([], [], false), prefix, probe);

    private static SandboxLaunchPlan Build(McpTransportRequest request, string? path = null) =>
        SandboxLaunchSpecBuilder.Build(request, path, fileExists: _ => true);

    private const string NodeRoot = @"C:\Users\u\node";

    private static readonly FakeRuntimeHost Host = new() { PathEntries = { @"C:\Windows\system32", NodeRoot } };

    private static SandboxRuntimeContext Runtime(
        IRuntimeDetector? detector, IReadOnlyList<string>? names = null, bool canProvision = true) =>
        new(detector, Host, canProvision, names ?? []);

    private static SandboxLaunchPlan Build(McpTransportRequest request, SandboxRuntimeContext runtime) =>
        SandboxLaunchSpecBuilder.Build(request, runtime, fileExists: _ => true);

    private static void IsPathFree(string error) => PathFreeAssert.IsPathFree(new SandboxStartException(error));

    private static RuntimeResolution Ready(RunnerKind kind, RuntimeInstall install, IReadOnlyList<string>? toolchain = null,
        string? programFolder = null) =>
        new(kind, RuntimeOutcome.Ready, RuntimeIssue.None, install, toolchain ?? [install.Root], programFolder);

    private static RuntimeInstall Node() => new(RuntimeFamily.Node, NodeRoot, Path.Combine(NodeRoot, "node.exe"), "24.1.0", "11.4.0",
        Path.Combine(NodeRoot, "node_modules", "npm", "bin", "npx-cli.js"), ReadableAlready: false, AdminOwned: false);

    private static RuntimeInstall Dotnet() => new(RuntimeFamily.Dotnet, @"C:\Program Files\dotnet", @"C:\Program Files\dotnet\dotnet.exe",
        "10.0.0", null, null, ReadableAlready: true, AdminOwned: false);

    private sealed class FakeDetector(Func<RunnerRequest, RuntimeResolution> detect) : IRuntimeDetector
    {
        public List<RunnerRequest> Calls { get; } = [];

        public RuntimeResolution Detect(RunnerRequest runner, CancellationToken cancellationToken)
        {
            Calls.Add(runner);
            return detect(runner);
        }
    }

    [Fact]
    public void ExeOutsideTheSystemFolders_GrantsItsDirectoryReadOnly()
    {
        var plan = Build(Request(@"C:\Tools\fs\fs.exe"));

        Assert.Null(plan.Error);
        Assert.Equal(@"C:\Tools\fs", plan.Spec!.GrantedCommandDirectory);
        Assert.Equal(@"C:\Tools\fs\fs.exe", plan.Spec.Command);
    }

    [Fact]
    public void ExeUnderWindows_NeedsNoGrant()
    {
        var plan = Build(Request(Path.Combine(Windows, "System32", "whoami.exe")));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void ExeUnderProgramFiles_NeedsNoGrant()
    {
        var plan = Build(Request(Path.Combine(ProgramFiles, "Vendor", "server.exe")));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void ExeDirectoryInsideAReadwritePath_IsNotGrantedAgain()
    {
        var plan = Build(Request(@"C:\Tools\fs\bin\fs.exe", new McpSandboxPolicy([@"C:\Tools\fs"], [], false)));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void ExeDirectoryEqualToAReadonlyPath_IsNotGrantedAgain()
    {
        var plan = Build(Request(@"C:\Tools\fs\fs.exe", new McpSandboxPolicy([], [@"c:\tools\fs\"], false)));

        Assert.Null(plan.Spec!.GrantedCommandDirectory);
    }

    [Fact]
    public void BareName_ResolvesThroughPath()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request("fs"), @"C:\nowhere;C:\Tools\fs",
            fileExists: p => string.Equals(p, @"C:\Tools\fs\fs.exe", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Tools\fs\fs.exe", plan.Spec!.Command);
        Assert.Equal(@"C:\Tools\fs", plan.Spec.GrantedCommandDirectory);
    }

    [Fact]
    public void BareName_NotOnPath_IsNotFound()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request("fs"), @"C:\nowhere", fileExists: _ => false);

        Assert.Null(plan.Spec);
        Assert.Contains("not found", plan.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootedPath_ThatDoesNotExist_IsNotFound()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request(@"C:\Tools\fs\fs.exe"), pathVariable: null, fileExists: _ => false);

        Assert.Null(plan.Spec);
        Assert.Contains("not found", plan.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Tools\run.cmd")]
    [InlineData(@"C:\Tools\run.bat")]
    [InlineData(@"C:\Tools\run.ps1")]
    public void ScriptLauncherPath_IsRefused(string command)
    {
        var plan = Build(Request(command));

        Assert.Null(plan.Spec);
        Assert.Equal(SandboxRuntimeMessages.ScriptFile, plan.Error);
    }

    [Fact]
    public void ABareNameResolvingToACmdScript_IsRefused()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request("fs"), @"C:\Tools\fs",
            fileExists: p => p.EndsWith("fs.cmd", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(SandboxRuntimeMessages.ScriptFile, plan.Error);
    }

    [Fact]
    public void AProgramThatIsNotAnExe_IsRefused()
    {
        var plan = Build(Request(@"C:\Tools\fs\server.py"));

        Assert.Equal(SandboxRuntimeMessages.NotAProgram, plan.Error);
    }

    [Fact]
    public void ARelativeProgramPath_IsRefused_WithoutAnExamplePath()
    {
        var plan = Build(Request(@"tools\fs.exe"));

        Assert.Null(plan.Spec);
        IsPathFree(plan.Error!);
    }

    private static string TempProgram(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "pia-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "server.exe");
        File.WriteAllText(exe, "x");
        return exe;
    }

    [Fact]
    public void PolicyRoots_AreEveryFolderTheContainerGetsAccessTo()
    {
        var exe = TempProgram(out var programFolder);
        try
        {
            var work = Path.Combine(Path.GetTempPath(), "pia-roots-work-" + Guid.NewGuid().ToString("N"));
            var roots = SandboxLaunchSpecBuilder.PolicyRoots(
                new McpSandboxPolicy([@"C:\box\rw"], [@"C:\box\ro"], false), exe, work);

            Assert.Equal([@"C:\box\rw", @"C:\box\ro", work, programFolder], roots);
        }
        finally
        {
            Directory.Delete(programFolder, recursive: true);
        }
    }

    [Fact]
    public void PolicyRoots_LeaveOutTheProgramsFolder_WhenAPolicyFolderAlreadyCoversIt()
    {
        var exe = TempProgram(out var programFolder);
        try
        {
            var roots = SandboxLaunchSpecBuilder.PolicyRoots(new McpSandboxPolicy([Path.GetDirectoryName(programFolder)!], [], false), exe, null);

            Assert.DoesNotContain(programFolder, roots);
        }
        finally
        {
            Directory.Delete(programFolder, recursive: true);
        }
    }

    [Fact]
    public void PolicyRoots_LeaveOutASystemProgramsFolder_AndIgnoreRelativePaths()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var roots = SandboxLaunchSpecBuilder.PolicyRoots(new McpSandboxPolicy([@"relative", @"C:\box"], [], false), cmd, "also-relative");

        Assert.Equal([@"C:\box"], roots);
    }

    /// <summary>A bare `python` can resolve to the Store's zero-byte alias, whose folder would then be the one shared.</summary>
    [Fact]
    public void AStoreAliasOrLink_IsRefused_BecauseItsFolderIsNotTheProgramsOwn()
    {
        var plan = SandboxLaunchSpecBuilder.Build(
            Request(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe"), pathVariable: null,
            fileExists: _ => true, isReparsePoint: _ => true);

        Assert.Null(plan.Spec);
        Assert.Contains("Microsoft Store alias", plan.Error);
        Assert.Contains("real python.exe", plan.Error);
    }

    [Fact]
    public void ARealProgram_IsNotMistakenForAnAlias()
    {
        var plan = SandboxLaunchSpecBuilder.Build(Request(@"C:\Tools\fs\fs.exe"), pathVariable: null, fileExists: _ => true, isReparsePoint: _ => false);

        Assert.NotNull(plan.Spec);
    }

    [Theory]
    [InlineData("npx")]
    [InlineData("uvx")]
    [InlineData(@"C:\Users\me\.local\bin\uvx.exe")]
    public void ARunnerByName_WithoutRuntimeDetection_IsRefused_EvenWhenItIsARealExe_OrNotOnPath(string command)
    {
        var plan = SandboxLaunchSpecBuilder.Build(
            Request(command, args: ["mcp-server-time"]), @"C:\nowhere",
            fileExists: p => p.EndsWith("uvx.exe", StringComparison.OrdinalIgnoreCase));

        Assert.Null(plan.Spec);
        Assert.Equal(SandboxRuntimeMessages.DetectionUnavailable, plan.Error);
    }

    [Fact]
    public void ContainerName_IsStablePerServer_AndProbesGetTheirOwn()
    {
        Assert.Equal("pia-mcp-fs", Build(Request(@"C:\Tools\fs.exe")).Spec!.ContainerName);
        Assert.Equal("pia-mcp-fs-probe", Build(Request(@"C:\Tools\fs.exe", probe: true)).Spec!.ContainerName);
    }

    [Fact]
    public void ContainerName_WithoutAPrefix_IsDerivedFromTheName()
    {
        Assert.Equal("pia-mcp-files", Build(Request(@"C:\Tools\fs.exe", prefix: null)).Spec!.ContainerName);
    }

    [Fact]
    public void WorkingDirectory_InsideAGrantedPath_IsKept()
    {
        var plan = Build(Request(@"C:\Tools\fs.exe", new McpSandboxPolicy([@"C:\box\rw"], [], false), @"C:\box\rw\sub"));

        Assert.Equal(@"C:\box\rw\sub", plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void WorkingDirectory_OutsideEveryGrant_FallsBackToTheFirstReadwritePath()
    {
        var plan = Build(Request(@"C:\Tools\fs.exe", new McpSandboxPolicy([@"C:\box\rw"], [@"C:\box\ro"], false), @"C:\elsewhere"));

        Assert.Equal(@"C:\box\rw", plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void WorkingDirectory_WithoutAnyReadwritePath_FallsBackToTheFirstReadonlyPath()
    {
        var plan = Build(Request(@"C:\Tools\fs.exe", new McpSandboxPolicy([], [@"C:\box\ro"], false)));

        Assert.Equal(@"C:\box\ro", plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void WorkingDirectory_WithNoGrantsAtAll_IsTheSystemDirectory()
    {
        var plan = Build(Request(Path.Combine(Windows, "System32", "whoami.exe")));

        Assert.Equal(Environment.SystemDirectory, plan.Spec!.WorkingDirectory);
    }

    [Fact]
    public void Environment_IsCopied_ButNeverCarriesTheRuntimeDirectory()
    {
        var env = new Dictionary<string, string> { ["TOKEN"] = "t", ["mxc_ffi_dir"] = @"C:\evil" };

        var spec = Build(Request(@"C:\Tools\fs.exe", env: env)).Spec!;

        Assert.Equal("t", spec.Environment["TOKEN"]);
        Assert.DoesNotContain(spec.Environment.Keys, k => string.Equals(k, "MXC_FFI_DIR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PolicyPaths_AreNormalisedAndDeduplicated()
    {
        var policy = new McpSandboxPolicy([@"C:\box\rw\", @"c:\BOX\rw", @"C:\box\.\other"], [@"C:\box\ro"], AllowNetwork: true);

        var spec = Build(Request(@"C:\Tools\fs.exe", policy)).Spec!;

        Assert.Equal([@"C:\box\rw", @"C:\box\other"], spec.ReadwritePaths);
        Assert.Equal([@"C:\box\ro"], spec.ReadonlyPaths);
        Assert.True(spec.AllowNetwork);
    }

    [Fact]
    public void Arguments_AreKeptVerbatim()
    {
        var spec = Build(Request(@"C:\Tools\fs.exe", args: ["--root", @"C:\a b"])).Spec!;

        Assert.Equal(["--root", @"C:\a b"], spec.Args);
    }

    [Fact]
    public void ARequestWithoutAPolicy_IsRefused()
    {
        var request = Request(@"C:\Tools\fs.exe") with { Sandbox = null };

        Assert.Throws<ArgumentException>(() => SandboxLaunchSpecBuilder.Build(request, pathVariable: null, fileExists: _ => true));
    }

    public static TheoryData<string, string[], string> Refusals => new()
    {
        { "pnpm", ["dlx", "x"], "Pia can't sandbox servers started with pnpm. npx, npm exec, uvx and dnx work; otherwise point the command at the server program itself." },
        { @"C:\Tools\run.ps1", [], SandboxRuntimeMessages.ScriptFile },
        { @"C:\Tools\cmd.exe", ["/c", "npx", "x"], SandboxRuntimeMessages.CmdNotSystem },
        { "cmd", ["/k", "npx", "x"], SandboxRuntimeMessages.CmdWithoutC },
        { "cmd", ["/c", "npx x & calc"], SandboxRuntimeMessages.CmdMetacharacters },
        { "npm", ["start"], "Pia can sandbox npm only as npx or npm exec." },
        { "uv", ["run", "server.py"], "Pia can sandbox uv only as uvx or uv tool run, with a command name it can read." },
        { "dotnet", ["run"], "Pia can sandbox dotnet only as dnx, dotnet dnx or dotnet <app>.dll." },
        { "node", ["-e", "1"], "Pia can sandbox node only as node <script>." },
        { "uvx", ["--index-url=https://user:token@host/simple", "pkg"],
            "Pia can't sandbox this uvx call: the option --index-url is not supported. Supported are --from, --with and --python." },
        { "uvx", [@"C:\w\x.whl"], SandboxRuntimeMessages.UvxValue },
        { "uvx", ["x-1.0.tar.gz"], SandboxRuntimeMessages.UvxValue },
        { "node", ["server.js"], SandboxRuntimeMessages.RelativePath },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void EveryRefusal_HasItsPathFreeText_AndNeverReachesDetection(string command, string[] args, string expected)
    {
        var detector = new FakeDetector(_ => throw new InvalidOperationException("not reached"));

        var plan = Build(Request(command, args: args), Runtime(detector));

        Assert.Null(plan.Spec);
        Assert.Equal(expected, plan.Error);
        IsPathFree(plan.Error!);
        Assert.Empty(detector.Calls);
    }

    [Fact]
    public void AnOptionSubjectHoldingAPath_IsCutBeforeThePath()
    {
        var plan = Build(Request("uvx", args: [@"--C:\secret\x", "pkg"]), Runtime(null));

        IsPathFree(plan.Error!);
    }

    [Theory]
    [InlineData(RuntimeOutcome.NotFound, RuntimeIssue.NodeNotFound,
        "Node.js was not found. Install it for your user only (the server's settings show how), then start the server again.")]
    [InlineData(RuntimeOutcome.NeedsAdminFix, RuntimeIssue.NodeAdminOwned,
        "The only Node.js found is installed for all users, and sandboxed apps can't read its folder. Run Pia as administrator once to allow it, or install it for your user only (the server's settings show how).")]
    [InlineData(RuntimeOutcome.NeedsAdminFix, RuntimeIssue.DotnetAdminOwned,
        "The only .NET found is installed for all users, and sandboxed apps can't read its folder. Run Pia as administrator once to allow it, or install it for your user only (the server's settings show how).")]
    [InlineData(RuntimeOutcome.Refused, RuntimeIssue.ToolchainTooWide,
        "The runtime sits directly on a drive, on a network share or in a folder Pia won't share (such as your profile, Desktop, Documents or Downloads). Install it into a folder of its own.")]
    public void ARuntimeThatIsNotReady_GivesItsIssueText(RuntimeOutcome outcome, RuntimeIssue issue, string expected)
    {
        var detector = new FakeDetector(runner => new RuntimeResolution(runner.Kind, outcome, issue, null, [], null));

        var plan = Build(Request("npx", args: ["-y", "@x/y"]), Runtime(detector));

        Assert.Null(plan.Spec);
        Assert.Equal(expected, plan.Error);
    }

    [Fact]
    public void AThrowingDetector_GivesTheCheckText_AndNoSpec()
    {
        var detector = new FakeDetector(_ => throw new IOException(@"Access to C:\Users\u\node is denied."));

        var plan = Build(Request("npx", args: ["-y", "@x/y"]), Runtime(detector));

        Assert.Null(plan.Spec);
        Assert.Equal("Pia could not check this server's runtime.", plan.Error);
        IsPathFree(plan.Error!);
    }

    [Fact]
    public void ADetectorRefusingWithASandboxStartException_PassesItOn()
    {
        var detector = new FakeDetector(_ => throw new SandboxStartException("refused"));

        Assert.Throws<SandboxStartException>(() => Build(Request("npx", args: ["-y", "@x/y"]), Runtime(detector)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ARunner_WithoutAllThreeRuntimeServices_IsRefused_BeforeDetection(bool canProvision, bool withHost)
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Node()));
        var runtime = new SandboxRuntimeContext(detector, withHost ? Host : null, canProvision, []);

        var plan = Build(Request("npx", args: ["-y", "@x/y"]), runtime);

        Assert.Equal("Pia can't sandbox this server: runtime detection is not available.", plan.Error);
        Assert.Empty(detector.Calls);
    }

    [Fact]
    public void AReadyNpxServer_GetsTheNodeLaunch_FoldersPreparationAndEnvironment()
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Node()));
        var request = Request("npx", new McpSandboxPolicy([@"C:\box\rw"], [], AllowNetwork: true),
            env: new Dictionary<string, string> { ["API_KEY"] = "mine" }, args: ["-y", "@x/y"]);

        var spec = Build(request, Runtime(detector, ["GH_TOKEN", "Path", "API_KEY"])).Spec!;

        var state = PiaPaths.McpRuntimeStateDirectory("fs");
        Assert.Equal(Path.Combine(NodeRoot, "node.exe"), spec.Command);
        Assert.Equal([Path.Combine(NodeRoot, "node_modules", "npm", "bin", "npx-cli.js"), "-y", "@x/y"], spec.Args);
        Assert.Equal([NodeRoot], spec.Toolchain);
        Assert.Equal(state, spec.RuntimeStateDirectory);
        Assert.False(spec.RuntimeStateReadOnly);
        Assert.Equal(PiaPaths.McpSandboxSupportDirectory, spec.SupportDirectory);
        Assert.Equal(RunnerKind.Npx, spec.RunnerKind);
        Assert.Null(spec.GrantedCommandDirectory);
        Assert.Equal(@"C:\box\rw", spec.WorkingDirectory);
        Assert.Contains(state, spec.Preparation!.Directories);
        Assert.Contains(spec.Preparation.Files, file => file.Path == Path.Combine(PiaPaths.McpSandboxSupportDirectory, "node-sandbox.cjs"));
        Assert.Equal(Path.Combine(state, "npm-cache"), spec.Environment["npm_config_cache"]);
        Assert.StartsWith("--preserve-symlinks", spec.Environment["NODE_OPTIONS"]);
        Assert.StartsWith(NodeRoot + ";", spec.Environment["PATH"]);
        Assert.Equal("", spec.Environment["GH_TOKEN"]);
        Assert.Equal("mine", spec.Environment["API_KEY"]);
        var npx = Assert.Single(detector.Calls);
        Assert.Equal(RunnerKind.Npx, npx.Kind);
    }

    /// <summary>The machine value reaches the server; the user's registry value stays blanked like every user variable.</summary>
    [Fact]
    public void AReadyNodeServer_MergesTheMachineNodeOptions_FromTheHost()
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Node()));
        var host = new FakeRuntimeHost
        {
            PathEntries = { @"C:\Windows\system32" },
            MachineEnvironment = { ["NODE_OPTIONS"] = "--use-openssl-ca" },
            RegistryEnvironment = { ["NODE_OPTIONS"] = "--users-registry-value" }
        };
        var request = Request("npx", env: new Dictionary<string, string> { ["API_KEY"] = "mine" }, args: ["-y", "@x/y"]);

        var spec = Build(request, new SandboxRuntimeContext(detector, host, CanProvision: true, ["NODE_OPTIONS"])).Spec!;

        var preload = Path.Combine(PiaPaths.McpSandboxSupportDirectory, "node-sandbox.cjs").Replace('\\', '/');
        Assert.Equal(
            $"--preserve-symlinks --preserve-symlinks-main --require \"{preload}\" --use-openssl-ca",
            spec.Environment["NODE_OPTIONS"]);
    }

    [Fact]
    public void CmdWrappingNpx_IsTheSameRunner()
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Node()));

        var spec = Build(Request("cmd", args: ["/c", "npx", "-y", "@x/y"]), Runtime(detector)).Spec!;

        Assert.Equal(Path.Combine(NodeRoot, "node.exe"), spec.Command);
        Assert.Equal(RunnerKind.Npx, spec.RunnerKind);
    }

    [Theory]
    [InlineData(@"C:\box", null)]
    [InlineData(@"C:\other", @"C:\box\app")]
    public void ADotnetDll_GrantsItsProgramFolder_UnlessAPolicyFolderCoversIt(string readonlyFolder, string? expectedGrant)
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Dotnet(), toolchain: [], programFolder: @"C:\box\app"));
        var request = Request("dotnet", new McpSandboxPolicy([], [readonlyFolder], false), args: [@"C:\box\app\server.dll", "--x"]);

        var spec = Build(request, Runtime(detector)).Spec!;

        Assert.Equal(expectedGrant, spec.GrantedCommandDirectory);
        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", spec.Command);
        Assert.Equal([@"C:\box\app\server.dll", "--x"], spec.Args);
        Assert.Empty(spec.Toolchain);
        Assert.Equal(RunnerKind.DotnetDll, spec.RunnerKind);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("support")]
    [InlineData("toolchain")]
    public void TheWorkingDirectory_IsNeverAFolderPiaManagesOrTheToolchain(string folder)
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Node()));
        var requested = folder switch
        {
            "state" => PiaPaths.McpRuntimeStateDirectory("fs"),
            "support" => PiaPaths.McpSandboxSupportDirectory,
            _ => NodeRoot,
        };

        var spec = Build(Request("npx", new McpSandboxPolicy([@"C:\box\rw"], [], false), requested, args: ["-y", "@x/y"]),
            Runtime(detector)).Spec!;

        Assert.Equal(@"C:\box\rw", spec.WorkingDirectory);
    }

    [Fact]
    public void ARunnerWithoutAnyFolders_WorksInTheSystemDirectory()
    {
        var detector = new FakeDetector(runner => Ready(runner.Kind, Node()));

        var spec = Build(Request("npx", workingDirectory: PiaPaths.McpRuntimeStateDirectory("fs"), args: ["-y", "@x/y"]),
            Runtime(detector)).Spec!;

        Assert.Equal(Environment.SystemDirectory, spec.WorkingDirectory);
    }

    [Fact]
    public void Uvx_IsLeftForTheProvisioner_WithAReadOnlyStateFolder()
    {
        var uv = new RuntimeInstall(RuntimeFamily.Uv, @"C:\Users\u\.local\bin", @"C:\Users\u\.local\bin\uv.exe", null, null, null, false, false);
        var detector = new FakeDetector(runner => Ready(runner.Kind, uv, toolchain: []));

        var spec = Build(Request("uvx", args: ["mcp-server-time", "--local-timezone", "UTC"]), Runtime(detector, ["GH_TOKEN"])).Spec!;

        Assert.Equal("", spec.Command);
        Assert.True(spec.RuntimeStateReadOnly);
        Assert.Equal(RunnerKind.Uvx, spec.RunnerKind);
        Assert.Equal("mcp-server-time", spec.Preparation!.Uv!.Spec.Package);
        Assert.Equal("", spec.Environment["GH_TOKEN"]);
    }

    [Fact]
    public void CmdWrappingAnExe_LaunchesTheExeItself()
    {
        var spec = Build(Request("cmd", args: ["/c", @"C:\Tools\fs\fs.exe", "--x"])).Spec!;

        Assert.Equal(@"C:\Tools\fs\fs.exe", spec.Command);
        Assert.Equal(["--x"], spec.Args);
        Assert.Equal(@"C:\Tools\fs", spec.GrantedCommandDirectory);
    }

    [Fact]
    public void AnExeServer_BlanksTheUsersRegistryVariables_ButItsOwnEntriesWin()
    {
        var names = new[] { "GH_TOKEN", "Path", "TEMP", "TMP", "OneDrive", "OneDriveCommercial", "API_KEY", "MXC_FFI_DIR" };
        var env = new Dictionary<string, string> { ["API_KEY"] = "mine", ["PATH"] = @"C:\mine", ["MXC_FFI_DIR"] = @"C:\evil" };

        var spec = Build(Request(@"C:\Tools\fs\fs.exe", env: env), Runtime(null, names)).Spec!;

        Assert.Equal("", spec.Environment["GH_TOKEN"]);
        Assert.Equal("mine", spec.Environment["API_KEY"]);
        Assert.Equal(@"C:\mine", spec.Environment["PATH"]);
        Assert.Equal("", spec.Environment["MXC_FFI_DIR"]);
        Assert.DoesNotContain("TEMP", spec.Environment.Keys);
        Assert.DoesNotContain("OneDriveCommercial", spec.Environment.Keys);
    }
}
