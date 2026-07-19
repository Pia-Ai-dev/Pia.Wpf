using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// R18: the surface publishes a durable Flow item for a terminal PLANNED run only when the assistant
/// window is NOT focused; foreground runs and SingleTurn runs publish nothing. Completed → Success,
/// Failed → Error, both carrying an OpenRunAction keyed by run id. Exercised via the internal
/// terminal handler (the ctor subscribe + dispatcher marshal are the production seam).
/// </summary>
public sealed class AgentRunNotificationSurfaceTests
{
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly Pia.Services.Flow.IFlowService _flow = Substitute.For<Pia.Services.Flow.IFlowService>();
    private readonly IWindowManagerService _windows = Substitute.For<IWindowManagerService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public AgentRunNotificationSurfaceTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private AgentRunNotificationSurface Create() =>
        new(_runs, _flow, _windows, _loc, NullLogger<AgentRunNotificationSurface>.Instance);

    private void SetupRun(Guid runId, RunShape shape)
        => _runs.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = shape, ChatId = Guid.NewGuid() });

    [Fact]
    public async Task Unfocused_Planned_Failed_PublishesDurableErrorItem()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleTerminalAsync(runId, AgentRunState.Failed);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Severity == FlowSeverity.Error &&
            d.Source == FlowSource.AgentRun &&
            d.DedupKey == runId.ToString() &&
            d.Lifetime.IsPersistent &&
            d.RequestDurable &&
            d.Action is OpenRunAction));
    }

    [Fact]
    public async Task Unfocused_Planned_Completed_PublishesSuccess()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleTerminalAsync(runId, AgentRunState.Completed);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d => d.Severity == FlowSeverity.Success));
    }

    [Fact]
    public async Task Foreground_PublishesNothing()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);

        await Create().HandleTerminalAsync(runId, AgentRunState.Completed);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task SingleTurnRun_PublishesNothing()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.SingleTurn);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleTerminalAsync(runId, AgentRunState.Completed);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }
}
