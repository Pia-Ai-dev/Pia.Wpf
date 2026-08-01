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
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();

    public AgentRunNotificationSurfaceTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private AgentRunNotificationSurface Create() =>
        new(_runs, _flow, _windows, _chats, _loc, NullLogger<AgentRunNotificationSurface>.Instance);

    private Guid SetupRun(Guid runId, RunShape shape, Guid? chatId = null, Guid? parentRunId = null)
    {
        var chat = chatId ?? Guid.NewGuid();
        _runs.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = shape, ChatId = chat, ParentRunId = parentRunId });
        return chat;
    }

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 fix pass). The Flow card's body must name WHY the run parked. Three reasons
    /// reach WaitingForInput since Batch 07 and only one is a budget, so a parent parked because a CHILD hit its
    /// own halved budget — or because the app restarted mid-fan-out — was told "Stopped at its budget".
    /// <para>
    /// The last three rows are the fallback pin and the non-vacuity control at once: an unknown or absent reason
    /// keeps the budget wording, which is correct for every pause the run loop writes for itself. Asserted on the
    /// key mapping rather than through a publish, because the reason lives in the run row's ExtraJson and the
    /// mapping is the whole decision. Neutralization: go back to a constant key → the first two rows red.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("children-parked", "Flow_Run_ChildrenParked")]
    [InlineData("children-interrupted", "Flow_Run_ChildrenInterrupted")]
    // Batch 08 G2. The "user" token is the one reason that is NOT a budget at all, and this row is the only
    // coverage its key has: the body is read as _localizationService[PausedBodyKey(...)], so the key literal
    // lives inside the switch arm where LocalizationTests' quoted-literal regex cannot see it.
    [InlineData("user", "Flow_Run_UserPaused")]
    [InlineData("step-cap", "Flow_Run_WaitingAtBudget")]
    [InlineData("wall-clock", "Flow_Run_WaitingAtBudget")]
    [InlineData(null, "Flow_Run_WaitingAtBudget")]
    public void AParkedRunsFlowBodyNamesWhyItParked(string? reason, string expectedKey)
        => Assert.Equal(expectedKey, AgentRunNotificationSurface.PausedBodyKey(reason));

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 fix pass). A DELEGATED run publishes nothing: the parent's own item already
    /// represents the whole fan-out, and a child lives in a stub chat the user never opened. Every row of the
    /// theory is a state a child really reaches — a clean 3-way fan-out used to produce four durable items and
    /// four toasts for one run started once. The WaitingForInput row is the load-bearing one: a child parked at
    /// its own halved budget published an ActionRequired card carrying a ContinueRunAction on the CHILD run id,
    /// a transition nothing supports.
    /// <para>
    /// Non-vacuity: the same states on a PARENTLESS run do publish, which the facts above and below this one
    /// assert directly. Neutralization: drop the <c>run.ParentRunId is not null</c> early return → red on every
    /// row.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.WaitingForInput)]
    // Batch 08 G8: Paused joined the publishable set below, so the same delegated-child filter must still
    // catch it — a cascade-paused child (D6) is exactly as un-actionable to the user as a budget-parked one.
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
        // R18: suppress ONLY the chat the user is actively watching in the foreground.
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
        // R18: a foreground window watching a DIFFERENT chat (e.g. a headless run's chat is never
        // the active session) still publishes — this fixes the interactive background-chat silent-drop.
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);
        _windows.ActiveAssistantChatId.Returns(Guid.NewGuid()); // some other chat

        await Create().HandleRunStateAsync(runId, AgentRunState.Completed);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d => d.Severity == FlowSeverity.Success));
    }

    [Fact]
    public async Task Foreground_NoActiveChat_Publishes()
    {
        // A headless run reaching terminal state while the window is up but no chat is active.
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
        // §15.5: a redundant terminal RunChanged for the same run must collapse to one durable Flow
        // item. The surface delegates dedup to FlowService via DedupKey, so both Publish drafts must
        // carry the SAME key (== run id) for the store to reconcile them onto a single item.
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
        // R17 deletion-side: once a chat (and its cascaded runs) is deleted, the durable OpenRun item(s)
        // this surface published for that chat must be retracted so nothing dangles in Flow.
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
        // A chat with no published run items (or an already-handled one) is a no-op — never a spurious retract.
        Create().HandleChatDeleted(Guid.NewGuid());

        _flow.DidNotReceive().Retract(Arg.Any<string>());
    }

    // ---- Budget-pause WaitingForInput publish + retract (Phase 2) --------------------------------

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

    [Fact]
    public async Task WaitingForInput_ForegroundActiveChat_Suppressed()
    {
        // A foreground run shows the panel Continue button instead — no Flow card.
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
        // A redundant WaitingForInput event carries the SAME DedupKey (run id) so the store collapses it.
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
        // A resumed parked run (→Running) must drop its WaitingForInput card.
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
        // D6: a run cancelled while parked must not leave a stale WaitingForInput card.
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);
        var surface = Create();
        await surface.HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        await surface.HandleRunStateAsync(runId, AgentRunState.Cancelled);

        _flow.Received(1).Retract(runId.ToString());
    }

    /// <summary>
    /// Batch 08 G8. A user-paused run needs the SAME ActionRequired/ContinueRun card
    /// <see cref="AgentRunState.WaitingForInput"/> gets, or a run the user paused from a background chat is
    /// invisible forever — the startup sweep's <c>State &lt; @Terminal</c> excludes <c>Paused</c> by design
    /// (W15), so there is no other surface that would ever tell the user about it.
    /// </summary>
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
        // An ordinary per-step Running event for a run that never parked issues no spurious Retract.
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned);

        await Create().HandleRunStateAsync(runId, AgentRunState.Running);

        _flow.DidNotReceive().Retract(Arg.Any<string>());
    }

    /// <summary>
    /// Batch 07 G8, <b>GUARD</b> (for the rows unrelated to <c>Paused</c>). A delegating parent
    /// (<c>WaitingForChildren</c>) is not user-actionable, so it must fall OUT of the publish filter entirely.
    /// Pinned deliberately rather than left to the <c>is … or …</c> set's shape, because widening that set is
    /// not harmless: the last arm of <c>HandleRunStateAsync</c> is the TERMINAL publish, so a state that passes
    /// the filter without an arm of its own would publish a "run finished" card for a run whose children are
    /// still working.
    /// <para>
    /// <b>Batch 08 G8:</b> the <c>Paused</c> row flips from <c>false</c> to <c>true</c> — a REGRESSION-shaped
    /// pin now, not a guard. A user-paused run needs the same ActionRequired card <c>WaitingForInput</c> gets
    /// (<see cref="PausedRun_PublishesAnActionRequiredCardWithContinueRun"/>), or it is invisible forever once
    /// the panel that shows its Continue button is closed.
    /// </para>
    /// <para>
    /// A row per state, plus a member-count pin so an appended state cannot slip through unasserted.
    /// </para>
    /// </summary>
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
