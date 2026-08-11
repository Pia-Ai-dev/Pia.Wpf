using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public sealed class AgentRunNotificationSurfaceTests
{
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly Pia.Services.Flow.IFlowService _flow = Substitute.For<Pia.Services.Flow.IFlowService>();
    private readonly IWindowManagerService _windows = Substitute.For<IWindowManagerService>();
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public AgentRunNotificationSurfaceTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private AgentRunNotificationSurface Create() =>
        new(_runs, _flow, _windows, _chats, _loc, NullLogger<AgentRunNotificationSurface>.Instance);

    private Guid SetupRun(
        Guid runId, RunShape shape, Guid? chatId = null, Guid? parentRunId = null, string? extraJson = null)
    {
        var chat = chatId ?? Guid.NewGuid();
        _runs.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun
            {
                Id = runId, RunShape = shape, ChatId = chat, ParentRunId = parentRunId, ExtraJson = extraJson,
            });
        return chat;
    }

    [Theory]
    [InlineData("children-parked", "Flow_Run_ChildrenParked")]
    [InlineData("children-interrupted", "Flow_Run_ChildrenInterrupted")]
    // The only coverage this key has: the literal sits inside a switch arm, invisible to LocalizationTests' regex.
    [InlineData("user", "Flow_Run_UserPaused")]
    // Written when a Continue claimed the row but never reached the orchestrator; it used to read as a budget stop.
    [InlineData("resume-interrupted", "Flow_Run_ResumeInterrupted")]
    // One key per resume behaviour (plan-time vs. mid-plan) — never the question itself.
    [InlineData("needs-goal", "Flow_Run_NeedsGoal")]
    [InlineData("needs-input", "Flow_Run_NeedsInput")]
    [InlineData("step-cap", "Flow_Run_WaitingAtBudget")]
    [InlineData("wall-clock", "Flow_Run_WaitingAtBudget")]
    [InlineData(null, "Flow_Run_WaitingAtBudget")]
    [InlineData("plan-approval", "Flow_Run_PlanApproval")]
    public void AParkedRunsFlowBodyNamesWhyItParked(string? reason, string expectedKey)
        => Assert.Equal(expectedKey, AgentRunNotificationSurface.PausedBodyKey(reason));

    // The parent's own item represents the whole fan-out, and a child lives in a stub chat the user never opened.
    [Theory]
    [InlineData(AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.WaitingForInput)]
    // Paused is publishable now, so the delegated-child filter has to catch it too.
    [InlineData(AgentRunState.Paused)]
    public async Task ADelegatedRun_PublishesNothing(AgentRunState state)
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned, parentRunId: Guid.NewGuid());
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, state);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task Unfocused_Planned_Failed_PublishesDurableErrorItem()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.Failed);

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

        await Create().HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d => d.Severity == FlowSeverity.Success));
    }

    [Fact]
    public async Task Foreground_ActiveChat_PublishesNothing()
    {
        var runId = Guid.NewGuid();
        var chatId = SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);
        _windows.ActiveAssistantChatId.Returns(chatId);

        await Create().HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task Foreground_NonActiveChat_Publishes()
    {
        // A headless run's chat is never the active session, so a foreground window on another chat publishes.
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);
        _windows.ActiveAssistantChatId.Returns(Guid.NewGuid());

        await Create().HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d => d.Severity == FlowSeverity.Success));
    }

    [Fact]
    public async Task Foreground_NoActiveChat_Publishes()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);
        _windows.ActiveAssistantChatId.Returns((Guid?)null);

        await Create().HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.Received(1).Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task SingleTurnRun_PublishesNothing()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.SingleTurn);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task SecondTerminalEvent_SameRun_CarriesIdenticalDedupKey()
    {
        // Dedup is FlowService's job via DedupKey, so both drafts have to carry the same key.
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);
        var surface = Create();

        await surface.HandleRunStateAsync(runId, AgentRunState.Completed);
        await surface.HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.Received(2).Publish(Arg.Is<FlowItemDraft>(d => d.DedupKey == runId.ToString()));
    }

    [Fact]
    public async Task ChatDeleted_RetractsPublishedRunItems()
    {
        var chatId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned, chatId);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);
        var surface = Create();
        await surface.HandleRunStateAsync(runId, AgentRunState.Failed); // records runId → chatId

        surface.HandleChatDeleted(chatId);

        _flow.Received(1).Retract(runId.ToString());
    }

    [Fact]
    public void ChatDeleted_UnknownChat_RetractsNothing()
    {
        Create().HandleChatDeleted(Guid.NewGuid());

        _flow.DidNotReceive().Retract(Arg.Any<string>());
    }

    [Fact]
    public async Task WaitingForInput_PublishesSingleActionRequiredContinueItem()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Severity == FlowSeverity.ActionRequired &&
            d.Source == FlowSource.AgentRun &&
            d.DedupKey == runId.ToString() &&
            d.Lifetime.IsPersistent &&
            d.RequestDurable &&
            d.Action is ContinueRunAction));
    }

    [Theory]
    [InlineData("needs-goal", "Flow_Run_NeedsGoal")]
    [InlineData("needs-input", "Flow_Run_NeedsInput")]
    // A plan-approval park answers in the chat too — one-click Continue would approve the plan unseen.
    [InlineData("plan-approval", "Flow_Run_PlanApproval")]
    public async Task NeedsClarificationPark_CardIsTokenKeyed_AndRoutesToTheRun(string reason, string expectedKey)
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned, extraJson: $$"""{"paused":true,"reason":"{{reason}}"}""");
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Severity == FlowSeverity.ActionRequired &&
            d.Title == "Flow_Run_Title" &&   // generic, never the run's Goal
            d.Body == expectedKey &&         // exact match: nothing appended, so no question could ride along
            // Opening this card must not retract it, since opening resolves nothing.
            d.Action is OpenParkedRunAction));
    }

    [Fact]
    public async Task WaitingForInput_ForegroundActiveChat_Suppressed()
    {
        // A foreground run shows the panel's Continue button instead.
        var runId = Guid.NewGuid();
        var chatId = SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);
        _windows.ActiveAssistantChatId.Returns(chatId);

        await Create().HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task WaitingForInput_SingleTurnRun_Suppressed()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.SingleTurn);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        _flow.DidNotReceive().Publish(Arg.Any<FlowItemDraft>());
    }

    [Fact]
    public async Task WaitingForInput_ThenRepeat_DedupesToOne()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);
        var surface = Create();

        await surface.HandleRunStateAsync(runId, AgentRunState.WaitingForInput);
        await surface.HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        _flow.Received(2).Publish(Arg.Is<FlowItemDraft>(d => d.DedupKey == runId.ToString()));
    }

    [Fact]
    public async Task Running_AfterWaitingPublished_Retracts()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);
        var surface = Create();
        await surface.HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        await surface.HandleRunStateAsync(runId, AgentRunState.Running);

        _flow.Received(1).Retract(runId.ToString());
    }

    [Fact]
    public async Task Cancelled_AfterWaitingPublished_Retracts()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);
        var surface = Create();
        await surface.HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        await surface.HandleRunStateAsync(runId, AgentRunState.Cancelled);

        _flow.Received(1).Retract(runId.ToString());
    }

    // Without this card a run paused from a background chat is invisible: the startup sweep excludes Paused.
    [Fact]
    public async Task PausedRun_PublishesAnActionRequiredCardWithContinueRun()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.Paused);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Severity == FlowSeverity.ActionRequired &&
            d.Source == FlowSource.AgentRun &&
            d.DedupKey == runId.ToString() &&
            d.Lifetime.IsPersistent &&
            d.RequestDurable &&
            d.Action is ContinueRunAction));
    }

    [Fact]
    public async Task Running_NoPriorPublish_RetractsNothing()
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);

        await Create().HandleRunStateAsync(runId, AgentRunState.Running);

        _flow.DidNotReceive().Retract(Arg.Any<string>());
    }

    // Widening the filter is not harmless: the last arm is the terminal publish, so a state without an arm of
    // its own would announce "run finished" while its children are still working.
    [Theory]
    [InlineData(AgentRunState.Planning, false)]
    [InlineData(AgentRunState.Running, true)]
    [InlineData(AgentRunState.Verifying, false)]
    [InlineData(AgentRunState.WaitingForInput, true)]
    [InlineData(AgentRunState.Paused, true)]
    [InlineData(AgentRunState.Completed, true)]
    [InlineData(AgentRunState.Failed, true)]
    [InlineData(AgentRunState.Cancelled, true)]
    [InlineData(AgentRunState.WaitingForChildren, false)]
    public void OnlyActionableStatesReachTheFlowSurface(AgentRunState state, bool publishable)
    {
        Assert.Equal(publishable, AgentRunNotificationSurface.IsPublishableState(state));
        Assert.Equal(9, Enum.GetValues<AgentRunState>().Length); // a 10th member needs a row above
    }
}
