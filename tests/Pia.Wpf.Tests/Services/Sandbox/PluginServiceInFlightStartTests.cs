using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

/// <summary>A start can run for minutes. Whatever the user does meanwhile, it must not leave a server running that no
/// row in Settings can stop.</summary>
public sealed class PluginServiceInFlightStartTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static readonly SandboxLaunchSpec Spec = new(
        "pia-mcp-fake", @"C:\Tools\fake.exe", [], new Dictionary<string, string>(), @"C:\box",
        [@"C:\box"], [], AllowNetwork: false, GrantedCommandDirectory: null);

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-inflight-start-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];
    private readonly List<PluginService> _services = [];

    public void Dispose()
    {
        foreach (var service in _services)
            service.ShutdownAllAsync().Wait(Patience);
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every connect waits at the gate; one that ignores cancellation stands for a step past its last check.</summary>
    private sealed class GatedFactory(bool honorsCancellation) : IMcpTransportFactory
    {
        public bool HonorsCancellation { get; } = honorsCancellation;

        public FakeMxcProcessLauncher Launcher { get; } = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Named per server like a real start, and unique to this factory: leases are process-wide.
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

        public IClientTransport Create(McpTransportRequest request)
        {
            var verifier = Substitute.For<ISandboxGrantVerifier>();
            verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(true, 0, null));
            var spec = Spec with { ContainerName = SandboxLaunchSpecBuilder.ContainerName($"{request.ToolPrefix}-{_suffix}", request.IsProbe) };
            return new GatedTransport(this, new MxcClientTransport(spec, Launcher, verifier, NullLogger.Instance, NullLoggerFactory.Instance));
        }

        private sealed class GatedTransport(GatedFactory factory, IClientTransport inner) : IClientTransport
        {
            public string Name => inner.Name;

            public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
            {
                var token = factory.HonorsCancellation ? cancellationToken : CancellationToken.None;
                factory.Entered.TrySetResult();
                await factory.Release.Task.WaitAsync(token);
                return await inner.ConnectAsync(token);
            }
        }
    }

    /// <summary>Only the first connect waits at the gate, so a later start can finish while it is held.</summary>
    private sealed class FirstConnectHeldFactory : IMcpTransportFactory
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private int _creates;

        public FakeMxcProcessLauncher Launcher { get; } = new();

        /// <summary>Completes with the name of the server whose connect is held.</summary>
        public TaskCompletionSource<string> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IClientTransport Create(McpTransportRequest request)
        {
            var verifier = Substitute.For<ISandboxGrantVerifier>();
            verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(true, 0, null));
            var spec = Spec with { ContainerName = SandboxLaunchSpecBuilder.ContainerName($"{request.ToolPrefix}-{_suffix}", request.IsProbe) };
            var inner = new MxcClientTransport(spec, Launcher, verifier, NullLogger.Instance, NullLoggerFactory.Instance);
            return Interlocked.Increment(ref _creates) == 1 ? new HeldTransport(this, request.Name, inner) : inner;
        }

        private sealed class HeldTransport(FirstConnectHeldFactory factory, string server, IClientTransport inner) : IClientTransport
        {
            public string Name => inner.Name;

            public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
            {
                factory.Entered.TrySetResult(server);
                await factory.Release.Task.WaitAsync(cancellationToken);
                return await inner.ConnectAsync(cancellationToken);
            }
        }
    }

    private sealed class ChangingAvailability(MxcAvailabilityStatus initial) : IMxcAvailability
    {
        public MxcAvailabilityStatus Status { get; private set; } = initial;

        public event EventHandler<MxcAvailabilityStatus>? StatusChanged;

        public void Change(MxcAvailabilityStatus next)
        {
            Status = next;
            StatusChanged?.Invoke(this, next);
        }

        public Task<MxcAvailabilityStatus> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

        public Task<MxcAvailabilityStatus> InstallRuntimeAsync(
            IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Status);
    }

    private PluginService Create(IMcpTransportFactory factory, IMxcAvailability? availability = null)
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        var service = SandboxPluginServices.Create(sqlite, factory, availability ?? new ReadyMxcAvailability());
        _services.Add(service);
        return service;
    }

    private static LocalMcpDefinition Definition(string name = "Files", McpSandboxPolicy? sandbox = null) =>
        new(name, @"C:\Tools\fs\fs.exe", [], new Dictionary<string, string>(), null,
            LocalMcpConfig.DeriveToolPrefix(name), null, Sandbox: sandbox);

    private static async Task<(Guid Id, Task Saving)> SaveWhileTheStartWaitsAsync(PluginService service, GatedFactory factory)
    {
        var id = Guid.NewGuid();
        var saving = service.SaveLocalMcpAsync(id, Definition(), Ct);
        await factory.Entered.Task.WaitAsync(Patience, Ct);
        return (id, saving);
    }

    [Fact]
    public async Task RemovingAServerWhileItStarts_LeavesNoHandler_AndStopsWhatTheStartLaunched()
    {
        var factory = new GatedFactory(honorsCancellation: false);
        var service = Create(factory);
        var (id, saving) = await SaveWhileTheStartWaitsAsync(service, factory);

        var removing = service.RemoveLocalMcpAsync(id);
        factory.Release.SetResult();
        await Task.WhenAll(saving, removing).WaitAsync(Patience, Ct);

        Assert.DoesNotContain(service.ActiveHandlers, handler => handler.PluginId == id);
        Assert.Empty(service.GetLocalMcpPlugins());
        Assert.Equal(factory.Launcher.Launched.Count, factory.Launcher.ShutdownCalls);
    }

    [Fact]
    public async Task RemovingAServerWhileItStarts_CancelsTheStart_WithoutWaitingItOut()
    {
        var factory = new GatedFactory(honorsCancellation: true);
        var service = Create(factory);
        var (id, saving) = await SaveWhileTheStartWaitsAsync(service, factory);

        await service.RemoveLocalMcpAsync(id).WaitAsync(Patience, Ct);
        await saving.WaitAsync(Patience, Ct);

        Assert.DoesNotContain(service.ActiveHandlers, handler => handler.PluginId == id);
        Assert.Empty(factory.Launcher.Launched);
    }

    [Fact]
    public async Task SwitchingAServerOffWhileItStarts_LeavesNoHandler()
    {
        var factory = new GatedFactory(honorsCancellation: false);
        var service = Create(factory);
        var (id, saving) = await SaveWhileTheStartWaitsAsync(service, factory);

        var switching = service.SetPluginEnabledAsync(id, false);
        factory.Release.SetResult();
        await Task.WhenAll(saving, switching).WaitAsync(Patience, Ct);

        Assert.DoesNotContain(service.ActiveHandlers, handler => handler.PluginId == id);
        Assert.Single(service.GetLocalMcpPlugins());
        Assert.Equal(factory.Launcher.Launched.Count, factory.Launcher.ShutdownCalls);
    }

    [Fact]
    public async Task SavingAServerAgainWhileItStarts_KeepsOnlyTheNewStart()
    {
        var factory = new GatedFactory(honorsCancellation: false);
        var service = Create(factory);
        var (id, first) = await SaveWhileTheStartWaitsAsync(service, factory);

        var second = service.SaveLocalMcpAsync(id, Definition("Documents"), Ct);
        factory.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Patience, Ct);

        var handler = Assert.Single(service.ActiveHandlers, h => h.PluginId == id);
        Assert.Equal(["documents__echo"], handler.GetTools().Select(t => t.Name));
        Assert.Equal(factory.Launcher.Launched.Count - 1, factory.Launcher.ShutdownCalls);
    }

    [Fact]
    public async Task ASecondActivationWhileItStarts_JoinsTheStart_InsteadOfLaunchingAgain()
    {
        var factory = new GatedFactory(honorsCancellation: false);
        var service = Create(factory);
        var (id, saving) = await SaveWhileTheStartWaitsAsync(service, factory);

        var enabling = service.SetPluginEnabledAsync(id, true);
        factory.Release.SetResult();
        await Task.WhenAll(saving, enabling).WaitAsync(Patience, Ct);

        Assert.Single(service.ActiveHandlers, h => h.PluginId == id);
        Assert.Single(factory.Launcher.Launched);
        Assert.Equal(0, factory.Launcher.ShutdownCalls);
    }

    private static readonly string[] TwoServers = ["Alpha", "Beta"];

    private async Task<Dictionary<string, Guid>> SeedAsync(McpSandboxPolicy? sandbox = null)
    {
        var seeding = new GatedFactory(honorsCancellation: false);
        seeding.Release.SetResult();
        var service = Create(seeding);
        var ids = new Dictionary<string, Guid>();
        foreach (var name in TwoServers)
            ids[name] = await service.SaveLocalMcpAsync(Guid.NewGuid(), Definition(name, sandbox), Ct);
        await service.ShutdownAllAsync();
        return ids;
    }

    // While startup is held on one server, the user switches the other off and on, which starts it.
    private static async Task<Guid> StartTheOtherServerWhileTheFirstIsHeldAsync(
        PluginService service, FirstConnectHeldFactory factory, Dictionary<string, Guid> ids)
    {
        var held = await factory.Entered.Task.WaitAsync(Patience, Ct);
        var other = ids.Single(server => server.Key != held).Value;
        await service.SetPluginEnabledAsync(other, false).WaitAsync(Patience, Ct);
        await service.SetPluginEnabledAsync(other, true).WaitAsync(Patience, Ct);
        Assert.Single(service.ActiveHandlers, h => h.PluginId == other);
        return other;
    }

    private static void AssertStillRunsOnce(PluginService service, FirstConnectHeldFactory factory, Guid id)
    {
        var handler = Assert.Single(service.ActiveHandlers, h => h.PluginId == id);
        Assert.NotEmpty(handler.GetTools());
        Assert.Equal(2, factory.Launcher.Launched.Count);
        Assert.Equal(0, factory.Launcher.ShutdownCalls);
    }

    [Fact]
    public async Task TheStartupLoop_ReachingAServerTheSwitchAlreadyStarted_LeavesThatServerRunning()
    {
        var ids = await SeedAsync();
        var factory = new FirstConnectHeldFactory();
        var service = Create(factory);

        var initializing = service.InitializePersistedPluginsAsync();
        var other = await StartTheOtherServerWhileTheFirstIsHeldAsync(service, factory, ids);
        factory.Release.SetResult();
        await initializing.WaitAsync(Patience, Ct);

        AssertStillRunsOnce(service, factory, other);
    }

    [Fact]
    public async Task TheRuntimeReadyLoop_ReachingAServerTheSwitchAlreadyStarted_LeavesThatServerRunning()
    {
        var availability = new ChangingAvailability(new MxcAvailabilityStatus(MxcRuntimeState.NotInstalled));
        var factory = new FirstConnectHeldFactory();
        var service = Create(factory, availability);
        var ids = new Dictionary<string, Guid>();
        foreach (var name in TwoServers)
            ids[name] = await service.SaveLocalMcpAsync(Guid.NewGuid(), Definition(name, McpSandboxPolicy.Empty), Ct);
        Assert.Empty(factory.Launcher.Launched);

        availability.Change(new MxcAvailabilityStatus(MxcRuntimeState.Ready));
        var other = await StartTheOtherServerWhileTheFirstIsHeldAsync(service, factory, ids);
        var loopDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PluginsChanged += (_, _) => loopDone.TrySetResult();
        factory.Release.SetResult();
        await loopDone.Task.WaitAsync(Patience, Ct);

        AssertStillRunsOnce(service, factory, other);
    }
}
