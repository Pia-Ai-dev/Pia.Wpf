using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Controls.Cards;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Navigation;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.ViewModels.Flow;
using Xunit;

namespace Pia.Tests.ViewModels.Flow;

/// <summary>
/// Covers the per-item Flow wrapper (design §5, §9): reminder decision derivation, command wiring
/// (Snooze/Done → reminder service then dismiss), IsBusy re-entrancy gating, and failure-keeps-card.
/// </summary>
public class FlowItemViewModelTests
{
    private static FlowItemViewModel Create(
        out IFlowService flow,
        out IReminderService reminders,
        out ILocalizationService loc,
        ILogger<FlowItemViewModel>? logger = null)
    {
        flow = Substitute.For<IFlowService>();
        reminders = Substitute.For<IReminderService>();
        loc = Substitute.For<ILocalizationService>();
        loc["Flow_Action_Snooze"].Returns("Snooze");
        loc["Flow_Action_Done"].Returns("Done");
        var windowManager = Substitute.For<IWindowManagerService>();
        var navigation = Substitute.For<INavigationService>();
        return new FlowItemViewModel(
            flow,
            reminders,
            windowManager,
            navigation,
            loc,
            Substitute.For<IAgentRunResumeService>(),
            logger ?? NullLogger<FlowItemViewModel>.Instance);
    }

