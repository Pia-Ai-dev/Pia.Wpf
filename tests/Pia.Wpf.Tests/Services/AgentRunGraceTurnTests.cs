using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// One tool-free wrap-up round before a BUDGET park, so the chat a person opens later ends with "here is where I
/// got to" — and the park still happens when that round throws.
/// </summary>
public sealed class AgentRunGraceTurnTests
{
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static StepTurnResult Ok(string text = "done") =>
        new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

    private static List<AgentStep> MakeSteps(params (string Title, string Intent)[] steps)
    {
        var result = new List<AgentStep>();
        for (var i = 0; i < steps.Length; i++)
            result.Add(new AgentStep
            {
                Id = Guid.Empty, Ordinal = i, Title = steps[i].Title, Intent = steps[i].Intent,
                Status = AgentStepStatus.Pending,
            });
        return result;
    }

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    // Records whether the grace turn was asked for, by overriding the interface's defaulted member.
    private class ParkingExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = [];
        public bool PausedCalled { get; private set; }
        public int GraceCalls { get; private set; }

        /// <summary>What the grace turn returns; null ⇒ "the executor produced nothing".</summary>
        public StepTurnResult? GraceResult { get; set; }

        /// <summary>When set, the grace turn throws it — the park must survive that.</summary>
        public Exception? GraceThrows { get; set; }

