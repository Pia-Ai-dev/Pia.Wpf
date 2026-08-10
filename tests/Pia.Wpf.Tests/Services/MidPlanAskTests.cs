using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Covers <c>request_user_input</c> parking a run mid-plan; real run/chat stores, doubles only at the provider/plugin/planner boundary.</summary>
public sealed class MidPlanAskTests : IDisposable
{
    /// <summary>Literal, not <c>AgentRunOrchestrator.NeedsInputReason</c>, so a wire-value regression on the constant itself would still be caught.</summary>
    private const string NeedsInputReason = "needs-input";

    /// <summary>User-derived payload: kept as a literal, and never logged directly (production routes this through <c>SensitiveDebug</c>).</summary>
    private const string TheQuestion = "Which cluster should I deploy to — staging or production?";

    private readonly string _dir;
    private readonly string _runsBase;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly ExecutingRunStore _executing = new();
    private readonly RecordingTimelineService _timeline = new();

    public MidPlanAskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaMidPlanAsk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A step calling <c>request_user_input</c> parks the run <c>WaitingForInput</c> with the step back at <c>Pending</c> (not Failed) so a resume doesn't burn a replan on a step that only waited.</summary>
    [Fact]
    public async Task AStepThatCallsRequestUserInput_ParksTheRunNeedsInput_AndTheQuestionReachesTheChat()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(NeedsInputReason, PauseMember(run, "reason"));
        // The envelope carries the token only, never the question, so it stays safe to log.
        Assert.Null(PauseMember(run, "question"));
        Assert.Null(PauseMember(run, "tool"));
        Assert.Null(run.CompletedAt);

        // Resumable, not consumed: a Continue must find the step again rather than draining an empty remainder.
        var pending = await _runs.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(AgentStepStatus.Pending, pending!.Status);

