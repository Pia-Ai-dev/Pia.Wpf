using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Navigation;
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

    private RunProgressViewModel CreateVm(Guid runId, INavigationService? navigation = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(
            _runs, runId, _loc, _resume, NullLogger.Instance, navigation: navigation);
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
    [InlineData("plan-approval", "Run_Activity_PlanApproval")]
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
    public async Task WaitingForInput_ProjectsPlanApprovalPause_ApproveLabelAndNoNudgeBox()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.True(vm.IsPlanApprovalPause);
        Assert.True(vm.ShowRejectPlanButton);
        Assert.False(vm.ShowNudgeBox);
        Assert.Equal("Run_Action_ApprovePlan", vm.ContinueLabel);
        vm.Dispose();
    }

    [Fact]
    public async Task WaitingForInput_OrdinaryToolApprovalPark_LeavesPlanApprovalPropertiesFalse()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.PauseAsync(run.Id, AgentRunOrchestrator.ToolApprovalReason,
            TestContext.Current.CancellationToken, approvalTool: "write_file");
        await vm.RefreshAsync();

        Assert.False(vm.IsPlanApprovalPause);
        Assert.False(vm.ShowRejectPlanButton);
        Assert.True(vm.ShowNudgeBox);
        Assert.Equal("Run_Action_Continue", vm.ContinueLabel);
        vm.Dispose();
    }

    [Fact]
    public async Task RejectPlan_CallsResumeServiceRejectPlanAsync()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        await _runs.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();
        Assert.True(vm.RejectPlanCommand.CanExecute(null));

        await vm.RejectPlanCommand.ExecuteAsync(null);

        await _resume.Received(1).RejectPlanAsync(run.Id, Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    // The getter returns the right value with or without the notify attribute on _state, so only the raised
    // event discriminates it — without this the steering box would go stale across an ordinary pause.
    [Fact]
    public async Task PausingAtBudget_RaisesShowNudgeBoxChanged_NotJustShowContinueButtonChanged()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await _runs.PauseAsync(run.Id, "step-cap", TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Contains(nameof(vm.ShowNudgeBox), raised);
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


    // Slice 1 of failure legibility. FailAsync has always persisted {"error": …}; until now the only reader of
    // that column short-circuited on "truncated" and never looked, so every failed run said "Ended with an
    // error" while the answer sat one JSON member away.
    [Fact]
    public async Task FailureNote_ShowsAnUnrecognisedReason_Verbatim()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        // The model's own StepOutcomeClaim.Summary reaches the column as-is. Paraphrasing it would throw away
        // the only actionable part, so the default arm passes it through — hence NOT a localization key here.
        const string modelReason = "The ingest script exited 2: config/pipeline.yml has no 'stages' key.";
        await _runs.FailAsync(run.Id, modelReason, ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Failed, vm.State);
        Assert.Equal(modelReason, vm.FailureNote);
        vm.Dispose();
    }

    // Slice 2 adds the descriptor ALONGSIDE the reason. This is the guard on that decision: a failure whose
    // exception maps to nothing must still reach the card with its message intact, exactly as slice 1 left it.
    [Fact]
    public async Task AnUnmappedFailure_StillShowsItsReason_AndNamesNoLayer()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        const string message = "The ingest script exited 2.";
        await _runs.FailAsync(
            run.Id, message, ct: TestContext.Current.CancellationToken,
            failure: FailureMapper.ForException(new InvalidOperationException(message)));
        await vm.RefreshAsync();

        Assert.Equal(message, vm.FailureNote);
        Assert.Null(vm.FailureLayerName);
        Assert.Null(vm.FailureActionLabel);
        vm.Dispose();
    }

    [Fact]
    public async Task AFailureWithNoDescriptorAtAll_RendersAsItDidBeforeTheColumnExisted()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(run.Id, "boom", ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal("boom", vm.FailureNote);
        Assert.Null(vm.FailureLayerName);
        vm.Dispose();
    }

    [Theory]
    [InlineData(FailureLayer.Provider, "Run_FailureLayer_Provider", "Run_FailureAction_Providers")]
    [InlineData(FailureLayer.Endpoint, "Run_FailureLayer_Endpoint", "Run_FailureAction_Providers")]
    [InlineData(FailureLayer.App, "Run_FailureLayer_App", "Run_FailureAction_Diagnostics")]
    [InlineData(FailureLayer.Workspace, "Run_FailureLayer_Workspace", "Run_FailureAction_Diagnostics")]
    public async Task AKnownLayer_IsNamed_AndOffersItsAction(
        FailureLayer layer, string expectedLayerKey, string expectedActionKey)
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(
            run.Id, "boom", ct: TestContext.Current.CancellationToken,
            failure: new PiaFailure(layer, "Code", false));
        await vm.RefreshAsync();

        Assert.Equal(expectedLayerKey, vm.FailureLayerName);
        Assert.Equal(expectedActionKey, vm.FailureActionLabel);
        vm.Dispose();
    }

    /// <summary>A layer nobody can act on names itself and stops there, rather than offering a dead button.</summary>
    [Fact]
    public async Task AToolFailure_NamesItsLayerButOffersNoAction()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(
            run.Id, "boom", ct: TestContext.Current.CancellationToken,
            failure: new PiaFailure(FailureLayer.Tool, "Undetailed", false));
        await vm.RefreshAsync();

        Assert.Equal("Run_FailureLayer_Tool", vm.FailureLayerName);
        Assert.Null(vm.FailureActionLabel);
        vm.Dispose();
    }

    /// <summary>
    /// The two arms use DIFFERENT NavigateTo overloads, and this code is the first caller of the tuple one
    /// outside the meeting overlay, so the values are pinned rather than assumed. The Endpoint arm was also
    /// driven through the running app; this covers the arm a live failure could not easily produce.
    /// </summary>
    [Theory]
    [InlineData(FailureLayer.Provider)]
    [InlineData(FailureLayer.Endpoint)]
    public async Task TheProviderAction_NavigatesToTheProvidersTab(FailureLayer layer)
    {
        var nav = Substitute.For<INavigationService>();
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id, nav);

        await _runs.FailAsync(
            run.Id, "boom", ct: TestContext.Current.CancellationToken,
            failure: new PiaFailure(layer, "Transport", false));
        await vm.RefreshAsync();
        vm.RunFailureActionCommand.Execute(null);

        nav.Received(1).NavigateTo<SettingsViewModel, int>((int)SettingsTab.Providers);
        vm.Dispose();
    }

    [Theory]
    [InlineData(FailureLayer.App)]
    [InlineData(FailureLayer.Workspace)]
    public async Task TheDiagnosticsAction_NavigatesToWhereTheExportButtonLives(FailureLayer layer)
    {
        var nav = Substitute.For<INavigationService>();
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id, nav);

        await _runs.FailAsync(
            run.Id, "boom", ct: TestContext.Current.CancellationToken,
            failure: new PiaFailure(layer, "WorkspaceSetup", false));
        await vm.RefreshAsync();
        vm.RunFailureActionCommand.Execute(null);

        nav.Received(1).NavigateTo<SettingsViewModel, (int, int)>(
            ((int)SettingsTab.General, (int)GeneralSettingsInnerTab.Application));
        vm.Dispose();
    }

    /// <summary>A layer with no action must not navigate anywhere when the command is reached anyway.</summary>
    [Fact]
    public async Task ALayerWithNoAction_NavigatesNowhere()
    {
        var nav = Substitute.For<INavigationService>();
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id, nav);

        await _runs.FailAsync(
            run.Id, "boom", ct: TestContext.Current.CancellationToken,
            failure: new PiaFailure(FailureLayer.Tool, "Undetailed", false));
        await vm.RefreshAsync();
        vm.RunFailureActionCommand.Execute(null);

        Assert.Empty(nav.ReceivedCalls());
        vm.Dispose();
    }

    /// <summary>Same gate as the note beside it: a run that has not failed says nothing about layers.</summary>
    [Fact]
    public async Task ARunningRun_NamesNoLayer_EvenWithADescriptorOnTheRow()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(
            run.Id, "boom", ct: TestContext.Current.CancellationToken,
            failure: new PiaFailure(FailureLayer.Provider, "Timeout", false));
        await _runs.SetStateAsync(run.Id, AgentRunState.Running, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Null(vm.FailureLayerName);
        Assert.Null(vm.FailureActionLabel);
        vm.Dispose();
    }

    // Each token is referenced from the VM by NAME, so this also pins that the five writers and the one reader
    // still agree on the spelling — a renamed const fails to compile rather than showing the raw string.
    [Theory]
    [InlineData(AgentStepTools.EmptyResponseFailure, "Run_Failed_EmptyResponse")]
    [InlineData(AgentStepTools.UndetailedFailure, "Run_Failed_Undetailed")]
    [InlineData(HeadlessRunLauncher.WorkspaceSetupFailure, "Run_Failed_WorkspaceSetup")]
    [InlineData(HeadlessRunLauncher.ShutdownInterruptedFailure, "Run_Failed_Interrupted")]
    [InlineData(AgentRunOrchestrator.SupersededFailureReason, "Run_Failed_Superseded")]
    public async Task FailureNote_LocalizesAnAppOwnedToken(string token, string expectedKey)
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(run.Id, token, ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(expectedKey, vm.FailureNote);
        Assert.NotEqual(token, vm.FailureNote);
        vm.Dispose();
    }

    // The user-cancel path passes a null error. Gating on the Failed FAMILY would otherwise have to special-case
    // Cancelled; a null reason selects itself out instead.
    [Fact]
    public async Task FailureNote_IsNull_WhenTheRunCarriesNoReason()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(run.Id, null, cancelled: true, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Failed, vm.State);
        Assert.Null(vm.FailureNote);
        vm.Dispose();
    }

    // A run cancelled BECAUSE something failed carries the reason, and saying so is the point: MapState folds
    // Cancelled into the Failed family, and a shutdown sweep or a failed child is exactly the case worth naming.
    [Fact]
    public async Task FailureNote_ShowsTheReason_OnACancelledRunThatCarriesOne()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.FailAsync(run.Id, HeadlessRunLauncher.ShutdownInterruptedFailure, cancelled: true,
            TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal("Run_Failed_Interrupted", vm.FailureNote);
        vm.Dispose();
    }

    // The two ExtraJson envelopes never coexist: CompleteAsync writes {truncated,reason}, FailAsync writes
    // {error}. Both directions asserted, because one reader picking up the other's envelope is exactly the
    // failure this shape invites.
    [Fact]
    public async Task FailureNote_AndTruncationNote_DoNotReadEachOthersEnvelope()
    {
        var truncated = await NewPlannedRunAsync();
        var truncVm = CreateVm(truncated.Id);
        await _runs.CompleteAsync(truncated.Id, truncated: true, truncationReason: "unverified",
            ct: TestContext.Current.CancellationToken);
        await truncVm.RefreshAsync();
        Assert.Equal("Run_Unverified", truncVm.TruncationNote);
        Assert.Null(truncVm.FailureNote);
        truncVm.Dispose();

        var failed = await NewPlannedRunAsync();
        var failVm = CreateVm(failed.Id);
        await _runs.FailAsync(failed.Id, "disk full", ct: TestContext.Current.CancellationToken);
        await failVm.RefreshAsync();
        Assert.Equal("disk full", failVm.FailureNote);
        Assert.False(failVm.IsTruncated);
        Assert.Null(failVm.TruncationNote);
        failVm.Dispose();
    }

    [Fact]
    public async Task FailureNote_IsNull_OnACleanCompletion()
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.CompleteAsync(run.Id, ct: TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Completed, vm.State);
        Assert.Null(vm.FailureNote);
        vm.Dispose();
    }

    // The envelope is not something this reader controls, so every shape it cannot use has to degrade to "no
    // note" rather than throw: a typed getter on the wrong JsonValueKind throws instead of returning false.
    [Theory]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("{\"error\":null}")]
    [InlineData("{\"error\":42}")]
    [InlineData("{\"error\":{\"nested\":\"x\"}}")]
    [InlineData("[]")]
    public async Task FailureNote_DegradesQuietly_OnAnEnvelopeItCannotRead(string extraJson)
    {
        var run = await NewPlannedRunAsync();
        await _runs.FailAsync(run.Id, "replaced below", ct: TestContext.Current.CancellationToken);

        using (var write = _ctx.GetConnection().CreateCommand())
        {
            write.CommandText = "UPDATE AgentRuns SET ExtraJson=@E WHERE Id=@Id";
            write.Parameters.AddWithValue("@E", extraJson);
            write.Parameters.AddWithValue("@Id", run.Id.ToString());
            write.ExecuteNonQuery();
        }

        var vm = CreateVm(run.Id);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.Failed, vm.State);
        Assert.Null(vm.FailureNote);
        vm.Dispose();
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
    }
}
