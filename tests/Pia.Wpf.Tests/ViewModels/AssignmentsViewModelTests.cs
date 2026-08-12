using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssignmentsViewModelTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAssignmentApiClient _api = Substitute.For<IAssignmentApiClient>();
    private readonly IAssignmentPendingStore _pending = Substitute.For<IAssignmentPendingStore>();
    private readonly IAssignmentRunOrchestrator _orchestrator = Substitute.For<IAssignmentRunOrchestrator>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IWindowManagerService _windows = Substitute.For<IWindowManagerService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly FixedTimeProvider _time = new(Now);

    public AssignmentsViewModelTests()
    {
        // NSubstitute's auto-value for a string is empty, which would let a label assertion pass on nothing.
        _localization[Arg.Any<string>()].Returns(ci => ci.Arg<string>());
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.Arg<string>());

        _api.GetSurfaceAsync(Arg.Any<CancellationToken>())
            .Returns(new AssignmentSurface(true, [new AssignmentSkill("brief", "Written brief", "brief", [])]));
        Server();
        Journal();
    }

    private AssignmentsViewModel Create() => new(
        _api, _pending, _orchestrator, _dialogs, _windows, _localization,
        () => new AssignmentConsentViewModel(
            Substitute.For<IAssignmentScopeResolver>(),
            Substitute.For<IAssignmentConsentStore>(),
            _orchestrator,
            _localization,
            NullLogger<AssignmentConsentViewModel>.Instance),
        _time,
        NullLogger<AssignmentsViewModel>.Instance);

    private void Server(params AssignmentDto[] rows) =>
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(rows);

    private void ServerUnreachable() =>
        _api.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AssignmentDto>?)null);

    private void Journal(params PendingAssignment[] entries) =>
        _pending.GetJournalAsync().Returns(entries);

    private static AssignmentDto Row(Guid id, string status, int stepCount = 0, DateTime? completedAt = null) =>
        new(id, "brief", "brief", status, stepCount, 0, 0, Now.AddMinutes(-5), Now,
            Now.AddMinutes(-5), completedAt, null, null, null);

    private static PendingAssignment Entry(Guid id, Guid chatId, DateTime? collectedAt) =>
        new(id, chatId, "brief", "Summarise the quarter", Now.AddMinutes(-5), collectedAt);

    [Fact]
    public async Task ACollectedRowOffersOpenChatAndAQueuedOneDoesNot()
    {
        var collected = Guid.NewGuid();
        var queued = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        Server(Row(collected, "Completed", stepCount: 3, completedAt: Now), Row(queued, "Queued"));
        Journal(Entry(collected, chatId, Now.AddMinutes(-1)), Entry(queued, Guid.NewGuid(), collectedAt: null));

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        var collectedRow = vm.Rows.Single(r => r.Id == collected);
        var queuedRow = vm.Rows.Single(r => r.Id == queued);

        Assert.True(collectedRow.CanOpenChat);
        Assert.True(collectedRow.OpenChatCommand.CanExecute(null));
        Assert.False(queuedRow.CanOpenChat);
        Assert.False(queuedRow.OpenChatCommand.CanExecute(null));

        collectedRow.OpenChatCommand.Execute(null);
        _windows.Received(1).ShowAssistantChat(chatId);
    }

    [Fact]
    public async Task AServerRowWithNoJournalEntryStillRendersWithoutPromptOrOpenChat()
    {
        var elsewhere = Guid.NewGuid();
        Server(Row(elsewhere, "Running", stepCount: 1));
        Journal();

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(elsewhere, row.Id);
        Assert.False(row.IsFromThisDevice);
        Assert.Equal(string.Empty, row.Prompt);
        Assert.False(row.HasPrompt);
        Assert.False(row.CanOpenChat);
        Assert.Null(row.ChatId);
        Assert.Equal("Written brief", row.SkillDisplayName);
        Assert.Equal(AssignmentRowStatus.Running, row.Status);
    }

    [Fact]
    public async Task CancelIsOfferedOnlyWhileTheRunIsLive()
    {
        var running = Guid.NewGuid();
        var failed = Guid.NewGuid();
        Server(Row(running, "Running"), Row(failed, "Failed", completedAt: Now));
        _orchestrator.CancelAsync(running, Arg.Any<CancellationToken>()).Returns(true);

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        var runningRow = vm.Rows.Single(r => r.Id == running);
        var failedRow = vm.Rows.Single(r => r.Id == failed);

        Assert.True(runningRow.CanCancel);
        Assert.True(runningRow.CancelCommand.CanExecute(null));
        Assert.False(failedRow.CanCancel);
        Assert.False(failedRow.CancelCommand.CanExecute(null));

        await runningRow.CancelCommand.ExecuteAsync(null);
        await _orchestrator.Received(1).CancelAsync(running, Arg.Any<CancellationToken>());
        Assert.Equal("Assignments_Cancel_Requested", vm.Notice);
    }

    [Fact]
    public async Task ACancelTheServerHadNothingToStopIsSaidPlainly()
    {
        var id = Guid.NewGuid();
        Server(Row(id, "Running"));
        _orchestrator.CancelAsync(id, Arg.Any<CancellationToken>()).Returns(false);

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        await vm.Rows.Single().CancelCommand.ExecuteAsync(null);

        Assert.Equal("Assignments_Cancel_NothingToStop", vm.Notice);
        Assert.True(vm.HasNotice);
    }

    [Fact]
    public async Task PollingStopsOnceTheViewIsHidden()
    {
        Server(Row(Guid.NewGuid(), "Running"));

        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await _api.Received(1).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal((AssignmentsViewModel.PollInterval, AssignmentsViewModel.PollInterval), _time.Timer!.Changes[^1]);

        await vm.TickAsync();
        await _api.Received(2).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        vm.OnNavigatedFrom();
        await vm.TickAsync();
        await vm.TickAsync();

        await _api.Received(2).ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        Assert.Equal((Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan), _time.Timer.Changes[^1]);

        Assert.False(_time.Timer.Disposed);
        vm.Dispose();
        Assert.True(_time.Timer.Disposed);
    }

    [Fact]
    public async Task AViewHiddenWhileItsFirstLoadIsInFlightNeverArmsThePoll()
    {
        var gate = new TaskCompletionSource<AssignmentSurface>();
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(gate.Task);
        Server(Row(Guid.NewGuid(), "Running"));

        using var vm = Create();
        var arriving = vm.OnNavigatedToAsync(null);
        vm.OnNavigatedFrom();

        gate.SetResult(AssignmentSurface.Hidden);
        await arriving;

        Assert.Equal((Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan), _time.Timer!.Changes[^1]);
        Assert.DoesNotContain(
            (AssignmentsViewModel.PollInterval, AssignmentsViewModel.PollInterval), _time.Timer.Changes);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerKeepsTheRowsItLastShowedAndSaysSo()
    {
        var id = Guid.NewGuid();
        Server(Row(id, "Running"));

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Single(vm.Rows);

        ServerUnreachable();
        await vm.TickAsync();

        Assert.Equal(id, Assert.Single(vm.Rows).Id);
        Assert.False(vm.IsEmpty);
        Assert.Equal("Assignments_Refresh_Failed", vm.Notice);
    }

    [Fact]
    public async Task AFirstLoadTheServerNeverAnsweredIsNotReportedAsNothingHavingRun()
    {
        ServerUnreachable();

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsEmpty);
        Assert.Equal("Assignments_Refresh_Failed", vm.Notice);
    }

    [Fact]
    public async Task ARefreshThatReachesTheServerAgainDropsTheUnreachableNotice()
    {
        ServerUnreachable();

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.True(vm.HasNotice);

        Server(Row(Guid.NewGuid(), "Running"));
        await vm.TickAsync();

        Assert.False(vm.HasNotice);
        Assert.Single(vm.Rows);
    }

    /// <summary>The trailing refresh must not overwrite what the action the user just took had to say.</summary>
    [Fact]
    public async Task AnUnreachableServerDoesNotClobberTheCancelMessage()
    {
        var id = Guid.NewGuid();
        Server(Row(id, "Running"));
        _orchestrator.CancelAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        ServerUnreachable();
        await vm.Rows.Single().CancelCommand.ExecuteAsync(null);

        Assert.Equal("Assignments_Cancel_Requested", vm.Notice);
    }

    [Fact]
    public async Task AnUnknownStatusRendersNeutrallyRatherThanThrowing()
    {
        var id = Guid.NewGuid();
        Server(Row(id, "Quarantined", stepCount: 2));
        Journal(Entry(id, Guid.NewGuid(), collectedAt: null));

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        var row = Assert.Single(vm.Rows);
        Assert.Equal(AssignmentRowStatus.Unknown, row.Status);
        Assert.Equal("Assignments_Status_Unknown", row.StatusLabel);
        Assert.False(row.IsLive);
        Assert.False(row.CanCancel);
        Assert.False(row.CancelCommand.CanExecute(null));
        Assert.False(row.CanOpenChat);
        Assert.True(row.HasSteps);
        Assert.Equal("Assignments_Steps", row.StepCountLabel);
        Assert.True(row.HasPrompt);
        Assert.Equal("Assignments_Elapsed_Minutes", row.ElapsedLabel);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task ARunStillGoingMeasuresItsElapsedAgainstNow()
    {
        var live = Guid.NewGuid();
        var done = Guid.NewGuid();
        var untagged = Guid.NewGuid();
        Server(
            Row(live, "Running"),
            Row(done, "Completed", completedAt: Now.AddMinutes(-4).AddSeconds(-30)),
            // What a wire timestamp without a trailing Z deserialises to.
            Row(untagged, "Running") with { CreatedAt = DateTime.SpecifyKind(Now.AddMinutes(-5), DateTimeKind.Unspecified) });

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal(TimeSpan.FromMinutes(5), vm.Rows.Single(r => r.Id == live).Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(30), vm.Rows.Single(r => r.Id == done).Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(5), vm.Rows.Single(r => r.Id == untagged).Elapsed);
    }

    [Fact]
    public async Task AJournalEntryTheServerNoLongerListsIsNotShown()
    {
        Server();
        Journal(Entry(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-20)));

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Empty(vm.Rows);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task TheNewAssignmentActionIsOnlyOfferedWhenTheSurfaceIsAvailable()
    {
        _api.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(AssignmentSurface.Hidden);

        using var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.False(vm.CanStartAssignment);
        Assert.False(vm.NewAssignmentCommand.CanExecute(null));
    }

    /// <summary>No FakeTimeProvider package is available, so the clock and the poll timer are stubbed here
    /// rather than waited on. The timer records rather than discards, so a test can see it armed and
    /// disarmed.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public InertTimer? Timer { get; private set; }

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new InertTimer();

        internal sealed class InertTimer : ITimer
        {
            public List<(TimeSpan Due, TimeSpan Period)> Changes { get; } = [];

            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Changes.Add((dueTime, period));
                return true;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