        // The question goes to chat, never the Flow card.
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.NotNull(chat);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);

        Assert.Equal(UserInputRequestStore.Accepted, probe.AskResults.Single());
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The interception is pre-route: <c>RouteToolCallAsync</c> never sees <c>request_user_input</c>, so no <c>UnknownTool</c> audit row is written for it.</summary>
    [Fact]
    public async Task TheAskIsInterceptedBeforeRouting_SoNoUnknownToolRowIsWritten()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AgentStepTools.RequestUserInputToolName, probe.RoutedNames);
        Assert.DoesNotContain(_timeline.Rows, r => r.ToolName == AgentStepTools.RequestUserInputToolName);
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.UnknownTool);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The step turn is actually offered the tool, on the same list as <c>emit_step_result</c> — parking alone doesn't prove the tool was offered, since the fake stream calls it regardless.</summary>
    [Fact]
    public async Task AStepTurnIsOfferedBothStepTools()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(AgentStepTools.OffersRequestUserInputTool(probe.OfferedTools));
        Assert.True(AgentStepTools.OffersStepResultTool(probe.OfferedTools),
            "18 D6: the ask is an ADDITIONAL channel, never a replacement for the declaration tool");
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>On a delegated step (non-null <c>ParentRunId</c>) the tool is refused: not offered, the call is still intercepted, and the child does not park — a child parking has no Flow card to surface it.</summary>
    [Fact]
    public async Task ADelegatedStep_IsRefusedTheAsk_AndTheRunDoesNotPark()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        var run = await GetRunAsync(child.Id);
        Assert.NotEqual(AgentRunState.WaitingForInput, run.State);
        Assert.Null(PauseMember(run, "reason"));

        Assert.False(AgentStepTools.OffersRequestUserInputTool(probe.OfferedTools));
        Assert.Equal(UserInputRequestStore.RefusedForDelegatedStep, probe.AskResults.Single());
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.UnknownTool);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Refusing the ask must not swallow the block: the delegated step declares <c>succeeded=false</c> through <c>emit_step_result</c>, so the failure still reaches the run.</summary>
    [Fact]
    public async Task ARefusedDelegatedStep_StillSurfacesItsBlockAsADeclaredStepFailure()
    {
        var probe = new AskProbe { DeclareFailureAfterAsk = "blocked: I need the target cluster and cannot ask" };
        var launcher = Build(probe);

        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        var run = await GetRunAsync(child.Id);
        Assert.Equal(AgentRunState.Failed, run.State);
        // The model's own words reach the run's failure reason, not a tool result nobody reads.
        Assert.Contains("target cluster", PauseMember(run, "error") ?? string.Empty, StringComparison.Ordinal);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A granted, side-effecting call the model makes after the ask does not run — the asking step is abandoned and re-runs from the top on resume, so it would otherwise execute twice.</summary>
    [Fact]
    public async Task AGrantedWriteAfterTheAsk_DoesNotRun()
    {
        var probe = new AskProbe { FollowUpTool = "write_file" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: ["write_file"]),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Empty(probe.ExecutedNames);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A second ask in the same step does not move the question — first wins, since the later call happens after the run was already told it is parking.</summary>
    [Fact]
    public async Task ASecondAskInTheSameStep_DoesNotMoveTheQuestion()
    {
        var probe = new AskProbe { SecondQuestion = "actually, which region?" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);
        Assert.DoesNotContain(chat.Messages, m => m.Content == "actually, which region?");
        Assert.Equal(UserInputRequestStore.AlreadyAsked, probe.AskResults[1]);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A run that already parked once and was answered can ask again and park again — deliberately, there is no per-run cap.</summary>
    [Fact]
    public async Task ARunMayParkToAskMoreThanOnce_ThereIsNoCap()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);

        probe.NextQuestion = "and which branch?";
        Assert.True(await launcher.ResumeAsync(
            handle.RunId, "the staging cluster", TestContext.Current.CancellationToken));
        await AwaitParkedAsync(handle.RunId);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(NeedsInputReason, PauseMember(run, "reason"));

        // Polled: the park writes the row before it posts the question, so reading the chat immediately is a race.
        await AwaitChatMessageAsync(run.ChatId, "and which branch?");
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);

        // The first answer survives the second park, in its own column rather than ExtraJson (which the resume claim nulls).
        Assert.Contains("staging cluster", ClarificationsJson(run.Id) ?? string.Empty, StringComparison.Ordinal);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>If the provider stream asks and then throws later in the same exchange, the run parks with the question rather than failing — the attempt is discarded either way and the question is the only durable result.</summary>
    [Fact]
    public async Task AFaultAfterTheAsk_KeepsTheQuestion_AndParksInsteadOfFailing()
    {
        var probe = new AskProbe { FaultMessage = "provider connection reset" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(NeedsInputReason, PauseMember(run, "reason"));
        Assert.Null(run.CompletedAt);
        Assert.Null(PauseMember(run, "error")); // a park, not a failure — FailAsync is what writes that member

        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A persistent post-ask fault cannot loop forever: the park-on-fault arm only fires when the current attempt itself asked, so a resumed, answered attempt that faults again fails outright.</summary>
    [Fact]
    public async Task AnAnsweredStepThatFaultsWithoutAskingAgain_FailsWithTheProviderError()
    {
        var probe = new AskProbe { FaultMessage = "provider connection reset" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);

        probe.Ask = false;
        Assert.True(await launcher.ResumeAsync(
            handle.RunId, "the staging cluster", TestContext.Current.CancellationToken));
        await AwaitSettledAsync(handle.RunId);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.Failed, run.State);
        Assert.Equal("provider connection reset", PauseMember(run, "error"));
        await launcher.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<AgentRun> GetRunAsync(Guid runId)
        => (await _runs.GetAsync(runId, TestContext.Current.CancellationToken))!;

    /// <summary>Poll to a terminal state. A mid-plan ask park is NOT terminal, so this also proves non-parking.</summary>
    private async Task AwaitSettledAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var s = (await _runs.GetAsync(runId, ct))!.State;
            if (s is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                return;
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Run {runId} never settled (state {(await _runs.GetAsync(runId, ct))!.State}).");
    }

    /// <summary>Poll to a PARK. A resume returns as soon as the dispatch is attached, not when it re-parks.</summary>
    private async Task AwaitParkedAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var run = (await _runs.GetAsync(runId, ct))!;
            if (run.State == AgentRunState.WaitingForInput && PauseMember(run, "reason") == NeedsInputReason)
                return;
            if (run.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                Assert.Fail($"Run {runId} settled {run.State} instead of parking to ask again.");
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Run {runId} never re-parked (state {(await _runs.GetAsync(runId, ct))!.State}).");
    }

    /// <summary>Poll until the run's chat carries <paramref name="content"/> — needed because the park writes the run row before it posts the question.</summary>
    private async Task AwaitChatMessageAsync(Guid chatId, string content)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var chat = await _chats.GetAsync(chatId, ct);
            if (chat is not null && chat.Messages.Any(m => m.Content == content))
                return;
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Chat {chatId} never received the expected message.");
    }

    /// <summary>A member of the pause envelope, read from the raw row (<c>RunPauseEnvelope</c> is src-internal).</summary>
    private static string? PauseMember(AgentRun run, string member)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        using var doc = JsonDocument.Parse(run.ExtraJson);
        return doc.RootElement.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    /// <summary>The run's accumulated clarification answers, straight off the column.</summary>
    private string? ClarificationsJson(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT ClarificationsJson FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        return cmd.ExecuteScalar() as string;
    }

    private async Task<AgentRun> NewRunAsync(Guid? parentRunId = null, string? policyJson = null)
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

        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "g",
                PolicyJson: policyJson, ParentRunId: parentRunId), ct);
    }

    /// <summary>A parked CHILD run with one Pending step and an EMPTY grant envelope, ready to be resumed.</summary>
    private async Task<AgentRun> ParkedChildAsync(Guid parentRunId)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(
            parentRunId, HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule));
        await _runs.ReplaceStepsAsync(run.Id, [new AgentStep
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Ordinal = 0,
            Title = "S1",
            Intent = "do it",
            Status = AgentStepStatus.Pending,
        }], ct);
        await _runs.PauseAsync(run.Id, "step-cap", ct);
        return run;
    }

    // ---------------------------------------------------------------- doubles

    /// <summary>Drives one step turn's tool calls and records what came back; every knob is off by default.</summary>
    private sealed class AskProbe
    {
        /// <summary>The question the next step turn asks; settable so a second park can ask something different.</summary>
        public string NextQuestion { get; set; } = TheQuestion;

        /// <summary>A second ask in the same turn — the first-wins fact. Null ⇒ one ask.</summary>
        public string? SecondQuestion { get; set; }

        /// <summary>Whether the turn calls <c>request_user_input</c> at all; false models an attempt after the question was already answered.</summary>
        public bool Ask { get; set; } = true;

        /// <summary>When set, the provider stream throws with this message once the turn's tool calls are done. Null ⇒ completes normally.</summary>
        public string? FaultMessage { get; set; }

        /// <summary>A tool the model calls after the ask — the containment fact. Null ⇒ no follow-up call.</summary>
        public string? FollowUpTool { get; set; }

        /// <summary>When set, the turn also declares <c>emit_step_result{succeeded:false}</c> with this summary.</summary>
        public string? DeclareFailureAfterAsk { get; set; }

        /// <summary>What the ask interception handed back, in call order.</summary>
        public List<string?> AskResults { get; } = [];

        /// <summary>The tool list this step turn was offered.</summary>
        public IList<AITool>? OfferedTools { get; set; }

        /// <summary>Every name that reached <c>RouteToolCallAsync</c> — a pre-route interception must appear in neither this nor the timeline.</summary>
        public List<string> RoutedNames { get; } = [];

        /// <summary>Every name that actually reached <c>Execute()</c>.</summary>
        public List<string> ExecutedNames { get; } = [];
    }

    /// <summary>Plans exactly one real step; not offered either step tool until a run reaches the drain loop.</summary>
    private sealed class OneStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S0", Intent = "do it", Status = AgentStepStatus.Pending }],
                FallBackToSingleTurn: false));

        // A step that asked must never reach the replanner — an ask is not a failure.
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    private HeadlessRunLauncher Build(AskProbe probe)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new OneStepPlanner();

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                probe.OfferedTools = ci.ArgAt<IList<AITool>?>(2);
                return Drive(ci.ArgAt<ToolCallHandler?>(3), probe);
            });

        var plugins = Substitute.For<IPluginService>();
        plugins.IsMcpTool(Arg.Any<string>()).Returns(false);
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.Arg<FunctionCallContent>().Name;
                probe.RoutedNames.Add(name);
                // A DEFERRED write (a pending action) — the only shape that reaches the gate at all.
                return ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                    name, Guid.NewGuid(), "files", "desc", null,
                    () => { probe.ExecutedNames.Add(name); return Task.FromResult<object?>("did it"); }));
            });

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAiClientService>(ai);
        services.AddSingleton<IPluginService>(plugins);
        // All-false: no fact in this file is about the standing tier, and the runner reads it for every call.
        services.AddSingleton(Substitute.For<IToolPermissionService>());
        services.AddSingleton<IAssistantPromptComposer>(composer);
        services.AddSingleton<IPersonaService>(personas);
        services.AddSingleton<IProviderService>(providers);
        services.AddSingleton<IChatTitleService>(titles);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<IAgentRunService>(_runs);
        services.AddSingleton<IAssistantChatService>(_chats);
        services.AddSingleton<IAgentPlanner>(planner);
        services.AddSingleton<IAgentVerifier>(new FakeVerifier());
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        services.AddSingleton<IExecutingRunStore>(_executing);
        // Real audit wiring, since "no UnknownTool row for the ask" is one of the facts under test.
        services.AddSingleton<IAgentTimelineService>(_timeline);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        return new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(ToolCallHandler? handler, AskProbe probe)
    {
        await Task.Yield();
        if (handler is not null)
        {
            if (probe.Ask)
            {
                probe.AskResults.Add(await handler(
                    new FunctionCallContent("call-1", AgentStepTools.RequestUserInputToolName,
                        new Dictionary<string, object?> { ["question"] = probe.NextQuestion }),
                    new ToolDispatchContext(1)) as string);
            }

            // Round-tripped through the same handler, since that is the only way first-wins is observable.
            if (probe.SecondQuestion is not null)
            {
                probe.AskResults.Add(await handler(
                    new FunctionCallContent("call-2", AgentStepTools.RequestUserInputToolName,
                        new Dictionary<string, object?> { ["question"] = probe.SecondQuestion }),
                    new ToolDispatchContext(1)) as string);
            }

            if (probe.FollowUpTool is not null)
            {
                await handler(
                    new FunctionCallContent("call-3", probe.FollowUpTool, new Dictionary<string, object?>()),
                    new ToolDispatchContext(2));
            }

            if (probe.DeclareFailureAfterAsk is not null)
            {
                await handler(
                    new FunctionCallContent("call-4", AgentStepTools.EmitStepResultToolName,
                        new Dictionary<string, object?>
                        {
                            ["succeeded"] = false,
                            ["summary"] = probe.DeclareFailureAfterAsk,
                        }),
                    new ToolDispatchContext(3));
            }
        }

        // Thrown out of the enumeration itself, so it lands in HeadlessTurnExecutor's catch like a dropped connection.
        if (probe.FaultMessage is not null)
            throw new InvalidOperationException(probe.FaultMessage);

        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }
}
