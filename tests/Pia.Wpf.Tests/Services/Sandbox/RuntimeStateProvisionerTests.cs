using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Paths;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

[Collection("PiaPathsStatic")]
public sealed class RuntimeStateProvisionerTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private const string Preload = "'use strict';\n// preload\n";
    private const string NuGetConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration />\n";

    private readonly RecordingStore _store = new();
    private readonly FakeUv _uv = new();
    private readonly List<string> _stderr = [];
    private readonly string _prefix = "p" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _outside;
    private readonly string _outsideFile;

    public RuntimeStateProvisionerTests(RedirectedProfileFixture profile)
    {
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxSupportDirectory);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxGrantsDirectory);
        _outside = Directory.CreateDirectory(Path.Combine(profile.Root, "outside-" + Guid.NewGuid().ToString("N"))).FullName;
        _outsideFile = Path.Combine(_outside, "keep.txt");
        File.WriteAllText(_outsideFile, "outside");
    }

    public void Dispose()
    {
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxSupportDirectory);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxGrantsDirectory);
        TempPath.Remove(_outside);
    }

    private string Container => $"pia-mcp-{_prefix}";

    private string ProbeContainer => $"pia-mcp-{_prefix}-probe";

    private string State => PiaPaths.McpRuntimeStateDirectory(_prefix);

    private string Support => PiaPaths.McpSandboxSupportDirectory;

    private string AcTemp => Path.Combine(State, "home", "AppData", "Local", "Packages", Container, "AC", "Temp");

    private string NuGetConfigPath => Path.Combine(State, "home", "AppData", "Roaming", "NuGet", "NuGet.Config");

    private RuntimeStateProvisioner Provisioner(ILogger<RuntimeStateProvisioner>? logger = null) =>
        new(_store, new UvToolRunner(NullLogger<UvToolRunner>.Instance, _uv.Run), logger ?? NullLogger<RuntimeStateProvisioner>.Instance);

    // Under the container's own lease, which the transport holds while the provisioner runs.
    private async Task<SandboxLaunchSpec> Prepare(SandboxLaunchSpec spec)
    {
        using var lease = SandboxContainerLease.Acquire(spec.ContainerName);
        return await Provisioner().PrepareAsync(spec, line => { lock (_stderr) _stderr.Add(line); }, TestContext.Current.CancellationToken);
    }

    // The real store, so the grant records live outside the state folder as they do in the app.
    private async Task<SandboxLaunchSpec> PrepareWithRecords(SandboxLaunchSpec spec)
    {
        using var lease = SandboxContainerLease.Acquire(spec.ContainerName);
        return await new RuntimeStateProvisioner(
                new RuntimeStateStore(NullLogger<RuntimeStateStore>.Instance),
                new UvToolRunner(NullLogger<UvToolRunner>.Instance, _uv.Run),
                NullLogger<RuntimeStateProvisioner>.Instance)
            .PrepareAsync(spec, line => { lock (_stderr) _stderr.Add(line); }, TestContext.Current.CancellationToken);
    }

    // An earlier uvx start left a record saying the state folder was read-only, so its content is trusted.
    private void TrustTheStateFolder() => _store.Earlier[Container] = true;

    private static SandboxLaunchSpec Base(string container) =>
        new(container, @"C:\Tools\node\node.exe", ["server.js"], new Dictionary<string, string>(), @"C:\box",
            [@"C:\box"], [], AllowNetwork: true, GrantedCommandDirectory: null);

    private static SandboxLaunchSpec ProgramSpec(string container) =>
        Base(container) with
        {
            Command = @"C:\Tools\server\server.exe",
            Args = [],
            ReadonlyPaths = [@"C:\data"],
            GrantedCommandDirectory = @"C:\Tools\server",
        };

    private SandboxLaunchSpec NodeSpec(params PreparedFile[] extraFiles) =>
        Base(Container) with
        {
            ToolchainDirectories = [@"C:\Users\Jane\node"],
            GrantedCommandDirectory = @"C:\Users\Jane\mcp\server",
            RuntimeStateDirectory = State,
            SupportDirectory = Support,
            RunnerKind = RunnerKind.NodeScript,
            Preparation = new RuntimePreparation(
                [State, Path.Combine(State, "home"), Path.Combine(State, "home", "AppData", "Roaming"), AcTemp],
                [new PreparedFile(Path.Combine(Support, "node-sandbox.cjs"), Preload), new PreparedFile(NuGetConfigPath, NuGetConfig), .. extraFiles],
                Uv: null),
        };

    private SandboxLaunchSpec UvxSpec(bool offline = false, UvxSpec? uvx = null)
    {
        var request = new UvPrepareRequest(
            Container,
            @"C:\Users\Jane\.local\bin\uv.exe",
            uvx ?? new UvxSpec("mcp-server-time", "mcp-server-time", [], null),
            new Dictionary<string, string> { ["UV_TOOL_DIR"] = Path.Combine(State, "tools") },
            offline,
            State,
            ["--local-timezone", "UTC"],
            [Path.Combine(State, "uv-cache"), Path.Combine(State, "python-cache")]);

        return Base(Container) with
        {
            Command = "",
            Args = [],
            AllowNetwork = !offline,
            RuntimeStateDirectory = State,
            RuntimeStateReadOnly = true,
            RunnerKind = RunnerKind.Uvx,
            Preparation = new RuntimePreparation([State, Path.Combine(State, "tmp")], [], request),
        };
    }

    private void WriteSpecRecord(string package, string[]? requirements = null, string? python = null)
    {
        Directory.CreateDirectory(State);
        var record = new Dictionary<string, object?> { ["package"] = package, ["with"] = requirements ?? [], ["python"] = python };
        File.WriteAllText(Path.Combine(State, "pia-uv-spec.json"), JsonSerializer.Serialize(record));
    }

    private static string ExpectedCode =>
        "import sys, importlib, functools; sys.argv[0] = 'mcp-server-time'; sys.exit(functools.reduce(getattr, 'main'.split('.'), importlib.import_module('mcp_server_time'))())";

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    [Fact]
    public async Task ASpecWithoutARunner_IsReturnedUnchanged_AndOnlyItsGrantRecordIsWritten()
    {
        var spec = Base(Container) with { RuntimeStateDirectory = State, Preparation = NodeSpec().Preparation };

        Assert.Same(spec, await Prepare(spec));
        Assert.False(Directory.Exists(State));
        var (container, roots, stateReadOnly, policyRoots) = Assert.Single(_store.Records);
        Assert.Equal(Container, container);
        Assert.Equal([State], roots);
        Assert.Equal([@"C:\box"], policyRoots);
        Assert.False(stateReadOnly);
    }

    [Fact]
    public async Task ARunnerWithoutAPreparation_IsReturnedUnchanged()
    {
        var spec = NodeSpec() with { Preparation = null };

        Assert.Same(spec, await Prepare(spec));
        Assert.False(Directory.Exists(State));
        Assert.Single(_store.Records);
    }

    [Fact]
    public async Task AProgramSpec_RecordsItsProgramAndPolicyFolders_WithTheStateFolderReadOnly()
    {
        var spec = ProgramSpec(ProbeContainer);

        Assert.Same(spec, await Prepare(spec));

        var (container, roots, stateReadOnly, policyRoots) = Assert.Single(_store.Records);
        Assert.Equal(ProbeContainer, container);
        Assert.Equal([@"C:\Tools\server"], roots);
        Assert.Equal([@"C:\box", @"C:\data"], policyRoots);
        Assert.True(stateReadOnly);
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public async Task AProgramSpec_KeepsAnEarlierReadWriteGrantOnRecord()
    {
        _store.Earlier[Container] = false;

        await Prepare(ProgramSpec(Container));

        Assert.False(Assert.Single(_store.Records).StateReadOnly);
    }

    [Fact]
    public async Task AProgramProbe_LeavesItsFoldersToTheOrphanSweep()
    {
        var readwrite = Directory.CreateDirectory(Path.Combine(_outside, "rw")).FullName;
        var readOnly = Directory.CreateDirectory(Path.Combine(_outside, "ro")).FullName;

        await PrepareWithRecords(ProgramSpec(ProbeContainer) with { ReadwritePaths = [readwrite], ReadonlyPaths = [readOnly] });

        var store = new RuntimeStateStore(NullLogger<RuntimeStateStore>.Instance);
        Assert.Contains(readwrite, store.SweepRoots(_prefix));
        Assert.Contains(readOnly, store.SweepRoots(_prefix));
        Assert.Contains(_prefix, store.OrphanPrefixes(new HashSet<string>()));
        Assert.DoesNotContain(_prefix, store.OrphanPrefixes(new HashSet<string> { _prefix }));
    }

    [Fact]
    public async Task SupportFileStateFoldersAndStateFiles_AreCreated_AsUtf8WithoutABom()
    {
        var spec = NodeSpec();

        Assert.Same(spec, await Prepare(spec));

        Assert.True(Directory.Exists(AcTemp));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Preload), File.ReadAllBytes(Path.Combine(Support, "node-sandbox.cjs")));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(NuGetConfig), File.ReadAllBytes(NuGetConfigPath));
        Assert.Empty(Directory.EnumerateFiles(Support, "*.tmp"));
    }

    [Fact]
    public async Task AnIdenticalFile_IsNotRewritten_AChangedOneIs()
    {
        await Prepare(NodeSpec());
        var preload = Path.Combine(Support, "node-sandbox.cjs");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(preload, old);
        File.SetLastWriteTimeUtc(NuGetConfigPath, old);

        await Prepare(NodeSpec());

        Assert.Equal(old, File.GetLastWriteTimeUtc(preload));
        Assert.Equal(old, File.GetLastWriteTimeUtc(NuGetConfigPath));

        File.WriteAllText(preload, "tampered");
        File.WriteAllText(NuGetConfigPath, "tampered");
        await Prepare(NodeSpec());

        Assert.Equal(Preload, File.ReadAllText(preload));
        Assert.Equal(NuGetConfig, File.ReadAllText(NuGetConfigPath));
    }

    [Fact]
    public async Task AJunctionPlantedOnAStateFilesPath_IsReplaced_AndItsTargetUntouched()
    {
        Directory.CreateDirectory(Path.Combine(State, "home", "AppData"));
        var roaming = Path.Combine(State, "home", "AppData", "Roaming");
        StateFolderSafetyTests.Junction(roaming, _outside);

        await Prepare(NodeSpec());

        Assert.False(IsLink(roaming));
        Assert.Equal(NuGetConfig, File.ReadAllText(NuGetConfigPath));
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
        Assert.Equal("outside", File.ReadAllText(_outsideFile));
    }

    [Fact]
    public async Task AJunctionPlantedOnAStateFolder_IsReplaced()
    {
        Directory.CreateDirectory(State);
        var home = Path.Combine(State, "home");
        StateFolderSafetyTests.Junction(home, _outside);

        await Prepare(NodeSpec());

        Assert.False(IsLink(home));
        Assert.True(Directory.Exists(AcTemp));
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
    }

    [Fact]
    public async Task TheGrantRecord_HoldsToolchainProgramStateSupportAndPolicyFolders()
    {
        await Prepare(NodeSpec() with { ReadonlyPaths = [@"C:\data"] });

        var (container, roots, stateReadOnly, policyRoots) = Assert.Single(_store.Records);
        Assert.Equal(Container, container);
        Assert.Equal([@"C:\Users\Jane\node", @"C:\Users\Jane\mcp\server", State, Support], roots);
        Assert.Equal([@"C:\box", @"C:\data"], policyRoots);
        Assert.False(stateReadOnly);
    }

    [Fact]
    public async Task AFileOutsideTheStateAndSupportFolders_IsAProgrammingError()
    {
        var spec = NodeSpec(new PreparedFile(Path.Combine(_outside, "evil.txt"), "x"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(spec));

        Assert.False(File.Exists(Path.Combine(_outside, "evil.txt")));
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public async Task AFolderThatCannotBeWritten_GivesThePathFreePrepareError()
    {
        Directory.CreateDirectory(NuGetConfigPath);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(NodeSpec()));

        Assert.Equal("Pia could not prepare the server's runtime folder.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    [Fact]
    public async Task AGrantRecordThatCannotBeWritten_DoesNotStopAStartWithoutAStateFolder_AndLogsTheTypeOnly()
    {
        var logger = new CapturingLogger<RuntimeStateProvisioner>();
        _store.Failure = new IOException(@"Access to the path 'C:\Users\Jane\grants\pia-mcp-x.json' is denied.");
        var spec = ProgramSpec(Container);

        Assert.Same(spec, await Provisioner(logger).PrepareAsync(spec, _ => { }, TestContext.Current.CancellationToken));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(IOException), warning.Message);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains(@"C:\"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGrantRecordThatCannotBeWritten_FailsAStartWithAStateFolder(bool withPreparation)
    {
        _store.Failure = new IOException(@"Access to the path 'C:\Users\Jane\grants\pia-mcp-x.json' is denied.");
        var spec = withPreparation ? NodeSpec() : NodeSpec() with { Preparation = null };

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(spec));

        Assert.Equal("Pia could not prepare the server's runtime folder.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    [Fact]
    public async Task Uvx_ExitZero_GivesThePythonCommand_WritesTheSpecRecord_AndPrunesTheCaches()
    {
        _uv.Lines = ["Resolved 33 packages in 621ms"];

        var prepared = await Prepare(UvxSpec());

        Assert.Equal(Path.Combine(State, "tools", "mcp-server-time", "Scripts", "python.exe"), prepared.Command);
        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
        Assert.Single(_uv.Calls);
        Assert.Equal(["Resolved 33 packages in 621ms"], _stderr);
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Equal("mcp-server-time", record.RootElement.GetProperty("package").GetString());
        Assert.False(Directory.Exists(Path.Combine(State, "uv-cache")));
        Assert.False(Directory.Exists(Path.Combine(State, "python-cache")));
        Assert.True(Directory.Exists(Path.Combine(State, "tmp")));
        Assert.Single(_store.Records);
    }

    [Fact]
    public async Task Uvx_TheGrantRecord_IsWrittenBeforeTheInstall()
    {
        _uv.OnRun = () => Assert.Single(_store.Records);

        await Prepare(UvxSpec());

        Assert.Equal([State], _store.Records.Single().Roots);
        Assert.Equal([@"C:\box"], _store.Records.Single().PolicyRoots);
        Assert.True(_store.Records.Single().StateReadOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AnUnchangedSpec_KeepsTheInstalledTool_WithoutRunningUv(bool offline)
    {
        TrustTheStateFolder();
        WriteSpecRecord("mcp-server-time");
        var tool = UvEntryPointResolverTests.InstallTimeTool(State);
        var marker = Path.Combine(tool, "marker.txt");
        File.WriteAllText(marker, "kept");

        var prepared = await Prepare(UvxSpec(offline));

        Assert.Empty(_uv.Calls);
        Assert.True(File.Exists(marker));
        Assert.Equal(Path.Combine(tool, "Scripts", "python.exe"), prepared.Command);
        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AnUnchangedSpecWithoutARunnableTool_DeletesToolsAndToolBin_BeforeUvRuns(bool onlyPythonMissing)
    {
        TrustTheStateFolder();
        WriteSpecRecord("mcp-server-time");
        var tool = UvEntryPointResolverTests.InstallTimeTool(State);
        File.Delete(onlyPythonMissing ? Path.Combine(tool, "Scripts", "python.exe") : Path.Combine(tool, "uv-receipt.toml"));
        Directory.CreateDirectory(Path.Combine(State, "tool-bin"));
        File.WriteAllText(Path.Combine(State, "tool-bin", "mcp-server-time.exe"), "old");
        bool? toolsExistedAtInstall = null;
        bool? toolBinExistedAtInstall = null;
        _uv.OnRun = () =>
        {
            toolsExistedAtInstall = Directory.Exists(Path.Combine(State, "tools"));
            toolBinExistedAtInstall = Directory.Exists(Path.Combine(State, "tool-bin"));
        };

        await Prepare(UvxSpec());

        Assert.Single(_uv.Calls);
        Assert.False(toolsExistedAtInstall);
        Assert.False(toolBinExistedAtInstall);
    }

    [Fact]
    public async Task Uvx_AChangedSpec_DeletesToolsAndToolBin_BeforeTheInstall()
    {
        TrustTheStateFolder();
        WriteSpecRecord("mcp-server-time", requirements: ["tzdata"]);
        UvEntryPointResolverTests.InstallTimeTool(State);
        Directory.CreateDirectory(Path.Combine(State, "tool-bin"));
        File.WriteAllText(Path.Combine(State, "tool-bin", "mcp-server-time.exe"), "old");
        bool? toolsExistedAtInstall = null;
        bool? toolBinExistedAtInstall = null;
        _uv.OnRun = () =>
        {
            toolsExistedAtInstall = Directory.Exists(Path.Combine(State, "tools"));
            toolBinExistedAtInstall = Directory.Exists(Path.Combine(State, "tool-bin"));
        };

        await Prepare(UvxSpec());

        Assert.False(toolsExistedAtInstall);
        Assert.False(toolBinExistedAtInstall);
        using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Equal(0, record.RootElement.GetProperty("with").GetArrayLength());
    }

    [Fact]
    public async Task Uvx_ADamagedSpecRecord_CountsAsChanged()
    {
        TrustTheStateFolder();
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "pia-uv-spec.json"), "{ not json");
        var marker = Path.Combine(UvEntryPointResolverTests.InstallTimeTool(State), "marker.txt");
        File.WriteAllText(marker, "old");

        await Prepare(UvxSpec());

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Uvx_AToolFileHeldByTheRunningServer_GivesTheInUseText()
    {
        TrustTheStateFolder();
        WriteSpecRecord("other-package");
        var tool = UvEntryPointResolverTests.InstallTimeTool(State);
        using var held = new FileStream(Path.Combine(tool, "Scripts", "python.exe"), FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec()));

        Assert.Equal("This server's package is in use by the running server. Stop the server, then try again.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.Empty(_uv.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AChangedSpec_WhileTheServersOtherContainerRuns_IsRefused_AndKeepsTheTool(bool probeRuns)
    {
        _store.Earlier[Container] = true;
        _store.Earlier[ProbeContainer] = true;
        WriteSpecRecord("mcp-server-time", python: "3.12");
        var tool = UvEntryPointResolverTests.InstallTimeTool(State);
        var (running, starting) = probeRuns ? (ProbeContainer, Container) : (Container, ProbeContainer);
        using var lease = SandboxContainerLease.Acquire(running);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => Prepare(UvxSpec() with { ContainerName = starting }));

        Assert.Equal("This server's package is in use by the running server. Stop the server, then try again.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.True(File.Exists(Path.Combine(tool, "uv-receipt.toml")));
        Assert.True(File.Exists(Path.Combine(tool, "Scripts", "python.exe")));
        Assert.True(Directory.Exists(Path.Combine(tool, "Lib", "site-packages", "mcp_server_time-2026.10.10.dist-info")));
        Assert.Contains("3.12", File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Empty(_uv.Calls);
    }

    private static UvxSpec TimeOnPython312 => new("mcp-server-time", "mcp-server-time", [], "3.12");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AChangedSpec_UnderItsOwnLeaseAlone_Reinstalls(bool probe)
    {
        var container = probe ? ProbeContainer : Container;
        await PrepareWithRecords(UvxSpec() with { ContainerName = container });

        var prepared = await PrepareWithRecords(UvxSpec(uvx: TimeOnPython312) with { ContainerName = container });

        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
        Assert.Contains("3.12", File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Equal(2, _uv.Calls.Count);
    }

    [Fact]
    public async Task Uvx_AChangedSpec_WhileAUvxStartOfTheServerRuns_IsRefused()
    {
        await PrepareWithRecords(UvxSpec());
        using var running = SandboxContainerLease.Acquire(Container);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => PrepareWithRecords(UvxSpec(uvx: TimeOnPython312) with { ContainerName = ProbeContainer }));

        Assert.Equal("This server's package is in use by the running server. Stop the server, then try again.", ex.Message);
        Assert.True(File.Exists(Path.Combine(State, "tools", "mcp-server-time", "uv-receipt.toml")));
        Assert.DoesNotContain("3.12", File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Single(_uv.Calls);
    }

    // A running server that was never given the state folder cannot be using the tool in it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AChangedSpec_WhileAProgramStartOfTheServerRuns_Installs(bool uvxBeforeTheProgram)
    {
        if (uvxBeforeTheProgram)
            await PrepareWithRecords(UvxSpec());
        await PrepareWithRecords(ProgramSpec(Container));
        using var running = SandboxContainerLease.Acquire(Container);

        var prepared = await PrepareWithRecords(UvxSpec(uvx: TimeOnPython312) with { ContainerName = ProbeContainer });

        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
        Assert.Contains("3.12", File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Equal(uvxBeforeTheProgram ? 2 : 1, _uv.Calls.Count);
    }

    // A runner start fails without its record, so a running container without one was given no state folder.
    [Fact]
    public async Task Uvx_WhileAContainerWithoutAGrantRecordRuns_Installs()
    {
        using var running = SandboxContainerLease.Acquire(Container);

        var prepared = await PrepareWithRecords(UvxSpec() with { ContainerName = ProbeContainer });

        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
        Assert.Single(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_ANonZeroExit_GivesThePathFreeError_KeepsTheCaches_AndForwardsTheOutput()
    {
        _uv.ExitCode = 1;
        _uv.Lines = [@"error: C:\Users\Jane\AppData\Local\Pia\McpSandbox\x.runtime has no wheel", "hint: sdist only"];

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec()));

        Assert.Equal(
            "uv could not install the package. Only packages published as wheels are supported; the server's error output has the details.",
            ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.Equal(_uv.Lines, _stderr);
        Assert.True(Directory.Exists(Path.Combine(State, "uv-cache")));
        Assert.True(Directory.Exists(Path.Combine(State, "python-cache")));
        Assert.False(File.Exists(Path.Combine(State, "pia-uv-spec.json")));
    }

    [Fact]
    public async Task Uvx_ExitZeroWithoutTheCommand_GivesTheMissingCommandText()
    {
        _uv.Install = false;

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec()));

        Assert.Equal("The installed package does not provide the command this server names.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    [Fact]
    public async Task Uvx_Offline_WithTheToolInstalled_SkipsTheInstall()
    {
        TrustTheStateFolder();
        WriteSpecRecord("mcp-server-time");
        UvEntryPointResolverTests.InstallTimeTool(State);

        var prepared = await Prepare(UvxSpec(offline: true));

        Assert.Empty(_uv.Calls);
        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
    }

    [Fact]
    public async Task Uvx_Offline_WithoutTheTool_RefusesWithoutRunningUv()
    {
        TrustTheStateFolder();
        WriteSpecRecord("mcp-server-time");

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec(offline: true)));

        Assert.Equal("Network access is off and this package is not installed yet. Allow network access for the first start.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.Empty(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_Offline_WithAnotherSpecInstalled_Refuses_AndKeepsTheOldTool()
    {
        TrustTheStateFolder();
        WriteSpecRecord("mcp-server-time", python: "3.12");
        var tool = UvEntryPointResolverTests.InstallTimeTool(State);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec(offline: true)));

        Assert.StartsWith("Network access is off", ex.Message);
        Assert.True(File.Exists(Path.Combine(tool, "uv-receipt.toml")));
        Assert.True(File.Exists(Path.Combine(tool, "Scripts", "python.exe")));
        Assert.Contains("3.12", File.ReadAllText(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Empty(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_AUvThatCannotStart_GivesPiaCouldNotRunUv()
    {
        _uv.Throw = new Win32Exception(2, @"C:\Users\Jane\.local\bin\uv.exe was not found");

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec()));

        Assert.Equal("Pia could not run uv.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
    }

    [Fact]
    public async Task Uvx_TheInstall_GetsTheRequestsPackageAndEnvironment()
    {
        await Prepare(UvxSpec(uvx: new UvxSpec("mcp-server-time", "mcp-server-time", ["tzdata"], "3.13")));

        var info = Assert.Single(_uv.Calls);
        Assert.Equal(["tool", "install", "mcp-server-time", "--with", "tzdata", "--python", "3.13"], info.ArgumentList);
        Assert.Equal(Path.Combine(State, "tools"), info.Environment["UV_TOOL_DIR"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AfterAnNpxStart_ClearsTheStateFolder_BeforeUvRuns(bool npxWasTheProbe)
    {
        await PrepareWithRecords(NodeSpec() with
        {
            ContainerName = npxWasTheProbe ? ProbeContainer : Container,
            RunnerKind = RunnerKind.Npx,
        });
        var planted = Path.Combine(State, "python", "cpython-3.13", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, "planted");
        bool? plantedAtInstall = null;
        bool? homeAtInstall = null;
        _uv.OnRun = () =>
        {
            plantedAtInstall = File.Exists(planted);
            homeAtInstall = Directory.Exists(Path.Combine(State, "home"));
        };

        await PrepareWithRecords(UvxSpec());

        Assert.False(plantedAtInstall);
        Assert.False(homeAtInstall);
        Assert.False(File.Exists(planted));
    }

    [Fact]
    public async Task Uvx_AfterAUvxStart_KeepsTheInstalledTool()
    {
        await PrepareWithRecords(UvxSpec());
        var marker = Path.Combine(State, "tools", "mcp-server-time", "marker.txt");
        File.WriteAllText(marker, "kept");

        await PrepareWithRecords(UvxSpec());

        Assert.True(File.Exists(marker));
        Assert.Single(_uv.Calls);
    }

    private async Task AfterAnNpxProbeAndAUvxStart()
    {
        await PrepareWithRecords(NodeSpec() with { ContainerName = ProbeContainer, RunnerKind = RunnerKind.Npx });
        await PrepareWithRecords(UvxSpec());
    }

    [Fact]
    public async Task Uvx_AfterAnNpxProbe_ClearsOnce_ThenKeepsTheInstalledTool()
    {
        await AfterAnNpxProbeAndAUvxStart();
        var marker = Path.Combine(State, "tools", "mcp-server-time", "marker.txt");
        File.WriteAllText(marker, "kept");

        await PrepareWithRecords(UvxSpec());

        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Single(_uv.Calls);
        Assert.True(new RuntimeStateStore(NullLogger<RuntimeStateStore>.Instance).StateGrantedReadOnly(ProbeContainer));
    }

    [Fact]
    public async Task Uvx_AfterAnNpxProbe_ClearsOnce_ThenStartsOffline()
    {
        await AfterAnNpxProbeAndAUvxStart();

        var prepared = await PrepareWithRecords(UvxSpec(offline: true));

        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
        Assert.Single(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_AfterAnNpxProbe_ClearsOnce_ThenProbesWhileTheServerRuns()
    {
        await AfterAnNpxProbeAndAUvxStart();
        using var running = SandboxContainerLease.Acquire(Container);

        var prepared = await PrepareWithRecords(UvxSpec() with { ContainerName = ProbeContainer });

        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
        Assert.Single(_uv.Calls);
    }

    private async Task<string> AfterAUvxStartAndAnNpxProbe(params string[] planted)
    {
        await PrepareWithRecords(UvxSpec());
        await PrepareWithRecords(NodeSpec() with { ContainerName = ProbeContainer, RunnerKind = RunnerKind.Npx });
        var path = Path.Combine([State, .. planted]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "planted");
        return path;
    }

    [Fact]
    public async Task Uvx_AfterAnNpxProbeOfAUvxServer_ClearsTheStateFolder_BeforeUvRuns()
    {
        var planted = await AfterAUvxStartAndAnNpxProbe("python", "cpython-3.13", "python.exe");
        bool? plantedAtInstall = null;
        _uv.OnRun = () => plantedAtInstall = File.Exists(planted);

        await PrepareWithRecords(UvxSpec());

        Assert.False(plantedAtInstall);
    }

    [Fact]
    public async Task Uvx_Offline_AfterAnNpxProbeOfAUvxServer_ClearsTheStateFolder()
    {
        var planted = await AfterAUvxStartAndAnNpxProbe("tools", "mcp-server-time", "planted.txt");

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => PrepareWithRecords(UvxSpec(offline: true)));

        Assert.Equal("Network access is off and this package is not installed yet. Allow network access for the first start.", ex.Message);
        Assert.False(File.Exists(planted));
    }

    [Fact]
    public async Task Uvx_AfterAnNpxProbeOfAUvxServer_WhileTheServerRuns_IsRefused_AndTouchesNothing()
    {
        var planted = await AfterAUvxStartAndAnNpxProbe("tools", "mcp-server-time", "planted.txt");
        using var running = SandboxContainerLease.Acquire(Container);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => PrepareWithRecords(UvxSpec() with { ContainerName = ProbeContainer }));

        Assert.Equal("This server's package is in use by the running server. Stop the server, then try again.", ex.Message);
        Assert.True(File.Exists(planted));
        Assert.Single(_uv.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uvx_AfterAnNpxStartThenAProgramStart_StillClearsTheStateFolder_BeforeUvRuns(bool probe)
    {
        var container = probe ? ProbeContainer : Container;
        await PrepareWithRecords(NodeSpec() with { ContainerName = container, RunnerKind = RunnerKind.Npx });
        await PrepareWithRecords(ProgramSpec(container));
        var planted = Path.Combine(State, "python", "cpython-3.13", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        File.WriteAllText(planted, "planted");
        bool? plantedAtInstall = null;
        _uv.OnRun = () => plantedAtInstall = File.Exists(planted);

        await PrepareWithRecords(UvxSpec());

        Assert.False(plantedAtInstall);
    }

    [Fact]
    public async Task Uvx_AfterAProgramProbe_KeepsTheStateFolder()
    {
        await PrepareWithRecords(UvxSpec());
        await PrepareWithRecords(ProgramSpec(ProbeContainer));
        var marker = Path.Combine(State, "marker.txt");
        File.WriteAllText(marker, "kept");

        await PrepareWithRecords(UvxSpec());

        Assert.True(File.Exists(marker));
        Assert.Single(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_WithoutAGrantRecord_ClearsANonEmptyStateFolder()
    {
        Directory.CreateDirectory(State);
        var leftover = Path.Combine(State, "leftover.txt");
        File.WriteAllText(leftover, "x");
        bool? leftoverAtInstall = null;
        _uv.OnRun = () => leftoverAtInstall = File.Exists(leftover);

        await PrepareWithRecords(UvxSpec());

        Assert.False(leftoverAtInstall);
    }

    [Fact]
    public async Task Uvx_ALinkPlantedInTheStateFolder_CountsAsContent()
    {
        await PrepareWithRecords(NodeSpec() with { RunnerKind = RunnerKind.Npx });
        StateFolderSafety.DeleteTree(State);
        Directory.CreateDirectory(State);
        var python = Path.Combine(State, "python");
        StateFolderSafetyTests.Junction(python, _outside);

        await PrepareWithRecords(UvxSpec());

        Assert.False(Directory.Exists(python));
        Assert.Equal("outside", File.ReadAllText(_outsideFile));
    }

    [Fact]
    public async Task Uvx_APlantedSpecRecordAndTool_DoNotPreventTheClear()
    {
        await PrepareWithRecords(NodeSpec() with { RunnerKind = RunnerKind.Npx });
        WriteSpecRecord("mcp-server-time");
        var planted = Path.Combine(UvEntryPointResolverTests.InstallTimeTool(State), "planted.txt");
        File.WriteAllText(planted, "planted");

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => PrepareWithRecords(UvxSpec(offline: true)));

        Assert.Equal("Network access is off and this package is not installed yet. Allow network access for the first start.", ex.Message);
        Assert.False(File.Exists(planted));
        Assert.False(File.Exists(Path.Combine(State, "pia-uv-spec.json")));
        Assert.Empty(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_WhileAContainerThatCanWriteTheFolderRuns_IsRefused_AndTouchesNothing()
    {
        await PrepareWithRecords(NodeSpec() with { RunnerKind = RunnerKind.Npx });
        var inUse = Path.Combine(State, "home", "keep.txt");
        File.WriteAllText(inUse, "in use");
        using var running = SandboxContainerLease.Acquire(Container);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(
            () => PrepareWithRecords(UvxSpec() with { ContainerName = ProbeContainer }));

        Assert.Equal("This server's package is in use by the running server. Stop the server, then try again.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.True(File.Exists(inUse));
        Assert.Empty(_uv.Calls);
    }

    private sealed class RecordingStore : IRuntimeStateStore
    {
        public List<(string Container, IReadOnlyList<string> Roots, bool StateReadOnly, IReadOnlyList<string> PolicyRoots)> Records { get; } = [];

        /// <summary>The state access of earlier starts that were given the state folder, by container name.</summary>
        public Dictionary<string, bool> Earlier { get; } = [];

        public Exception? Failure { get; set; }

        public void WriteGrantRecord(
            string containerName, IReadOnlyList<string> roots, bool stateReadOnly = false, IReadOnlyList<string>? policyRoots = null)
        {
            if (Failure is not null)
                throw Failure;
            Records.Add((containerName, roots, stateReadOnly, policyRoots ?? []));
        }

        public bool? StateGrantedReadOnly(string containerName)
        {
            var written = Records.FindLast(record => record.Container == containerName);
            if (written.Container is not null)
                return written.StateReadOnly;
            return Earlier.TryGetValue(containerName, out var readOnly) ? readOnly : null;
        }

        public bool StateGranted(string containerName)
        {
            var written = Records.FindLast(record => record.Container == containerName);
            if (written.Container is not null)
                return written.Roots.Contains(PiaPaths.McpRuntimeStateDirectory(SandboxLaunchSpecBuilder.PrefixOf(containerName)));
            return Earlier.ContainsKey(containerName);
        }

        public IReadOnlyList<string> SweepRoots(string prefix) => [];

        public IReadOnlyList<string> RuntimeRoots(string prefix) => [];

        public bool TryDelete(string prefix, Action? beforeDelete = null) => true;

        public IReadOnlyList<string> OrphanPrefixes(IReadOnlySet<string> knownPrefixes) => [];
    }

    private sealed class FakeUv
    {
        public int ExitCode { get; set; }

        public string[] Lines { get; set; } = [];

        public Exception? Throw { get; set; }

        public bool Install { get; set; } = true;

        public Action? OnRun { get; set; }

        public List<ProcessStartInfo> Calls { get; } = [];

        public Task<int> Run(ProcessStartInfo info, Action<string> outputLine, CancellationToken cancellationToken)
        {
            Calls.Add(info);
            if (Throw is not null)
                throw Throw;

            OnRun?.Invoke();
            foreach (var line in Lines)
                outputLine(line);

            var state = info.WorkingDirectory;
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(state, "uv-cache", "wheels")).FullName, "w.whl"), "w");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(state, "python-cache")).FullName, "cpython.tar.zst"), "p");
            if (ExitCode == 0 && Install)
                UvEntryPointResolverTests.InstallTimeTool(state);

            return Task.FromResult(ExitCode);
        }
    }
}
