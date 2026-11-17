using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class RunProgressStopAndCheckTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _runs;
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressStopAndCheckTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaRunStop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _ctx = new SqliteContext(Path.Combine(_tmpDir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        TempPath.Remove(_tmpDir);
    }

    private async Task<AgentRun> NewRunAsync(AgentRunState state, int steps)
    {
        var ct = TestContext.Current.CancellationToken;
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
        }, ct);

        var run = await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "g"), ct);
        await _runs.ReplaceStepsAsync(run.Id, Enumerable.Range(0, steps)
            .Select(i => new AgentStep { Id = Guid.NewGuid(), RunId = run.Id, Ordinal = i, Title = "s" + i })
            .ToList(), ct);

        switch (state)
        {
            case AgentRunState.Completed: await _runs.CompleteAsync(run.Id, ct: ct); break;
            case AgentRunState.Cancelled: await _runs.FailAsync(run.Id, null, cancelled: true, ct); break;
            default: await _runs.SetStateAsync(run.Id, state, ct); break;
        }

        return run;
    }

    private async Task<RunProgressViewModel> OpenAsync(Guid runId)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var vm = new RunProgressViewModel(_runs, runId, _loc, _resume, NullLogger.Instance);
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Stop_CancelsTheHeadlessRun()
    {
        var run = await NewRunAsync(AgentRunState.Running, 2);
        _resume.CancelRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(true);
        var vm = await OpenAsync(run.Id);

        Assert.True(vm.ShowStopButton);
        await vm.StopCommand.ExecuteAsync(null);

        await _resume.Received(1).CancelRunAsync(run.Id, Arg.Any<CancellationToken>());
        Assert.Null(vm.StopNote);
        Assert.True(vm.IsStopping); // held until the row settles
    }

    [Fact]
    public async Task Stop_FallsBackToTheLiveTurn_WhenNoHeadlessDispatchHoldsTheRun()
    {
        var run = await NewRunAsync(AgentRunState.Running, 2);
        _resume.CancelRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(false);
        var vm = await OpenAsync(run.Id);
        var fellBack = false;
        vm.LiveStopFallback = () => fellBack = true;

        await vm.StopCommand.ExecuteAsync(null);

        Assert.True(fellBack);
        Assert.Null(vm.StopNote);
    }

    [Fact]
    public async Task Stop_ThatNothingCouldApply_SaysSoAndGivesTheButtonBack()
    {
        var run = await NewRunAsync(AgentRunState.Paused, 2);
        _resume.CancelRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(false);
        var vm = await OpenAsync(run.Id);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal("Run_Stop_Error_Refused", vm.StopNote);
        Assert.False(vm.IsStopping);
        Assert.True(vm.CanStop);
    }

    [Fact]
    public async Task AFinishedRun_OffersNoStop()
    {
        var run = await NewRunAsync(AgentRunState.Completed, 2);
        var vm = await OpenAsync(run.Id);

        Assert.False(vm.ShowStopButton);
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task APlanWaitingForApproval_OffersDiscardRatherThanStop()
    {
        var run = await NewRunAsync(AgentRunState.Running, 3);
        await _runs.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason, TestContext.Current.CancellationToken);
        var vm = await OpenAsync(run.Id);

        Assert.True(vm.IsPlanApprovalPause);
        Assert.False(vm.ShowStopButton);
    }

    [Theory]
    [InlineData(AgentRunState.Running, AgentStepStatus.Pending, null)]
    [InlineData(AgentRunState.Verifying, AgentStepStatus.Running, "Run_Check_Checking")]
    [InlineData(AgentRunState.Completed, AgentStepStatus.Done, "Run_Check_Passed")]
    [InlineData(AgentRunState.Cancelled, AgentStepStatus.Skipped, "Run_Check_NotRun")]
    public async Task TheCheckRow_FollowsTheRun(AgentRunState state, AgentStepStatus status, string? outcome)
    {
        var run = await NewRunAsync(state, 2);
        var vm = await OpenAsync(run.Id);

        Assert.True(vm.ShowCheckRow);
        Assert.Equal(status, vm.CheckStatus);
        Assert.Equal(outcome, vm.CheckOutcome);
        Assert.Equal(2, vm.Steps.Count); // never a row of the plan itself
    }

    // The verifier accepts a one-step plan without an artifact unchecked, so no row promises a check there.
    [Fact]
    public async Task AOneStepPlan_HasNoCheckRow()
    {
        var run = await NewRunAsync(AgentRunState.Running, 1);
        var vm = await OpenAsync(run.Id);

        Assert.False(vm.ShowCheckRow);
    }

    [Fact]
    public async Task ThePublishResult_MovesIntoTheCheckRow()
    {
        var run = await NewRunAsync(AgentRunState.Completed, 2);
        var vm = await OpenAsync(run.Id);

        vm.PublishNote = "published";

        Assert.Equal("published", vm.CheckPublishNote);
        Assert.False(vm.ShowLoosePublishNote);
    }

    [Fact]
    public async Task TheBlockCarriesItsAnchorInputs()
    {
        var run = await NewRunAsync(AgentRunState.Running, 2);
        var first = Guid.NewGuid();
        var last = Guid.NewGuid();
        await _runs.SetRunMessageRangeAsync(run.Id, first, last, TestContext.Current.CancellationToken);
        var vm = await OpenAsync(run.Id);

        Assert.Equal(first, vm.FirstMessageId);
        Assert.Equal(last, vm.LastMessageId);
        Assert.NotNull(vm.RunCreatedAt);
    }
}
