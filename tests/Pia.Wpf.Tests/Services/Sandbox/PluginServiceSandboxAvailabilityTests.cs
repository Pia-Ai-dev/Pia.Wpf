using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>A server saved as sandboxed never starts uncontained: whatever the runtime is doing, and however
/// its saved settings look, the only way a process starts is through the factory with the policy.</summary>
public sealed class PluginServiceSandboxAvailabilityTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-sandbox-avail-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    private static readonly McpSandboxPolicy Policy = new([@"C:\box\rw"], [], AllowNetwork: false);

    private static LocalMcpDefinition Definition(McpSandboxPolicy? sandbox, bool enabled = true, string name = "Files") =>
        new(name, @"C:\Tools\fs\fs.exe", [], new Dictionary<string, string>(), null,
            LocalMcpConfig.DeriveToolPrefix(name), null, DefaultEnabled: enabled, Sandbox: sandbox);

    private sealed class FakeAvailability(MxcAvailabilityStatus initial) : IMxcAvailability
    {
        private MxcAvailabilityStatus _status = initial;

        public int StatusReads;
        public int InstallCalls;

        public MxcAvailabilityStatus Status
        {
            get
            {
                Interlocked.Increment(ref StatusReads);
                return _status;
            }
        }

        public event EventHandler<MxcAvailabilityStatus>? StatusChanged;

        public void Change(MxcAvailabilityStatus next)
        {
            _status = next;
            StatusChanged?.Invoke(this, next);
        }

        public Task<MxcAvailabilityStatus> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(_status);

        public Task<MxcAvailabilityStatus> InstallRuntimeAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref InstallCalls);
            return Task.FromResult(_status);
        }
    }

    private sealed class RecordingFactory : IMcpTransportFactory
    {
        public List<McpTransportRequest> Requests { get; } = [];

        public List<string>? Log { get; init; }

        public IClientTransport Create(McpTransportRequest request)
        {
            lock (Requests)
                Requests.Add(request);
            Log?.Add("start");
            return new DeadTransport();
        }

        public int Count
        {
            get
            {
                lock (Requests)
                    return Requests.Count;
            }
        }
    }

    private sealed class DeadTransport : IClientTransport
    {
        public string Name => "dead";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("dead transport");
    }

    private sealed class RecordingProfiles : IAppContainerProfileService
    {
        public List<string> Deleted { get; } = [];

        public List<string>? Log { get; init; }

        public bool DeleteServerProfiles(string toolPrefix)
        {
            Deleted.Add(toolPrefix);
            Log?.Add("profile:" + toolPrefix);
            return true;
        }
    }

    private sealed class RecordingSweeper : IAceSweeper
    {
        public List<(List<string> Roots, List<string> Names)> Calls { get; } = [];

        public List<string>? Log { get; init; }

        public bool Throw { get; init; }

        public AceSweepResult Sweep(IEnumerable<string> roots, IEnumerable<string> containerNames)
        {
            Calls.Add(([.. roots], [.. containerNames]));
            Log?.Add("sweep");
            return Throw ? throw new IOException("sweep failed") : new AceSweepResult(1, 0, 0);
        }
    }

    private PluginService CreateService(
        RecordingFactory factory, FakeAvailability? availability, IAppContainerProfileService? profiles = null, IAceSweeper? sweeper = null)
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);

        return new PluginService(
            Substitute.For<IMemoryToolHandler>(),
            Substitute.For<ITodoToolHandler>(),
            Substitute.For<IReminderToolHandler>(),
            Substitute.For<IScheduledJobToolHandler>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IIngestToolHandler>(),
            Substitute.For<IGitToolHandler>(),
            Substitute.For<IChatHistoryToolHandler>(),
            Substitute.For<IAssignmentToolHandler>(),
            Substitute.For<IScreenCaptureToolHandler>(),
            Substitute.For<IHelpToolHandler>(),
            Substitute.For<IKnowledgeManagerToolHandler>(),
            Substitute.For<IAssignmentSurfaceCache>(),
            Substitute.For<IKnowledgeManagerSurfaceCache>(),
            SettingsStubs.Returning(),
            NullLogger<PluginService>.Instance,
            sqlite,
            cabManager: null,
            dpapiHelper: new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            transportFactory: factory,
            mxcAvailability: availability,
            profileService: profiles,
            aceSweeper: sweeper);
    }

    /// <summary>A row written straight to the database, as a hand edit or a damaged save would leave it.</summary>
    private Guid SeedRow(string configJson, bool enabled = true, string name = "Files")
    {
        var id = Guid.NewGuid();
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        using var command = sqlite.GetConnection().CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Plugins (Id, Kind, Name, Description, IconUrl, ConfigJson, Version, IsPreloaded, IsActive, UserEnabled, UpdatedAt)
            VALUES (@Id, 'mcp_server', @Name, NULL, NULL, @ConfigJson, '1.0.0', 0, 1, @UserEnabled, @UpdatedAt)
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Name", name);
        command.Parameters.AddWithValue("@ConfigJson", configJson);
        command.Parameters.AddWithValue("@UserEnabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        return id;
    }

    private static string ConfigJson(LocalMcpDefinition definition) => LocalMcpConfig.ToConfigJson(definition, v => v);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "the condition was never met");
    }

    private static readonly MxcAvailabilityStatus Ready = new(MxcRuntimeState.Ready);
    private static readonly MxcAvailabilityStatus NotInstalled = new(MxcRuntimeState.NotInstalled);

    [Fact]
    public async Task ASandboxedServer_WhenTheRuntimeIsNotInstalled_IsNotRunning_StartsTheDownload_AndLaunchesNothing()
    {
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(NotInstalled);
        var service = CreateService(factory, availability);

        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        var status = service.GetLocalMcpStatus(id);
        Assert.False(status.IsRunning);
        Assert.Contains("not installed", status.Error);
        Assert.Equal(0, factory.Count);
        await Eventually(() => availability.InstallCalls == 1);
    }

    [Fact]
    public async Task AtStartup_AnEnabledSandboxedServer_StartsTheDownload_AndRunsOnceTheRuntimeIsReady()
    {
        var id = SeedRow(ConfigJson(Definition(Policy)));
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(NotInstalled);
        var service = CreateService(factory, availability);

        await service.InitializePersistedPluginsAsync();

        Assert.Equal(0, factory.Count);
        Assert.Equal(1, availability.InstallCalls);
        availability.Change(Ready);
        await Eventually(() => factory.Count == 1);
        Assert.Equal(Policy.ReadwritePaths, factory.Requests[0].Sandbox!.ReadwritePaths);
        Assert.NotNull(service.GetLocalMcpDefinition(id));
    }

    [Fact]
    public async Task AtStartup_ADisabledSandboxedServer_NeverDownloadsTheRuntime()
    {
        SeedRow(ConfigJson(Definition(Policy, enabled: false)), enabled: false);
        var availability = new FakeAvailability(NotInstalled);

        await CreateService(new RecordingFactory(), availability).InitializePersistedPluginsAsync();

        Assert.Equal(0, availability.InstallCalls);
        Assert.Equal(0, availability.StatusReads);
    }

    [Fact]
    public async Task AtStartup_WithNoSandboxedServer_NeverReadsTheStatusOrDownloads()
    {
        SeedRow(ConfigJson(Definition(sandbox: null)));
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(NotInstalled);

        await CreateService(factory, availability).InitializePersistedPluginsAsync();

        Assert.Equal(1, factory.Count);
        Assert.Equal(0, availability.InstallCalls);
        Assert.Equal(0, availability.StatusReads);
    }

    [Fact]
    public async Task WhileTheRuntimeDownloads_TheServerSaysSo_AndNothingStarts()
    {
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(new MxcAvailabilityStatus(MxcRuntimeState.Downloading));
        var service = CreateService(factory, availability);

        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        Assert.Contains("Downloading", service.GetLocalMcpStatus(id).Error);
        Assert.Equal(0, factory.Count);
        Assert.Equal(0, availability.InstallCalls);
    }

    [Fact]
    public async Task OnAnUnsupportedHost_TheReasonIsShown_AndNothingStarts()
    {
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(new MxcAvailabilityStatus(MxcRuntimeState.Unsupported, "This edition of Windows cannot create sandboxes."));
        var service = CreateService(factory, availability);

        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        Assert.Equal("This edition of Windows cannot create sandboxes.", service.GetLocalMcpStatus(id).Error);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task WithoutAnAvailabilityService_ASandboxedServerIsNeverStarted()
    {
        var factory = new RecordingFactory();
        var service = CreateService(factory, availability: null);

        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        Assert.False(service.GetLocalMcpStatus(id).IsRunning);
        Assert.Contains("not available", service.GetLocalMcpStatus(id).Error);
        Assert.Equal(0, factory.Count);
    }

    [Fact]
    public async Task WhenTheDownloadFails_TheServersReasonBecomesTheFailure()
    {
        var availability = new FakeAvailability(NotInstalled);
        var service = CreateService(new RecordingFactory(), availability);
        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        availability.Change(new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled, "The sandbox runtime could not be downloaded: offline"));

        await Eventually(() => service.GetLocalMcpStatus(id).Error?.Contains("offline") == true);
    }

    [Fact]
    public async Task AReadyRuntime_LetsTheServerThroughTheFactoryWithItsPolicy()
    {
        var factory = new RecordingFactory();
        var service = CreateService(factory, new FakeAvailability(Ready));

        await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        Assert.Equal(Policy.ReadwritePaths, Assert.Single(factory.Requests).Sandbox!.ReadwritePaths);
    }

    [Fact]
    public async Task AnUncontainedServer_DoesNotNeedTheRuntime()
    {
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(NotInstalled);

        await CreateService(factory, availability).SaveLocalMcpAsync(null, Definition(sandbox: null), TestContext.Current.CancellationToken);

        Assert.Equal(1, factory.Count);
        Assert.Null(factory.Requests[0].Sandbox);
        Assert.Equal(0, availability.InstallCalls);
    }

    [Fact]
    public async Task TheProbeOfASandboxedServer_WhenTheRuntimeIsNotReady_FailsWithTheReason_AndStartsNoDownload()
    {
        var factory = new RecordingFactory();
        var availability = new FakeAvailability(NotInstalled);

        var result = await CreateService(factory, availability).ProbeLocalMcpAsync(Definition(Policy), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not installed", result.Error);
        Assert.Equal(0, factory.Count);
        Assert.Equal(0, availability.InstallCalls);
    }

    [Fact]
    public async Task TheProbeOfASandboxedServer_WhenTheRuntimeIsReady_GoesThroughTheFactory()
    {
        var factory = new RecordingFactory();

        await CreateService(factory, new FakeAvailability(Ready)).ProbeLocalMcpAsync(Definition(Policy), TestContext.Current.CancellationToken);

        var request = Assert.Single(factory.Requests);
        Assert.True(request.IsProbe);
        Assert.NotNull(request.Sandbox);
    }

    [Fact]
    public async Task ARowWhoseSettingsCannotBeRead_IsNotStarted_AndSaysWhy()
    {
        var json = """{"source":"local","transport":"stdio","command":"C:\\Tools\\fs\\fs.exe","args":[],"toolPrefix":"files","env":{"A":5},"sandbox":{"readwrite":["C:\\box"],"readonly":[],"network":"deny"}}""";
        var id = SeedRow(json);
        var factory = new RecordingFactory();
        var service = CreateService(factory, new FakeAvailability(Ready));

        await service.InitializePersistedPluginsAsync();

        Assert.Equal(0, factory.Count);
        Assert.False(service.GetLocalMcpStatus(id).IsRunning);
        Assert.Contains("could not be read", service.GetLocalMcpStatus(id).Error);
    }

    [Fact]
    public async Task ARowWhoseJsonIsDamaged_StartsNothing()
    {
        var json = ConfigJson(Definition(Policy));
        SeedRow(json[..^20]);
        var factory = new RecordingFactory();

        await CreateService(factory, new FakeAvailability(Ready)).InitializePersistedPluginsAsync();

        Assert.Equal(0, factory.Count);
    }

    /// <summary>The damaged block must not read as "no sandbox": the server still starts, contained, with nothing granted.</summary>
    [Fact]
    public async Task ARowWhoseSandboxBlockIsDamaged_StillStartsContained_WithNothingGranted()
    {
        var json = ConfigJson(Definition(Policy));
        var start = json.IndexOf("\"sandbox\":", StringComparison.Ordinal);
        SeedRow(json[..start] + "\"sandbox\":\"garbage\"}");
        var factory = new RecordingFactory();

        await CreateService(factory, new FakeAvailability(Ready)).InitializePersistedPluginsAsync();

        var request = Assert.Single(factory.Requests);
        Assert.NotNull(request.Sandbox);
        Assert.Empty(request.Sandbox.ReadwritePaths);
        Assert.Empty(request.Sandbox.ReadonlyPaths);
        Assert.False(request.Sandbox.AllowNetwork);
    }

    [Fact]
    public async Task RemovingAServer_DeletesItsContainerProfiles()
    {
        var profiles = new RecordingProfiles();
        var service = CreateService(new RecordingFactory(), new FakeAvailability(Ready), profiles);
        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);

        await service.RemoveLocalMcpAsync(id);

        Assert.Equal(["files"], profiles.Deleted);
        Assert.Empty(service.GetLocalMcpPlugins());
    }

    [Fact]
    public async Task AtStartup_LeakedAcesAreSweptBeforeTheFirstServerStarts()
    {
        var log = new List<string>();
        var sweeper = new RecordingSweeper { Log = log };
        SeedRow(ConfigJson(Definition(Policy) with { WorkingDirectory = @"C:\box\rw\work" }));

        await CreateService(new RecordingFactory { Log = log }, new FakeAvailability(Ready), sweeper: sweeper)
            .InitializePersistedPluginsAsync();

        Assert.Equal(["sweep", "start"], log);
        var call = Assert.Single(sweeper.Calls);
        Assert.Contains(@"C:\box\rw", call.Roots);
        Assert.Contains(@"C:\box\rw\work", call.Roots);
        Assert.Equal(["pia-mcp-files", "pia-mcp-files-probe"], call.Names);
    }

    [Fact]
    public async Task AtStartup_ADisabledSandboxedServer_IsSweptToo_BecauseItsLeakIsStillThere()
    {
        var sweeper = new RecordingSweeper();
        SeedRow(ConfigJson(Definition(Policy, enabled: false)), enabled: false);

        await CreateService(new RecordingFactory(), new FakeAvailability(NotInstalled), sweeper: sweeper).InitializePersistedPluginsAsync();

        Assert.Single(sweeper.Calls);
    }

    [Fact]
    public async Task AtStartup_WithNoSandboxedServer_NothingIsSwept()
    {
        var sweeper = new RecordingSweeper();
        SeedRow(ConfigJson(Definition(sandbox: null)));

        await CreateService(new RecordingFactory(), new FakeAvailability(Ready), sweeper: sweeper).InitializePersistedPluginsAsync();

        Assert.Empty(sweeper.Calls);
    }

    [Fact]
    public async Task ASweepThatFails_DoesNotStopTheServersFromStarting()
    {
        var factory = new RecordingFactory();
        SeedRow(ConfigJson(Definition(Policy)));

        await CreateService(factory, new FakeAvailability(Ready), sweeper: new RecordingSweeper { Throw = true })
            .InitializePersistedPluginsAsync();

        Assert.Equal(1, factory.Count);
    }

    [Fact]
    public async Task RemovingASandboxedServer_SweepsItsFolders_BeforeItsProfilesAreDeleted()
    {
        var log = new List<string>();
        var sweeper = new RecordingSweeper { Log = log };
        var service = CreateService(new RecordingFactory(), new FakeAvailability(Ready), new RecordingProfiles { Log = log }, sweeper);
        var id = await service.SaveLocalMcpAsync(null, Definition(Policy), TestContext.Current.CancellationToken);
        log.Clear();

        await service.RemoveLocalMcpAsync(id);

        Assert.Equal(["sweep", "profile:files"], log);
    }

    [Fact]
    public async Task RemovingAnUncontainedServer_SweepsNothing()
    {
        var sweeper = new RecordingSweeper();
        var service = CreateService(new RecordingFactory(), new FakeAvailability(Ready), new RecordingProfiles(), sweeper);
        var id = await service.SaveLocalMcpAsync(null, Definition(sandbox: null), TestContext.Current.CancellationToken);

        await service.RemoveLocalMcpAsync(id);

        Assert.Empty(sweeper.Calls);
    }

    [Fact]
    public async Task RemovingAServerThatWasNeverSandboxed_AlsoCleansUp_BecauseItMayOnceHaveBeen()
    {
        var profiles = new RecordingProfiles();
        var service = CreateService(new RecordingFactory(), new FakeAvailability(Ready), profiles);
        var id = await service.SaveLocalMcpAsync(null, Definition(sandbox: null, name: "Plain"), TestContext.Current.CancellationToken);

        await service.RemoveLocalMcpAsync(id);

        Assert.Equal(["plain"], profiles.Deleted);
    }
}
