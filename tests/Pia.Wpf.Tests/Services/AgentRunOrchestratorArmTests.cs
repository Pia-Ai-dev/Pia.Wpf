using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// One test per arm the run loop delegates to. Each arm is a private method reached only through
/// <c>RunAsync</c>, so every test drives the loop and asserts on the row the arm leaves behind.
/// </summary>
public sealed class AgentRunOrchestratorArmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PiaArms_" + Guid.NewGuid().ToString("N"));
    private readonly SqliteContext _sqlite;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;

    public AgentRunOrchestratorArmTests()
    {
        Directory.CreateDirectory(_dir);
        _sqlite = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_sqlite, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_sqlite, _runs);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _chats.Dispose();
        _sqlite.Dispose();
        TempPath.Remove(_dir);
    }

    [Fact]
    public async Task AnUngroundableGoal_ParksNeedsGoal_WithNoStepRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);

        await RunAsync(run, new StubPlanner(new PlanResult([], false, CannotGroundGoal: true, ClarificationQuestion: "which repo?")), ct);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(AgentRunOrchestrator.NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));
        // The zero step rows are what lets a resume tell this park from a mid-plan one.
        Assert.Null(await _runs.NextPendingStepAsync(run.Id, ct));
    }

    [Fact]
    public async Task ADegradedPlan_RunsOneFallbackTurn_AndCompletesWithoutSteps()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor();

        await RunAsync(run, new StubPlanner(PlanResult.Fallback), ct, executor);

        Assert.Equal(1, executor.FallbackTurns);
        Assert.Equal(0, executor.StepTurns);
        Assert.Equal(AgentRunState.Completed, (await _runs.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task ExhaustingTheStepBudget_ParksAtTheBudget_AfterOneGraceTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor();

        // Two steps against a one-step budget: the second iteration finds the budget spent.
        await RunAsync(run, new StubPlanner(Plan("A", "B")), ct, executor,
            profile: new RunProfile(MaxSteps: 1, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20)));

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal("step-cap", RunPauseEnvelope.ReadReason(settled));
        // The park spends one tool-free wrap-up turn, so the chat ends with "here is where I got to".
        Assert.Equal(1, executor.GraceTurns);
    }

    [Fact]
    public async Task AUserPauseRequest_ReturnsTheStepToPending_AndParksTheRunPaused()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);

        var steering = Substitute.For<IRunSteeringStore>();
        steering.TryConsumePauseRequest(run.Id).Returns(true, false);

        await RunAsync(run, new StubPlanner(Plan("A")), ct, steering: steering);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.Paused, settled.State);
        // Back to Pending, not the Failed(3) an unconditional record would have written — the resumed
        // run must still see the step the pause interrupted.
        Assert.NotNull(await _runs.NextPendingStepAsync(run.Id, ct));
    }

    [Fact]
    public async Task AStepThatNeedsToolApproval_ParksNamingTheTool_AndKeepsTheStepPending()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { ApprovalRequiredTool = "write_file" };

        await RunAsync(run, new StubPlanner(Plan("A")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(AgentRunOrchestrator.ToolApprovalReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal("write_file", RunPauseEnvelope.ReadApprovalTool(settled));
        Assert.NotNull(await _runs.NextPendingStepAsync(run.Id, ct));
    }

    [Fact]
    public async Task AStepThatAsksTheUser_ParksNeedsInput_AndKeepsTheStepPending()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { UserInputQuestion = "which branch?" };

        await RunAsync(run, new StubPlanner(Plan("A")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(AgentRunOrchestrator.NeedsInputReason, RunPauseEnvelope.ReadReason(settled));
        Assert.NotNull(await _runs.NextPendingStepAsync(run.Id, ct));
    }

    [Fact]
    public async Task ACleanDrain_EndsTheRunBeforeMarkingItCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor();

        await RunAsync(run, new StubPlanner(Plan("A")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.Completed, settled.State);
        // EndRun runs BEFORE CompleteAsync, so no consumer sees a Completed run whose chat is unsaved.
        Assert.True(executor.EndedRun);
        // The settle pins the transcript slice, so a run that executed steps never keeps a null range.
        Assert.NotNull(settled.FirstMessageId);
    }

    [Fact]
    public async Task AFirstPlanOfThreeOrMoreSteps_ParksForApproval_WhenTheExecutorSupportsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { SupportsPlanApproval = true };

        await RunAsync(run, new StubPlanner(Plan("A", "B", "C")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
        // Nothing ran — the park fires before the drain loop's first iteration.
        Assert.Equal(0, executor.StepTurns);
    }

    [Fact]
    public async Task AFirstPlanOfTwoSteps_DoesNotParkForApproval_EvenWhenTheExecutorSupportsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { SupportsPlanApproval = true };

        await RunAsync(run, new StubPlanner(Plan("A", "B")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal(AgentRunState.Completed, settled.State);
    }

    [Fact]
    public async Task AFirstPlanOfThreeSteps_DoesNotParkForApproval_WhenTheExecutorDoesNotSupportIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor(); // SupportsPlanApproval defaults false — the headless shape

        await RunAsync(run, new StubPlanner(Plan("A", "B", "C")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal(AgentRunState.Completed, settled.State);
    }

    [Fact]
    public async Task AReplanAfterAStepFailure_NeverParksForApproval_EvenThoughItHasThreeSteps()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { SupportsPlanApproval = true, FailFirstStep = true };
        // The first plan stays under the gate's threshold on its own, so only the REPLAN path is under test.
        var planner = new StubPlanner(Plan("A"), replan: Plan("D", "E", "F"));

        await RunAsync(run, planner, ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal(AgentRunState.Completed, settled.State);
    }

    [Fact]
    public async Task AnApprovedPlan_DrainsThePersistedSteps_WithoutRePlanning()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var planner = new StubPlanner(Plan("A", "B", "C"));

        await RunAsync(run, planner, ct, new StubExecutor { SupportsPlanApproval = true });
        Assert.Equal(
            AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason((await _runs.GetAsync(run.Id, ct))!));

        // Approve is the ordinary resume every other park takes — headless, and carrying the park's reason.
        var approved = new StubExecutor();
        await RunAsync(run, planner, ct, approved, resume: true, parkReason: AgentRunOrchestrator.PlanApprovalReason);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.Completed, settled.State);
        Assert.Equal(3, approved.StepTurns);
        Assert.All(settled.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    [Fact]
    public async Task ARePlanAfterAClarificationAnswer_NeverParksForApproval_BecauseEveryResumeRunsHeadless()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        // Zero step rows + the needs-goal reason is what makes this resume re-plan; the executor is the
        // headless shape every resume dispatches with, so the gate's second term can never hold here.
        var executor = new StubExecutor();

        await RunAsync(run, new StubPlanner(Plan("A", "B", "C")), ct, executor,
            resume: true, parkReason: AgentRunOrchestrator.NeedsGoalReason);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal(AgentRunState.Completed, settled.State);
        Assert.Equal(3, executor.StepTurns);
    }

    [Theory]
    [InlineData(StepInterruption.Credit, "interrupted-credit")]
    [InlineData(StepInterruption.Offline, "interrupted-offline")]
    [InlineData(StepInterruption.Timeout, "interrupted-timeout")]
    [InlineData(StepInterruption.Provider, "interrupted-provider")]
    public async Task AnInterruptedStep_ParksWithItsToken_AndKeepsTheStepPending(StepInterruption kind, string token)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var planner = new StubPlanner(Plan("A"));
        var executor = new StubExecutor { Interruption = kind };

        await RunAsync(run, planner, ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(token, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal("A", (await _runs.NextPendingStepAsync(run.Id, ct))?.Title);
        Assert.DoesNotContain(settled.Plan, s => s.Status == AgentStepStatus.Failed);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.Equal(0, executor.GraceTurns);
        Assert.False(executor.EndedRun);
        Assert.Null(settled.CompletedAt);
    }

    [Fact]
    public async Task AnInterruptionAtTheThirdStep_KeepsTheFirstTwoDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { Interruption = StepInterruption.Offline, InterruptAtStepTurn = 3 };

        await RunAsync(run, new StubPlanner(Plan("A", "B", "C")), ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentStepStatus.Done, settled.Plan.Single(s => s.Title == "A").Status);
        Assert.Equal(AgentStepStatus.Done, settled.Plan.Single(s => s.Title == "B").Status);
        Assert.Equal(AgentStepStatus.Pending, settled.Plan.Single(s => s.Title == "C").Status);
    }

    [Fact]
    public async Task AResumeAfterAnInterruption_RunsOnlyThePendingStep_WithoutRePlanning()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var planner = new StubPlanner(Plan("A", "B"));
        await RunAsync(run, planner, ct,
            new StubExecutor { Interruption = StepInterruption.Offline, InterruptAtStepTurn = 2 });

        var resumed = new StubExecutor();
        await RunAsync(run, planner, ct, resumed,
            resume: true, parkReason: AgentRunOrchestrator.InterruptedOfflineReason);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.Completed, settled.State);
        Assert.Equal(1, resumed.StepTurns);
        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(0, planner.ReplanCalls);
        Assert.All(settled.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
    }

    [Fact]
    public async Task AUserPauseRequest_OutranksAnInterruption()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var steering = Substitute.For<IRunSteeringStore>();
        steering.TryConsumePauseRequest(run.Id).Returns(true, false);

        await RunAsync(run, new StubPlanner(Plan("A")), ct,
            new StubExecutor { Interruption = StepInterruption.Offline }, steering: steering);

        Assert.Equal(AgentRunState.Paused, (await _runs.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task AnInterruptedFallbackTurn_StillFailsTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);

        await RunAsync(run, new StubPlanner(PlanResult.Fallback), ct,
            new StubExecutor { FallbackInterruption = StepInterruption.Offline });

        Assert.Equal(AgentRunState.Failed, (await _runs.GetAsync(run.Id, ct))!.State);
    }

    [Theory]
    [InlineData("interrupted-credit", true)]
    [InlineData("interrupted-offline", true)]
    [InlineData("interrupted-timeout", true)]
    [InlineData("interrupted-provider", true)]
    [InlineData("step-cap", false)]
    [InlineData("needs-input", false)]
    [InlineData("resume-interrupted", false)]
    [InlineData(null, false)]
    public void IsInterruptionReason_KnowsExactlyTheFourTokens(string? reason, bool expected)
        => Assert.Equal(expected, AgentRunOrchestrator.IsInterruptionReason(reason));

    [Fact]
    public async Task AnInterruptedReplanAfterAStepFailure_Parks_AndPutsTheFailedStepBackToPending()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var executor = new StubExecutor { FailFirstStep = true };

        await RunAsync(run, new StubPlanner(Plan("A")) { ReplanThrows = new CreditLimitException("x") }, ct, executor);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(AgentRunOrchestrator.InterruptedCreditReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal("A", (await _runs.NextPendingStepAsync(run.Id, ct))?.Title);
        Assert.False(executor.EndedRun);
    }

    [Fact]
    public async Task TheStepPutBack_RunsAgainOnResume_AndEndsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var planner = new StubPlanner(Plan("A")) { ReplanThrows = new CreditLimitException("x") };
        await RunAsync(run, planner, ct, new StubExecutor { FailFirstStep = true });

        var resumed = new StubExecutor();
        await RunAsync(run, planner, ct, resumed,
            resume: true, parkReason: AgentRunOrchestrator.InterruptedCreditReason);

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.Completed, settled.State);
        Assert.Equal(AgentStepStatus.Done, Assert.Single(settled.Plan).Status);
        Assert.Equal(1, resumed.StepTurns);
    }

    [Fact]
    public async Task AnInterruptedReplanAfterAFailedVerify_Parks_WithEveryStepDone()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);
        var planner = new StubPlanner(Plan("A"))
        {
            ReplanThrows = new HttpRequestException(HttpRequestError.ConnectionError),
        };

        await RunAsync(run, planner, ct, verifier: new RejectingVerifier());

        var settled = (await _runs.GetAsync(run.Id, ct))!;
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(AgentRunOrchestrator.InterruptedOfflineReason, RunPauseEnvelope.ReadReason(settled));
        Assert.Equal(AgentStepStatus.Done, Assert.Single(settled.Plan).Status);
        Assert.Null(await _runs.NextPendingStepAsync(run.Id, ct));
    }

    [Fact]
    public async Task AnOrdinaryReplanFault_StillFailsTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(ct);

        await RunAsync(run, new StubPlanner(Plan("A")) { ReplanThrows = new InvalidOperationException("boom") }, ct,
            new StubExecutor { FailFirstStep = true });

        Assert.Equal(AgentRunState.Failed, (await _runs.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public async Task AnInterruptedReplanDuringACancel_DoesNotPark()
    {
        var run = await NewRunAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var planner = new StubPlanner(Plan("A"))
        {
            ReplanThrows = new CreditLimitException("x"),
            CancelBeforeReplanThrows = cts,
        };

        await RunAsync(run, planner, cts.Token, new StubExecutor { FailFirstStep = true });

        var settled = (await _runs.GetAsync(run.Id, TestContext.Current.CancellationToken))!;
        Assert.NotEqual(AgentRunState.WaitingForInput, settled.State);
    }

    [Fact]
    public void SupportsPlanApproval_DefaultsFalseForAnExecutorThatDoesNotOverrideIt()
    {
        IAgentTurnExecutor executor = new StubExecutor();
        Assert.False(executor.SupportsPlanApproval);
    }

    private async Task<AgentRun> NewRunAsync(CancellationToken ct)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "t",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), Messages = [],
        }, ct);

        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "goal"), ct);
    }

    private Task RunAsync(
        AgentRun run, IAgentPlanner planner, CancellationToken ct,
        StubExecutor? executor = null, RunProfile? profile = null, IRunSteeringStore? steering = null,
        bool resume = false, string? parkReason = null, IAgentVerifier? verifier = null)
    {
        var orchestrator = new AgentRunOrchestrator(
            _runs, planner, verifier ?? new AcceptingVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
            steering: steering);

        return orchestrator.RunAsync(
            run, executor ?? new StubExecutor(), new Persona { Name = "Pia", SystemPrompt = "sys" },
            new AiProvider { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI },
            profile ?? RunProfile.Interactive, ct, resume: resume, parkReason: parkReason);
    }

    private static PlanResult Plan(params string[] titles) => new(
        [.. titles.Select((t, i) => new AgentStep
        {
            Id = Guid.Empty, Ordinal = i, Title = t, Intent = "do " + t, Status = AgentStepStatus.Pending,
        })],
        false);

    private sealed class StubPlanner(PlanResult plan, PlanResult? replan = null) : IAgentPlanner
    {
        public int PlanCalls { get; private set; }

        public int ReplanCalls { get; private set; }

        public Exception? ReplanThrows { get; init; }

        public CancellationTokenSource? CancelBeforeReplanThrows { get; init; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanCalls++;
            return Task.FromResult(plan);
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            ReplanCalls++;
            if (ReplanThrows is { } thrown)
            {
                CancelBeforeReplanThrows?.Cancel();
                return Task.FromException<PlanResult>(thrown);
            }

            return Task.FromResult(replan ?? PlanResult.Fallback);
        }
    }

    private sealed class RejectingVerifier : IAgentVerifier
    {
        public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new VerdictResult(false, "nothing was done", new[] { "x" }, null));
    }

    private sealed class AcceptingVerifier : IAgentVerifier
    {
        public Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(VerdictResult.Accept);
    }

    private sealed class StubExecutor : IAgentTurnExecutor
    {
        public string? ApprovalRequiredTool { get; init; }

        public string? UserInputQuestion { get; init; }

        public bool SupportsPlanApproval { get; init; }

        public bool FailFirstStep { get; init; }

        public StepInterruption? Interruption { get; init; }

        // Null interrupts every turn.
        public int? InterruptAtStepTurn { get; init; }

        public StepInterruption? FallbackInterruption { get; init; }

        public int StepTurns { get; private set; }

        public int FallbackTurns { get; private set; }

        public int GraceTurns { get; private set; }

        public bool EndedRun { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            StepTurns++;
            if (Interruption is { } kind && (InterruptAtStepTurn is null || InterruptAtStepTurn == StepTurns))
                return Task.FromResult(Interrupted(kind));

            var failThisOne = FailFirstStep && StepTurns == 1;
            return Task.FromResult(new StepTurnResult(
                Succeeded: !failThisOne, Cancelled: false, Error: failThisOne ? "boom" : null, VisibleText: "done", Usage: null,
                FirstMessageId: Guid.NewGuid(), LastMessageId: Guid.NewGuid(),
                ApprovalRequiredTool: ApprovalRequiredTool, UserInputQuestion: UserInputQuestion));
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            FallbackTurns++;
            if (FallbackInterruption is { } kind)
                return Task.FromResult(Interrupted(kind));

            return Task.FromResult(new StepTurnResult(
                true, false, null, "fell back", null, Guid.NewGuid(), Guid.NewGuid()));
        }

        private static StepTurnResult Interrupted(StepInterruption kind) => new(
            Succeeded: false, Cancelled: false, Error: "transport", VisibleText: string.Empty, Usage: null,
            FirstMessageId: Guid.Empty, LastMessageId: Guid.Empty, Interruption: kind);

        public Task<StepTurnResult?> RunGraceTurnAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            GraceTurns++;
            return Task.FromResult<StepTurnResult?>(new StepTurnResult(
                true, false, null, "wrapped up", null, Guid.NewGuid(), Guid.NewGuid()));
        }

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndedRun = true;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
