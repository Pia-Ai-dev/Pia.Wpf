using System.IO;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Services;

public sealed class HeadlessRunLauncherTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly string _runsBase;

    /// <summary>The real launch-bracket index — the launcher's registrations are asserted through it.</summary>
    private readonly ExecutingRunStore _executing = new();

    // Gate that a FakePlanner blocks on, letting a test hold a run inside the orchestrator to probe
    // concurrency / shutdown.
    private sealed class FakePlanner : IAgentPlanner
    {
        private readonly Func<Task>? _onPlan;
        public int Concurrent;
        public int MaxConcurrent;
        private readonly object _lock = new();

        /// <summary>The <see cref="RunContext"/> the orchestrator handed the planner; null when the planner was
        /// never called, since a resume skips planning.</summary>
        public RunContext? PlanContext { get; private set; }

        /// <summary>The run persona and provider the orchestrator resolved. The stub chat records only the
        /// provider's ID, which can show WHICH persona won but not which EFFORT.</summary>
        public Persona? PlanPersona { get; private set; }

        public AiProvider? PlanProvider { get; private set; }

        /// <summary>Like the ctor hook, but handed the run's own token — the only in-process evidence that a
        /// dispatch was cancelled, since the teardown after it is fire-and-forget.</summary>
        public Func<CancellationToken, Task>? OnPlanWithToken { get; set; }

        /// <summary>How many real steps to plan; zero keeps the run on <see cref="PlanResult.Fallback"/>, the
        /// single-turn degrade that never reaches the drain loop.</summary>
        public int Steps { get; set; }

        public FakePlanner(Func<Task>? onPlan = null) => _onPlan = onPlan;

        public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanContext = ctx;
            PlanPersona = persona;
            PlanProvider = provider;
            lock (_lock) { Concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, Concurrent); }
            try
            {
                if (OnPlanWithToken is not null) await OnPlanWithToken(ct);
                if (_onPlan is not null) await _onPlan();
                if (Steps > 0)
                {
                    return new PlanResult(
                        Enumerable.Range(0, Steps).Select(i => new AgentStep
                        {
                            Id = Guid.NewGuid(),
                            Ordinal = i,
                            Title = "S" + i,
                            Intent = "do it",
                            Status = AgentStepStatus.Pending,
                        }).ToList(),
                        FallBackToSingleTurn: false);
                }

                return PlanResult.Fallback; // single-turn fallback → one exchange, then Completed
            }
            finally
            {
                lock (_lock) { Concurrent--; }
            }
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    public HeadlessRunLauncherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaLauncher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        // Per-test workspace base — never the real %LOCALAPPDATA%\Pia\runs, so the destructive startup
        // sweep can't touch a developer's actual run workspaces.
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Drives one tool call through the executor's unattended grant gate and records the outcome, so a
    /// test observes the grant set actually handed to the executor rather than trusting a constant.</summary>
    private sealed class ToolProbe
    {
        public ToolProbe(string toolName) => ToolName = toolName;

        public string ToolName { get; }
        public bool Executed { get; private set; }
        public string? GateResult { get; private set; }

        public void MarkExecuted() => Executed = true;
        public void Record(object? gateResult) => GateResult = gateResult as string;
    }

    /// <param name="verifier">A resume skips planning, so the verify pass is the only place a resumed run's <c>ctx.WorkspaceRoot</c> is observable.</param>
    /// <param name="workspaces">Omitted ⇒ no provisioner, so the launcher does its own <c>CreateDirectory</c> under the <c>try/catch → FailAsync</c> guard.</param>
    /// <param name="runsBaseOverride">Lets a test point the runs base at an unwritable path (a file).</param>
    /// <param name="rosterProvider">The one provider <c>GetProviderAsync</c> can answer — an unstubbed lookup
    /// returns null and the ladder falls through. A child's stub chat records the resolved provider id, which is
    /// how the persona ladder's answer is observable from outside; a launch request's own <c>ProviderId</c> pin
    /// resolves through the same stub.</param>
    /// <param name="pinnedPersona">What <c>GetPersonasAsync</c> answers — the list a JOB's pin resolves against,
    /// which unlike a delegated id is not roster-gated. Stubbed either way: an unstubbed one returns a null task
    /// and NREs the pin path.</param>
    /// <param name="modePersona">What <c>ResolveActiveAsync</c> answers. A second launcher built with a different
    /// one is how a fact moves the per-mode default while a run is parked.</param>
    /// <param name="steering">Registered with the per-run scope too, so the run's own orchestrator reads the same instance the launcher writes.</param>
    /// <param name="stream">Replaces <see cref="Drive"/> so a fact can hold a run inside a step rather than only inside the planner.</param>
    /// <param name="settingsService">A substitute cannot raise <c>SettingsChanged</c>; pass a <see cref="MutableSettingsService"/> to drive a real save. Supersedes <paramref name="appSettings"/>.</param>
    private (HeadlessRunLauncher Launcher, FakePlanner Planner) BuildLauncher(
        Func<Task>? onPlan = null, bool nullDefaultProvider = false, ToolProbe? probe = null,
        AppSettings? appSettings = null, FakeVerifier? verifier = null,
        FakeRunWorkspaceService? workspaces = null, string? runsBaseOverride = null,
        Persona? rosterPersona = null, AiProvider? rosterProvider = null,
        Persona? pinnedPersona = null,
        Persona? modePersona = null,
        IRunSteeringStore? steering = null,
        Func<CancellationToken, IAsyncEnumerable<ChatStreamItem>>? stream = null,
        IExecutingRunStore? executing = null,
        ISettingsService? settingsService = null)
    {
        // The one fact that overrides this needs a seam between the resume's RegisterDispatch and RunAsync, and
        // Register is the last statement before the orchestrator is entered.
        var executingRuns = executing ?? _executing;
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = modePersona ?? new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new FakePlanner(onPlan);

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => stream is not null
                ? stream(ci.ArgAt<CancellationToken>(7))
                : probe is null
                    ? Drive()
                    : DriveWithToolCall(ci.ArgAt<ToolCallHandler?>(3), probe));

        var plugins = Substitute.For<IPluginService>();
        if (probe is not null)
        {
            // Every tool call routes to a deferred write (a pending action) that records whether the gate
            // let it run. IsMcpTool stays false → a built-in, so only the grant set decides.
            plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
                .Returns(_ => ((object?)null, new PluginToolCall(
                    probe.ToolName, Guid.NewGuid(), "files", "desc", null,
                    () => { probe.MarkExecuted(); return Task.FromResult<object?>("write-done"); })));
        }

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", probe is null ? null : new List<AITool>(),
                SupportsTools: probe is not null, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        if (rosterPersona is not null)
            personas.GetPersonaAsync(rosterPersona.Id).Returns(rosterPersona);
        personas.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>(
            pinnedPersona is null ? [] : [pinnedPersona]));
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(nullDefaultProvider ? (AiProvider?)null : provider);
        if (rosterProvider is not null)
            providers.GetProviderAsync(rosterProvider.Id).Returns(rosterProvider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        ISettingsService settings;
        if (settingsService is not null)
        {
            settings = settingsService;
        }
        else
        {
            var settingsSub = Substitute.For<ISettingsService>();
            settingsSub.GetSettingsAsync().Returns(appSettings ?? new AppSettings());
            settings = settingsSub;
        }

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
        // The orchestrator's terminal critic pass; the default (empty queue) FakeVerifier accepts.
        services.AddSingleton<IAgentVerifier>(verifier ?? new FakeVerifier());
        // Echoes the key back, so a chat notice can be asserted by key instead of by translated sentence.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        services.AddSingleton(loc);
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        // The per-run scope resolves HeadlessTurnExecutor -> BackgroundAssistantTurnRunner, which requires this;
        // omit it and the resolve throws inside the launcher's dispatch task, where it is swallowed.
        services.AddSingleton<IExecutingRunStore>(executingRuns);
        // The loop needs the SAME registry the launcher registers its sink with; the orchestrator's parameter is
        // trailing-optional, so an unregistered store is silently "no steering".
        if (steering is not null) services.AddSingleton(steering);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            executingRuns, NullLogger<HeadlessRunLauncher>.Instance,
            runsBaseDirOverride: runsBaseOverride ?? _runsBase, workspaces: workspaces, steering: steering);
        return (launcher, planner);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive()
    {
        await Task.Yield();
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }

    private static async IAsyncEnumerable<ChatStreamItem> DriveWithToolCall(
        ToolCallHandler? handler, ToolProbe probe)
    {
        await Task.Yield();
        if (handler is not null)
            probe.Record(await handler(new FunctionCallContent("call-1", probe.ToolName, new Dictionary<string, object?>()), new ToolDispatchContext(1)));
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }

    /// <summary>Persist a stub chat + a parked (WaitingForInput) Planned run carrying one Pending step.</summary>
    /// <param name="parentRunId">Makes the parked run a child; every child owns a stub chat, so a user can press Continue on one.</param>
    private async Task<AgentRun> ParkRunWithPendingStepAsync(string? policyJson, Guid? parentRunId = null)
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
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "g",
                PolicyJson: policyJson, ParentRunId: parentRunId), ct);
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

    /// <summary>A settled delegated run of <paramref name="parentRunId"/>, with its own stub chat.</summary>
    private async Task<AgentRun> SettledChildAsync(Guid parentRunId, bool complete = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var child = await NewChildRowAsync(parentRunId);
        if (complete)
            await _runs.CompleteAsync(child.Id, ct: ct);
        else
            await _runs.FailAsync(child.Id, "child failed", ct: ct);
        return child;
    }

    private async Task<AgentRun> NewChildRowAsync(Guid parentRunId)
    {
        var ct = TestContext.Current.CancellationToken;
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "child",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [],
        }, ct);
        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "c",
                ParentRunId: parentRunId), ct);
    }

    /// <summary>Polls until the run leaves <c>WaitingForInput</c>, or returns false at the deadline.</summary>
    private async Task<bool> AwaitLeftTheParkAsync(Guid runId, int seconds = 10)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if ((await _runs.GetAsync(runId, ct))!.State != AgentRunState.WaitingForInput)
                return true;
            await Task.Delay(20, ct);
        }

        return false;
    }

    /// <summary>Persists a stub chat and a parked (<c>WaitingForInput</c>) Planned run with no step rows and the given pause reason.</summary>
    private async Task<AgentRun> ParkRunWithNoStepsAsync(string reason)
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
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "g"), ct);
        await _runs.PauseAsync(run.Id, reason, ct);
        return run;
    }

    /// <summary>Poll until the run leaves the non-terminal states, then drain the launcher's in-flight task.</summary>
    private async Task AwaitRunSettledAsync(HeadlessRunLauncher launcher, Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var state = (await _runs.GetAsync(runId, ct))!.State;
        while (DateTime.UtcNow < deadline)
        {
            state = (await _runs.GetAsync(runId, ct))!.State;
            if (state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                break;
            await Task.Delay(20, ct);
        }

        // Assert terminality rather than giving up at the deadline: a caller that only asserts a tool was NOT
        // executed would otherwise pass vacuously when the resume dispatched no step at all.
        Assert.Contains(state, new[] { AgentRunState.Completed, AgentRunState.Failed, AgentRunState.Cancelled });

        // Drains the resume task (StopAsync awaits every in-flight run) so nothing touches the
        // SqliteContext after the test disposes it.
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Sibling of <see cref="AwaitRunSettledAsync"/> that polls until the run is parked again; the
    /// reason token is asserted too, because only the approval park writes that one.</summary>
    private async Task AwaitRunParkedForApprovalAsync(HeadlessRunLauncher launcher, Guid runId, string expectedTool)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        AgentRun run;
        while (true)
        {
            run = (await _runs.GetAsync(runId, ct))!;
            if (run.State == AgentRunState.WaitingForInput || DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(20, ct);
        }

        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("tool-approval", ReadPauseReason(run));
        Assert.Equal(expectedTool, ReadPauseTool(run));

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The pause envelope's reason, read from the raw row (<c>RunPauseEnvelope</c> is internal to src).</summary>
    private static string? ReadPauseReason(AgentRun run) => ReadPauseMember(run, "reason");

    /// <summary>The pause envelope's <c>tool</c> member.</summary>
    private static string? ReadPauseTool(AgentRun run) => ReadPauseMember(run, "tool");

    private static string? ReadPauseMember(AgentRun run, string member)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(run.ExtraJson);
        return doc.RootElement.TryGetProperty(member, out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
    }

    /// <summary>
    /// A children-parked parent used to sit at WaitingForInput after every child had finished, and recovering
    /// it took a second human action nobody knew was needed. It resumes itself now — but only once the LAST
    /// child settles, or the group would be re-dispatched under a child that is still running.
    /// </summary>
    [Fact]
    public async Task ChildrenParkedParent_ResumesItself_OnlyOnceTheLastChildSettles()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        // Children first, then the park — the order the fan-out produces, and the order this handler needs:
        // a children-parked row with no child rows yet is a state it deliberately draws no conclusion from.
        var parent = await ParkRunWithPendingStepAsync(null);
        var childA = await NewChildRowAsync(parent.Id);
        var childB = await NewChildRowAsync(parent.Id);
        await _runs.PauseAsync(parent.Id, "children-parked", ct);

        await _runs.CompleteAsync(childA.Id, ct: ct);
        Assert.False(await AwaitLeftTheParkAsync(parent.Id, seconds: 1),
            "one of two children settling must not re-dispatch the group");

        await _runs.CompleteAsync(childB.Id, ct: ct);
        Assert.True(await AwaitLeftTheParkAsync(parent.Id));

        await AwaitRunSettledAsync(launcher, parent.Id);
    }

    /// <summary>
    /// The other edge of the same race, and the one a callback on the child's settle cannot see: the parent's
    /// park can land AFTER its last child is already terminal, and a resume keyed only on a child settling
    /// would then never fire. Keying on the state change catches whichever of the two is second.
    /// </summary>
    [Fact]
    public async Task ChildrenParkedParent_Resumes_EvenWhenItParksAfterEveryChildHasSettled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        var parent = await ParkRunWithPendingStepAsync(null);   // parked for a budget first
        await SettledChildAsync(parent.Id);
        await SettledChildAsync(parent.Id, complete: false);     // a FAILED child is settled too

        // The park itself is the trigger here: every child was already terminal when it landed.
        await _runs.PauseAsync(parent.Id, "children-parked", ct);

        Assert.True(await AwaitLeftTheParkAsync(parent.Id));
        await AwaitRunSettledAsync(launcher, parent.Id);
    }

    /// <summary>Every other park has a question only a person can answer, so a settling child must not answer it
    /// for them. The reason token is the whole discriminator.</summary>
    [Fact]
    public async Task ABudgetParkedParent_IsNotResumedByASettlingChild()
    {
        var (launcher, _) = BuildLauncher();
        var parent = await ParkRunWithPendingStepAsync(null);    // "step-cap"
        await SettledChildAsync(parent.Id);

        Assert.False(await AwaitLeftTheParkAsync(parent.Id, seconds: 1));

        var still = (await _runs.GetAsync(parent.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(AgentRunState.WaitingForInput, still.State);
        Assert.Equal("step-cap", ReadPauseReason(still));
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The auto-resume takes the SAME path Continue does, so the two can race — and the resume CAS is
    /// what makes that harmless. One dispatch, whichever claim wins.</summary>
    [Fact]
    public async Task AManualContinueRacingTheAutoResume_DispatchesTheGroupOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var turns = 0;
        var (launcher, _) = BuildLauncher(stream: _ =>
        {
            Interlocked.Increment(ref turns);
            return Drive();
        });
        var parent = await ParkRunWithPendingStepAsync(null);
        var child = await NewChildRowAsync(parent.Id);
        await _runs.PauseAsync(parent.Id, "children-parked", ct);

        await _runs.CompleteAsync(child.Id, ct: ct);
        await launcher.ResumeAsync(parent.Id, ct: ct);

        Assert.True(await AwaitLeftTheParkAsync(parent.Id));
        await AwaitRunSettledAsync(launcher, parent.Id);

        // One pending step, so one model turn. A second dispatch would run it again.
        Assert.Equal(1, turns);
    }

    [Fact]
    public async Task Launch_PersistsStubChat_CreatesPlannedUserRun_AndWorkspace()
    {
        var (launcher, _) = BuildLauncher();

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken);

        // FK order: the parent chat exists (the run's CreateAsync would have failed otherwise).
        var chat = await _chats.GetAsync(handle.ChatId, TestContext.Current.CancellationToken);
        Assert.NotNull(chat);

        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.Equal(RunShape.Planned, run!.RunShape);
        Assert.Equal(AgentRunTrigger.User, run.TriggerKind);
        Assert.Equal(handle.ChatId, run.ChatId);

        // Isolated workspace created under %LOCALAPPDATA%\Pia\runs\<runId>.
        var runRoot = Path.Combine(_runsBase, handle.RunId.ToString());
        Assert.True(Directory.Exists(runRoot));

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var settled = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, settled!.State);

        try { Directory.Delete(runRoot, true); } catch { }
    }

    /// <summary>The launch and resume call sites are separate literals, so each gets its own fact; this one reads
    /// <c>ctx.WorkspaceRoot</c> because the harness has no provider that emits a tool call.</summary>
    [Fact]
    public async Task Launch_InitializesTheExecutorWithTheRunWorkspaceRoot()
    {
        var (launcher, planner) = BuildLauncher();

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // GetTempPath can carry an 8.3 or a link component, so the expectation is canonicalized the way the
        // launcher does — while the workspace still exists, since Canonicalize needs a real handle.
        var expected = SafeFolderPath.Canonicalize(Path.Combine(_runsBase, handle.RunId.ToString()));

        // Non-vacuity control: a planner that was never called leaves PlanContext null, and the claim below
        // would be about nothing at all.
        Assert.NotNull(planner.PlanContext);
        Assert.Equal(expected, planner.PlanContext!.WorkspaceRoot);

        try { Directory.Delete(expected, true); } catch { }
    }

    /// <summary>A resume does not re-plan, so the planner never sees the context and the terminal verify pass is
    /// the only place <c>ctx.WorkspaceRoot</c> is still observable.</summary>
    [Fact]
    public async Task Resume_InitializesTheExecutorWithTheSameRunWorkspaceRoot()
    {
        var verifier = new FakeVerifier();
        var (launcher, planner) = BuildLauncher(verifier: verifier);
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        var expected = SafeFolderPath.Canonicalize(Path.Combine(_runsBase, parked.Id.ToString()));

        // Non-vacuity: without this the assertion below would index an empty list, i.e. the fact would pass on
        // a resume that never executed anything.
        Assert.Single(verifier.SeenWorkspaceRoots);
        Assert.Equal(expected, verifier.SeenWorkspaceRoots[0]);
        // This run parked on `step-cap`, not `needs-goal`, so the resume must not re-plan.
        Assert.Null(planner.PlanContext);

        try { Directory.Delete(expected, true); } catch { }
    }

    /// <summary>Every other resume fact here covers the <c>WaitingForInput</c> arm; this is the only cover for the
    /// <c>Paused</c> one, and a swapped or missing arm would otherwise stay green.</summary>
    [Fact]
    public async Task Resume_ClaimsAUserPausedRun_AndDrainsItToCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);

        // Turn the budget park into a genuine USER pause, through the real CAS rather than by writing the
        // column: WaitingForInput is deliberately NOT in that CAS's source set, so the row goes through Running.
        await _runs.SetStateAsync(parked.Id, AgentRunState.Running, ct);
        Assert.True(await _runs.TryPauseUserAsync(parked.Id, ct));
        Assert.Equal(AgentRunState.Paused, (await _runs.GetAsync(parked.Id, ct))!.State);

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: ct));
        await AwaitRunSettledAsync(launcher, parked.Id);

        var final = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        // The claim retired the pause envelope it consumed, so the panel and the Flow surface stop calling a
        // finished run paused.
        Assert.Null(RunPauseEnvelope.ReadReason(final));

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>A provisioner that cannot isolate the run returns null and the run proceeds: an unattended run
    /// that failed because a scratch directory could not be prepared would deliver nothing.</summary>
    [Fact]
    public async Task ProvisioningFailure_DoesNotFailTheRun()
    {
        var workspaces = new FakeRunWorkspaceService(_runsBase) { ProvisionSucceeds = false };
        var (launcher, planner) = BuildLauncher(workspaces: workspaces);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var settled = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, settled!.State);

        // Non-vacuity: the provisioner really was consulted (a launcher that ignored it would also pass the
        // state assertion), and the degrade really was "no isolation" — the executor got a null root.
        Assert.Equal([handle.RunId], workspaces.Provisioned);
        Assert.NotNull(planner.PlanContext);
        Assert.Null(planner.PlanContext!.WorkspaceRoot);
    }

    /// <summary>Control for the fact above: with no provisioner the legacy create path is still in force, and a
    /// workspace it cannot create must still settle the run rather than leave it dangling non-terminal.</summary>
    [Fact]
    public async Task WithNoProvisioner_AnUncreatableWorkspace_StillSettlesTheRun()
    {
        // A FILE where the runs base should be: Directory.CreateDirectory(<file>\<runId>) throws.
        var blocked = Path.Combine(_dir, "runs-as-a-file");
        await File.WriteAllTextAsync(blocked, "x", TestContext.Current.CancellationToken);
        var (launcher, _) = BuildLauncher(runsBaseOverride: blocked);

        await Assert.ThrowsAnyAsync<IOException>(() => launcher.LaunchAsync(
            new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken));

        // The launch threw before returning a handle, so the run is found the way a crash-recovery pass would:
        // through its chat. Exactly one run exists, and it is settled — not left Planning.
        var runs = new List<AgentRun>();
        foreach (var chatId in await _chats.GetAllIdsAsync(TestContext.Current.CancellationToken))
            runs.AddRange(await _runs.GetByChatAsync(chatId, TestContext.Current.CancellationToken));

        var run = Assert.Single(runs);
        Assert.Equal(AgentRunState.Failed, run.State);
    }

    [Fact]
    public async Task ConcurrencyCap_NeverExceedsTwoConcurrentRuns()
    {
        var release = new TaskCompletionSource();
        var (launcher, planner) = BuildLauncher(onPlan: () => release.Task);

        var h1 = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var h2 = await launcher.LaunchAsync(new HeadlessRunRequest("b", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var h3 = await launcher.LaunchAsync(new HeadlessRunRequest("c", AgentRunTrigger.User), TestContext.Current.CancellationToken);

        // Give the two admitted runs time to enter the planner; the 3rd is slot-starved.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (planner.MaxConcurrent < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(planner.MaxConcurrent <= 2, $"MaxConcurrent was {planner.MaxConcurrent}");
        Assert.Equal(2, planner.Concurrent);

        release.SetResult();
        await Task.WhenAll(h1.Completion, h2.Completion, h3.Completion).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(planner.MaxConcurrent <= 2);

        foreach (var h in new[] { h1, h2, h3 })
            try { Directory.Delete(Path.Combine(_runsBase, h.RunId.ToString()), true); } catch { }
    }

    /// <summary>A substitute cannot raise <c>SettingsChanged</c> without a reflection helper, and a four-member
    /// interface is cheaper to implement than to trick.</summary>
    private sealed class MutableSettingsService : ISettingsService
    {
        public MutableSettingsService(AppSettings initial) => Current = initial;

        public event EventHandler<AppSettings>? SettingsChanged;

        public AppSettings Current { get; private set; }

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Current);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }

        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;

        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    /// <summary>Wait, bounded, for the planner to hold <paramref name="expected"/> runs at once.</summary>
    private static async Task WaitForConcurrencyAsync(FakePlanner planner, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (planner.Concurrent < expected && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LaunchAsync_AppliesTheConfiguredPoolWidthOnColdStart_WithoutASettingsChangeEvent()
    {
        // Nothing here saves settings, so SettingsChanged never fires and the only thing that can apply the
        // configured width is the Resize at the top of LaunchCoreAsync.
        var release = new TaskCompletionSource();
        var (launcher, planner) = BuildLauncher(
            onPlan: () => release.Task,
            appSettings: new AppSettings { MaxParallelBackgroundRuns = 3 });

        var handles = new List<HeadlessRunHandle>();
        foreach (var goal in new[] { "a", "b", "c", "d" })
            handles.Add(await launcher.LaunchAsync(
                new HeadlessRunRequest(goal, AgentRunTrigger.User), TestContext.Current.CancellationToken));

        await WaitForConcurrencyAsync(planner, 3);

        // THREE, not two: the configured width reached the pool. And the 4th is still queued, so the width is
        // the setting rather than "no cap at all".
        Assert.Equal(3, planner.Concurrent);
        Assert.True(planner.MaxConcurrent <= 3, $"MaxConcurrent was {planner.MaxConcurrent}");

        release.SetResult();
        await Task.WhenAll(handles.Select(h => h.Completion))
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.True(planner.MaxConcurrent <= 3, $"MaxConcurrent was {planner.MaxConcurrent}");

        foreach (var h in handles)
            try { Directory.Delete(Path.Combine(_runsBase, h.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task SettingsChanged_RaisingTheCap_StartsAQueuedRun()
    {
        // The setting must take effect for a run that is ALREADY QUEUED; the lazy Resize on the dispatch paths
        // cannot reach it, so only the SettingsChanged subscription can.
        var release = new TaskCompletionSource();
        var settings = new MutableSettingsService(new AppSettings { MaxParallelBackgroundRuns = 2 });
        var (launcher, planner) = BuildLauncher(onPlan: () => release.Task, settingsService: settings);

        var handles = new List<HeadlessRunHandle>();
        foreach (var goal in new[] { "a", "b", "c" })
            handles.Add(await launcher.LaunchAsync(
                new HeadlessRunRequest(goal, AgentRunTrigger.User), TestContext.Current.CancellationToken));

        await WaitForConcurrencyAsync(planner, 2);

        // PRE-STATE, and it is what makes this fact about the event rather than about the cold-start Resize:
        // the pool is 2 wide here, so the third run is queued and nothing but a widening can admit it.
        Assert.Equal(2, planner.Concurrent);

        await settings.SaveSettingsAsync(new AppSettings { MaxParallelBackgroundRuns = 3 });
        await WaitForConcurrencyAsync(planner, 3);

        // Nothing finished — no run has been released from the planner yet — so the third run is inside the
        // planner because the raise handed it a permit.
        Assert.Equal(3, planner.Concurrent);

        release.SetResult();
        await Task.WhenAll(handles.Select(h => h.Completion))
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        foreach (var h in handles)
            try { Directory.Delete(Path.Combine(_runsBase, h.RunId.ToString()), true); } catch { }
    }

    /// <summary>Reflection rather than an internal accessor: this fails loudly if the field is renamed, which is
    /// cheaper than production surface that exists for one test.</summary>
    private static RunSlotPool SlotPoolOf(HeadlessRunLauncher launcher) =>
        (RunSlotPool)typeof(HeadlessRunLauncher)
            .GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(launcher)!;

    [Fact]
    public async Task LaunchAsync_QueuesItsSlotWaitBehindTheTicketTakenBeforeIt()
    {
        // The slot wait runs as the first statement of a detached Task.Run, so the only thing that can carry
        // launch order into the pool is a ticket taken on the launching thread before it.
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource();
        var planCalls = 0;
        // Run A sails through; every later run holds inside the planner, which is how "B started" is observable.
        var (launcher, planner) = BuildLauncher(
            onPlan: () => Interlocked.Increment(ref planCalls) == 1 ? Task.CompletedTask : gate.Task,
            appSettings: new AppSettings { MaxParallelBackgroundRuns = 1 });

        var a = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), ct);
        await a.Completion.WaitAsync(TimeSpan.FromSeconds(20), ct);

        var pool = SlotPoolOf(launcher);
        // The width the setting asked for — which also proves the reflected field is the pool the launch used.
        Assert.Equal(1, pool.Width);

        // A permit is free, taken and handed straight back: without this the negative assertion below would pass
        // on a tree with no ticket chain at all, because a saturated pool explains it just as well.
        var probe = pool.WaitAsync(CancellationToken.None);
        Assert.True(probe.IsCompleted, "the pool should be idle after run A settled");
        pool.Release();

        // Hold the head of the chain. Production never does this (see RunSlotPool.TakeTicket's caller contract);
        // it is the only way to occupy a place in the queue that the launcher must be seen to queue behind.
        var head = pool.TakeTicket();

        var b = await launcher.LaunchAsync(new HeadlessRunRequest("b", AgentRunTrigger.User), ct);

        // B's dispatch task is running and the pool is idle, yet B has not started: its ticket was issued after
        // `head`, so its wait is not even enqueued. Bounded, because "has not happened" has no state to read.
        await Task.Delay(300, ct);
        Assert.Equal(0, planner.Concurrent);
        Assert.Equal(1, planCalls);

        // Use the head ticket exactly as a dispatch does — wait, then release — which hands the chain on.
        await pool.WaitAsync(head, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        pool.Release();

        await WaitForConcurrencyAsync(planner, 1);
        Assert.Equal(1, planner.Concurrent);
        Assert.Equal(2, planCalls);

        gate.SetResult();
        await b.Completion.WaitAsync(TimeSpan.FromSeconds(20), ct);

        foreach (var h in new[] { a, b })
            try { Directory.Delete(Path.Combine(_runsBase, h.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Stop_DuringInFlightRun_SettlesRun_NeverRunning()
    {
        var release = new TaskCompletionSource();
        // Asynchronous continuations: entered fires on the planner's thread inside PlanAsync, so resuming
        // inline would call StopAsync from within that call stack instead of from the test's.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (launcher, _) = BuildLauncher(onPlan: () => { entered.TrySetResult(); return release.Task; });

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await launcher.StopAsync(TestContext.Current.CancellationToken);
        release.TrySetResult();
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.NotEqual(AgentRunState.Running, run!.State);
        Assert.NotEqual(AgentRunState.Planning, run.State);

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task ChatDeleted_DeletesRunWorkspace()
    {
        var (launcher, _) = BuildLauncher();
        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var runRoot = Path.Combine(_runsBase, handle.RunId.ToString());
        Assert.True(Directory.Exists(runRoot));

        // Deleting the chat (raises ChatsChanged/Deleted) must remove the same-session run workspace.
        await _chats.DeleteAsync(handle.ChatId, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(runRoot));
    }

    [Fact]
    public async Task StartupSweep_DeletesOrphanWorkspace()
    {
        var (launcher, _) = BuildLauncher();

        // An orphan dir whose name is a GUID with no matching AgentRuns row → swept.
        var orphan = Path.Combine(_runsBase, Guid.NewGuid().ToString());
        Directory.CreateDirectory(orphan);
        // A non-GUID dir is left untouched.
        var keep = Path.Combine(_runsBase, "not-a-guid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keep);

        await launcher.RunStartupSweepAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(keep));

        try { Directory.Delete(keep, true); } catch { }
    }

    [Fact]
    public async Task Launch_WithoutExplicitGrants_PersistsWriteFileOnlyEnvelope()
    {
        // The default grant set for an unattended run drops delete_file, and the launch persists the resolved
        // set as its opaque PolicyJson envelope, which is what a later resume restores.
        var (launcher, _) = BuildLauncher();

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.NotNull(run!.PolicyJson);
        var restored = HeadlessRunLauncher.TryRestoreGrantEnvelope(run.PolicyJson);
        Assert.Equal(new[] { "write_file" }, restored);
        Assert.DoesNotContain("delete_file", run.PolicyJson);

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Launch_WithoutExplicitGrants_DeniesDeleteFile_ButAllowsWriteFile()
    {
        // The default set is observed at the GATE, not just in the envelope: delete_file is refused as
        // ungranted while write_file still runs, so unattended runs keep producing real deliverables.
        var deleteProbe = new ToolProbe("delete_file");
        var (deleteLauncher, _) = BuildLauncher(probe: deleteProbe);
        var deleteHandle = await deleteLauncher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await deleteHandle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("needs a person's approval", deleteProbe.GateResult ?? string.Empty);

        var writeProbe = new ToolProbe("write_file");
        var (writeLauncher, _) = BuildLauncher(probe: writeProbe);
        var writeHandle = await writeLauncher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await writeHandle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(writeProbe.Executed);

        foreach (var h in new[] { deleteHandle, writeHandle })
            try { Directory.Delete(Path.Combine(_runsBase, h.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Launch_WithExplicitDeleteGrant_StillHonoursIt()
    {
        // Only the DEFAULT is narrow — an explicit GrantedWrites naming delete_file keeps working, and the
        // envelope records it so a resume restores it too.
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = BuildLauncher(probe: probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User, GrantedWrites: ["write_file", "delete_file"]),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(probe.Executed);
        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "write_file", "delete_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(run!.PolicyJson));

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Resume_RestoresTheNarrowLaunchGrant_AndNeverWidensIt()
    {
        // A scheduled job launched with a NARROW grant list that budget-pauses must not silently acquire
        // delete_file on resume — nor even write_file, which its launch never granted.
        var deleteProbe = new ToolProbe("delete_file");
        var (deleteLauncher, _) = BuildLauncher(probe: deleteProbe);
        var narrowEnvelope = HeadlessRunLauncher.SerializeGrantEnvelope(["create_todo"], AgentRunTrigger.Schedule);

        var parked = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await deleteLauncher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(deleteLauncher, parked.Id);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("needs a person's approval", deleteProbe.GateResult ?? string.Empty);

        var writeProbe = new ToolProbe("write_file");
        var (writeLauncher, _) = BuildLauncher(probe: writeProbe);
        var parked2 = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await writeLauncher.ResumeAsync(parked2.Id, ct: TestContext.Current.CancellationToken));
        // The resume never widens the grant set: write_file is not in the envelope, so it does not run — a root
        // run stops and asks rather than hard-denying a promptable capability and marching on.
        await AwaitRunParkedForApprovalAsync(writeLauncher, parked2.Id, "write_file");

        Assert.False(writeProbe.Executed); // the floor is a FALLBACK, never an addition to a known set
        // Positive counter-assertion: the gate was actually consulted, so this leg cannot pass just because
        // the resume never dispatched a step.
        Assert.Contains("approval", writeProbe.GateResult ?? string.Empty);

        var grantProbe = new ToolProbe("create_todo");
        var (grantLauncher, _) = BuildLauncher(probe: grantProbe);
        var parked3 = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await grantLauncher.ResumeAsync(parked3.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(grantLauncher, parked3.Id);

        Assert.True(grantProbe.Executed); // exactly what the launch granted still runs

        foreach (var id in new[] { parked.Id, parked2.Id, parked3.Id })
            try { Directory.Delete(Path.Combine(_runsBase, id.ToString()), true); } catch { }
    }

    // The pins the LAUNCH resolved have to survive the park, because the resume has no job store to re-read
    // them from. The per-mode default is moved while the run is parked, which is what makes this fact able to
    // tell a read-back from a re-resolution that happens to agree.
    [Fact]
    public async Task Resume_RunsThePersonaAndEffortTheLaunchResolved_NotTheCurrentModeDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        // Low, so the XHigh asserted below can only have come from the request's own pin outranking it.
        var pinned = new Persona { Name = "Pinned", SystemPrompt = "sys", ReasoningEffort = ReasoningEffort.Low };

        var (launcher, planner) = BuildLauncher(pinnedPersona: pinned);
        // Steps > 0, or the plan degrades to the single-turn fallback, which writes no step row and never parks.
        planner.Steps = 1;
        Guid runId;
        try
        {
            var handle = await launcher.LaunchAsync(new HeadlessRunRequest(
                "pinned goal", AgentRunTrigger.Schedule,
                PersonaId: pinned.Id, ReasoningEffort: ReasoningEffort.XHigh,
                // Wall-clock is already spent on the first drain iteration, so the step stays Pending.
                Budget: new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.Zero)), ct);
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
            runId = handle.RunId;

            var parked = await _runs.GetAsync(runId, ct);
            Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
            Assert.Equal(pinned.Id, parked.PersonaId);
            Assert.Equal(ReasoningEffort.XHigh, parked.ReasoningEffort);
        }
        finally
        {
            await launcher.StopAsync(CancellationToken.None);
        }

        var movedOn = new Persona { Name = "MovedOn", SystemPrompt = "sys", ReasoningEffort = ReasoningEffort.Minimal };
        var verifier = new FakeVerifier();
        var (resumer, resumePlanner) = BuildLauncher(
            pinnedPersona: pinned, modePersona: movedOn, verifier: verifier);
        try
        {
            Assert.True(await resumer.ResumeAsync(runId, ct: ct));
            await AwaitRunSettledAsync(resumer, runId);

            // A resume skips planning, so the verify pass is the only run-level hook that sees these two —
            // and a null PlanPersona is the proof the planner really was not the source.
            Assert.Null(resumePlanner.PlanPersona);
            Assert.Equal(pinned.Id, Assert.Single(verifier.SeenPersonas).Id);
            Assert.Equal(ReasoningEffort.XHigh, Assert.Single(verifier.SeenProviders).ReasoningEffort);
        }
        finally
        {
            await resumer.StopAsync(CancellationToken.None);
        }

        try { Directory.Delete(Path.Combine(_runsBase, runId.ToString()), true); } catch { }
    }

    // A job that pinned an EXPLICIT provider has to get it back on Continue. The resume launcher's own mode
    // default is a different provider (BuildLauncher mints one per call), so reading the pin back is the only way
    // the pinned id can appear here.
    [Fact]
    public async Task Resume_RunsTheProviderTheLaunchResolved_NotTheCurrentModeDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var pinnedProvider = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "Pinned", Endpoint = "https://pinned", ProviderType = AiProviderType.OpenAI,
        };

        var (launcher, planner) = BuildLauncher(rosterProvider: pinnedProvider);
        planner.Steps = 1;
        Guid runId;
        Guid chatId;
        try
        {
            // An effort pin as well, which is the shape a scheduled job actually launches with — and the one
            // that makes the launch stamp the chat off ApplyEffort's CLONE rather than the stored provider.
            var handle = await launcher.LaunchAsync(new HeadlessRunRequest(
                "pinned provider goal", AgentRunTrigger.Schedule, ProviderId: pinnedProvider.Id,
                ReasoningEffort: ReasoningEffort.XHigh,
                Budget: new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.Zero)), ct);
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
            runId = handle.RunId;
            chatId = handle.ChatId;

            Assert.Equal(AgentRunState.WaitingForInput, (await _runs.GetAsync(runId, ct))!.State);
            // The seam the resume reads. Reds if the clone the launch stamps it from ever loses its Id, which
            // would make the whole row no-op silently while every other assertion here stayed green.
            Assert.Equal(pinnedProvider.Id, await _chats.GetProviderIdAsync(chatId, ct));
        }
        finally
        {
            await launcher.StopAsync(CancellationToken.None);
        }

        var verifier = new FakeVerifier();
        var (resumer, _) = BuildLauncher(rosterProvider: pinnedProvider, verifier: verifier);
        try
        {
            Assert.True(await resumer.ResumeAsync(runId, ct: ct));
            await AwaitRunSettledAsync(resumer, runId);

            var resumed = Assert.Single(verifier.SeenProviders);
            Assert.Equal(pinnedProvider.Id, resumed.Id);
            // Both pins on one object: the resume clones the pinned provider to stamp the effort, so an Id
            // dropped by that clone would show here even though the launch-side assertion passed.
            Assert.Equal(ReasoningEffort.XHigh, resumed.ReasoningEffort);
        }
        finally
        {
            await resumer.StopAsync(CancellationToken.None);
        }

        try { Directory.Delete(Path.Combine(_runsBase, runId.ToString()), true); } catch { }
    }

    // The pinned provider was deleted during the park. The ladder still has to answer, or a park would become
    // unresumable — strictly worse than the pre-E10 behaviour it replaces.
    [Fact]
    public async Task Resume_WhenTheLaunchProviderIsGone_FallsBackToTheLadder()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await ParkRunWithPendingStepAsync(policyJson: null);
        // The chat row names a provider no store can answer, which is what a deleted provider looks like here.
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = run.ChatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = run.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            WindowMode = WindowMode.Assistant.ToString(),
            ProviderId = Guid.NewGuid(),
            Messages = [],
        }, ct);

        var verifier = new FakeVerifier();
        var (resumer, _) = BuildLauncher(verifier: verifier);
        try
        {
            Assert.True(await resumer.ResumeAsync(run.Id, ct: ct));
            await AwaitRunSettledAsync(resumer, run.Id);

            Assert.Single(verifier.SeenProviders);
        }
        finally
        {
            await resumer.StopAsync(CancellationToken.None);
        }

        try { Directory.Delete(Path.Combine(_runsBase, run.Id.ToString()), true); } catch { }
    }

    // The other half of E9's freeze, and the direction it originally left open. The launch resolved NO effort;
    // the persona then gains one while the run is parked. Recording that the launch resolved nothing is what
    // stops the resume from picking it up — otherwise a park could change what the remaining steps cost.
    [Fact]
    public async Task Resume_WhenTheLaunchResolvedNoEffort_KeepsTheProvidersOwn_NotThePersonasEditedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var personaId = Guid.NewGuid();
        // Medium rather than null, so the assertion names a value the provider itself carries: it separates
        // "the freeze held" from "nothing was applied because there was nothing to apply".
        var pinnedProvider = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "Own", Endpoint = "https://own", ProviderType = AiProviderType.OpenAI,
            ReasoningEffort = ReasoningEffort.Medium,
        };
        var effortlessPersona = new Persona
        {
            Id = personaId, Name = "Effortless", SystemPrompt = "sys", ReasoningEffort = null,
        };

        var (launcher, planner) = BuildLauncher(
            pinnedPersona: effortlessPersona, rosterProvider: pinnedProvider);
        planner.Steps = 1;
        Guid runId;
        try
        {
            var handle = await launcher.LaunchAsync(new HeadlessRunRequest(
                "no effort goal", AgentRunTrigger.Schedule,
                ProviderId: pinnedProvider.Id, PersonaId: personaId,
                Budget: new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.Zero)), ct);
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
            runId = handle.RunId;

            var parked = await _runs.GetAsync(runId, ct);
            Assert.Equal(AgentRunState.WaitingForInput, parked!.State);
            Assert.Null(parked.ReasoningEffort);
            // Without this the null above is indistinguishable from a row written before the columns existed.
            Assert.True(parked.EffortPinRecorded);
        }
        finally
        {
            await launcher.StopAsync(CancellationToken.None);
        }

        // Same persona, edited during the park.
        var edited = new Persona
        {
            Id = personaId, Name = "Effortless", SystemPrompt = "sys", ReasoningEffort = ReasoningEffort.XHigh,
        };
        var verifier = new FakeVerifier();
        var (resumer, _) = BuildLauncher(
            pinnedPersona: edited, rosterProvider: pinnedProvider, verifier: verifier);
        try
        {
            Assert.True(await resumer.ResumeAsync(runId, ct: ct));
            await AwaitRunSettledAsync(resumer, runId);

            Assert.Equal(ReasoningEffort.Medium, Assert.Single(verifier.SeenProviders).ReasoningEffort);
        }
        finally
        {
            await resumer.StopAsync(CancellationToken.None);
        }

        try { Directory.Delete(Path.Combine(_runsBase, runId.ToString()), true); } catch { }
    }

    // A row written before the marker existed cannot say whether its null effort was resolved, so it has to keep
    // falling through — freezing it would silently drop the effort those runs have always resumed on.
    [Fact]
    public async Task Resume_OfARowThatRecordedNoPins_StillFallsThroughToThePersonasEffort()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await ParkRunWithPendingStepAsync(policyJson: null);
        Assert.False(run.EffortPinRecorded);

        var verifier = new FakeVerifier();
        var (resumer, _) = BuildLauncher(
            modePersona: new Persona { Name = "Mode", SystemPrompt = "sys", ReasoningEffort = ReasoningEffort.High },
            verifier: verifier);
        try
        {
            Assert.True(await resumer.ResumeAsync(run.Id, ct: ct));
            await AwaitRunSettledAsync(resumer, run.Id);

            Assert.Equal(ReasoningEffort.High, Assert.Single(verifier.SeenProviders).ReasoningEffort);
        }
        finally
        {
            await resumer.StopAsync(CancellationToken.None);
        }

        try { Directory.Delete(Path.Combine(_runsBase, run.Id.ToString()), true); } catch { }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("{\"v\":99,\"grantedWrites\":[\"write_file\",\"delete_file\"]}")]
    [InlineData("{\"somethingElse\":true}")]
    public void GrantEnvelope_MissingOrUnreadable_RestoresNothing_SoTheCallerAppliesTheFloor(string? policyJson)
    {
        Assert.Null(HeadlessRunLauncher.TryRestoreGrantEnvelope(policyJson));
    }

    [Fact]
    public void GrantEnvelope_PresentButEmptyGrantList_IsHonoured_NotReWidened()
    {
        // A launch that granted NO writes must not gain any on resume, so an explicitly empty list is
        // restored as empty rather than treated as "unreadable" (which would apply the {write_file} floor).
        var envelope = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule);
        var restored = HeadlessRunLauncher.TryRestoreGrantEnvelope(envelope);

        Assert.NotNull(restored);
        Assert.Empty(restored!);
    }

    [Fact]
    public void GrantEnvelope_IsVersionedCamelCase_AndCarriesTheOriginTrigger()
    {
        var json = HeadlessRunLauncher.SerializeGrantEnvelope(["write_file"], AgentRunTrigger.Schedule);

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"grantedWrites\"", json);
        Assert.Contains("Schedule", json);
    }

    [Fact]
    public async Task Resume_WithUnreadableEnvelope_UsesTheWriteOnlyFloor()
    {
        // Missing/garbage envelope (a legacy run): resume with {write_file} ONLY — the fallback is a floor,
        // never a ceiling, so delete_file stays refused.
        var deleteProbe = new ToolProbe("delete_file");
        var (deleteLauncher, _) = BuildLauncher(probe: deleteProbe);
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);
        Assert.True(await deleteLauncher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(deleteLauncher, parked.Id);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("needs a person's approval", deleteProbe.GateResult ?? string.Empty); // the gate refused it — not "never asked"

        var writeProbe = new ToolProbe("write_file");
        var (writeLauncher, _) = BuildLauncher(probe: writeProbe);
        var parked2 = await ParkRunWithPendingStepAsync(policyJson: "{\"totally\":\"foreign\"}");
        Assert.True(await writeLauncher.ResumeAsync(parked2.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(writeLauncher, parked2.Id);

        Assert.True(writeProbe.Executed);

        foreach (var id in new[] { parked.Id, parked2.Id })
            try { Directory.Delete(Path.Combine(_runsBase, id.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Launch_WithTheSettingOn_PersistsThePresetClasses()
    {
        // The launch resolves the policy from SETTINGS (it never reads the envelope back) and stores the
        // RESOLVED class list, not the preset's name — so a later per-run editor needs no document change.
        var (launcher, _) = BuildLauncher(
            appSettings: new AppSettings { AgentRunAutoApproveBuiltInWrites = true });

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        var policy = HeadlessRunLauncher.TryRestorePolicy(run!.PolicyJson);
        Assert.NotNull(policy);
        Assert.True(policy!.Covers(ToolClass.Files));
        Assert.False(policy.Covers(ToolClass.Git));
        Assert.False(policy.Covers(ToolClass.External));
        // The grant list is untouched by the policy.
        Assert.Equal(new[] { "write_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(run.PolicyJson));

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Launch_WithTheSettingOn_AutoApprovesAWriteWithNoNamedGrant()
    {
        // The policy is observed at the GATE, not just in the envelope: write_file executes although the run's
        // grant set is explicitly EMPTY, because the Files class is covered.
        var probe = new ToolProbe("write_file");
        var (launcher, _) = BuildLauncher(
            probe: probe, appSettings: new AppSettings { AgentRunAutoApproveBuiltInWrites = true });

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(probe.Executed);

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Resume_RestoresThePolicyFromTheEnvelope_NotFromSettings()
    {
        // The envelope is the run's authority of record: this run parked with no policy and no grants, so a
        // resume that consulted the (now on) setting would hand it card-free write_file.
        var probe = new ToolProbe("write_file");
        var (launcher, _) = BuildLauncher(
            probe: probe, appSettings: new AppSettings { AgentRunAutoApproveBuiltInWrites = true });

        var policylessEnvelope = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule);
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(policylessEnvelope));

        var parked = await ParkRunWithPendingStepAsync(policylessEnvelope);
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        // The run parks on write_file rather than denying it: "did not run" is a question put to a human here,
        // not a denial.
        await AwaitRunParkedForApprovalAsync(launcher, parked.Id, "write_file");

        Assert.False(probe.Executed);
        Assert.Contains("approval", probe.GateResult ?? string.Empty);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Resume_RestoresAPolicyTheEnvelopeDoesCarry()
    {
        // The other half: a run whose envelope DOES carry the policy gets it back on resume, with the setting
        // off — so the envelope, not the current setting, is what decides.
        var probe = new ToolProbe("write_file");
        var (launcher, _) = BuildLauncher(probe: probe); // setting OFF

        var envelope = HeadlessRunLauncher.SerializeGrantEnvelope(
            [], AgentRunTrigger.Schedule, new RunAutonomyPolicy([ToolClass.Files]));

        var parked = await ParkRunWithPendingStepAsync(envelope);
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.True(probe.Executed);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    [Fact]
    public async Task ResumedPolicy_StillCannotRunADeleteLikeSiblingOfACoveredClass()
    {
        // The Files class covers write_file and delete_file alike, and a POLICY must never be the reason a
        // delete-like tool ran — only a NAMED grant can do that.
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = BuildLauncher(probe: probe);

        var envelope = HeadlessRunLauncher.SerializeGrantEnvelope(
            [], AgentRunTrigger.Schedule, new RunAutonomyPolicy([ToolClass.Files]));

        var parked = await ParkRunWithPendingStepAsync(envelope);
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.False(probe.Executed);
        Assert.Contains("needs a person's approval", probe.GateResult ?? string.Empty);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>The raise sits after <c>_slots.Release</c> so bookkeeping cannot strand the shared slot; the slot
    /// is private, so what is asserted is its neighbour, the composer bracket released one line after it.</summary>
    [Fact]
    public async Task ResumeAsync_RaisesResumedRunSettled_AfterReleasingTheSlot()
    {
        var (launcher, _) = BuildLauncher();
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);

        var raised = new List<ResumedRunSettledEventArgs>();
        Guid? bracketedChatAtRaise = null;
        launcher.ResumedRunSettled += (_, e) =>
        {
            raised.Add(e);
            bracketedChatAtRaise = _executing.GetChatId(e.RunId);
        };

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        var only = Assert.Single(raised);
        Assert.Equal(parked.Id, only.RunId);
        Assert.Equal(parked.ChatId, only.ChatId);
        Assert.Null(bracketedChatAtRaise);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>
    /// StopAsync drains the in-flight set, and this event is the resume path's only completion signal, so
    /// un-tracking the run before raising it lets a shutdown drain to empty and drop the notification. The
    /// slow subscriber holds the window open that a load-dependent race would otherwise only sometimes hit.
    /// </summary>
    [Fact]
    public async Task ResumeAsync_RaisesResumedRunSettled_BeforeStopAsyncStopsWaiting()
    {
        var (launcher, _) = BuildLauncher();
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);

        var raised = 0;
        launcher.ResumedRunSettled += (_, _) =>
        {
            Thread.Sleep(200);
            Interlocked.Increment(ref raised);
        };

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.Equal(1, Volatile.Read(ref raised));

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>The launcher cannot distinguish "re-parked before starting" from "ran, then parked again", so it
    /// raises on every arm and the subscriber's state check decides.</summary>
    [Fact]
    public async Task ResumeAsync_RaisesResumedRunSettled_OnTheReParkArmToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = BuildLauncher(probe: probe);

        var raised = new List<ResumedRunSettledEventArgs>();
        launcher.ResumedRunSettled += (_, e) => raised.Add(e);

        // An envelope with no grants and no policy: the resumed run reaches write_file and parks to ask.
        var parked = await ParkRunWithPendingStepAsync(
            HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule));

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: ct));
        await AwaitRunParkedForApprovalAsync(launcher, parked.Id, "write_file");

        var only = Assert.Single(raised);
        Assert.Equal(parked.Id, only.RunId);
        // Non-vacuity: the run really is non-terminal, so the raise above happened on the park arm and not on a
        // run that quietly completed instead.
        Assert.Equal(AgentRunState.WaitingForInput, (await _runs.GetAsync(parked.Id, ct))!.State);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Resume_PreDispatchFailure_ReParksRun_ReturnsFalse()
    {
        // After the CAS claim (WaitingForInput→Running) a pre-dispatch failure must re-park the run — never
        // leave it dangling Running and unresumable until the crash sweep cancels it.
        var (launcher, _) = BuildLauncher(nullDefaultProvider: true);

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
        }, TestContext.Current.CancellationToken);

        var run = await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "g"),
            TestContext.Current.CancellationToken);
        await _runs.PauseAsync(run.Id, "step-cap", TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput,
            (await _runs.GetAsync(run.Id, TestContext.Current.CancellationToken))!.State);

        var resumed = await launcher.ResumeAsync(run.Id, ct: TestContext.Current.CancellationToken);

        Assert.False(resumed);
        var after = await _runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, after!.State); // re-parked, still resumable

        try { Directory.Delete(Path.Combine(_runsBase, run.Id.ToString()), true); } catch { }
    }

    /// <summary>Exercises the real <c>ResumeAsync</c> wire (unlike <c>AgentRunClarificationResumeTests</c>, which calls <c>RunAsync(parkReason:)</c> directly) — the pause reason must be read before the claim nulls the pause envelope, or a needs-goal resume would silently stop re-planning.</summary>
    [Fact]
    public async Task Resume_NeedsGoalPark_RePlans_AndThePlanTurnSeesTheAnswerThatRodeTheResume()
    {
        const string answer = "I mean the nightly export job";
        var ct = TestContext.Current.CancellationToken;
        var (launcher, planner) = BuildLauncher();
        planner.Steps = 1; // a real plan, so the re-plan is observable as a persisted step row too
        var parked = await ParkRunWithNoStepsAsync("needs-goal");

        Assert.True(await launcher.ResumeAsync(parked.Id, answer, ct));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.NotNull(planner.PlanContext);
        Assert.Equal(new[] { answer }, planner.PlanContext!.Clarifications);

        var final = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Single(final.Plan);                                                     // the re-plan was written
        Assert.Equal("g", final.Goal);                                                 // never rewritten
        Assert.Equal(new[] { answer }, RunClarifications.Read(final.ClarificationsJson)); // durable beside it

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>A resume that claims the row and then dies before dispatch must re-park with the reason the next resume needs — <c>needs-goal</c>/<c>needs-input</c>/<c>plan-approval</c> preserve their own token (losing it would silently drop re-planning, answer persistence, or the approval card), other reasons get the generic <c>resume-interrupted</c> diagnostic.</summary>
    [Theory]
    [InlineData("needs-goal", "needs-goal")]
    [InlineData("needs-input", "needs-input")]
    [InlineData("step-cap", "resume-interrupted")]
    [InlineData("plan-approval", "plan-approval")]
    public async Task Resume_InterruptedBeforeDispatch_ReParksWithTheTokenTheNextResumeNeeds(
        string parkReason, string expectedAfterRePark)
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher(nullDefaultProvider: true);
        var parked = await ParkRunWithNoStepsAsync(parkReason);

        Assert.False(await launcher.ResumeAsync(parked.Id, ct: ct));

        var after = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, after!.State); // re-parked, still resumable
        Assert.Equal(expectedAfterRePark, ReadPauseReason(after));

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>The claim arm: the two CAS sources are disjoint BY STATE, so a run that is in neither parked
    /// state is not claimable at all — and the caller returns before touching slots or inflight.</summary>
    [Fact]
    public async Task Resume_OfARunInNeitherParkedState_IsNotClaimed_AndDispatchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, planner) = BuildLauncher();
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);
        Assert.True(await _runs.TryBeginResumeAsync(parked.Id, ct)); // someone else claimed it first

        Assert.False(await launcher.ResumeAsync(parked.Id, ct: ct));

        // Never reached the orchestrator: the row still reads Running from the winning claim, and no
        // re-park happened either — a lost claim must not write the row at all.
        Assert.Equal(AgentRunState.Running, (await _runs.GetAsync(parked.Id, ct))!.State);
        Assert.Null(planner.PlanContext);
    }

    /// <summary>A decline answers a tool-approval park only: on any other park there is no question to say no
    /// to, and claiming the CAS anyway would turn a budget pause into a denied-tool resume.</summary>
    [Fact]
    public async Task Decline_OfARunParkedAtItsBudget_IsRefused_AndLeavesTheRunParked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        var parked = await ParkRunWithNoStepsAsync("step-cap");

        Assert.False(await launcher.DeclineAsync(parked.Id, ct));

        var after = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, after!.State);
        Assert.Equal("step-cap", ReadPauseReason(after)); // the park it had, not a denied-tool resume
    }

    [Fact]
    public async Task RejectPlanAsync_CancelsTheRun_AndPostsANotice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        var parked = await ParkRunWithNoStepsAsync(AgentRunOrchestrator.PlanApprovalReason);

        Assert.True(await launcher.RejectPlanAsync(parked.Id, ct));

        var updated = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.Cancelled, updated!.State);
        Assert.NotNull(updated.CompletedAt);
        var chat = await _chats.GetAsync(updated.ChatId, ct);
        Assert.Contains(chat!.Messages, m => m.Content == "Run_PlanRejected_ChatNote");
    }

    /// <summary>Reject answers a plan-approval park only; on any other park there is no plan to say no to.</summary>
    [Fact]
    public async Task RejectPlanAsync_ReturnsFalse_WhenRunIsNotParkedOnPlanApproval()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        var parked = await ParkRunWithNoStepsAsync(AgentRunOrchestrator.NeedsInputReason);

        Assert.False(await launcher.RejectPlanAsync(parked.Id, ct));

        var after = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, after!.State);
        Assert.Equal(AgentRunOrchestrator.NeedsInputReason, ReadPauseReason(after));
    }

    /// <summary>Continue IS the approval, so the parked tool joins the run's grants and is persisted — a second
    /// park would otherwise restore the launch envelope and forget the first approval.</summary>
    [Fact]
    public async Task Resume_OfAToolApprovalPark_WidensTheEnvelopeByTheParkedTool_AndPersistsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher();
        // A readable envelope granting nothing: the widening may only be written back over one this build read.
        var parked = await ParkRunWithPendingStepAsync(
            HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule));
        await _runs.PauseAsync(parked.Id, "tool-approval", ct, approvalTool: "delete_file");

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: ct));
        await AwaitRunSettledAsync(launcher, parked.Id);

        var grants = HeadlessRunLauncher.TryRestoreGrantEnvelope((await _runs.GetAsync(parked.Id, ct))!.PolicyJson);
        Assert.NotNull(grants);
        Assert.Equal(["delete_file"], grants!);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>The workspace arm is idempotent in both directions: with no provisioner it RE-CREATES the run's
    /// own root when the launch's directory is gone, and hands the executor the canonicalized path.</summary>
    [Fact]
    public async Task Resume_WithNoProvisioner_ReCreatesTheRunsOwnWorkspaceRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = new FakeVerifier();
        var (launcher, _) = BuildLauncher(verifier: verifier);
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);
        var expected = Path.Combine(_runsBase, parked.Id.ToString());
        try { Directory.Delete(expected, true); } catch { }
        Assert.False(Directory.Exists(expected));

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: ct));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.True(Directory.Exists(expected));
        Assert.Single(verifier.SeenWorkspaceRoots); // non-vacuity: the resume really drained a step
        Assert.Equal(SafeFolderPath.Canonicalize(expected), verifier.SeenWorkspaceRoots[0]);

        try { Directory.Delete(expected, true); } catch { }
    }

    [Fact]
    public async Task LaunchedRun_HoldsTheComposerBracket_WhileItExecutes_AndReleasesWhenItEnds()
    {
        // ChatSessionManager reads this index synchronously when a chat is activated, so it must be open for the
        // whole span in which the executor writes the chat — and empty afterwards, or the composer is dead.
        var release = new TaskCompletionSource();
        var (launcher, _) = BuildLauncher(onPlan: () => release.Task);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);

        // Registration is AFTER the concurrency-slot wait (deliberately: that window is fail-open), so wait
        // for the run to actually be inside the orchestrator rather than assuming it already is.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_executing.IsExecuting(handle.ChatId) && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(_executing.IsExecuting(handle.ChatId), "the launch bracket must be open while the run executes");
        Assert.Equal<Guid?>(handle.ChatId, _executing.GetChatId(handle.RunId));

        release.SetResult();
        // Awaiting Completion is what makes the post-assertions deterministic: the launcher's finally (which
        // releases the bracket) has run by the time this task completes.
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.False(_executing.IsExecuting(handle.ChatId), "a finished run must not leave the composer gated");
        Assert.Null(_executing.GetChatId(handle.RunId));

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    /// <summary>A child has no workspace of its own and writes the parent's directory: exactly one promotion is
    /// allowed per workspace, and a per-child worktree would mean N branches per fan-out.</summary>
    [Fact]
    public async Task LaunchChild_CreatesAChildRunInTheParentsWorkspace_WithANarrowedEnvelope()
    {
        var (launcher, planner) = BuildLauncher();
        var parentRunId = Guid.NewGuid();
        var parentRoot = Path.Combine(_dir, "parent-workspace");
        Directory.CreateDirectory(parentRoot);
        // The parent held delete_file; a child is a delegate and does not get to destroy anything.
        var parentEnvelope = HeadlessRunLauncher.SerializeGrantEnvelope(
            ["write_file", "delete_file"], AgentRunTrigger.Schedule);

        var handle = await launcher.LaunchChildAsync(
            new HeadlessRunRequest("do the sub-thing", AgentRunTrigger.Schedule), parentRunId, parentEnvelope,
            parentRoot, ct: TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var child = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.Equal<Guid?>(parentRunId, child!.ParentRunId);
        Assert.Equal(new[] { "write_file" }, HeadlessRunLauncher.TryRestoreGrantEnvelope(child.PolicyJson));

        // No directory at the CHILD's run id, and the executor was initialized with the PARENT's root, read off
        // the RunContext the orchestrator handed the planner.
        Assert.False(Directory.Exists(Path.Combine(_runsBase, handle.RunId.ToString())));
        Assert.NotNull(planner.PlanContext);
        Assert.Equal(parentRoot, planner.PlanContext!.WorkspaceRoot);
    }

    /// <summary>A child's run persona is the specialist the plan chose, so its provider is too; that provider is
    /// observable only through the child's stub chat, which records it.</summary>
    [Fact]
    public async Task AChildsAssignedPersonaDecidesItsRunPersonaAndProvider_WhileItIsStillOnTheRoster()
    {
        var preferred = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "specialist", Endpoint = "https://s", ProviderType = AiProviderType.OpenAI,
        };
        var researcher = new Persona
        {
            Id = Guid.NewGuid(), Name = "Researcher", SystemPrompt = "research", PreferredProviderId = preferred.Id,
        };

        var onRoster = new AppSettings();
        onRoster.SetAgentPersonaRoster(UserOperatingMode.Personal, [researcher.Id]);
        var (launcher, _) = BuildLauncher(
            appSettings: onRoster, rosterPersona: researcher, rosterProvider: preferred);

        var honoured = await launcher.LaunchChildAsync(
            new HeadlessRunRequest("sub-thing", AgentRunTrigger.Schedule), Guid.NewGuid(), null,
            parentWorkspaceRoot: null, personaId: researcher.Id, TestContext.Current.CancellationToken);
        await honoured.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var honouredChat = await _chats.GetAsync(honoured.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(preferred.Id, honouredChat!.ProviderId);

        // A plan outlives the setting that produced it: the same persona, off the roster the user now has.
        var (offRosterLauncher, _) = BuildLauncher(
            appSettings: new AppSettings(), rosterPersona: researcher, rosterProvider: preferred);
        var declined = await offRosterLauncher.LaunchChildAsync(
            new HeadlessRunRequest("sub-thing", AgentRunTrigger.Schedule), Guid.NewGuid(), null,
            parentWorkspaceRoot: null, personaId: researcher.Id, TestContext.Current.CancellationToken);
        await declined.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var declinedChat = await _chats.GetAsync(declined.ChatId, TestContext.Current.CancellationToken);
        Assert.NotEqual(preferred.Id, declinedChat!.ProviderId);
    }

    /// <summary>A ROUTINE's persona pin is the user's own choice, so unlike a delegated step's it is honoured
    /// with an EMPTY roster — the roster is the allow-list for what a PLANNER may assign, and it is empty by
    /// default.</summary>
    [Fact]
    public async Task AJobsPinnedPersona_IsHonouredWithAnEmptyRoster()
    {
        var preferred = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "specialist", Endpoint = "https://s", ProviderType = AiProviderType.OpenAI,
        };
        var researcher = new Persona
        {
            Id = Guid.NewGuid(), Name = "Researcher", SystemPrompt = "research", PreferredProviderId = preferred.Id,
        };

        // No SetAgentPersonaRoster call: the roster this run sees is empty.
        var (launcher, planner) = BuildLauncher(
            rosterProvider: preferred, pinnedPersona: researcher);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("digest", AgentRunTrigger.Schedule, PersonaId: researcher.Id),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Same(researcher, planner.PlanPersona);
        // The pin also brings its preferred provider, which the stub chat records.
        var chat = await _chats.GetAsync(handle.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(preferred.Id, chat!.ProviderId);
    }

    /// <summary>The pin decides the STEP TURNS too, not only the plan: the executor composes every turn's
    /// system prompt from the run persona it is seeded with.</summary>
    [Fact]
    public async Task AJobsPinnedPersona_AlsoRunsTheTurns_NotOnlyThePlan()
    {
        var researcher = new Persona { Id = Guid.NewGuid(), Name = "Researcher", SystemPrompt = "research" };
        var (launcher, _) = BuildLauncher(pinnedPersona: researcher);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("digest", AgentRunTrigger.Schedule, PersonaId: researcher.Id),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The stamp on the persisted reply is the persona that turn was composed from.
        var chat = await _chats.GetAsync(handle.ChatId, TestContext.Current.CancellationToken);
        var reply = Assert.Single(chat!.Messages, m => m.Role == "assistant");
        Assert.Equal(researcher.Id, reply.Persona!.Id);
    }

    /// <summary>A deleted persona must not retire a daily routine: the run falls back to the mode persona and
    /// still COMPLETES.</summary>
    [Fact]
    public async Task AJobsDanglingPersonaPin_FallsBackAndTheRunStillCompletes()
    {
        var (launcher, planner) = BuildLauncher();

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("digest", AgentRunTrigger.Schedule, PersonaId: Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotNull(planner.PlanPersona);
        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, run!.State);
    }

    [Fact]
    public async Task AJobsEffortPin_BeatsThePersonas_AndThePersonasAppliesWhenThereIsNoPin()
    {
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys", ReasoningEffort = ReasoningEffort.High };

        var (pinned, pinnedPlanner) = BuildLauncher(pinnedPersona: persona);
        var pinnedHandle = await pinned.LaunchAsync(
            new HeadlessRunRequest("digest", AgentRunTrigger.Schedule,
                PersonaId: persona.Id, ReasoningEffort: ReasoningEffort.Minimal),
            TestContext.Current.CancellationToken);
        await pinnedHandle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.Minimal, pinnedPlanner.PlanProvider!.ReasoningEffort);

        var (unpinned, unpinnedPlanner) = BuildLauncher(pinnedPersona: persona);
        var unpinnedHandle = await unpinned.LaunchAsync(
            new HeadlessRunRequest("digest", AgentRunTrigger.Schedule, PersonaId: persona.Id),
            TestContext.Current.CancellationToken);
        await unpinnedHandle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(ReasoningEffort.High, unpinnedPlanner.PlanProvider!.ReasoningEffort);
    }

    /// <summary>A permit is released only after <c>RunAsync</c> returns, so a child sharing the parent pool
    /// deadlocks behind two blocked parents; this fact TIMES OUT rather than failing an assertion.</summary>
    [Fact]
    public async Task LaunchChild_UsesASeparatePool_SoAChildRunsWhileBothParentSlotsAreHeld()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (launcher, planner) = BuildLauncher(onPlan: () => release.Task);

        var p1 = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var p2 = await launcher.LaunchAsync(new HeadlessRunRequest("b", AgentRunTrigger.User), TestContext.Current.CancellationToken);

        // Both permits are provably held: two runs are inside the planner, blocked on the gate.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (planner.Concurrent < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(2, planner.Concurrent);

        // A child now dispatched must still run. Its own planner call blocks on the same gate, so "it ran" is
        // observable as the concurrency count reaching THREE — which the shared 2-permit pool cannot produce.
        var parentRoot = Path.Combine(_dir, "child-pool-workspace");
        Directory.CreateDirectory(parentRoot);
        var child = await launcher.LaunchChildAsync(
            new HeadlessRunRequest("c", AgentRunTrigger.User), Guid.NewGuid(), null, parentRoot,
            ct: TestContext.Current.CancellationToken);

        deadline = DateTime.UtcNow.AddSeconds(10);
        while (planner.Concurrent < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(3, planner.Concurrent);

        release.SetResult();
        await Task.WhenAll(p1.Completion, p2.Completion, child.Completion)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        foreach (var h in new[] { p1, p2 })
            try { Directory.Delete(Path.Combine(_runsBase, h.RunId.ToString()), true); } catch { }
    }

    /// <summary><c>CancelAsync</c> is a silent no-op for a run this process is not running, which is why the
    /// orchestrator also settles such a row directly.</summary>
    [Fact]
    public async Task CancelAsync_CancelsOneInFlightRun_AndIsANoOpForAnUnknownId()
    {
        var planEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (launcher, planner) = BuildLauncher();
        planner.OnPlanWithToken = async ct =>
        {
            planEntered.TrySetResult();
            using var registration = ct.Register(() => planCancelled.TrySetResult());
            await planCancelled.Task;
        };

        // Never throws, records nothing, and above all does not fault the caller.
        await launcher.CancelAsync(Guid.NewGuid());

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await planEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await launcher.CancelAsync(handle.RunId);

        // The run's OWN token fired — the only in-process evidence that the dispatch was cancelled.
        await planCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var run = await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, run!.State);

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    /// <summary>Teardown is keyed on workspace ownership, not run id: deleting a child's stub chat would otherwise
    /// <c>git worktree remove</c> a directory the parent and its still-running siblings own.</summary>
    [Fact]
    public async Task AChildsChatDeletion_DoesNotTearDownAWorkspace_ButAParentsStillDoes()
    {
        var workspaces = new FakeRunWorkspaceService(_runsBase);
        var (launcher, _) = BuildLauncher(workspaces: workspaces);

        var parent = await launcher.LaunchAsync(new HeadlessRunRequest("p", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await parent.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var child = await launcher.LaunchChildAsync(
            new HeadlessRunRequest("c", AgentRunTrigger.User), parent.RunId, null,
            workspaces.RootFor(parent.RunId), ct: TestContext.Current.CancellationToken);
        await child.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await _chats.DeleteAsync(child.ChatId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(child.RunId, workspaces.TornDown);

        await _chats.DeleteAsync(parent.ChatId, TestContext.Current.CancellationToken);
        await workspaces.TornDownOnce.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(parent.RunId, workspaces.TornDown);
    }

    /// <summary><c>ResumeAsync</c> provisions at its own run id, so pressing Continue on a parked child's chat
    /// would otherwise create a second workspace that diverges from the parent's and outlives it.</summary>
    [Fact]
    public async Task ResumedChild_ReEntersTheParentsWorkspace_AndNeverProvisionsAtItsOwnId()
    {
        var workspaces = new FakeRunWorkspaceService(_runsBase);
        var verifier = new FakeVerifier();
        var (launcher, _) = BuildLauncher(verifier: verifier, workspaces: workspaces);

        var parent = await launcher.LaunchAsync(new HeadlessRunRequest("p", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await parent.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var parentRoot = workspaces.RootFor(parent.RunId);
        Directory.CreateDirectory(parentRoot); // the parent's workspace, still standing while its children work

        var parked = await ParkRunWithPendingStepAsync(policyJson: null, parentRunId: parent.RunId);
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        // The provisioner was never asked about the child's id (it was asked about the parent's, at launch).
        Assert.DoesNotContain(parked.Id, workspaces.Provisioned);
        Assert.False(Directory.Exists(Path.Combine(_runsBase, parked.Id.ToString())));

        // Non-vacuity: a resume that dispatched nothing would satisfy both assertions above for free.
        Assert.Single(verifier.SeenWorkspaceRoots);
        Assert.Equal(SafeFolderPath.Canonicalize(parentRoot), verifier.SeenWorkspaceRoots[0]);
    }

    /// <summary>Parent and child are always in the same isolation regime: a child must never provision a
    /// workspace of its own to "fix" a parent that has none.</summary>
    [Fact]
    public async Task AChildOfAnUnisolatedParentIsAlsoUnisolated_AndProvisionsNothing()
    {
        var workspaces = new FakeRunWorkspaceService(_runsBase);
        var (launcher, planner) = BuildLauncher(workspaces: workspaces);

        var handle = await launcher.LaunchChildAsync(
            new HeadlessRunRequest("c", AgentRunTrigger.User), Guid.NewGuid(), null, parentWorkspaceRoot: null,
            ct: TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Empty(workspaces.Provisioned);
        Assert.False(Directory.Exists(Path.Combine(_runsBase, handle.RunId.ToString())));
        Assert.NotNull(planner.PlanContext);
        Assert.Null(planner.PlanContext!.WorkspaceRoot);
        Assert.Equal(AgentRunState.Completed, (await _runs.GetAsync(handle.RunId, TestContext.Current.CancellationToken))!.State);
    }

    /// <summary>Creates a run row in a given state plus a workspace directory of a given age — the two inputs the
    /// sweep's retention predicate reads.</summary>
    private async Task<Guid> SeedRunWithAgedWorkspaceAsync(AgentRunState state, int ageDays)
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

        switch (state)
        {
            case AgentRunState.Failed:
                await _runs.FailAsync(run.Id, "boom", cancelled: false, ct);
                break;
            case AgentRunState.Cancelled:
                await _runs.FailAsync(run.Id, null, cancelled: true, ct);
                break;
            case AgentRunState.Completed:
                await _runs.CompleteAsync(run.Id, ct: ct);
                break;
            case AgentRunState.WaitingForInput:
                await _runs.PauseAsync(run.Id, "step-cap", ct);
                break;
            default:
                await _runs.SetStateAsync(run.Id, state, ct);
                break;
        }

        var dir = Path.Combine(_runsBase, run.Id.ToString());
        Directory.CreateDirectory(dir);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-ageDays));
        return run.Id;
    }

    /// <summary>A settled run's workspace is kept only long enough to answer the publish offer; anything
    /// non-terminal keeps the 30-day floor, because it may still hold the only copy of resumable work.</summary>
    [Fact]
    public async Task Sweep_KeepsANonTerminalRunsWorkspace_ButRemovesASettledOneAfterTheTerminalWindow()
    {
        var (launcher, _) = BuildLauncher();

        // (a) no run row at all → removed immediately, unchanged behaviour.
        var orphan = Path.Combine(_runsBase, Guid.NewGuid().ToString());
        Directory.CreateDirectory(orphan);
        // (b) settled, past the terminal window → removed.
        var staleFailed = await SeedRunWithAgedWorkspaceAsync(AgentRunState.Failed, ageDays: 8);
        // (c) settled, inside the terminal window → kept: the publish offer is still live.
        var freshFailed = await SeedRunWithAgedWorkspaceAsync(AgentRunState.Failed, ageDays: 1);
        // (d) NON-terminal and older than the terminal window → kept on the 30-day floor.
        var parked = await SeedRunWithAgedWorkspaceAsync(AgentRunState.WaitingForInput, ageDays: 8);

        await launcher.RunStartupSweepAsync(TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(orphan));
        Assert.False(Directory.Exists(Path.Combine(_runsBase, staleFailed.ToString())));
        Assert.True(Directory.Exists(Path.Combine(_runsBase, freshFailed.ToString())));
        Assert.True(Directory.Exists(Path.Combine(_runsBase, parked.ToString())));
    }

    /// <summary>A run's workspace holds the only copy of its un-promoted work, so a chat deletion must cancel a
    /// live dispatch before tearing it down — off the synchronous event handler, since it may spawn git.</summary>
    [Fact]
    public async Task ChatDeleted_CancelsAnInFlightRunBeforeTearingDownItsWorkspace()
    {
        var planEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspaces = new FakeRunWorkspaceService(_runsBase);

        // The planner reports whether its own token was cancelled — the only in-process evidence that the run's
        // CTS fired, since the teardown after it is fire-and-forget.
        var (launcher, planner) = BuildLauncher(workspaces: workspaces);
        planner.OnPlanWithToken = async ct =>
        {
            planEntered.TrySetResult();
            using var registration = ct.Register(() => planCancelled.TrySetResult());
            await planCancelled.Task;
        };

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await planEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await _chats.DeleteAsync(handle.ChatId, TestContext.Current.CancellationToken);

        // Both halves, each awaited rather than polled (xUnit1031: no.Result /.Wait in a test body).
        await planCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await workspaces.TornDownOnce.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(handle.RunId, workspaces.TornDown);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary><c>Cancel</c> returns while the step is still inside a <c>write_file</c>, which is exactly when
    /// <c>git worktree remove</c> fails — so the teardown awaits the dispatch task, not just the cancel.</summary>
    [Fact]
    public async Task ChatDeleted_AwaitsTheCancelledDispatchsUnwind_BeforeTearingDownItsWorkspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var planEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The test's grip on the dispatch: the step has observed its cancellation but has NOT unwound yet, which
        // is the window a workspace removal must not run in.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspaces = new FakeRunWorkspaceService(_runsBase);

        var (launcher, planner) = BuildLauncher(workspaces: workspaces);
        planner.OnPlanWithToken = async ct2 =>
        {
            planEntered.TrySetResult();
            using var registration = ct2.Register(() => planCancelled.TrySetResult());
            await planCancelled.Task;
            await release.Task;
        };

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), ct);
        await planEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        await _chats.DeleteAsync(handle.ChatId, ct);
        await planCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The cancel has landed and the dispatch is still in the step: nothing may have been removed yet.
        Assert.Empty(workspaces.TornDown);

        release.TrySetResult();

        await workspaces.TornDownOnce.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Contains(handle.RunId, workspaces.TornDown);

        await launcher.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------------------------------------------
    // The two steering asymmetries this launcher owns: chat delete REVOKES a pending pause, shutdown does not.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>Records every steering call in order: the chat delete takes the run row with it by FK cascade, so
    /// the call order is the only observable left.</summary>
    private sealed class RecordingSteeringStore : IRunSteeringStore
    {
        private readonly RunSteeringStore _inner = new();
        private readonly List<string> _log = new();

        /// <summary>Snapshot, taken under the same lock every append uses.</summary>
        public List<string> Log
        {
            get { lock (_log) return _log.ToList(); }
        }

        public int ConsumedTrue { get; private set; }

        private void Add(string entry)
        {
            lock (_log) _log.Add(entry);
        }

        public void RegisterDispatch(Guid runId, Action cancel)
        {
            Add("register");
            _inner.RegisterDispatch(runId, cancel);
        }

        public void ReleaseDispatch(Guid runId, Action ownCancel)
        {
            Add("release");
            _inner.ReleaseDispatch(runId, ownCancel);
        }

        public bool RecordPauseRequest(Guid runId)
        {
            var ok = _inner.RecordPauseRequest(runId);
            Add(ok ? "record" : "record-refused");
            return ok;
        }

        public void FireCancel(Guid runId)
        {
            Add("fire");
            _inner.FireCancel(runId);
        }

        public bool TryConsumePauseRequest(Guid runId)
        {
            var consumed = _inner.TryConsumePauseRequest(runId);
            if (consumed) ConsumedTrue++;
            Add(consumed ? "consume" : "consume-empty");
            return consumed;
        }

        public void RevokePauseRequest(Guid runId)
        {
            Add("revoke");
            _inner.RevokePauseRequest(runId);
        }

        // Deliberately not logged: these facts assert the ORDER of the record/fire/revoke/consume calls, and the
        // fan-out mark is unrelated dispatch bookkeeping.
        public void BeginFanOut(Guid runId) => _inner.BeginFanOut(runId);

        public void EndFanOut(Guid runId) => _inner.EndFanOut(runId);

        public bool IsFanningOut(Guid runId) => _inner.IsFanningOut(runId);

        /// <summary>Not part of the interface: appended to the same log so the two orderings are comparable, from
        /// a <c>CancellationToken.Register</c> callback that runs inside <c>Cts.Cancel</c>.</summary>
        public void NoteDispatchCancelled() => Add("cancelled");
    }

    /// <summary>Deleting the chat is terminal intent, so an unconsumed pause must be revoked before the dispatch
    /// is cancelled — otherwise the unwinding loop reads that cancel as a pause and the run comes back alive.</summary>
    [Fact]
    public async Task ChatDelete_RevokesAPendingPause_BeforeCancellingTheDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RecordingSteeringStore();
        var planEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (launcher, planner) = BuildLauncher(steering: store);
        planner.OnPlanWithToken = async ct2 =>
        {
            planEntered.TrySetResult();
            // Appends to the SAME log the store writes, which is the only way the two orderings are comparable.
            using var registration = ct2.Register(store.NoteDispatchCancelled);
            await release.Task;
        };

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), ct);
        await planEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // A pause the user asked for that no step has consumed yet — accepted, so the revoke has something real
        // to revoke (a refused record would make this fact vacuous).
        Assert.True(store.RecordPauseRequest(handle.RunId));

        await _chats.DeleteAsync(handle.ChatId, ct);

        var log = store.Log;
        var recorded = log.IndexOf("record");
        var cancelled = log.IndexOf("cancelled");
        var revoked = log.FindIndex(recorded + 1, e => e == "revoke");
        Assert.True(recorded >= 0 && cancelled > recorded, $"the delete did not cancel the dispatch: [{string.Join(",", log)}]");
        Assert.True(revoked >= 0 && revoked < cancelled, $"the revoke must precede the cancel: [{string.Join(",", log)}]");

        // Let the dispatch unwind and prove the request is really gone rather than merely unread: the loop's
        // catch(OperationCanceledException) arm consumes on this path, and it must come back empty.
        release.TrySetResult();
        await launcher.StopAsync(CancellationToken.None);
        Assert.Equal(0, store.ConsumedTrue);
        Assert.Null(await _runs.GetAsync(handle.RunId, ct)); // the chat took its run with it (FK cascade)

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    /// <summary>Shutdown deliberately does not revoke, so an unconsumed pause is honoured; the pause is recorded
    /// straight on the store because <c>IAgentRunSteeringService</c> would also fire the cancel.</summary>
    [Fact]
    public async Task Shutdown_DoesNotRevokeAPendingPause_SoTheRunComesBackResumable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RunSteeringStore();
        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (launcher, planner) = BuildLauncher(
            steering: store, stream: token => HoldInsideTheStep(stepEntered, token));
        planner.Steps = 2; // a REAL plan, so the run reaches the drain loop instead of the single-turn degrade

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), ct);
        await stepEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // The row is Running with a step in flight, the only state a user pause is legal from — asserted so a
        // change that parks the run elsewhere cannot make this fact pass through the Planning hole.
        var mid = await _runs.GetAsync(handle.RunId, ct);
        Assert.Equal(AgentRunState.Running, mid!.State);
        Assert.True(store.RecordPauseRequest(handle.RunId));

        await launcher.StopAsync(CancellationToken.None);

        var final = await _runs.GetAsync(handle.RunId, ct);
        Assert.Equal(AgentRunState.Paused, final!.State);                                    // not Cancelled
        Assert.Null(final.CompletedAt);                                                     // not settled
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(final));  // a USER pause
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Pending, s.Status));        // the step went back
        var next = await _runs.NextPendingStepAsync(handle.RunId, ct);
        Assert.Equal("S0", next!.Title);                                                     // …and is drainable

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
    }

    /// <summary>Holds a dispatch in the resume ramp-up: <c>Register</c> is the last statement before
    /// <c>RunAsync</c>, so the row reads <c>Running</c> with the new dispatch's sink installed and unused.</summary>
    private sealed class RampUpGate : IExecutingRunStore
    {
        private readonly ExecutingRunStore _inner = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Named apart from <see cref="Release(Guid)"/>, which is the interface's own member.</summary>
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Armed { get; set; }

        public void Register(Guid chatId, Guid runId)
        {
            _inner.Register(chatId, runId);
            if (!Armed) return;

            Entered.TrySetResult();
            Assert.True(Opened.Task.Wait(TimeSpan.FromSeconds(30)), "the ramp-up gate was never released");
        }

        public void Release(Guid runId) => _inner.Release(runId);

        public bool IsExecuting(Guid chatId) => _inner.IsExecuting(chatId);

        public bool IsAnyExecuting => _inner.IsAnyExecuting;

        public bool IsAnyExecutingExcept(Guid runId) => _inner.IsAnyExecutingExcept(runId);

        public Guid? GetChatId(Guid runId) => _inner.GetChatId(runId);
    }

    /// <summary>Continue, then Pause a beat later. The launcher's own order is half the claim: moving
    /// <c>RegisterDispatch</c> after <c>Task.Run</c> would make the pause below refused rather than accepted.</summary>
    [Fact]
    public async Task APauseInTheResumeRampUp_LeavesTheRunPausedAndResumable_NotCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RunSteeringStore();
        var gate = new RampUpGate();
        var (launcher, _) = BuildLauncher(steering: store, executing: gate);
        var steering = new AgentRunSteeringService(_runs, store, NullLogger<AgentRunSteeringService>.Instance);

        var parked = await ParkRunWithPendingStepAsync(policyJson: null);
        await _runs.SetStateAsync(parked.Id, AgentRunState.Running, ct);
        Assert.True(await _runs.TryPauseUserAsync(parked.Id, ct)); // a genuine USER pause, through the real CAS

        gate.Armed = true;
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: ct));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // THE WINDOW, asserted rather than assumed: the row reads Running (so CanPause is true and the button
        // is live) while the loop has not started.
        Assert.Equal(AgentRunState.Running, (await _runs.GetAsync(parked.Id, ct))!.State);
        Assert.True(await steering.PauseAsync(parked.Id, ct)); // accepted — the resume's sink is registered

        gate.Opened.TrySetResult();
        await launcher.StopAsync(CancellationToken.None); // drains the dispatch task; deliberately does not revoke

        var final = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.Paused, final!.State);                                    // NOT Cancelled
        Assert.NotEqual(AgentRunState.Cancelled, final.State);
        Assert.Null(final.CompletedAt);                                                      // not settled
        Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(final));  // a USER pause …
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Pending, s.Status));        // … with its work kept
        Assert.Equal("S1", (await _runs.NextPendingStepAsync(parked.Id, ct))!.Title);
        Assert.True(await _runs.TryResumeFromPauseAsync(parked.Id, ct));                     // claimable again

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    /// <summary>Signals that a step is in flight, then holds it there until its own token is cancelled.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> HoldInsideTheStep(
        TaskCompletionSource entered, [EnumeratorCancellation] CancellationToken ct)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
