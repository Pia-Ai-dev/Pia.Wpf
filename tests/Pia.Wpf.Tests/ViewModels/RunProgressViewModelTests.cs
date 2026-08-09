using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class RunProgressViewModelTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _runs;
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]); // echo the key so activity text is assertable
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}|{string.Join(",", (object[])ci[1])}");
    }

    private async Task<AgentRun> NewPlannedRunAsync()
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [],
        });
        return await _runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "g"));
    }

    private RunProgressViewModel CreateVm(Guid runId)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, runId, _loc, _resume, NullLogger.Instance);
    }

    [Fact]
    public async Task CurrentActivity_PlanningShowsNote_RunningShowsStepTitle_TerminalHides()
    {
        var run = await NewPlannedRunAsync();
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "Read notes", Status = AgentStepStatus.Pending };
        await _runs.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);

        var vm = CreateVm(run.Id);
        await vm.RefreshAsync();
        Assert.Equal("Run_Activity_Planning", vm.CurrentActivity);
        Assert.True(vm.HasCurrentActivity);

        await _runs.SetStateAsync(run.Id, AgentRunState.Running, TestContext.Current.CancellationToken);
        await _runs.SetStepStatusAsync(step.Id, AgentStepStatus.Running, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.Equal("Read notes", vm.CurrentActivity);

        await _runs.CompleteAsync(run.Id, ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.Null(vm.CurrentActivity);
        Assert.False(vm.HasCurrentActivity);

        vm.Dispose();
    }

    [Fact]
    public async Task Projects_Planning_Then_Running_And_MovesHighlight()
    {
        var run = await NewPlannedRunAsync();
        var stepA = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        var stepB = new AgentStep { Id = Guid.NewGuid(), Ordinal = 1, Title = "B", Status = AgentStepStatus.Pending };
        await _runs.ReplaceStepsAsync(run.Id, new[] { stepA, stepB }, TestContext.Current.CancellationToken);

        var vm = CreateVm(run.Id);
        await vm.RefreshAsync();
        Assert.Equal(RunProgressState.Planning, vm.State);
        Assert.Equal(2, vm.Steps.Count);
        Assert.Equal("A", vm.Steps[0].Title);

        await _runs.SetStateAsync(run.Id, AgentRunState.Running, TestContext.Current.CancellationToken);
        await _runs.SetStepStatusAsync(stepA.Id, AgentStepStatus.Running, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Running, vm.State);
        Assert.True(vm.Steps[0].IsRunning);
        Assert.False(vm.Steps[1].IsRunning);

        await _runs.SetStepStatusAsync(stepA.Id, AgentStepStatus.Done, TestContext.Current.CancellationToken);
        await _runs.SetStepStatusAsync(stepB.Id, AgentStepStatus.Running, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(AgentStepStatus.Done, vm.Steps[0].Status);
        Assert.False(vm.Steps[0].IsRunning);
        Assert.True(vm.Steps[1].IsRunning);

        vm.Dispose();
    }

    [Fact]
    public async Task LedgerAccrues_Live()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(10, vm.TotalInputTokens);
        Assert.Equal(4, vm.TotalOutputTokens);
        Assert.Contains("Run_Sub_Tokens|14", vm.LedgerSummary);

        vm.Dispose();
    }

    [Fact]
    public async Task TruncatedComplete_IsDistinctFromCleanComplete()
    {
        var truncatedRun = await NewPlannedRunAsync();
        var truncVm = CreateVm(truncatedRun.Id);
        await _runs.CompleteAsync(truncatedRun.Id, truncated: true, truncationReason: "budget", TestContext.Current.CancellationToken);
        await truncVm.RefreshAsync();
        Assert.Equal(RunProgressState.TruncatedCompleted, truncVm.State);
        Assert.True(truncVm.IsTruncated);
        truncVm.Dispose();

        var cleanRun = await NewPlannedRunAsync();
        var cleanVm = CreateVm(cleanRun.Id);
        await _runs.CompleteAsync(cleanRun.Id, ct: TestContext.Current.CancellationToken);
        await cleanVm.RefreshAsync();
        Assert.Equal(RunProgressState.Completed, cleanVm.State);
        Assert.False(cleanVm.IsTruncated);
        Assert.Null(cleanVm.TruncationNote);
        cleanVm.Dispose();
    }

    // Budget exhaustion parks the run now, so a truncated run must not claim a budget it never hit.
    [Fact]
    public async Task TruncationNote_Unverified_IsNotTheBudgetCopy()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.CompleteAsync(run.Id, truncated: true, truncationReason: "unverified",
            ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.TruncatedCompleted, vm.State);
        Assert.True(vm.IsTruncated);
        Assert.Equal("Run_Unverified", vm.TruncationNote);
        Assert.NotEqual("Run_StoppedAtBudget", vm.TruncationNote);
        vm.Dispose();
    }

    // Rows written before budget-pause carry these reasons, and the budget wording is true for them.
    [Theory]
    [InlineData("budget")]
    [InlineData("step-cap")]
    [InlineData("wall-clock")]
    public async Task TruncationNote_LegacyBudgetReasons_KeepTheBudgetCopy(string reason)
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.CompleteAsync(run.Id, truncated: true, truncationReason: reason,
            ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal("Run_StoppedAtBudget", vm.TruncationNote);
        vm.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-new")]
    public async Task TruncationNote_UnknownReason_DegradesToEndedEarly(string? reason)
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.CompleteAsync(run.Id, truncated: true, truncationReason: reason,
            ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.True(vm.IsTruncated);
        Assert.Equal("Run_EndedEarly", vm.TruncationNote);
        vm.Dispose();
    }

    [Fact]
    public async Task TruncationNote_ClearsWhenTheRunIsNoLongerTruncated()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await _runs.CompleteAsync(run.Id, truncated: true, truncationReason: "unverified",
            ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.Equal("Run_Unverified", vm.TruncationNote);

        await _runs.SetStateAsync(run.Id, AgentRunState.Running, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.False(vm.IsTruncated);
        Assert.Null(vm.TruncationNote);
        vm.Dispose();
    }

    // Verifying keeps the Running chip lit but supplies its own activity line, the only signal the critic ran.
    [Fact]
    public async Task Verifying_FoldsToRunningChip_WithItsOwnActivityLine()
    {
        var run = await NewPlannedRunAsync();
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "Write report", Status = AgentStepStatus.Done };
        await _runs.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);
        var vm = CreateVm(run.Id);

        await _runs.SetStateAsync(run.Id, AgentRunState.Verifying, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Running, vm.State);
        Assert.False(vm.IsTruncated);
        Assert.Equal("Run_Activity_Verifying", vm.CurrentActivity);
        Assert.True(vm.HasCurrentActivity);
        Assert.False(vm.CanContinue);
        vm.Dispose();
    }

    [Fact]
    public async Task Failed_MapsToFailed()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await _runs.FailAsync(run.Id, "generic", ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.Equal(RunProgressState.Failed, vm.State);
        vm.Dispose();
    }

    [Fact]
    public async Task RunChanged_ForOtherRunId_IsIgnored()
    {
        var run = await NewPlannedRunAsync();
        var other = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await vm.RefreshAsync();
        var before = vm.State;

        await _runs.CompleteAsync(other.Id, truncated: true, truncationReason: "budget", TestContext.Current.CancellationToken);

        Assert.Equal(before, vm.State);
        Assert.False(vm.IsTruncated);
        vm.Dispose();
    }

    [Fact]
    public async Task OffThreadRunChanged_Marshals_NoCrossThreadException()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        // The whole claim is that nothing throws: the vm marshals through the captured SynchronizationContext.
        await Task.Run(async () => await _runs.SetStateAsync(run.Id, AgentRunState.Running), TestContext.Current.CancellationToken);

        vm.Dispose();
    }

    [Fact]
    public async Task WaitingForInput_ProjectsWaitingState_ContinueEnabled()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.PauseAsync(run.Id, "step-cap", TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.WaitingForInput, vm.State);
        Assert.Equal("Run_Activity_WaitingAtBudget", vm.CurrentActivity);
        Assert.True(vm.CanContinue);
        Assert.True(vm.ContinueCommand.CanExecute(null));
        vm.Dispose();
    }

    [Theory]
    [InlineData("children-parked", "Run_Activity_ChildrenParked")]
    [InlineData("children-interrupted", "Run_Activity_ChildrenInterrupted")]
    // Driven through the WaitingForInput route: the activity line is null for Paused itself, since the chip carries it.
    [InlineData("user", "Run_Activity_UserPaused")]
    // Reachable today: the launcher's re-park arms write this reason together with WaitingForInput.
    [InlineData("resume-interrupted", "Run_Activity_ResumeInterrupted")]
    // On the interactive path this label is the only surface for these two — token-keyed, never the model's question.
    [InlineData("needs-goal", "Run_Activity_NeedsGoal")]
    [InlineData("needs-input", "Run_Activity_NeedsInput")]
    [InlineData("step-cap", "Run_Activity_WaitingAtBudget")]
    [InlineData("wall-clock", "Run_Activity_WaitingAtBudget")]
    [InlineData("something-a-later-build-invented", "Run_Activity_WaitingAtBudget")]
    public async Task AParkedRunsActivityLineNamesWhyItParked(string reason, string expectedKey)
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.PauseAsync(run.Id, reason, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.WaitingForInput, vm.State);
        Assert.Equal(expectedKey, vm.CurrentActivity);
        vm.Dispose();
    }

    // Asserting only a non-empty line would pass on the fall-through arm, which degrades to the budget copy.
    [Fact]
    public async Task AToolApprovalParksActivityLineNamesTheToolAndIsNotTheBudgetWording()
    {
        // Format is stubbed only here; every other activity string is a bare key lookup.
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => (string)ci[0] + "|" + string.Join(',', ((object[])ci[1]).Select(a => a?.ToString())));

        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.PauseAsync(run.Id, "tool-approval", TestContext.Current.CancellationToken, approvalTool: "write_file");
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.WaitingForInput, vm.State);
        Assert.Equal("Run_Activity_WaitingForToolApproval|write_file", vm.CurrentActivity);
        Assert.NotEqual("Run_Activity_WaitingAtBudget", vm.CurrentActivity);
        vm.Dispose();
    }

    [Fact]
    public async Task Continue_InvokesResumeService()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await _runs.PauseAsync(run.Id, "wall-clock", TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        await vm.ContinueCommand.ExecuteAsync(null);

        await _resume.Received(1).ResumeAsync(run.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    [Fact]
    public async Task Completed_CannotContinue()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await _runs.CompleteAsync(run.Id, ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Completed, vm.State);
        Assert.False(vm.CanContinue);
        Assert.False(vm.ContinueCommand.CanExecute(null));
        vm.Dispose();
    }

    // Asserted as the whole string: a money segment appended after the seconds would slip past a Contains.
    [Fact]
    public async Task LedgerSummary_IsTokensAndActiveSecondsOnly_WithNoMoneySegment()
    {
        var run = await NewPlannedRunAsync();
        WriteRawLedger(run.Id, """{"inputTokens":10,"outputTokens":4,"wallClockMs":5000,"activeMs":5000,"perStep":[]}""");
        var vm = CreateVm(run.Id);

        await vm.RefreshAsync();

        Assert.Equal("Run_Sub_Tokens|14 · 5s", vm.LedgerSummary);
        Assert.DoesNotContain('$', vm.LedgerSummary);
        vm.Dispose();
    }

    // Legacy rows carry the withdrawn money key literally and no serializer sets UnmappedMemberHandling, so the
    // reader skips it; the fixture value is non-null on purpose, since a null would also pass a reader that bound it.
    [Fact]
    public async Task LegacyLedger_CarryingTheWithdrawnMoneyKey_ProjectsTokensAndTimeUnchanged()
    {
        var run = await NewPlannedRunAsync();
        WriteRawLedger(run.Id, """{"inputTokens":10,"outputTokens":4,"costUsd":0.42,"wallClockMs":5000,"perStep":[]}""");
        var vm = CreateVm(run.Id);

        await vm.RefreshAsync();

        Assert.Equal(10, vm.TotalInputTokens);
        Assert.Equal(4, vm.TotalOutputTokens);
        Assert.Equal(5_000, vm.WallClockMs); // exact: GetAsync is a pure read, so no clock upgrade runs
        Assert.Equal("Run_Sub_Tokens|14 · 5s", vm.LedgerSummary);
        vm.Dispose();
    }

    /// <summary>Plants a ledger the service would never write — the only way to stand in a legacy row.</summary>
    private void WriteRawLedger(Guid runId, string ledgerJson)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET LedgerJson = @Ledger WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Ledger", ledgerJson);
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
    }
}
