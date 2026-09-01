using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Shared.Models;
using Pia.ViewModels.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The cap/flatten/fence shape is covered in isolation by <c>RunContextNudgeTests</c>.</summary>
public sealed class AgentRunNudgeParityTests
{
    // ---- shared fixtures ----

    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    // ---- LIVE step request capture (ChatSession.RunStepTurnAsync directly — no UI thread needed) ----

    private readonly IAiClientService _liveAi = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public AgentRunNudgeParityTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    private ChatSession CreateLiveSession() => new(
        _tokenMap, _liveAi, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private static StepTurnSpec LiveSpec() => new(
        RunId: Guid.NewGuid(),
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: null,
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: Provider(),
        Tools: null,
        SupportsTools: false,
        WebSearchActive: false,
        TokenizationEnabled: false);

    /// <summary>Drives one LIVE step turn with <paramref name="nudgeText"/> set and returns the request the
    /// step actually sent, so a fact can inspect any message role/position in it.</summary>
    private async Task<IReadOnlyList<ChatMessage>> CaptureLiveStepRequestAsync(string? nudgeText, CancellationToken ct)
    {
        List<ChatMessage>? sent = null;
        _liveAi.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent = [.. ci.ArgAt<IList<ChatMessage>>(0)];
                return Stream(new TextDelta("done"), new Finished(null, "m"));
            });

        var session = CreateLiveSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        var ctx = new RunContext("goal", RunProfile.Interactive);
        ctx.SetNudge(nudgeText);

