using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
        _outside = Directory.CreateDirectory(Path.Combine(profile.Root, "outside-" + Guid.NewGuid().ToString("N"))).FullName;
        _outsideFile = Path.Combine(_outside, "keep.txt");
        File.WriteAllText(_outsideFile, "outside");
    }

    public void Dispose()
    {
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxSupportDirectory);
        TempPath.Remove(_outside);
    }

    private string Container => $"pia-mcp-{_prefix}";

    private string State => PiaPaths.McpRuntimeStateDirectory(_prefix);

    private string Support => PiaPaths.McpSandboxSupportDirectory;

    private string AcTemp => Path.Combine(State, "home", "AppData", "Local", "Packages", Container, "AC", "Temp");

    private string NuGetConfigPath => Path.Combine(State, "home", "AppData", "Roaming", "NuGet", "NuGet.Config");

    private RuntimeStateProvisioner Provisioner() =>
        new(_store, new UvToolRunner(NullLogger<UvToolRunner>.Instance, _uv.Run), NullLogger<RuntimeStateProvisioner>.Instance);

    private Task<SandboxLaunchSpec> Prepare(SandboxLaunchSpec spec) =>
        Provisioner().PrepareAsync(spec, line => { lock (_stderr) _stderr.Add(line); }, TestContext.Current.CancellationToken);

    private static SandboxLaunchSpec Base(string container) =>
        new(container, @"C:\Tools\node\node.exe", ["server.js"], new Dictionary<string, string>(), @"C:\box",
            [@"C:\box"], [], AllowNetwork: true, GrantedCommandDirectory: null);

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
    public async Task ASpecWithoutARunner_IsReturnedUnchanged_AndNothingIsWritten()
    {
        var spec = Base(Container) with { RuntimeStateDirectory = State, Preparation = NodeSpec().Preparation };

        Assert.Same(spec, await Prepare(spec));
        Assert.False(Directory.Exists(State));
        Assert.Empty(_store.Records);
    }

    [Fact]
    public async Task ARunnerWithoutAPreparation_IsReturnedUnchanged()
    {
        var spec = NodeSpec() with { Preparation = null };

        Assert.Same(spec, await Prepare(spec));
        Assert.Empty(_store.Records);
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
    public async Task TheGrantRecord_HoldsToolchainProgramStateAndSupportFolders()
    {
        await Prepare(NodeSpec());

        var (container, roots) = Assert.Single(_store.Records);
        Assert.Equal(Container, container);
        Assert.Equal([@"C:\Users\Jane\node", @"C:\Users\Jane\mcp\server", State, Support], roots);
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
    }

    [Fact]
    public async Task Uvx_AnUnchangedSpec_KeepsTheInstalledTool()
    {
        WriteSpecRecord("mcp-server-time");
        var marker = Path.Combine(UvEntryPointResolverTests.InstallTimeTool(State), "marker.txt");
        File.WriteAllText(marker, "kept");

        await Prepare(UvxSpec());

        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task Uvx_AChangedSpec_DeletesToolsAndToolBin_BeforeTheInstall()
    {
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
        WriteSpecRecord("other-package");
        var tool = UvEntryPointResolverTests.InstallTimeTool(State);
        using var held = new FileStream(Path.Combine(tool, "Scripts", "python.exe"), FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec()));

        Assert.Equal("This server's package is in use by the running server. Stop the server, then try again.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.Empty(_uv.Calls);
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
        WriteSpecRecord("mcp-server-time");
        UvEntryPointResolverTests.InstallTimeTool(State);

        var prepared = await Prepare(UvxSpec(offline: true));

        Assert.Empty(_uv.Calls);
        Assert.Equal(["-c", ExpectedCode, "--local-timezone", "UTC"], prepared.Args);
    }

    [Fact]
    public async Task Uvx_Offline_WithoutTheTool_RefusesWithoutRunningUv()
    {
        WriteSpecRecord("mcp-server-time");

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec(offline: true)));

        Assert.Equal("Network access is off and this package is not installed yet. Allow network access for the first start.", ex.Message);
        PathFreeAssert.IsPathFree(ex);
        Assert.Empty(_uv.Calls);
    }

    [Fact]
    public async Task Uvx_Offline_WithAnotherSpecInstalled_Refuses()
    {
        WriteSpecRecord("mcp-server-time", python: "3.12");
        UvEntryPointResolverTests.InstallTimeTool(State);

        var ex = await Assert.ThrowsAsync<SandboxStartException>(() => Prepare(UvxSpec(offline: true)));

        Assert.StartsWith("Network access is off", ex.Message);
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

    private sealed class RecordingStore : IRuntimeStateStore
    {
        public List<(string Container, IReadOnlyList<string> Roots)> Records { get; } = [];

        public void WriteGrantRecord(string containerName, IReadOnlyList<string> roots) => Records.Add((containerName, roots));

        public IReadOnlyList<string> SweepRoots(string prefix) => [];

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
