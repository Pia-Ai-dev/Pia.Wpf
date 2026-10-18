using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Pia.Infrastructure;
using Pia.Paths;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>The leak sweeps read the grant records a start leaves, so these run against the real store.</summary>
[Collection("PiaPathsStatic")]
public sealed class PluginServiceGrantRecordSweepTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-grant-sweep-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];
    private readonly RuntimeStateStore _store = new(NullLogger<RuntimeStateStore>.Instance);
    private readonly RecordingSweeper _sweeper = new();
    private readonly string _folders;
    private readonly string _dropped;
    private readonly string _kept;
    private readonly string _program;

    public PluginServiceGrantRecordSweepTests(RedirectedProfileFixture profile)
    {
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxGrantsDirectory);
        _folders = Directory.CreateDirectory(Path.Combine(profile.Root, "folders-" + Guid.NewGuid().ToString("N"))).FullName;
        _dropped = Directory.CreateDirectory(Path.Combine(_folders, "dropped")).FullName;
        _kept = Directory.CreateDirectory(Path.Combine(_folders, "kept")).FullName;
        _program = Directory.CreateDirectory(Path.Combine(_folders, "program")).FullName;
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxRoot);
        StateFolderSafety.DeleteTree(PiaPaths.McpSandboxGrantsDirectory);
        TempPath.Remove(_folders);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LocalMcpDefinition Definition(params string[] readwrite) =>
        new("Files", @"C:\Tools\fs\fs.exe", [], new Dictionary<string, string>(), null,
            LocalMcpConfig.DeriveToolPrefix("Files"), null, Sandbox: new McpSandboxPolicy(readwrite, [], AllowNetwork: false));

    private PluginService CreateService()
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        return SandboxPluginServices.Create(
            sqlite, new DeadFactory(), new ReadyMxcAvailability(), sweeper: _sweeper, runtimeStateStore: _store);
    }

    // The record a start of an exe server leaves: its program folder, and its policy folders.
    private Task RecordAStart(string containerName, params string[] readwrite) =>
        new RuntimeStateProvisioner(_store, new UvToolRunner(NullLogger<UvToolRunner>.Instance), NullLogger<RuntimeStateProvisioner>.Instance)
            .PrepareAsync(
                new SandboxLaunchSpec(containerName, Path.Combine(_program, "fs.exe"), [], new Dictionary<string, string>(), readwrite[0],
                    readwrite, [], AllowNetwork: false, GrantedCommandDirectory: _program),
                _ => { },
                Ct);

    [Theory]
    [InlineData("pia-mcp-files")]
    [InlineData("pia-mcp-files-probe")]
    public async Task ASamePrefixSave_SweepsAPolicyFolderItDrops_ThoughTheLastStartsRecordHoldsIt(string startedContainer)
    {
        var service = CreateService();
        var id = await service.SaveLocalMcpAsync(null, Definition(_dropped, _kept), Ct);
        await RecordAStart(startedContainer, _dropped, _kept);
        _sweeper.Calls.Clear();

        await service.SaveLocalMcpAsync(id, Definition(_kept), Ct);

        var call = Assert.Single(_sweeper.Calls);
        Assert.Equal([_dropped], call.Roots);
        Assert.Equal(["pia-mcp-files", "pia-mcp-files-probe"], call.Names);
    }

    [Fact]
    public async Task AtStartup_APolicyFolderOnlyARecordHolds_IsStillSwept()
    {
        await CreateService().SaveLocalMcpAsync(null, Definition(_kept), Ct);
        await RecordAStart("pia-mcp-files-probe", _dropped, _kept);
        _sweeper.Calls.Clear();

        await CreateService().InitializePersistedPluginsAsync();

        var roots = Assert.Single(_sweeper.Calls).Roots;
        Assert.Contains(_dropped, roots);
        Assert.Contains(_kept, roots);
        Assert.Contains(_program, roots);
    }

    private sealed class RecordingSweeper : IAceSweeper
    {
        public List<(List<string> Roots, List<string> Names)> Calls { get; } = [];

        public AceSweepResult Sweep(IEnumerable<string> roots, IEnumerable<string> containerNames)
        {
            Calls.Add(([.. roots], [.. containerNames]));
            return new AceSweepResult(1, 0, 0);
        }
    }

    private sealed class DeadFactory : IMcpTransportFactory
    {
        public IClientTransport Create(McpTransportRequest request) => new DeadTransport();
    }

    private sealed class DeadTransport : IClientTransport
    {
        public string Name => "dead";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("dead transport");
    }
}
