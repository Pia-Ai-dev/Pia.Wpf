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
/// Runs through the real orchestrator and a real SQLite store because Done/Failed is decided when the step is
/// recorded — asserting only the returned <c>StepTurnResult</c> would test the record, not the recorded status.
/// </summary>
public sealed class HeadlessStepOutcomeSignalTests
{
    // ---- the discriminating pair ----

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

    /// <summary>Failing an undeclared step would break every run on a provider that cannot call tools at all.</summary>
    [Fact]
    public async Task NoDeclaration_FallsBackToTheTextHeuristic_AndStillRecordsDone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(_ => Task.FromResult("I did the thing."));

        var run = await h.DispatchAsync(ct);

        Assert.Equal(AgentStepStatus.Done, Assert.Single(run.Plan).Status);
        Assert.Equal(AgentRunState.Completed, run.State);
        // …but recorded as UNCONFIRMED: no claim reached the run context, so the "ok" is only an inference.
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

    /// <summary>An unusable <c>succeeded</c> argument is silence, not failure — a provider's encoding quirk must never fail a run.</summary>
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

    /// <summary>An in-place add would leak the step tool into every other turn on that setup.</summary>
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

    /// <summary>The planner-degrade turn creates no step row, so there is no Done/Failed for a declaration to decide.</summary>
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

    /// <summary>Augmenting the run default instead of the step's resolved setup compiles and leaves every other fact here green.</summary>
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

    /// <summary>The interception is PRE-ROUTE, so no <c>UnknownTool</c> audit row is written and the model gets a real acknowledgement.</summary>
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

    // ---- the release-visible outcome line ----

    [Theory]
    [InlineData(null, "artifactReported=False")]
    [InlineData("   ", "artifactReported=False")]
    [InlineData("data/clean.csv", "artifactReported=True")]
    public async Task TheOutcomeLine_ReportsWhetherAnArtifactWasDeclared(string? artifact, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "renamed the columns", artifact: artifact),
                new ToolDispatchContext(1));
            return "done";
        });

        await h.DispatchAsync(ct);

        var line = h.OutcomeLine();
        Assert.Contains("confirmed=True", line, StringComparison.Ordinal);
        Assert.Contains(expected, line, StringComparison.Ordinal);
    }

    /// <summary>The ref and the summary are user content, so they stay on the SensitiveDebug line below this one.</summary>
    [Fact]
    public async Task TheOutcomeLine_NeverCarriesTheArtifactValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new Harness();

        h.Drive(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "renamed the columns", artifact: "data/clean.csv"),
                new ToolDispatchContext(1));
            return "done";
        });

        await h.DispatchAsync(ct);

        var line = h.OutcomeLine();
        Assert.Contains("artifactReported=True", line, StringComparison.Ordinal);
        Assert.DoesNotContain("data/clean.csv", line, StringComparison.Ordinal);
        Assert.DoesNotContain("renamed the columns", line, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static FunctionCallContent Emit(bool succeeded, string summary, string? artifact = null)
    {
        var args = new Dictionary<string, object?> { ["succeeded"] = succeeded, ["summary"] = summary };
        if (artifact is not null) args["artifact_ref"] = artifact;
        return new FunctionCallContent("call-emit", AgentStepTools.EmitStepResultToolName, args);
    }

    /// <summary>Every replan degrades, so a failed step terminates the run instead of looping and the terminal
    /// state becomes an assertable consequence of the step's status.</summary>
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

    /// <summary>Everything a headless run needs, wired to one temp SQLite file.</summary>
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

            // Tools must be ON with a non-empty base list, or there is nothing to offer or intercept.
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
                _ai, Plugins, Substitute.For<IToolPermissionService>(), _composer, personas, Chats,
                Titles, Settings, TokenMapFactory, Runs,
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

        public CapturingLogger<HeadlessTurnExecutor> Log { get; } = new();

        /// <summary>An empty returned string means no TextDelta at all.</summary>
        public void Drive(Func<ToolCallHandler?, Task<string>> drive) => _drive = drive;

        /// <summary>Roster membership is checked executor-side, so stubbing the persona store alone would see the assignment ignored.</summary>
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

        /// <summary>Bootstraps the FK parent chat before the run row, then dispatches the real orchestrator.</summary>
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
                Log, Timeline, _stepPersonas);
            executor.Initialize(workspaceRoot: null, grantedWrites: []);

            var orchestrator = new AgentRunOrchestrator(
                Runs, Planner, Verifier, NullLogger<AgentRunOrchestrator>.Instance);
            await orchestrator.RunAsync(run, executor, Persona, Provider, RunProfile.Interactive, ct);

            return (await Runs.GetAsync(run.Id, ct))!;
        }

        public string OutcomeLine() =>
            Assert.Single(Log.Entries, e => e.Message.Contains("step outcome:", StringComparison.Ordinal)).Message;

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
