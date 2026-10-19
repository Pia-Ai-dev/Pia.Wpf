using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Pia.Infrastructure;
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

    private PluginService Create(GatedFactory factory)
    {
        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        var service = SandboxPluginServices.Create(sqlite, factory, new ReadyMxcAvailability());
        _services.Add(service);
        return service;
    }

    private static LocalMcpDefinition Definition(string name = "Files") =>
        new(name, @"C:\Tools\fs\fs.exe", [], new Dictionary<string, string>(), null,
            LocalMcpConfig.DeriveToolPrefix(name), null);

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
}
