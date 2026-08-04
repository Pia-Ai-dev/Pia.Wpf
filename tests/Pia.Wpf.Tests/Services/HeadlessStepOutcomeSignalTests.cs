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
/// hermes #9 on the HEADLESS executor, end to end through the real <see cref="AgentRunOrchestrator"/> and a
/// real SQLite run store — because the defect is stated in terms of the PERSISTED status ("records as Done"),
/// and Done/Failed is decided in <c>AgentRunOrchestrator.SafeRecordStep</c> from <c>r.Succeeded</c>. Asserting
/// only the returned <c>StepTurnResult</c> would test the record, not the recorded status.
/// <para>
/// The discriminating pair is <see cref="DeclaredFailure_WithPlentyOfText_RecordsFailed"/> — the old
/// heuristic's exact blind spot, a step that fails and then eloquently explains itself — and
/// <see cref="DeclaredSuccess_WithNoTextAtAll_RecordsDone"/>, its inverse.
/// </para>
/// <para>
/// <b>Neutralize</b> (for both): restore <c>var succeeded = !string.IsNullOrWhiteSpace(exchange.Visible);</c>
/// at the decision line in <c>HeadlessTurnExecutor.RunExchangeStepAsync</c>. Deleting the tool instead reds
/// every fact here and proves nothing.
/// </para>
/// </summary>
public sealed class HeadlessStepOutcomeSignalTests
{
    // ---- the discriminating pair ----

    /// <summary>
    /// <b>THE RED DEMO.</b> The step produces a full paragraph of articulate prose AND declares
    /// <c>succeeded:false</c>. Old behaviour: non-empty text, so <c>AgentStepStatus.Done</c> and the run
    /// marches on with a false premise. New behaviour: the declaration wins and the row is <c>Failed</c>.
    /// </summary>
    [Fact]
    public async Task DeclaredFailure_WithPlentyOfText_RecordsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        const string eloquent =
            "I attempted to write the quarterly report. The source spreadsheet could not be opened because "
            + "the path does not exist, so no report was produced. Here is what I would have done instead.";

