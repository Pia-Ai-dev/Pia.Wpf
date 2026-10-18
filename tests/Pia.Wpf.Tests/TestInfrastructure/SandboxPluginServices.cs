using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Interfaces;
using Pia.Services.KnowledgeManager;
using Pia.Services.Operators;
using Pia.Services.Plugins;
using Pia.Services.Plugins.Sandbox;
using Pia.Services.Plugins.Sandbox.Runtimes;

namespace Pia.Tests.TestInfrastructure;

internal static class SandboxPluginServices
{
    public static PluginService Create(
        SqliteContext sqlite,
        IMcpTransportFactory factory,
        IMxcAvailability availability,
        IAppContainerProfileService? profiles = null,
        IAceSweeper? sweeper = null,
        ILogger<PluginService>? logger = null,
        IRuntimeStateStore? runtimeStateStore = null,
        DpapiHelper? dpapi = null) =>
        new(
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
            logger ?? NullLogger<PluginService>.Instance,
            sqlite,
            cabManager: null,
            dpapiHelper: dpapi ?? new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            transportFactory: factory,
            mxcAvailability: availability,
            profileService: profiles,
            aceSweeper: sweeper,
            runtimeStateStore: runtimeStateStore);
}

internal sealed class ReadyMxcAvailability : IMxcAvailability
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
