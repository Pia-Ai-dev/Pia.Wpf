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

    private Guid SetupRun(Guid runId, RunShape shape, Guid? chatId = null)
    {
        var chat = chatId ?? Guid.NewGuid();
        _runs.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = shape, ChatId = chat });
        return chat;
    }

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
    public async Task Foreground_ActiveChat_PublishesNothing()
    {
        // R18: suppress ONLY the chat the user is actively watching in the foreground.
        var runId = Guid.NewGuid();
        var chatId = SetupRun(runId, RunShape.Planned);
        _windows.IsInForeground(WindowMode.Assistant).Returns(true);
        _windows.ActiveAssistantChatId.Returns(chatId);

        await Create().HandleTerminalAsync(runId, AgentRunState.Completed);

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

        await Create().HandleTerminalAsync(runId, AgentRunState.Completed);

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

        await Create().HandleTerminalAsync(runId, AgentRunState.Completed);

        _flow.Received(1).Publish(Arg.Any<FlowItemDraft>());
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

        await surface.HandleTerminalAsync(runId, AgentRunState.Completed);
        await surface.HandleTerminalAsync(runId, AgentRunState.Completed);

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
        await surface.HandleTerminalAsync(runId, AgentRunState.Failed); // records runId → chatId

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
}