        h.Drive(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "the source spreadsheet is missing"), new ToolDispatchContext(1));
            return eloquent;
        });

        var run = await h.DispatchAsync(ct);

        // The text really was there — this is not "an empty step failed".
        Assert.Equal(eloquent, await h.LastAssistantTextAsync(run, ct));
        // …and the PERSISTED status is Failed, which is the whole defect.
        Assert.Equal(AgentStepStatus.Failed, Assert.Single(run.Plan).Status);
        Assert.Equal(AgentRunState.Failed, run.State);
        // The model's own reason became the failure text the replanner is handed.
        Assert.Equal("the source spreadsheet is missing", h.Planner.LastReplanFailure);
    }

    /// <summary>
    /// <b>THE INVERSE DEMO.</b> The step emits no visible text at all and declares <c>succeeded:true</c>.
    /// Old behaviour: empty text, so <c>Failed</c> + "Empty response". New behaviour: <c>Done</c>.
    /// </summary>
    [Fact]
    public async Task DeclaredSuccess_WithNoTextAtAll_RecordsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "moved the files", artifact: "out/report.md"), new ToolDispatchContext(1));
            return string.Empty; // no TextDelta whatsoever
        });

        var run = await h.DispatchAsync(ct);

        Assert.True(string.IsNullOrWhiteSpace(await h.LastAssistantTextAsync(run, ct)));
        Assert.Equal(AgentStepStatus.Done, Assert.Single(run.Plan).Status);
        Assert.Equal(AgentRunState.Completed, run.State);

        // The claim crossed into the run context, so the critic can tell a declared "ok" from an inferred one.
        var seen = Assert.Single(Assert.Single(h.Verifier.SeenCompletedSteps));
        Assert.NotNull(seen.Outcome);
        Assert.True(seen.Outcome!.Succeeded);
        Assert.Equal("out/report.md", seen.Outcome.ArtifactRef);
    }

    // ---- the fallback ----

    /// <summary>
    /// THE FALLBACK, pinned so it cannot be turned fail-closed by accident: a step that never calls the tool
    /// keeps the old non-empty-text verdict and still records <c>Done</c>. Failing an undeclared step would
    /// break every run on a provider that cannot call tools at all.
    /// </summary>
    [Fact]
    public async Task NoDeclaration_FallsBackToTheTextHeuristic_AndStillRecordsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(_ => Task.FromResult("I did the thing."));

        var run = await h.DispatchAsync(ct);

        Assert.Equal(AgentStepStatus.Done, Assert.Single(run.Plan).Status);
        Assert.Equal(AgentRunState.Completed, run.State);
        // …but it is recorded as UNCONFIRMED: no claim reached the run context, so the critic is told the
        // "ok" is only an inference.
        Assert.Null(Assert.Single(Assert.Single(h.Verifier.SeenCompletedSteps)).Outcome);
    }

    /// <summary>The fallback's other half: no declaration AND no text is still the historical failure.</summary>
    [Fact]
    public async Task NoDeclaration_AndNoText_StillRecordsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(_ => Task.FromResult(string.Empty));

        var run = await h.DispatchAsync(ct);

        Assert.Equal(AgentStepStatus.Failed, Assert.Single(run.Plan).Status);
        Assert.Equal("Empty response", h.Planner.LastReplanFailure);
    }

    /// <summary>
    /// <b>GUARD</b>. A declaration whose <c>succeeded</c> argument is unusable is NOT a failure — it is
    /// silence, so the step falls back. A provider's argument-encoding quirk must never fail a user's run.
    /// </summary>
    [Fact]
    public async Task AMalformedDeclaration_FallsBackInsteadOfFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(async handler =>
        {
            await handler!(new FunctionCallContent("c1", AgentStepTools.EmitStepResultToolName,
                new Dictionary<string, object?> { ["summary"] = "no succeeded key at all" }), new ToolDispatchContext(1));
            return "I did the thing.";
        });

        var run = await h.DispatchAsync(ct);

        Assert.Equal(AgentStepStatus.Done, Assert.Single(run.Plan).Status);
        Assert.Null(Assert.Single(Assert.Single(h.Verifier.SeenCompletedSteps)).Outcome);
    }

    // ---- scoping ----

    /// <summary>
    /// The tool is SCOPED: it reaches the provider on an agent step, and the run's cached tool list is not
    /// mutated on the way there (an in-place add would leak a step tool into every other turn on that setup).
    /// Paired with <see cref="TheFallbackTurn_IsNotOfferedTheTool"/>.
    /// </summary>
    [Fact]
    public async Task AStepTurn_IsOfferedTheTool()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(_ => Task.FromResult("done"));
        await h.DispatchAsync(ct);

        Assert.True(AgentStepTools.OffersStepResultTool(h.LastTools),
            "an agent step must be offered emit_step_result");
        Assert.False(AgentStepTools.OffersStepResultTool(h.RunTools),
            "the run's cached tool list must not have been mutated");
    }

    /// <summary>
    /// The R10 planner-degrade turn runs the goal as one ordinary turn and creates no <c>AgentStep</c> row, so
    /// there is no Done/Failed for a declaration to decide — it is deliberately not offered the tool. The live
    /// executor draws the same line at the same place.
    /// </summary>
    [Fact]
    public async Task TheFallbackTurn_IsNotOfferedTheTool()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Planner.DegradePlan = true;
        h.Drive(_ => Task.FromResult("done"));
        await h.DispatchAsync(ct);

        Assert.NotNull(h.LastTools); // the turn really ran
        Assert.False(AgentStepTools.OffersStepResultTool(h.LastTools));
    }

    /// <summary>
    /// <b>GUARD</b>. A step that resolved its OWN persona (Batch 07 G6) runs on a different
    /// <c>AssistantTurnSetup</c> than the run default, and it is still offered the tool. Augmenting the run
    /// default instead of the resolved setup compiles, leaves every other fact here green, and silently
    /// strands exactly those steps on the text heuristic forever — the Batch 14 failure mode.
    /// <para>
    /// Non-vacuity: the assertion also requires the SPECIALIST's own tool in the same list, so a fixture
    /// where the step persona quietly degraded to the run default cannot pass this.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AStepWithItsOwnPersona_IsStillOfferedTheTool()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        var specialist = h.WithSpecialistPersona();
        h.Planner.AssignedPersonaId = specialist.Id;
        h.Drive(_ => Task.FromResult("done"));

        await h.DispatchAsync(ct);

        Assert.Contains(h.LastTools!, t => t.Name == "specialist_only_tool"); // the step really re-resolved
        Assert.True(AgentStepTools.OffersStepResultTool(h.LastTools),
            "a step running on its own persona must still be offered emit_step_result");
    }

    /// <summary>
    /// <b>GUARD</b>. The interception is PRE-ROUTE: <c>RouteToolCallAsync</c> never sees the call, so no
    /// <c>ToolGateDecision.UnknownTool</c> audit row is written and the model gets a real acknowledgement
    /// rather than "Unknown tool.". Without the short-circuit the claim would be lost and the audit trail
    /// would accuse the model of inventing a tool the run itself offered it.
    /// </summary>
    [Fact]
    public async Task TheDeclarationIsInterceptedBeforeRouting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "ok"), new ToolDispatchContext(1));
            return "done";
        });
        await h.DispatchAsync(ct);

        await h.Plugins.DidNotReceive().RouteToolCallAsync(
            Arg.Is<FunctionCallContent>(c => c.Name == AgentStepTools.EmitStepResultToolName),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(h.Timeline.Rows, r => r.Decision == ToolGateDecision.UnknownTool);
        Assert.DoesNotContain(h.ToolReplies, r => r is "Unknown tool.");
        Assert.Contains(h.ToolReplies, r => r is string s && s.Contains("Recorded", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static FunctionCallContent Emit(bool succeeded, string summary, string? artifact = null)
    {
        var args = new Dictionary<string, object?> { ["succeeded"] = succeeded, ["summary"] = summary };
        if (artifact is not null) args["artifact_ref"] = artifact;
        return new FunctionCallContent("call-emit", AgentStepTools.EmitStepResultToolName, args);
    }

    /// <summary>Plans exactly one step; every replan degrades so a failed step terminates the run instead of
    /// looping, which makes the run's terminal state an assertable consequence of the step's status. Records
    /// the failure text it was handed — that is what the model's own summary has to reach.</summary>
    private sealed class OneStepPlanner : IAgentPlanner
    {
        public bool DegradePlan { get; set; }
        public Guid? AssignedPersonaId { get; set; }
        public string? LastReplanFailure { get; private set; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(DegradePlan
                ? PlanResult.Fallback
                : new PlanResult(
                    [new AgentStep
                    {
                        Ordinal = 0, Title = "S", Intent = "do it", Status = AgentStepStatus.Pending,
                        AssignedPersonaId = AssignedPersonaId,
                    }],
                    false));

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
        {
            LastReplanFailure = failure;
            return Task.FromResult(PlanResult.Fallback);
        }
    }

    /// <summary>Everything a headless run needs, wired to one temp SQLite file — the shape
    /// <c>HeadlessTurnExecutorTests.DurabilityHarness</c> established.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;
        private readonly SqliteContext _ctx;
        private readonly IAssistantPromptComposer _composer;
        private readonly BackgroundAssistantTurnRunner _engine;
        private readonly IAiClientService _ai;
        private Func<ToolCallHandler?, Task<string>> _drive = _ => Task.FromResult("ok");
        private StepPersonaResolver? _stepPersonas;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaStepOutcome_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(_ctx, Runs);
            _ai = Substitute.For<IAiClientService>();
            Plugins = Substitute.For<IPluginService>();
            Timeline = new RecordingTimelineService();

            // SupportsTools TRUE with a non-empty base list: with tools gated off the exchange engine passes
            // neither tools nor a tool handler, and there would be nothing to offer or intercept.
            RunTools = [AIFunctionFactory.Create(() => "ok", "unrelated_tool", "not the step-result tool")];
            _composer = Substitute.For<IAssistantPromptComposer>();
            _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                    Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new AssistantTurnSetup("system", RunTools, SupportsTools: true, WebSearchActive: false));

            Persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
            Provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
            var personas = Substitute.For<IPersonaService>();
            personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(Persona);
            var providers = Substitute.For<IProviderService>();
            providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider);
            Titles = Substitute.For<IChatTitleService>();
            Titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            Settings = Substitute.For<ISettingsService>();
            AppSettings = new AppSettings();
            Settings.GetSettingsAsync().Returns(AppSettings);
            Personas = personas;
            Providers = providers;

            _engine = new BackgroundAssistantTurnRunner(
                _ai, Plugins, _composer, personas, Chats, Titles, Settings, TokenMapFactory, Runs,
                new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);

            _ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                    cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
                .Returns(ci =>
                {
                    LastTools = ci.ArgAt<IList<AITool>?>(2);
                    return Stream(ci.ArgAt<ToolCallHandler?>(3));
                });
        }

        private static ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        public AgentRunService Runs { get; }
        public AssistantChatService Chats { get; }
        public IPluginService Plugins { get; }
        public RecordingTimelineService Timeline { get; }
        public ISettingsService Settings { get; }
        public AppSettings AppSettings { get; }
        public IChatTitleService Titles { get; }
        public IPersonaService Personas { get; }
        public IProviderService Providers { get; }
        public Persona Persona { get; }
        public AiProvider Provider { get; }
        public OneStepPlanner Planner { get; } = new();
        public FakeVerifier Verifier { get; } = new();

        /// <summary>The run-level cached tool list — the one the augmentation must not mutate.</summary>
        public IList<AITool> RunTools { get; }

        /// <summary>The tool list the AI client was handed on the most recent exchange (the scoping probe).</summary>
        public IList<AITool>? LastTools { get; private set; }

        /// <summary>What the interception handed back to the model, per tool call.</summary>
        public List<object?> ToolReplies { get; } = [];

        /// <summary>Sets what the model does inside the exchange: optionally call the tool handler, then
        /// return the visible text it leaves behind (empty string = no TextDelta at all).</summary>
        public void Drive(Func<ToolCallHandler?, Task<string>> drive) => _drive = drive;

        /// <summary>
        /// Puts one roster persona in place with a turn setup of its OWN — a distinct tool list carrying a
        /// marker tool — and gives the executor a real <see cref="StepPersonaResolver"/>. Roster membership is
        /// checked executor-side, so stubbing the persona store alone would see the assignment ignored.
        /// </summary>
        public Persona WithSpecialistPersona()
        {
            var specialist = new Persona { Id = Guid.NewGuid(), Name = "Specialist", SystemPrompt = "spec" };
            AppSettings.SetAgentPersonaRoster(UserOperatingMode.Personal, [specialist.Id]);
            Personas.GetPersonasAsync().Returns([specialist]);
            Personas.GetPersonaAsync(specialist.Id).Returns(specialist);
            _composer.PrepareTurn(Arg.Is<Persona>(p => p.Id == specialist.Id), Arg.Any<AiProvider>(),
                    Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new AssistantTurnSetup(
                    "specialist system",
                    [AIFunctionFactory.Create(() => "ok", "specialist_only_tool", "only the specialist has this")],
                    SupportsTools: true,
                    WebSearchActive: false));
            _stepPersonas = new StepPersonaResolver(
                Personas, Providers, _composer, Settings, NullLogger<StepPersonaResolver>.Instance);
            return specialist;
        }

        private async IAsyncEnumerable<ChatStreamItem> Stream(ToolCallHandler? handler)
        {
            ToolCallHandler? recording = handler is null
                ? null
                : async (call, ctx) =>
                {
                    var reply = await handler(call, ctx);
                    ToolReplies.Add(reply);
                    return reply;
                };

            var text = await _drive(recording);
            if (!string.IsNullOrEmpty(text))
                yield return new TextDelta(text);
            yield return new Finished(null, "test-model");
        }

        /// <summary>Bootstraps the FK parent chat + a Planned run (R1 ordering), dispatches the real
        /// orchestrator over a real headless executor, and returns the persisted run with its plan.</summary>
        public async Task<AgentRun> DispatchAsync(CancellationToken ct)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId,
                SchemaVersion = 1,
                Title = "stub",
                CreatedAt = now,
                UpdatedAt = now,
                LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(),
                Messages = [],
            }, ct);
            var run = await Runs.CreateAsync(
                new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "the goal"), ct);

            var executor = new HeadlessTurnExecutor(
                _engine, Chats, Settings, Personas, Providers, _composer, Titles, TokenMapFactory,
                NullLogger<HeadlessTurnExecutor>.Instance, Timeline, _stepPersonas);
            executor.Initialize(workspaceRoot: null, grantedWrites: []);

            var orchestrator = new AgentRunOrchestrator(
                Runs, Planner, Verifier, NullLogger<AgentRunOrchestrator>.Instance);
            await orchestrator.RunAsync(run, executor, Persona, Provider, RunProfile.Interactive, ct);

            return (await Runs.GetAsync(run.Id, ct))!;
        }

        public async Task<string> LastAssistantTextAsync(AgentRun run, CancellationToken ct)
        {
            var chat = await Chats.GetAsync(run.ChatId, ct);
            return chat?.Messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? string.Empty;
        }

        public void Dispose()
        {
            Runs.Dispose();
            _ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }
}
