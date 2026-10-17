using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>Closing Pia must take every sandboxed server down, quickly, from wherever it is called: a leaked container
/// leaves its ACEs on the user's folders until the next start's sweep.</summary>
public sealed class PluginServiceShutdownTests : IDisposable
{
    private static readonly TimeSpan PerServer = TimeSpan.FromMilliseconds(700);

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "pia-shutdown-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly List<SqliteContext> _contexts = [];

    public void Dispose()
    {
        foreach (var context in _contexts)
            context.Dispose();
        TempPath.RemoveFile(_dbPath);
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

    private sealed class NeverRunsContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // A UI thread that is blocked waiting never gets to run what is posted to it.
        }
    }

    private async Task<(PluginService Service, FakeMxcProcessLauncher Launcher)> StartTwoSandboxedServersAsync()
    {
        var launcher = new FakeMxcProcessLauncher { ShutdownDelay = PerServer };
        var verifier = Substitute.For<ISandboxGrantVerifier>();
        verifier.Verify(Arg.Any<SandboxLaunchSpec>()).Returns(new SandboxGrantResult(true, 0, null));
        var factory = new McpTransportFactory(launcher, verifier, NullLoggerFactory.Instance, fileExists: _ => true,
            guardEnvironment: SandboxGuardEnvironment.Current() with { PathExists = _ => true });

        var sqlite = new SqliteContext(_dbPath);
        _contexts.Add(sqlite);
        var service = new PluginService(
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

        foreach (var name in new[] { "One", "Two" })
        {
            await service.SaveLocalMcpAsync(null,
                new LocalMcpDefinition(name, @"C:\Tools\fs\fs.exe", [], new Dictionary<string, string>(), null,
                    LocalMcpConfig.DeriveToolPrefix(name), null, Sandbox: McpSandboxPolicy.Empty),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, launcher.Launched.Count);
        return (service, launcher);
    }

    [Fact]
    public async Task ShutdownAll_ClosesEverySandboxedServer()
    {
        var (service, launcher) = await StartTwoSandboxedServersAsync();

        await service.ShutdownAllAsync();

        Assert.Equal(2, launcher.ShutdownCalls);
    }

    /// <summary>Two servers cost the slowest one, not the sum, so closing Pia stays inside a bounded wait.</summary>
    [Fact]
    public async Task ShutdownAll_ClosesTheServersInParallel()
    {
        var (service, _) = await StartTwoSandboxedServersAsync();

        var clock = Stopwatch.StartNew();
        await service.ShutdownAllAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < PerServer * 1.8, $"took {clock.Elapsed}");
    }

    /// <summary>The caller may be the UI thread blocking on the result; a continuation that needs that thread would never run.</summary>
    [Fact]
    public async Task ShutdownAll_DoesNotNeedTheCallersContext_SoABlockingCallerCannotDeadlockIt()
    {
        var (service, launcher) = await StartTwoSandboxedServersAsync();
        var finished = false;

        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverRunsContext());
            finished = service.ShutdownAllAsync().Wait(TimeSpan.FromSeconds(10));
        });
        caller.Start();
        await Task.Run(caller.Join, TestContext.Current.CancellationToken);

        Assert.True(finished, "ShutdownAllAsync waited for the caller's context");
        Assert.Equal(2, launcher.ShutdownCalls);
    }
}