    private static FlowItem ReminderItem(Guid reminderId)
        => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.Reminder,
            Title = "Take a break",
            Body = "",
            DedupKey = reminderId.ToString(),
            Lifetime = FlowLifetime.Persistent,
        };

    private static FlowItem NonReminderItem()
        => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.Info,
            Source = FlowSource.TodoDeadline,
            Title = "t",
            Body = "",
            DedupKey = Guid.NewGuid().ToString(),
            Lifetime = FlowLifetime.Persistent,
        };

    private static FlowItem BgChatItem(FlowSeverity severity)
        => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = severity,
            Source = FlowSource.BackgroundChat,
            Title = "research summary",
            Body = "Waiting for your confirmation",
            DedupKey = Guid.NewGuid().ToString(),
            Lifetime = FlowLifetime.Persistent,
        };

    [Theory]
    [InlineData(FlowSeverity.ActionRequired, ChatState.WaitingForTool)]
    [InlineData(FlowSeverity.Success, ChatState.Completed)]
    [InlineData(FlowSeverity.Error, ChatState.Error)]
    public void Bind_BackgroundChatItem_DerivesChatStateFromSeverity(FlowSeverity severity, ChatState expected)
    {
        var vm = Create(out _, out _, out _);
        vm.Bind(BgChatItem(severity));

        Assert.True(vm.HasChatState);
        Assert.Equal(expected, vm.State);
    }

    [Fact]
    public void Bind_BackgroundChatItem_WithNonSurfaceSeverity_HasNoChatState()
    {
        var vm = Create(out _, out _, out _);
        vm.Bind(BgChatItem(FlowSeverity.Info));

        Assert.False(vm.HasChatState);
        Assert.Null(vm.State);
    }

    [Fact]
    public void Bind_NonChatSource_HasNoChatState_EvenWhenActionRequired()
    {
        var vm = Create(out _, out _, out _);
        // A reminder is ActionRequired too, but only the BackgroundChat source carries a chat state.
        vm.Bind(ReminderItem(Guid.NewGuid()));

        Assert.False(vm.HasChatState);
        Assert.Null(vm.State);
    }

    [Fact]
    public void Bind_ReminderItem_DerivesSnoozeAndDoneDecisions()
    {
        var vm = Create(out _, out _, out _);
        vm.Bind(ReminderItem(Guid.NewGuid()));

        Assert.True(vm.HasDecisions);
        Assert.Equal(2, vm.Decisions.Count);

        var snooze = vm.Decisions[0];
        Assert.Equal("Snooze", snooze.Label);
        Assert.Equal(DecisionEmphasis.Default, snooze.Emphasis);
        Assert.Same(vm.SnoozeCommand, snooze.Command);

        var done = vm.Decisions[1];
        Assert.Equal("Done", done.Label);
        Assert.Equal(DecisionEmphasis.Primary, done.Emphasis);
        Assert.Same(vm.DoneCommand, done.Command);
    }

    [Fact]
    public void Bind_NonReminderItem_HasNoDecisions()
    {
        var vm = Create(out _, out _, out _);
        vm.Bind(NonReminderItem());

        Assert.False(vm.HasDecisions);
        Assert.Empty(vm.Decisions);
    }

    [Fact]
    public void ExecuteAction_OpenRun_ShowsAgentRun_ThenRetractsByKey()
    {
        var flow = Substitute.For<IFlowService>();
        var windowManager = Substitute.For<IWindowManagerService>();
        var vm = new FlowItemViewModel(
            flow,
            Substitute.For<IReminderService>(),
            windowManager,
            Substitute.For<INavigationService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IAgentRunResumeService>(),
            NullLogger<FlowItemViewModel>.Instance);

        var runId = Guid.NewGuid();
        vm.Bind(new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.Success,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "",
            DedupKey = runId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenRunAction(runId, "Open run"),
        });

        vm.ExecuteActionCommand.Execute(null);

        windowManager.Received(1).ShowAgentRun(runId);
        flow.Received(1).Retract(runId.ToString()); // RetractByKey — DedupKey present
    }

    /// <summary>Opening a needs-goal/needs-input card must not retract it — the run is still WaitingForInput right after the click, so retracting would delete its only durable trace.</summary>
    [Fact]
    public void ExecuteAction_OpenParkedRun_ShowsAgentRun_MarksReadButDoesNotRetract()
    {
        var flow = Substitute.For<IFlowService>();
        var windowManager = Substitute.For<IWindowManagerService>();
        var vm = new FlowItemViewModel(
            flow,
            Substitute.For<IReminderService>(),
            windowManager,
            Substitute.For<INavigationService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<IAgentRunResumeService>(),
            NullLogger<FlowItemViewModel>.Instance);

        var runId = Guid.NewGuid();
        var item = new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "",
            DedupKey = runId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenParkedRunAction(runId, "Open run"),
        };
        vm.Bind(item);

        vm.ExecuteActionCommand.Execute(null);

        windowManager.Received(1).ShowAgentRun(runId);
        flow.Received(1).MarkRead(item.Id);
        flow.DidNotReceive().Retract(Arg.Any<string>());
        flow.DidNotReceive().Dismiss(Arg.Any<Guid>());
    }

    [Fact]
    public void ExecuteAction_ContinueRun_InvokesResume_ThenRetracts()
    {
        var flow = Substitute.For<IFlowService>();
        var resume = Substitute.For<IAgentRunResumeService>();
        var vm = new FlowItemViewModel(
            flow,
            Substitute.For<IReminderService>(),
            Substitute.For<IWindowManagerService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ILocalizationService>(),
            resume,
            NullLogger<FlowItemViewModel>.Instance);

        var runId = Guid.NewGuid();
        vm.Bind(new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "",
            DedupKey = runId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new ContinueRunAction(runId, "Continue run"),
        });

        vm.ExecuteActionCommand.Execute(null);

        resume.Received(1).ResumeAsync(runId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        flow.Received(1).Retract(runId.ToString()); // RetractByKey — DedupKey present
    }

    private static FlowItem ToolApprovalItem(Guid runId)
        => new()
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "",
            DedupKey = runId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new ToolApprovalRunAction(runId, "Continue run"),
        };

    /// <summary>A tool-approval card carries the approve/deny pair the park's question has — derived from
    /// the persisted action kind, so the bar survives a reload exactly like the link does.</summary>
    [Fact]
    public void Bind_ToolApprovalItem_DerivesDenyAndApproveDecisions()
    {
        var vm = Create(out _, out _, out var loc);
        loc["Run_Action_Deny"].Returns("Deny");
        loc["Run_Action_Approve"].Returns("Allow");
        vm.Bind(ToolApprovalItem(Guid.NewGuid()));

        Assert.True(vm.HasDecisions);
        Assert.Equal(2, vm.Decisions.Count);

        var deny = vm.Decisions[0];
        Assert.Equal("Deny", deny.Label);
        Assert.Equal(DecisionEmphasis.Default, deny.Emphasis);
        Assert.Same(vm.DeclineRunCommand, deny.Command);

        var approve = vm.Decisions[1];
        Assert.Equal("Allow", approve.Label);
        Assert.Equal(DecisionEmphasis.Primary, approve.Emphasis);
        Assert.Same(vm.ApproveRunCommand, approve.Command);
    }

    [Fact]
    public async Task ApproveRunCommand_ResumesTheRun_ThenRetracts()
    {
        var flow = Substitute.For<IFlowService>();
        var resume = Substitute.For<IAgentRunResumeService>();
        var vm = new FlowItemViewModel(
            flow, Substitute.For<IReminderService>(), Substitute.For<IWindowManagerService>(),
            Substitute.For<INavigationService>(), Substitute.For<ILocalizationService>(), resume,
            NullLogger<FlowItemViewModel>.Instance);
        var runId = Guid.NewGuid();
        vm.Bind(ToolApprovalItem(runId));

        await vm.ApproveRunCommand.ExecuteAsync(null);

        await resume.Received(1).ResumeAsync(runId, Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        await resume.DidNotReceive().DeclineAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        flow.Received(1).Retract(runId.ToString());
    }

    [Fact]
    public async Task DeclineRunCommand_DeclinesTheTool_ThenRetracts()
    {
        var flow = Substitute.For<IFlowService>();
        var resume = Substitute.For<IAgentRunResumeService>();
        var vm = new FlowItemViewModel(
            flow, Substitute.For<IReminderService>(), Substitute.For<IWindowManagerService>(),
            Substitute.For<INavigationService>(), Substitute.For<ILocalizationService>(), resume,
            NullLogger<FlowItemViewModel>.Instance);
        var runId = Guid.NewGuid();
        vm.Bind(ToolApprovalItem(runId));

        await vm.DeclineRunCommand.ExecuteAsync(null);

        await resume.Received(1).DeclineAsync(runId, Arg.Any<CancellationToken>());
        await resume.DidNotReceive().ResumeAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        flow.Received(1).Retract(runId.ToString());
    }

    [Fact]
    public async Task SnoozeCommand_SnoozesReminderThenDismissesCard()
    {
        var reminderId = Guid.NewGuid();
        var vm = Create(out var flow, out var reminders, out _);
        var item = ReminderItem(reminderId);
        vm.Bind(item);

        await vm.SnoozeCommand.ExecuteAsync(null);

        await reminders.Received(1).SnoozeAsync(reminderId, TimeSpan.FromMinutes(10));
        flow.Received(1).Dismiss(item.Id);
    }

    [Fact]
    public async Task DoneCommand_DismissesReminderThenDismissesCard()
    {
        var reminderId = Guid.NewGuid();
        var vm = Create(out var flow, out var reminders, out _);
        var item = ReminderItem(reminderId);
        vm.Bind(item);

        await vm.DoneCommand.ExecuteAsync(null);

        await reminders.Received(1).DismissAsync(reminderId);
        flow.Received(1).Dismiss(item.Id);
    }

    [Fact]
    public async Task SnoozeCommand_WhileBusy_IsBusyTrueAndCannotReExecute()
    {
        var reminderId = Guid.NewGuid();
        var vm = Create(out _, out var reminders, out _);
        vm.Bind(ReminderItem(reminderId));

        var gate = new TaskCompletionSource();
        reminders.SnoozeAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>()).Returns(gate.Task);

        Assert.False(vm.IsBusy);
        Assert.True(vm.SnoozeCommand.CanExecute(null));

        var running = vm.SnoozeCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.SnoozeCommand.CanExecute(null));
        Assert.False(vm.DoneCommand.CanExecute(null));

        gate.SetResult();
        await running;

        Assert.False(vm.IsBusy);
        Assert.True(vm.SnoozeCommand.CanExecute(null));
    }

    [Fact]
    public async Task SnoozeCommand_WhenServiceThrows_KeepsCardAndResetsBusyAndLogs()
    {
        var reminderId = Guid.NewGuid();
        var logger = Substitute.For<ILogger<FlowItemViewModel>>();
        var vm = Create(out var flow, out var reminders, out _, logger);
        var item = ReminderItem(reminderId);
        vm.Bind(item);

        reminders.SnoozeAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>())
            .Throws(new InvalidOperationException("boom"));

        await vm.SnoozeCommand.ExecuteAsync(null);

        flow.DidNotReceive().Dismiss(item.Id);
        Assert.False(vm.IsBusy);
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
