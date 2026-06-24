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
