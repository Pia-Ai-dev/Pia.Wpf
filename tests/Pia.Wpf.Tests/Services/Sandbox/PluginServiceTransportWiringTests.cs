using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Services.Exceptions;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>Probe and activation must take the same launch path, or a server that tests fine starts differently.</summary>
public sealed class PluginServiceTransportWiringTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-wiring-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    private static readonly McpSandboxPolicy Policy = new([@"C:\box\rw"], [@"C:\box\ro"], AllowNetwork: false);

    private static LocalMcpDefinition Definition() =>
        new("Files", @"C:\Tools\fs\fs.exe", ["--x"], new Dictionary<string, string>(), null, "files", null, Sandbox: Policy);

    private PluginService CreateService(IMcpTransportFactory factory)
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
            mxcAvailability: new ReadyAvailability());
    }

    private sealed class ReadyAvailability : IMxcAvailability
    {
        public MxcAvailabilityStatus Status { get; } = new(MxcRuntimeState.Ready);

        public event EventHandler<MxcAvailabilityStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<MxcAvailabilityStatus> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task<MxcAvailabilityStatus> InstallRuntimeAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);
    }

    private sealed class RecordingFactory : IMcpTransportFactory
    {
        public List<McpTransportRequest> Requests { get; } = [];

        public Exception? Refuse { get; init; }

        public IClientTransport Create(McpTransportRequest request)
        {
            Requests.Add(request);
            if (Refuse is not null) throw Refuse;
            return new DeadTransport();
        }
    }

    private sealed class DeadTransport : IClientTransport
    {
        public string Name => "dead";

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new IOException("dead transport");
    }

    [Fact]
    public async Task TheProbe_GoesThroughTheFactory_WithThePolicy_AndAProbeFlag()
    {
        var factory = new RecordingFactory();

        var result = await CreateService(factory).ProbeLocalMcpAsync(Definition(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        var request = Assert.Single(factory.Requests);
        Assert.True(request.IsProbe);
        Assert.Same(Policy, request.Sandbox);
        Assert.Equal(@"C:\Tools\fs\fs.exe", request.Command);
    }

    [Fact]
    public async Task Activation_GoesThroughTheSameFactory_WithThePolicy_AndNoProbeFlag()
    {
        var factory = new RecordingFactory();

        await CreateService(factory).SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        var request = Assert.Single(factory.Requests);
        Assert.False(request.IsProbe);
        Assert.Equal(Policy.ReadwritePaths, request.Sandbox!.ReadwritePaths);
        Assert.Equal(Policy.ReadonlyPaths, request.Sandbox.ReadonlyPaths);
        Assert.Equal("files", request.ToolPrefix);
    }

    [Fact]
    public async Task ARestartedSandboxedServer_ReachesTheFactoryWithItsSavedPolicy()
    {
        await CreateService(new RecordingFactory()).SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);
        var factory = new RecordingFactory();

        await CreateService(factory).InitializePersistedPluginsAsync();

        Assert.NotEmpty(factory.Requests);
        Assert.All(factory.Requests, r => Assert.Equal(Policy.ReadwritePaths, r.Sandbox!.ReadwritePaths));
    }

    [Fact]
    public async Task WhenTheFactoryRefuses_TheServerIsNotRunning_AndTheReasonIsShown()
    {
        var factory = new RecordingFactory { Refuse = new SandboxStartException("Sandboxing is not available on this PC.") };
        var service = CreateService(factory);

        var id = await service.SaveLocalMcpAsync(null, Definition(), TestContext.Current.CancellationToken);

        var status = service.GetLocalMcpStatus(id);
        Assert.False(status.IsRunning);
        Assert.Contains("not available on this PC", status.Error);
    }

    private static LocalMcpDefinition Server(string name, McpSandboxPolicy? sandbox, bool enabled = true) =>
        new(name, "uvx", ["mcp-server-time"], new Dictionary<string, string>(), null,
            LocalMcpConfig.DeriveToolPrefix(name), null, DefaultEnabled: enabled, Sandbox: sandbox);

    // Any of them may have planted the uv another server names; a switched-off one may have run before.
    [Fact]
    public async Task EverySandboxedStartAndProbe_CarriesTheReadwriteFoldersOfEverySavedSandboxedServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingFactory();
        var service = CreateService(factory);
        await service.SaveLocalMcpAsync(null, Server("Writer", new([@"D:\tools"], [@"D:\reference"], true)), ct);
        await service.SaveLocalMcpAsync(null, Server("Off", new([@"E:\shared"], [], true), enabled: false), ct);
        await service.SaveLocalMcpAsync(null, Server("Bare", sandbox: null), ct);
        await service.SaveLocalMcpAsync(null, Server("Time", McpSandboxPolicy.Empty), ct);
        await service.ProbeLocalMcpAsync(Server("Probe", new([@"F:\probe"], [], true)), ct);

        string[] expected = [@"D:\tools", @"E:\shared"];
        var time = Assert.Single(factory.Requests, r => r.Name == "Time");
        Assert.Equal(expected, time.SandboxedReadwriteFolders!.Order(StringComparer.OrdinalIgnoreCase));
        var probe = Assert.Single(factory.Requests, r => r.IsProbe);
        Assert.Equal(expected, probe.SandboxedReadwriteFolders!.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Null(Assert.Single(factory.Requests, r => r.Name == "Bare").SandboxedReadwriteFolders);
    }

    // A saved server can hold a folder the guard always refuses; it never starts, so it never wrote there.
    [Fact]
    public async Task AReadwriteFolderTheGuardNeverGrants_IsLeftOutOfTheSandboxedFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new RecordingFactory();
        var service = CreateService(factory);
        await service.SaveLocalMcpAsync(null, Server("Writer", new([@"D:\tools"], [], true)), ct);
        await service.SaveLocalMcpAsync(null,
            Server("Never", new([PiaPaths.UserProfileRoot, @"\\server\share\x", @"relative\x"], [], true), enabled: false), ct);

        Assert.Equal([@"D:\tools"], service.GetSandboxedReadwriteFolders());
    }

    [Fact]
    public async Task WhenTheFactoryRefusesTheProbe_TheReasonIsTheResult()
    {
        var factory = new RecordingFactory { Refuse = new SandboxStartException("Sandboxing is not available on this PC.") };

        var result = await CreateService(factory).ProbeLocalMcpAsync(Definition(), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not available on this PC", result.Error);
    }
}