        /// <summary>Captured so a fact can assert the grace turn was not handed the run's own token verbatim.</summary>
        public bool GraceTokenWasCancelled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(Ok());
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok("fallback"));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true;
            return Task.CompletedTask;
        }

        public virtual Task<StepTurnResult?> RunGraceTurnAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            GraceCalls++;
            GraceTokenWasCancelled = ct.IsCancellationRequested;
            if (GraceThrows is not null) throw GraceThrows;
            return Task.FromResult(GraceResult);
        }
    }

    /// <summary>Keeps the interface default — i.e. every existing hand-written fake, and the live executor.</summary>
    private sealed class DefaultExecutor : IAgentTurnExecutor
    {
        public bool PausedCalled { get; private set; }

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok());

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(Ok("fallback"));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Harness : IDisposable
    {
        public readonly SqliteContext Ctx;
        public readonly AgentRunService Runs;
        public readonly AssistantChatService Chats;
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaGrace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(Ctx, Runs);
        }

        public async Task<AgentRun> NewRunAsync(string goal)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
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
            return await Runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: goal));
        }

        public AgentRunOrchestrator Build(IAgentPlanner planner) =>
            new(Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>A plan of three steps against a two-step budget — the shape that parks.</summary>
    private static (FakePlanner Planner, RunProfile Profile) TwoStepBudget()
    {
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2"), ("C", "s3")), false));
        return (planner, new RunProfile(MaxSteps: 2, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnABudgetPark_OneGraceTurnIsSpent_AndBilledRunLevel()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var (planner, profile) = TwoStepBudget();
        var exec = new ParkingExecutor
        {
            GraceResult = new StepTurnResult(
                true, false, null, "here is where I got to",
                new UsageDetails { InputTokenCount = 30, OutputTokenCount = 7 },
                Guid.NewGuid(), Guid.NewGuid()),
        };

        await h.Build(planner).RunAsync(run, exec, Persona(), Provider(), profile, Ct);

        Assert.Equal(1, exec.GraceCalls);                        // exactly one, after the budget was hit
        Assert.Equal(2, exec.Executed.Count);                    // and it did NOT buy an extra step
        Assert.False(exec.GraceTokenWasCancelled);

        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("step-cap", final.ExtraJson ?? string.Empty);
        // The wrap-up's tokens are real spend and are billed run-level, like every other non-step turn.
        Assert.Contains("\"inputTokens\":30", final.LedgerJson ?? string.Empty);
        Assert.Contains("\"outputTokens\":7", final.LedgerJson ?? string.Empty);
    }

    // A courtesy round must never cost the park.
    [Fact]
    public async Task AThrowingGraceTurn_StillParksTheRun()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var (planner, profile) = TwoStepBudget();
        var exec = new ParkingExecutor { GraceThrows = new InvalidOperationException("provider exploded") };

        await h.Build(planner).RunAsync(run, exec, Persona(), Provider(), profile, Ct);

        Assert.Equal(1, exec.GraceCalls);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("step-cap", final.ExtraJson ?? string.Empty);
        Assert.True(exec.PausedCalled); // the non-terminal release hook still ran
        Assert.Null(final.CompletedAt);
    }

    /// <summary>
    /// An executor that produced nothing (or spends no grace turn at all) parks exactly as before — the run is
    /// not held up and nothing is billed.
    /// </summary>
    [Fact]
    public async Task AGraceTurnThatProducesNothing_ChangesNothing()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var (planner, profile) = TwoStepBudget();
        var exec = new ParkingExecutor { GraceResult = null };

        await h.Build(planner).RunAsync(run, exec, Persona(), Provider(), profile, Ct);

        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.True(exec.PausedCalled);
    }

    // The interface default: ten hand-written fakes and LiveTurnExecutor take this path, so it must stay
    // indistinguishable from the loop as it was before the grace turn existed.
    [Fact]
    public async Task GraceTurnIsNotSpentByAnExecutorThatDoesNotWantOne()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var (planner, profile) = TwoStepBudget();
        var exec = new DefaultExecutor();

        await h.Build(planner).RunAsync(run, exec, Persona(), Provider(), profile, Ct);

        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.WaitingForInput, final!.State);
        Assert.Contains("step-cap", final.ExtraJson ?? string.Empty);
        Assert.True(exec.PausedCalled);
        // The ledger row exists (it also carries wall clock), but no tokens were billed: this executor's steps
        // report none and no grace turn was spent.
        Assert.Contains("\"inputTokens\":0", final.LedgerJson ?? string.Empty);
        Assert.Contains("\"outputTokens\":0", final.LedgerJson ?? string.Empty);
    }

    // The real executor's grace turn is sent NO tools: the run's budget is already spent, so a wrap-up that could
    // still call write_file would be an action past the cap.
    [Fact]
    public async Task TheHeadlessGraceTurn_IsSentNoTools()
    {
        using var h = new ToolFreeHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2")), false));
        var budget = new RunProfile(MaxSteps: 1, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget, Ct);

        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, Ct))!.State);
        Assert.Equal(2, h.ToolsPerTurn.Count);                    // one step turn + the grace turn
        Assert.NotNull(h.ToolsPerTurn[0]);
        Assert.NotEmpty(h.ToolsPerTurn[0]!);                      // the step really was offered tools
        Assert.True(h.ToolsPerTurn[1] is null or { Count: 0 });    // the grace turn was not
    }

    /// <summary>
    /// A real <see cref="HeadlessTurnExecutor"/> over a temp database, with the tool list handed to the AI client
    /// recorded per turn. Deliberately minimal — the only question it exists to answer is the one above.
    /// </summary>
    private sealed class ToolFreeHarness : IDisposable
    {
        private readonly string _dir;
        private readonly SqliteContext _ctx;
        private readonly BackgroundAssistantTurnRunner _engine;
        private readonly IAssistantPromptComposer _composer;
        private readonly IChatTitleService _titles;
        private readonly ISettingsService _settings;
        private readonly IPersonaService _personas;
        private readonly IProviderService _providers;

        public AgentRunService Runs { get; }
        public AssistantChatService Chats { get; }
        public Persona Persona { get; } = new() { Name = "Pia", SystemPrompt = "sys" };
        public AiProvider Provider { get; } =
            new() { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

        /// <summary>The tool list the client was handed, in turn order.</summary>
        public List<IList<AITool>?> ToolsPerTurn { get; } = [];

        public ToolFreeHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaGraceTools_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(_ctx, Runs);

            // SupportsTools TRUE with a non-empty list, or the assertion above would be vacuous.
            IList<AITool> tools = [AIFunctionFactory.Create(() => "ok", "some_tool", "a tool")];
            _composer = Substitute.For<IAssistantPromptComposer>();
            _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                    Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new AssistantTurnSetup("system", tools, SupportsTools: true, WebSearchActive: false));

            _personas = Substitute.For<IPersonaService>();
            _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(Persona);
            _providers = Substitute.For<IProviderService>();
            _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider);
            _titles = Substitute.For<IChatTitleService>();
            _titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            _settings = Substitute.For<ISettingsService>();
            _settings.GetSettingsAsync().Returns(new AppSettings());

            var ai = Substitute.For<IAiClientService>();
            ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                    cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
                .Returns(ci =>
                {
                    ToolsPerTurn.Add(ci.ArgAt<IList<AITool>?>(2));
                    return Reply("ok " + ToolsPerTurn.Count);
                });

            _engine = new BackgroundAssistantTurnRunner(
                ai, Substitute.For<IPluginService>(), Substitute.For<IToolPermissionService>(),
                _composer, _personas, Chats, _titles, _settings,
                static () => Substitute.For<ITokenMapService>(), Runs, new ExecutingRunStore(),
                NullLogger<BackgroundAssistantTurnRunner>.Instance);
        }

        private static async IAsyncEnumerable<ChatStreamItem> Reply(string text)
        {
            yield return new TextDelta(text);
            await Task.Yield();
            yield return new Finished(null, "test-model");
        }

        public HeadlessTurnExecutor NewExecutor() => new(
            _engine, Chats, _settings, _personas, _providers, _composer, _titles,
            static () => Substitute.For<ITokenMapService>(),
            NullLogger<HeadlessTurnExecutor>.Instance, timelineService: null, stepPersonas: null);

        public async Task<AgentRun> NewRunAsync(string goal)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "t",
                CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            });
            return await Runs.CreateAsync(
                new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: goal));
        }

        public AgentRunOrchestrator Orchestrator(IAgentPlanner planner) =>
            new(Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        public void Dispose()
        {
            Runs.Dispose();
            _ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    // The exchange engine turns a cancellation or a provider fault into a RETURNED failure result rather than a
    // throw, so a null check alone would log a wrap-up that never happened.
    [Fact]
    public async Task AGraceTurnThatFailed_IsNotLoggedAsAWrapUp()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var (planner, profile) = TwoStepBudget();
        var logger = new CapturingLogger<AgentRunOrchestrator>();
        // What a cancelled or faulted exchange actually returns: non-null, not succeeded, no text.
        var exec = new ParkingExecutor
        {
            GraceResult = new StepTurnResult(false, true, "cancelled", string.Empty, null, Guid.Empty, Guid.Empty),
        };

        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), logger);
        await orchestrator.RunAsync(run, exec, Persona(), Provider(), profile, Ct);

        var lines = logger.Entries.Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.Contains("grace turn produced no wrap-up"));
        Assert.DoesNotContain(lines, l => l.Contains("grace turn produced a wrap-up"));
        Assert.Equal(AgentRunState.WaitingForInput, (await h.Runs.GetAsync(run.Id, Ct))!.State);
    }

    /// <summary>The positive half, so the line above is not simply never emitted.</summary>
    [Fact]
    public async Task AGraceTurnThatSpoke_IsLoggedAsAWrapUp()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var (planner, profile) = TwoStepBudget();
        var logger = new CapturingLogger<AgentRunOrchestrator>();
        var exec = new ParkingExecutor { GraceResult = Ok("here is where I got to") };

        var orchestrator = new AgentRunOrchestrator(h.Runs, planner, new FakeVerifier(), logger);
        await orchestrator.RunAsync(run, exec, Persona(), Provider(), profile, Ct);

        Assert.Contains(logger.Entries.Select(e => e.Message), l => l.Contains("grace turn produced a wrap-up"));
    }

    /// <summary>
    /// A user pause is NOT a budget park, and it must not spend a round: the person is right there, and they
    /// asked the run to stop now.
    /// </summary>
    [Fact]
    public async Task AUserPause_SpendsNoGraceTurn()
    {
        using var h = new Harness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(MakeSteps(("A", "s1"), ("B", "s2")), false));
        var steering = new RunSteeringStore();
        var exec = new ParkingExecutor();

        // Request the pause before the first step returns, the way the panel's Pause command does.
        steering.RegisterDispatch(run.Id, () => { });
        steering.RecordPauseRequest(run.Id);

        var orchestrator = new AgentRunOrchestrator(
            h.Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance,
            workspaces: null, childLauncher: null, chats: null, steering: steering);
        await orchestrator.RunAsync(run, exec, Persona(), Provider(),
            new RunProfile(MaxSteps: 8, MaxReplans: 2, WallClock: TimeSpan.FromMinutes(20)), Ct);

        Assert.Equal(0, exec.GraceCalls);
        var final = await h.Runs.GetAsync(run.Id, Ct);
        Assert.Equal(AgentRunState.Paused, final!.State);
    }
}
