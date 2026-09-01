using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
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
        bool resume = false, string? parkReason = null)
    {
        var orchestrator = new AgentRunOrchestrator(
            _runs, planner, new AcceptingVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
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
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(plan);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(replan ?? PlanResult.Fallback);
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

        public int StepTurns { get; private set; }

        public int FallbackTurns { get; private set; }

        public int GraceTurns { get; private set; }

        public bool EndedRun { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            StepTurns++;
            var failThisOne = FailFirstStep && StepTurns == 1;
            return Task.FromResult(new StepTurnResult(
                Succeeded: !failThisOne, Cancelled: false, Error: failThisOne ? "boom" : null, VisibleText: "done", Usage: null,
                FirstMessageId: Guid.NewGuid(), LastMessageId: Guid.NewGuid(),
                ApprovalRequiredTool: ApprovalRequiredTool, UserInputQuestion: UserInputQuestion));
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            FallbackTurns++;
            return Task.FromResult(new StepTurnResult(
                true, false, null, "fell back", null, Guid.NewGuid(), Guid.NewGuid()));
        }

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