        var result = await session.RunStepTurnAsync(LiveSpec(), ctx, ct);
        Assert.True(result.Succeeded);
        Assert.NotNull(sent);
        return sent!;
    }

    // ---- HEADLESS step request capture (HeadlessTurnExecutor directly, real SQLite store) ----

    private sealed class HeadlessHarness : IDisposable
    {
        private readonly string _dir;
        public readonly SqliteContext SqlCtx;
        public readonly AgentRunService Runs;
        public readonly AssistantChatService Chats;
        public readonly IAiClientService Ai = Substitute.For<IAiClientService>();
        public readonly HeadlessTurnExecutor Executor;

        public HeadlessHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaNudge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            SqlCtx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(SqlCtx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(SqlCtx, Runs);

            var plugins = Substitute.For<IPluginService>();
            var composer = Substitute.For<IAssistantPromptComposer>();
            composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>())
                .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
            var personas = Substitute.For<IPersonaService>();
            personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(Persona());
            var providers = Substitute.For<IProviderService>();
            providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());
            var titles = Substitute.For<IChatTitleService>();
            titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            var engine = new BackgroundAssistantTurnRunner(
                Ai, plugins, Substitute.For<IToolPermissionService>(), composer, personas, Chats,
                titles, settings, TokenMapFactory, Runs,
                new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
            Executor = new HeadlessTurnExecutor(
                engine, Chats, settings, personas, providers, composer, titles, TokenMapFactory,
                NullLogger<HeadlessTurnExecutor>.Instance);
            Executor.Initialize(workspaceRoot: null, grantedWrites: Array.Empty<string>());
        }

        public async Task<(AgentRun Run, AgentStep Step)> NewRunWithOneStepAsync(string goal, CancellationToken ct)
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
                new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal), ct);
            var step = new AgentStep
            {
                Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Intent = "do the thing",
                Status = AgentStepStatus.Pending,
            };
            return (run, step);
        }

        public void Dispose()
        {
            Runs.Dispose();
            Chats.Dispose();
            SqlCtx.Dispose();
            TempPath.Remove(_dir);
        }
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(string answer)
    {
        await Task.Yield();
        yield return new TextDelta(answer);
        yield return new Finished(null, "test-model");
    }

    /// <summary>Drives one HEADLESS step turn (BeginRun + ExecuteStep) with <paramref name="nudgeText"/> set
    /// and returns the request the step actually sent.</summary>
    private async Task<(IReadOnlyList<ChatMessage> Sent, HeadlessHarness Harness)> CaptureHeadlessStepRequestAsync(
        string? nudgeText, CancellationToken ct)
    {
        var h = new HeadlessHarness();
        var (run, step) = await h.NewRunWithOneStepAsync("goal", ct);
        List<ChatMessage>? sent = null;
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                sent = [.. ci.ArgAt<IList<ChatMessage>>(0)];
                return Drive("done");
            });

        var ctx = new RunContext("goal", RunProfile.Interactive);
        ctx.SetNudge(nudgeText);
        await h.Executor.BeginRunAsync(run, ctx, ct);
        var result = await h.Executor.ExecuteStepAsync(run, step, ctx, ct);

        Assert.True(result.Succeeded);
        Assert.NotNull(sent);
        return (sent!, h);
    }

    // ---- VERIFY / REPLAN request capture (AgentVerifier / AgentPlanner directly) ----

    private static Dictionary<string, object?> PlanArgs() => new()
    {
        ["steps"] = new object[]
        {
            new Dictionary<string, object?> { ["title"] = "A", ["intent"] = "do a", ["expectedArtifact"] = null },
        },
    };

    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(ToolCallHandler? handler)
    {
        if (handler is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", PlanArgs()), new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private static Dictionary<string, object?> VerdictArgs() =>
        new() { ["passed"] = true, ["reason"] = "ok", ["missing"] = Array.Empty<object?>() };

    private static async IAsyncEnumerable<ChatStreamItem> VerdictStream(ToolCallHandler? handler)
    {
        if (handler is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_verdict", VerdictArgs()), new ToolDispatchContext(1));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private async Task<IReadOnlyList<ChatMessage>> CaptureVerifyRequestAsync(string? nudgeText, CancellationToken ct)
    {
        var ai = Substitute.For<IAiClientService>();
        List<ChatMessage>? sent = null;
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent = [.. ci.ArgAt<IList<ChatMessage>>(0)];
                return VerdictStream(ci.ArgAt<ToolCallHandler?>(3));
            });
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(Task.FromResult(new AppSettings()));
        var verifier = new AgentVerifier(ai, settings, NullLogger<AgentVerifier>.Instance);

        var ctx = new RunContext("goal", RunProfile.Interactive);
        ctx.SetNudge(nudgeText);
        await verifier.VerifyAsync(ctx, Persona(), Provider(), ct);

        Assert.NotNull(sent);
        return sent!;
    }

    private async Task<IReadOnlyList<ChatMessage>> CaptureReplanRequestAsync(string? nudgeText, CancellationToken ct)
    {
        var ai = Substitute.For<IAiClientService>();
        List<ChatMessage>? sent = null;
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent = [.. ci.ArgAt<IList<ChatMessage>>(0)];
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3));
            });
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        var planner = new AgentPlanner(
            ai, new AiProviderHandlerResolver([handler]), Substitute.For<ISettingsService>(), NullLogger<AgentPlanner>.Instance);

        var ctx = new RunContext("goal", RunProfile.Interactive);
        ctx.SetNudge(nudgeText);
        await planner.ReplanAsync(ctx, "step failed", Persona(), Provider(), ct);

        Assert.NotNull(sent);
        return sent!;
    }

    // ---- the facts ----

    [Fact]
    public async Task Nudge_RidesTheUserMessage_OnBothExecutors()
    {
        var ct = TestContext.Current.CancellationToken;
        const string nudge = "focus on the CSV export";

        var liveSent = await CaptureLiveStepRequestAsync(nudge, ct);
        var liveUser = liveSent.Last(m => m.Role == ChatRole.User);
        Assert.Contains(nudge, liveUser.Text);
        Assert.Contains("Steering note from the user", liveUser.Text);

        var (headlessSent, harness) = await CaptureHeadlessStepRequestAsync(nudge, ct);
        harness.Dispose();
        var headlessUser = headlessSent.Last(m => m.Role == ChatRole.User);
        Assert.Contains(nudge, headlessUser.Text);
        Assert.Contains("Steering note from the user", headlessUser.Text);
    }

    [Fact]
    public async Task Nudge_NeverAppearsOnASystemMessage_InAnyOfTheFourRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        const string nudge = "NEVER-ON-SYSTEM-MARKER";

        var liveSent = await CaptureLiveStepRequestAsync(nudge, ct);
        var (headlessSent, harness) = await CaptureHeadlessStepRequestAsync(nudge, ct);
        harness.Dispose();
        var verifySent = await CaptureVerifyRequestAsync(nudge, ct);
        var replanSent = await CaptureReplanRequestAsync(nudge, ct);

        foreach (var sent in new[] { liveSent, headlessSent, verifySent, replanSent })
        {
            Assert.DoesNotContain(sent, m => m.Role == ChatRole.System && (m.Text ?? string.Empty).Contains(nudge));
            // Non-vacuity: the nudge really did ride the SAME request, just never the System message.
            Assert.Contains(sent, m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains(nudge));
        }
    }

    [Fact]
    public async Task Nudge_ReachesTheCriticAndTheReplan()
    {
        var ct = TestContext.Current.CancellationToken;
        const string nudge = "prefer the CSV format";

        var verifySent = await CaptureVerifyRequestAsync(nudge, ct);
        var verifyUser = verifySent.Last(m => m.Role == ChatRole.User);
        Assert.Contains(nudge, verifyUser.Text);
        Assert.Contains("Steering note from the user", verifyUser.Text);

        var replanSent = await CaptureReplanRequestAsync(nudge, ct);
        var replanUser = replanSent.Last(m => m.Role == ChatRole.User);
        Assert.Contains(nudge, replanUser.Text);
        Assert.Contains("Steering note from the user", replanUser.Text);
    }

    [Fact]
    public async Task Nudge_IsNotSeededIntoThePersistedTranscript()
    {
        var ct = TestContext.Current.CancellationToken;
        const string nudge = "MUST-NOT-BE-PERSISTED";

        var (_, harness) = await CaptureHeadlessStepRequestAsync(nudge, ct);
        using var h = harness;

        var chatId = (await h.Chats.GetAllIdsAsync(ct)).Single();
        var persisted = await h.Chats.GetAsync(chatId, ct);
        Assert.NotNull(persisted);
        var goalMessage = Assert.Single(persisted!.Messages, m => m.Role == "user");
        Assert.Equal("goal", goalMessage.Content);
        Assert.DoesNotContain(nudge, goalMessage.Content);
        Assert.DoesNotContain("Steering note from the user", goalMessage.Content);
    }

    // ---- resume-scoped facts (real orchestrator + real steering CASes) ----

    private static Persona OrchestratorPersona() => Persona();
    private static AiProvider OrchestratorProvider() => Provider();

    private sealed class FakePlanner : IAgentPlanner
    {
        public Queue<PlanResult> Plans { get; } = new();

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Plans.Count > 0 ? Plans.Dequeue() : PlanResult.Fallback);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <summary>Records what <c>ctx.AppendNudge</c> yields per dispatch; the step titled <c>pauseOnTitle</c>
    /// requests a pause and honours the sink's cancel, every other step succeeds immediately.</summary>
    private sealed class NudgeCapturingExecutor : IAgentTurnExecutor
    {
        private readonly Func<Guid, Task<bool>>? _pause;
        private readonly string? _pauseOnTitle;

        /// <param name="pause">Null ⇒ never pauses, whatever <paramref name="pauseOnTitle"/> says.</param>
        /// <param name="pauseOnTitle">The step title to pause on; other steps always succeed immediately.</param>
        public NudgeCapturingExecutor(Func<Guid, Task<bool>>? pause, string? pauseOnTitle = null)
        {
            _pause = pause;
            _pauseOnTitle = pauseOnTitle;
        }

        public List<string> Captured { get; } = new();

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public async Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Captured.Add(ctx.AppendNudge("MARK"));

            if (_pause is null || step.Title != _pauseOnTitle)
                return new StepTurnResult(true, false, null, "done", null, Guid.NewGuid(), Guid.NewGuid());

            await _pause(run.Id);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { /* the sink's cancel reached this step */ }
            return new StepTurnResult(false, true, "cancelled", string.Empty, null, Guid.NewGuid(), Guid.NewGuid());
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
            => Task.FromResult(new StepTurnResult(true, false, null, "fallback", null, Guid.NewGuid(), Guid.NewGuid()));

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct) => Task.CompletedTask;

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OrchestratorHarness : IDisposable
    {
        private readonly string _dir;

        public OrchestratorHarness(ILogger<AgentRunOrchestrator>? orchestratorLogger = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaNudgeResume_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            SqlCtx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(SqlCtx, NullLogger<AgentRunService>.Instance);
            Chats = new AssistantChatService(SqlCtx, Runs);
            Store = new RunSteeringStore();
            Steering = new AgentRunSteeringService(Runs, Store, NullLogger<AgentRunSteeringService>.Instance);
            OrchestratorLogger = orchestratorLogger ?? NullLogger<AgentRunOrchestrator>.Instance;
        }

        public SqliteContext SqlCtx { get; }
        public AgentRunService Runs { get; }
        public AssistantChatService Chats { get; }
        public RunSteeringStore Store { get; }
        public AgentRunSteeringService Steering { get; }
        public ILogger<AgentRunOrchestrator> OrchestratorLogger { get; }

        public async Task<AgentRun> NewRunAsync(string goal, CancellationToken ct)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "t", CreatedAt = now, UpdatedAt = now,
                LastAccessedAt = now, WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            }, ct);
            return await Runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal), ct);
        }

        public AgentRunOrchestrator BuildOrchestrator(IAgentPlanner planner) =>
            new(Runs, planner, new FakeVerifier(), OrchestratorLogger, workspaces: null, childLauncher: null, chats: null, steering: Store);

        public void Dispose()
        {
            Runs.Dispose();
            SqlCtx.Dispose();
            TempPath.Remove(_dir);
        }
    }

    private static List<AgentStep> OneStep(string title) =>
        new() { new AgentStep { Id = Guid.Empty, Ordinal = 0, Title = title, Intent = title, Status = AgentStepStatus.Pending } };

    /// <summary>A nudge is scoped to one dispatch: each gets a fresh <c>RunContext</c>, so the third capture
    /// must carry no fence at all.</summary>
    [Fact]
    public async Task Nudge_DoesNotSurviveASecondResume()
    {
        var ct = TestContext.Current.CancellationToken;
        using var h = new OrchestratorHarness();
        var run = await h.NewRunAsync("goal", ct);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(OneStep("s1"), false));

        // Dispatch 1: fresh launch, pauses on its only step.
        using (var dispatchCts1 = new CancellationTokenSource())
        {
            Action sink1 = () => { try { dispatchCts1.Cancel(); } catch { /* disposed */ } };
            h.Store.RegisterDispatch(run.Id, sink1);
            var exec1 = new NudgeCapturingExecutor(runId => h.Steering.PauseAsync(runId, ct), pauseOnTitle: "s1");
            await h.BuildOrchestrator(planner).RunAsync(run, exec1, OrchestratorPersona(), OrchestratorProvider(), RunProfile.Interactive, dispatchCts1.Token);
        }

        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(run.Id, ct))!.State);

        // Dispatch 2: resume WITH a nudge, pause again.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed2 = (await h.Runs.GetAsync(run.Id, ct))!;
        List<string> captured2;
        using (var dispatchCts2 = new CancellationTokenSource())
        {
            Action sink2 = () => { try { dispatchCts2.Cancel(); } catch { /* disposed */ } };
            h.Store.RegisterDispatch(run.Id, sink2);
            var exec2 = new NudgeCapturingExecutor(runId => h.Steering.PauseAsync(runId, ct), pauseOnTitle: "s1");
            await h.BuildOrchestrator(new FakePlanner())
                .RunAsync(resumed2, exec2, OrchestratorPersona(), OrchestratorProvider(), RunProfile.Interactive, dispatchCts2.Token,
                    resume: true, nudge: "steer-toward-A");
            captured2 = exec2.Captured;
        }

        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(run.Id, ct))!.State);
        var dispatch2Request = Assert.Single(captured2);
        Assert.Contains("steer-toward-A", dispatch2Request);
        Assert.Contains("Steering note from the user", dispatch2Request);

        // Dispatch 3: resume WITHOUT a nudge — the second dispatch's note must not have survived.
        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed3 = (await h.Runs.GetAsync(run.Id, ct))!;
        var exec3 = new NudgeCapturingExecutor(pause: null);
        await h.BuildOrchestrator(new FakePlanner())
            .RunAsync(resumed3, exec3, OrchestratorPersona(), OrchestratorProvider(), RunProfile.Interactive, ct, resume: true, nudge: null);

        var dispatch3Request = Assert.Single(exec3.Captured);
        Assert.Equal("MARK", dispatch3Request); // no fence at all — scope-to-dispatch, not "the last nudge wins"
        Assert.DoesNotContain("steer-toward-A", dispatch3Request);
        Assert.Equal(AgentRunState.Completed, (await h.Runs.GetAsync(run.Id, ct))!.State);
    }

    /// <summary>The nudge text may only ever reach <c>SensitiveDebug</c>; the resume-seed Information line is
    /// the non-vacuity control, since an empty log would otherwise pass.</summary>
    [Fact]
    public async Task Nudge_IsNeverLoggedAtInformationOrAbove()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new CapturingLogger<AgentRunOrchestrator>();
        using var h = new OrchestratorHarness(log);
        var run = await h.NewRunAsync("goal", ct);
        var planner = new FakePlanner();
        planner.Plans.Enqueue(new PlanResult(new List<AgentStep>
        {
            new() { Id = Guid.Empty, Ordinal = 0, Title = "s1", Intent = "s1", Status = AgentStepStatus.Pending },
            new() { Id = Guid.Empty, Ordinal = 1, Title = "s2", Intent = "s2", Status = AgentStepStatus.Pending },
        }, false));

        const string secretNudge = "SECRET-STEERING-TEXT-Q7";

        // s1 succeeds (so the resume below has a Done step to seed — the non-vacuity Information line),
        // s2 is where the pause lands.
        using (var dispatchCts = new CancellationTokenSource())
        {
            Action sink = () => { try { dispatchCts.Cancel(); } catch { /* disposed */ } };
            h.Store.RegisterDispatch(run.Id, sink);
            var exec = new NudgeCapturingExecutor(runId => h.Steering.PauseAsync(runId, ct), pauseOnTitle: "s2");
            await h.BuildOrchestrator(planner).RunAsync(run, exec, OrchestratorPersona(), OrchestratorProvider(), RunProfile.Interactive, dispatchCts.Token);
        }

        Assert.Equal(AgentRunState.Paused, (await h.Runs.GetAsync(run.Id, ct))!.State);

        Assert.True(await h.Runs.TryResumeFromPauseAsync(run.Id, ct));
        var resumed = (await h.Runs.GetAsync(run.Id, ct))!;
        var exec2 = new NudgeCapturingExecutor(pause: null);
        await h.BuildOrchestrator(new FakePlanner())
            .RunAsync(resumed, exec2, OrchestratorPersona(), OrchestratorProvider(), RunProfile.Interactive, ct, resume: true, nudge: secretNudge);

        // Non-vacuity: the resume-context-seed Information line really did fire.
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Information
            && e.Message.Contains("Resume seeded", StringComparison.Ordinal));
        // The nudge text itself never appears at Information or above.
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Information
            && e.Message.Contains(secretNudge, StringComparison.Ordinal));
    }
}
