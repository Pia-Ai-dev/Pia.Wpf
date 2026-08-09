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

/// <summary>
/// R12/G3: the run-progress projection over a real <see cref="AgentRunService"/>. Covers state mapping
/// (incl. the distinct truncated-Completed), the moving Running highlight, live ledger accrual, run-id
/// filtering, and off-thread RunChanged marshaling with no cross-thread WPF exception.
/// Written to run on Windows/CI — the WPF test assembly cannot execute on macOS.
/// </summary>
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

    /// <summary>Runs Post callbacks inline so the projection is observable synchronously in tests.</summary>
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
        Assert.Equal("Run_Activity_Planning", vm.CurrentActivity); // Planning note (fake echoes the loc key)
        Assert.True(vm.HasCurrentActivity);

        await _runs.SetStateAsync(run.Id, AgentRunState.Running, TestContext.Current.CancellationToken);
        await _runs.SetStepStatusAsync(step.Id, AgentStepStatus.Running, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.Equal("Read notes", vm.CurrentActivity); // active step title

        await _runs.CompleteAsync(run.Id, ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.Null(vm.CurrentActivity); // terminal → line hidden
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
        Assert.Null(cleanVm.TruncationNote); // no chip on a clean completion
        cleanVm.Dispose();
    }

    /// <summary>J1: the truncation chip must name the REAL reason. Budget exhaustion now parks the run,
    /// so the only reason the run loop still produces is "unverified" — a run whose work was never
    /// verified must not claim it hit a budget it never hit.</summary>
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
        Assert.Equal("Run_Unverified", vm.TruncationNote); // fake echoes the loc key
        Assert.NotEqual("Run_StoppedAtBudget", vm.TruncationNote);
        vm.Dispose();
    }

    /// <summary>A run persisted BEFORE the budget-pause change carries reason "budget" (or the two
    /// orchestrator cap reasons) — those keep the budget wording, which is true for them.</summary>
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

    /// <summary>An unknown or absent reason must degrade to the neutral "ended early" copy — falling
    /// back to the budget wording is exactly the lie this mapping removes.</summary>
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

    /// <summary>The chip clears when the run leaves the truncated state (a resumed/replanned run must
    /// not keep a stale note): Completed+truncated → Running drops both flag and note.</summary>
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

    /// <summary>Verifying folds into the Running chip (spinner stays lit) but supplies its own
    /// current-activity line — the only rendered signal that the critic pass is running.</summary>
    [Fact]
    public async Task Verifying_FoldsToRunningChip_WithItsOwnActivityLine()
    {
        var run = await NewPlannedRunAsync();
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "Write report", Status = AgentStepStatus.Done };
        await _runs.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);
        var vm = CreateVm(run.Id);

        await _runs.SetStateAsync(run.Id, AgentRunState.Verifying, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Running, vm.State); // spinner stays lit (MapState default)
        Assert.False(vm.IsTruncated);
        Assert.Equal("Run_Activity_Verifying", vm.CurrentActivity); // fake echoes the loc key
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

        // A terminal event for a DIFFERENT run must not reproject this vm (no throw either).
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

        // Raise a state change from a background thread — the vm must marshal via the captured
        // SynchronizationContext (G3). With the inline context installed on THIS thread, the assert is
        // that no exception escapes; production posts to the WPF dispatcher.
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
        Assert.Equal("Run_Activity_WaitingAtBudget", vm.CurrentActivity); // fake echoes the loc key
        Assert.True(vm.CanContinue);
        Assert.True(vm.ContinueCommand.CanExecute(null));
        vm.Dispose();
    }

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 fix pass). WaitingForInput is reached for THREE reasons since Batch 07 and only
    /// one of them is a budget: a fan-out's child can park at its own halved budget (the parent re-parks with
    /// "children-parked"), and the startup reconcile re-parks a parent interrupted mid-fan-out
    /// ("children-interrupted"). Announcing either as "Stopped at budget — continue?" sends the user to raise
    /// budgets in Settings that were never reached, which changes nothing.
    /// <para>
    /// The <c>step-cap</c> row is the non-vacuity control AND the fallback pin: an unknown or absent reason must
    /// keep the budget wording, because that is what every pause the run loop writes for itself really is.
    /// Neutralization: go back to a constant <c>Run_Activity_WaitingAtBudget</c> → the first two rows red.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("children-parked", "Run_Activity_ChildrenParked")]
    [InlineData("children-interrupted", "Run_Activity_ChildrenInterrupted")]
    // Batch 08 G2. Driven through PauseAsync (→ WaitingForInput) because that is the only route that reaches
    // DescribePause at all: ComputeActivity returns null for Paused itself, since the state chip carries it.
    // The arm exists so the mapping is defensible the day that changes, instead of saying "Stopped at budget"
    // to someone who pressed Pause.
    [InlineData("user", "Run_Activity_UserPaused")]
    // Batch 08 F19, and unlike the "user" row above this one IS reachable today: the launcher's three re-park
    // arms write it and they write WaitingForInput, which is exactly the state this mapping renders for. It
    // fell through to the budget arm, so a Continue that never started announced itself as "Stopped at
    // budget" — and for a run the user had paused by hand a moment earlier, that also overwrote who paused it.
    [InlineData("resume-interrupted", "Run_Activity_ResumeInterrupted")]
    // On the interactive path this label is the only surface for these two reasons — token-keyed, never the model's question.
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
        Assert.Equal(expectedKey, vm.CurrentActivity); // the fake localization echoes the loc key
        vm.Dispose();
    }

    /// <summary>
    /// hermes #16, <b>REGRESSION</b>. The FOURTH reason a run reaches WaitingForInput, and the first one that
    /// has to name something: the run is waiting for a human to approve a specific tool, and the Continue
    /// button beside this line is what grants it.
    /// <para>
    /// The assertion is deliberately "not the budget wording, AND it carries the tool name". Asserting only
    /// that the line is non-empty would pass on the fall-through arm — both pause readers degrade to the
    /// budget copy rather than failing, which is exactly how Batch 08 F19 shipped a lie, and is the failure
    /// this branch has now logged three times as "the assertion observed the default, not the mechanism".
    /// </para>
    /// <para>Neutralize: delete the <c>ToolApprovalReason</c> arm from <c>DescribePause</c> → red.</para>
    /// </summary>
    [Fact]
    public async Task AToolApprovalParksActivityLineNamesTheToolAndIsNotTheBudgetWording()
    {
        // Format is stubbed only here: every other activity string is a bare key lookup, and echoing
        // "key|arg0" keeps both halves of the claim readable in one assert.
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

    /// <summary>Batch 02: pricing is withdrawn, so the strip is tokens + active seconds and nothing else.
    /// Asserted as the WHOLE string rather than a Contains — a money segment appended after the seconds
    /// is exactly what a substring check would let through.</summary>
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

    /// <summary>Persisted-data compatibility for that removal. The ledger's JSON options never suppressed
    /// nulls, so every row written before 2026-07-30 carries the withdrawn money key literally; neither
    /// serializer sets <c>UnmappedMemberHandling</c>, so the reader skips it — no migration, no shim. The
    /// fixture value is deliberately non-null: a null would also pass against a reader that still bound
    /// the field. If the key made <c>TryParseLedger</c> throw it would return null and the tokens below
    /// would read 0, so these asserts are what proves the parse.</summary>
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
