using System.IO;
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

namespace Pia.Tests.Services;

/// <summary>
/// §17.1/17.5 headless "Run in background" launcher: stub-chat-first FK order (G-3), Planned+User run,
/// isolated per-run workspace, resolved persona/provider passed to the orchestrator, default write
/// grants, a shared concurrency cap, and shutdown/cleanup lifecycle (G-4, decision c/d).
/// </summary>
public sealed class HeadlessRunLauncherTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly string _runsBase;

    /// <summary>The real A2 launch-bracket index — the launcher's registrations are asserted through it.</summary>
    private readonly ExecutingRunStore _executing = new();

    // Gate that a FakePlanner blocks on, letting a test hold a run inside the orchestrator to probe
    // concurrency / shutdown.
    private sealed class FakePlanner : IAgentPlanner
    {
        private readonly Func<Task>? _onPlan;
        public int Concurrent;
        public int MaxConcurrent;
        private readonly object _lock = new();

        /// <summary>
        /// The <see cref="RunContext"/> the orchestrator handed the planner, captured so a test can read
        /// <c>ctx.WorkspaceRoot</c> — the value the executor published in BeginRunAsync (Batch 06 B3) and
        /// therefore the value the launcher passed to <c>Initialize</c>. PlanAsync runs AFTER
        /// BeginRunAsync (AgentRunOrchestrator.cs:73 then :91), so the root is already assigned here.
        /// Null when the planner was never called (a resume skips planning) — check before reading it.
        /// </summary>
        public RunContext? PlanContext { get; private set; }

        /// <summary>
        /// Like the ctor hook, but handed the run's OWN cancellation token (Batch 06 G4): a fact about
        /// "the dispatch was cancelled" has no other in-process evidence to read, since the teardown that
        /// follows the cancel is fire-and-forget and says nothing about it. Settable after construction
        /// because <c>BuildLauncher</c> hands the planner back before the launch.
        /// </summary>
        public Func<CancellationToken, Task>? OnPlanWithToken { get; set; }

        /// <summary>
        /// Batch 08 G4: how many REAL steps to plan. Zero (the default) keeps every pre-existing fact in this
        /// file on <see cref="PlanResult.Fallback"/>, i.e. the single-turn degrade — which is also why no run
        /// built here could previously reach the drain loop, park, or be paused.
        /// </summary>
        public int Steps { get; set; }

        public FakePlanner(Func<Task>? onPlan = null) => _onPlan = onPlan;

        public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanContext = ctx;
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

    /// <summary>
    /// Drives ONE tool call through the executor's unattended grant gate and records the outcome, so a
    /// test can observe the grant set a launch or a resume actually handed to the executor (D1/A1) instead
    /// of trusting a constant.
    /// </summary>
    private sealed class ToolProbe
    {
        public ToolProbe(string toolName) => ToolName = toolName;

        public string ToolName { get; }
        public bool Executed { get; private set; }
        public string? GateResult { get; private set; }

        public void MarkExecuted() => Executed = true;
        public void Record(object? gateResult) => GateResult = gateResult as string;
    }

    /// <param name="appSettings">Trailing and defaulted (Batch 04): the autonomy tests need a launcher whose
    /// settings have <c>AgentRunAutoApproveBuiltInWrites</c> on, and every existing call site keeps compiling.</param>
    /// <param name="verifier">Trailing and defaulted (Batch 06 G2), same precedent as <paramref name="appSettings"/>:
    /// a resume skips planning, so the verify pass is the only place the resumed run's
    /// <c>ctx.WorkspaceRoot</c> is observable. Pass a FakeVerifier the test holds to read it back; omit
    /// for the default accept-everything instance every other test wants.</param>
    /// <param name="workspaces">Trailing and defaulted (Batch 06 G3). Omitted ⇒ the LEGACY shape — no
    /// provisioner, so the launcher does its own <c>CreateDirectory</c> under the <c>try/catch → FailAsync</c>
    /// guard — which is what every other test in this file exercises.</param>
    /// <param name="runsBaseOverride">Trailing and defaulted (Batch 06 G3): lets one test point the runs base
    /// at an UNWRITABLE path (a file) to prove the legacy settle path still fires.</param>
    /// <param name="rosterPersona">Trailing and defaulted (Phase 3 fix pass): a roster persona the store can
    /// resolve, so a fact can see whether <c>LaunchChildAsync</c>'s <c>personaId</c> really reaches the run's
    /// persona-and-provider ladder. Omitted ⇒ the store resolves no persona by id, i.e. every other test.</param>
    /// <param name="rosterProvider">The provider <paramref name="rosterPersona"/> prefers, registered with the
    /// provider store. The child's stub CHAT records the resolved provider id, which is how the ladder's answer
    /// is observable at all from outside.</param>
    /// <param name="steering">Trailing and defaulted (Batch 08 G4): the steering registry, registered with the
    /// per-run scope as well so the run's own orchestrator reads the SAME instance the launcher writes.
    /// Omitted ⇒ no registry anywhere, i.e. the pre-Batch-08 launcher every other fact here exercises.</param>
    /// <param name="stream">Trailing and defaulted (Batch 08 G4): replaces <see cref="Drive"/> so a fact can
    /// hold a run INSIDE a step (the state a pause is legal from) instead of only inside the planner. Handed the
    /// step's own token.</param>
    private (HeadlessRunLauncher Launcher, FakePlanner Planner) BuildLauncher(
        Func<Task>? onPlan = null, bool nullDefaultProvider = false, ToolProbe? probe = null,
        AppSettings? appSettings = null, FakeVerifier? verifier = null,
        FakeRunWorkspaceService? workspaces = null, string? runsBaseOverride = null,
        Persona? rosterPersona = null, AiProvider? rosterProvider = null,
        IRunSteeringStore? steering = null,
        Func<CancellationToken, IAsyncEnumerable<ChatStreamItem>>? stream = null,
        IExecutingRunStore? executing = null)
    {
        // Trailing-optional and defaulted to the shared field, so every existing call site is untouched. The
        // one fact that overrides it needs a SEAM between the resume's RegisterDispatch and RunAsync, and
        // Register is the last statement before the orchestrator is entered (Batch 08 F3).
        var executingRuns = executing ?? _executing;
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new FakePlanner(onPlan);

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => stream is not null
                ? stream(ci.ArgAt<CancellationToken>(6))
                : probe is null
                    ? Drive()
                    : DriveWithToolCall(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), probe));

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
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(nullDefaultProvider ? (AiProvider?)null : provider);
        if (rosterProvider is not null)
            providers.GetProviderAsync(rosterProvider.Id).Returns(rosterProvider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings ?? new AppSettings());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAiClientService>(ai);
        services.AddSingleton<IPluginService>(plugins);
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
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        // A2: the same index the launcher below is given, because the per-run scope resolves
        // HeadlessTurnExecutor -> concrete BackgroundAssistantTurnRunner, which now requires it. Omit this
        // and the resolve throws inside the launcher's dispatch task, which is swallowed there.
        services.AddSingleton<IExecutingRunStore>(executingRuns);
        // Batch 08: the loop needs the SAME registry the launcher registers its sink with, or it can never
        // consume the request the launcher's dispatch made possible (the orchestrator's parameter is
        // trailing-optional, so an unregistered store is silently "no steering" — the pre-Batch-08 loop).
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
        Func<FunctionCallContent, Task<object?>>? handler, ToolProbe probe)
    {
        await Task.Yield();
        if (handler is not null)
            probe.Record(await handler(new FunctionCallContent("call-1", probe.ToolName, new Dictionary<string, object?>())));
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }

    /// <summary>Persist a stub chat + a parked (WaitingForInput) Planned run carrying one Pending step.</summary>
    /// <param name="parentRunId">Batch 07: makes the parked run a CHILD, which is the shape §7.6 change 3 is
    /// about — every child owns a stub chat, so a user can press Continue on one.</param>
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

        // Assert terminality rather than just giving up at the deadline: a caller that only asserts a tool
        // was NOT executed would otherwise pass VACUOUSLY when the resume dispatched no step at all (e.g. a
        // pre-dispatch failure re-parks the run) — the grant-refusal legs would report green on a resume
        // path that is entirely broken.
        Assert.Contains(state, new[] { AgentRunState.Completed, AgentRunState.Failed, AgentRunState.Cancelled });

        // Drains the resume task (StopAsync awaits every in-flight run) so nothing touches the
        // SqliteContext after the test disposes it.
        await launcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Launch_PersistsStubChat_CreatesPlannedUserRun_AndWorkspace()
    {
        var (launcher, _) = BuildLauncher();

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken);

        // FK order (G-3): the parent chat exists (the run's CreateAsync would have failed otherwise).
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

    /// <summary>
    /// T-G2-1, <b>REGRESSION</b>. Batch 06 G2's launch half: the launch dispatch hands the executor the
    /// run's workspace root instead of <c>null</c>, so every file operation the run performs resolves
    /// inside <c>runs\&lt;runId&gt;</c>. Asserted at the seam that changed rather than by driving a real
    /// <c>write_file</c> through the launcher (§9.2's scoping note): at G2 the workspace is empty and this
    /// harness has no provider that emits a tool call, so the observable value is the one G1 published —
    /// <c>ctx.WorkspaceRoot</c>, read here off the RunContext the orchestrator handed the planner.
    /// <para>
    /// Reverting the launch call site to <c>workspaceRoot: null</c> turns this red while
    /// <see cref="Resume_InitializesTheExecutorWithTheSameRunWorkspaceRoot"/> stays green — the crossed
    /// pattern is the evidence the two call sites are covered by one fact each.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Launch_InitializesTheExecutorWithTheRunWorkspaceRoot()
    {
        var (launcher, planner) = BuildLauncher();

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("do the thing", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Canonicalized, exactly as the launcher canonicalizes it — GetTempPath can carry an 8.3 or a link
        // component, so a raw Path.Combine expectation would compare two spellings of the same directory.
        // Computed while the workspace still exists (Canonicalize needs a real handle).
        var expected = SafeFolderPath.Canonicalize(Path.Combine(_runsBase, handle.RunId.ToString()));

        // Non-vacuity control: a planner that was never called leaves PlanContext null, and the claim below
        // would be about nothing at all.
        Assert.NotNull(planner.PlanContext);
        Assert.Equal(expected, planner.PlanContext!.WorkspaceRoot);

        try { Directory.Delete(expected, true); } catch { }
    }

    /// <summary>
    /// T-G2-2, <b>REGRESSION</b>. Batch 06 G2's resume half — a SEPARATE literal from the launch call site,
    /// which has drifted from it before, hence its own fact: a resumed run re-enters the SAME isolated
    /// workspace it was parked in. A resume deliberately does not re-plan (D1), so the planner never sees
    /// the context; the terminal verify pass is the only place <c>ctx.WorkspaceRoot</c> is still observable
    /// (the per-step ambient that also carries it is restored in each step's <c>finally</c>).
    /// </summary>
    [Fact]
    public async Task Resume_InitializesTheExecutorWithTheSameRunWorkspaceRoot()
    {
        var verifier = new FakeVerifier();
        var (launcher, planner) = BuildLauncher(verifier: verifier);
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);

        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        var expected = SafeFolderPath.Canonicalize(Path.Combine(_runsBase, parked.Id.ToString()));

        // The resumed dispatch drained its Pending remainder and reached the critic — without this the
        // assertion below would be indexing an empty list, i.e. the fact would pass on a resume that never
        // executed anything. Assert.Single is the non-vacuity control.
        Assert.Single(verifier.SeenWorkspaceRoots);
        Assert.Equal(expected, verifier.SeenWorkspaceRoots[0]);
        Assert.Null(planner.PlanContext); // pins D1: a resume does not re-plan

        try { Directory.Delete(expected, true); } catch { }
    }

    /// <summary>
    /// Batch 08 G3. The resume claim is now dispatched on the row's STATE, over an explicit two-member set and
    /// never a range (D7): a budget park is <c>WaitingForInput</c> → <c>TryBeginResumeAsync</c>, a USER pause is
    /// <c>Paused</c> → <c>TryResumeFromPauseAsync</c>, anything else is refused. Every other resume fact in this
    /// file covers the first arm; this is the only cover for the second, and without it a swapped or missing arm
    /// would stay green here and surface as a dead Continue button once the UI lands.
    /// <para>
    /// Not listed in the impl spec's §17 file table for G3, which names only the two new test files. Added
    /// anyway: shipping a new switch arm with no fact is the thing the plan's own hazards forbid.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-G3-14a, <b>REGRESSION</b>. Batch 06 B16's first half: a provisioner that cannot isolate the run
    /// returns null — "no isolation", the pre-Batch-06 behaviour — and the run proceeds and settles
    /// <c>Completed</c>. It must NOT be settled <c>Failed</c> with <c>"workspace setup failed"</c>: an
    /// unattended run that fails because a scratch directory could not be prepared delivers nothing, while the
    /// same run writing into the assistant folder delivers exactly what it delivered before this batch (plan
    /// R16 — degrade rather than fail).
    /// </summary>
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

    /// <summary>
    /// T-G3-14b, <b>REGRESSION</b> and the non-vacuity control for the fact above: with no provisioner
    /// injected the LEGACY create path is still in force, and a workspace it cannot create still settles the
    /// run rather than leaving it dangling non-terminal (G-4). Deleting the legacy <c>try/catch → FailAsync</c>
    /// as "unreachable" (B16 says it is unreachable only on the PROVISIONER path) turns this red, which is
    /// what keeps T-G3-14a from passing on a launcher that simply stopped settling failed launches.
    /// </summary>
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

    [Fact]
    public async Task Stop_DuringInFlightRun_SettlesRun_NeverRunning()
    {
        var release = new TaskCompletionSource();
        var (launcher, _) = BuildLauncher(onPlan: () => release.Task);

        var handle = await launcher.LaunchAsync(new HeadlessRunRequest("a", AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // let it enter the planner

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
        // A1: the default grant set for an unattended run drops delete_file. The launch also persists the
        // resolved set as its opaque PolicyJson envelope, which is what a later resume restores (D1).
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
        Assert.Contains("not granted", deleteProbe.GateResult ?? string.Empty);

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
        // A1 narrows the DEFAULT only — an explicit GrantedWrites naming delete_file keeps working, and
        // the envelope records it so a resume restores it too.
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
        // D1 (top-severity): a scheduled job launched with a NARROW grant list that budget-pauses must not
        // silently acquire delete_file on resume — nor even write_file, which its launch never granted.
        var deleteProbe = new ToolProbe("delete_file");
        var (deleteLauncher, _) = BuildLauncher(probe: deleteProbe);
        var narrowEnvelope = HeadlessRunLauncher.SerializeGrantEnvelope(["create_todo"], AgentRunTrigger.Schedule);

        var parked = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await deleteLauncher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(deleteLauncher, parked.Id);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("not granted", deleteProbe.GateResult ?? string.Empty);

        var writeProbe = new ToolProbe("write_file");
        var (writeLauncher, _) = BuildLauncher(probe: writeProbe);
        var parked2 = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await writeLauncher.ResumeAsync(parked2.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(writeLauncher, parked2.Id);

        Assert.False(writeProbe.Executed); // the floor is a FALLBACK, never an addition to a known set
        // Positive counter-assertion: the gate was actually consulted and refused, so this leg cannot pass
        // just because the resume never dispatched a step.
        Assert.Contains("not granted", writeProbe.GateResult ?? string.Empty);

        var grantProbe = new ToolProbe("create_todo");
        var (grantLauncher, _) = BuildLauncher(probe: grantProbe);
        var parked3 = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await grantLauncher.ResumeAsync(parked3.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(grantLauncher, parked3.Id);

        Assert.True(grantProbe.Executed); // exactly what the launch granted still runs

        foreach (var id in new[] { parked.Id, parked2.Id, parked3.Id })
            try { Directory.Delete(Path.Combine(_runsBase, id.ToString()), true); } catch { }
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
        // Missing/garbage envelope (e.g. a run created before D1): resume with {write_file} ONLY — the
        // fallback is a floor, never a ceiling, so delete_file stays refused.
        var deleteProbe = new ToolProbe("delete_file");
        var (deleteLauncher, _) = BuildLauncher(probe: deleteProbe);
        var parked = await ParkRunWithPendingStepAsync(policyJson: null);
        Assert.True(await deleteLauncher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(deleteLauncher, parked.Id);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("not granted", deleteProbe.GateResult ?? string.Empty); // the gate refused it — not "never asked"

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
        // 04 D9: the launch resolves the policy from SETTINGS (it never reads the envelope back) and stores the
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
        // D9's exclusions, as a test.
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
        // 04 D10, the red-before-green for the whole decision: the envelope is the run's authority of record.
        // This run parked with an envelope carrying NO policy and NO grants. The user then turns the setting
        // ON. A resume that consulted settings would hand the parked run card-free write_file; it must not.
        var probe = new ToolProbe("write_file");
        var (launcher, _) = BuildLauncher(
            probe: probe, appSettings: new AppSettings { AgentRunAutoApproveBuiltInWrites = true });

        var policylessEnvelope = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule);
        Assert.Null(HeadlessRunLauncher.TryRestorePolicy(policylessEnvelope));

        var parked = await ParkRunWithPendingStepAsync(policylessEnvelope);
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.False(probe.Executed);
        Assert.Contains("not granted", probe.GateResult ?? string.Empty);

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
        // D6 end-to-end: the Files class covers write_file and delete_file alike, and a POLICY must never be
        // the reason a delete-like tool ran. Only a NAMED grant can do that.
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = BuildLauncher(probe: probe);

        var envelope = HeadlessRunLauncher.SerializeGrantEnvelope(
            [], AgentRunTrigger.Schedule, new RunAutonomyPolicy([ToolClass.Files]));

        var parked = await ParkRunWithPendingStepAsync(envelope);
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(launcher, parked.Id);

        Assert.False(probe.Executed);
        Assert.Contains("not granted", probe.GateResult ?? string.Empty);

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }

    [Fact]
    public async Task Resume_PreDispatchFailure_ReParksRun_ReturnsFalse()
    {
        // Guardrail 1/3: after the CAS claim (WaitingForInput→Running) a pre-dispatch failure (here: no
        // provider resolvable) must re-park the run to WaitingForInput — never leave it dangling Running,
        // unresumable, until the crash sweep cancels it.
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

    [Fact]
    public async Task LaunchedRun_HoldsTheComposerBracket_WhileItExecutes_AndReleasesWhenItEnds()
    {
        // A2's bracket premise for this launcher. ChatSessionManager reads this index SYNCHRONOUSLY when a
        // chat is activated, so it must be open for the whole span in which the executor writes the chat — and
        // empty again afterwards, because a stale entry is a permanently dead composer. Drop either the
        // Register or the Release edit in HeadlessRunLauncher and this fails; a future refactor that
        // dispatched the orchestrator from somewhere other than those two lambdas fails here too.
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

    /// <summary>
    /// T-CHILD-1, <b>REGRESSION</b>. What a child dispatch is: its own stub chat, a run row carrying
    /// <c>ParentRunId</c>, the grant envelope NARROWED from the parent's (never the launch default), and — the
    /// load-bearing half — <b>no workspace of its own</b>. It writes the parent's directory, because Batch 06
    /// allows exactly one promotion per workspace and a per-child worktree would mean N branches per fan-out.
    /// </summary>
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

        // No directory at the CHILD's run id, and the executor was initialized with the PARENT's root — read
        // off the RunContext the orchestrator handed the planner, the same seam T-G2-1 uses.
        Assert.False(Directory.Exists(Path.Combine(_runsBase, handle.RunId.ToString())));
        Assert.NotNull(planner.PlanContext);
        Assert.Equal(parentRoot, planner.PlanContext!.WorkspaceRoot);
    }

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 fix pass). The other end of a delegated step's persona assignment: the child's
    /// RUN persona is the specialist the plan chose, and therefore so is its provider and reasoning effort
    /// (07 D5, "each persona running on its own provider"). Before this the dispatch resolved the GLOBAL
    /// per-mode persona and that persona's provider, so a fan-out ran exactly as if no roster were configured.
    /// <para>
    /// The resolved provider is observable through the child's stub CHAT, which records it — that value can only
    /// be the roster provider if the assigned persona reached <c>ResolveProviderAsync</c>. The second leg is the
    /// containment control and the non-vacuity control in one: the SAME persona, still resolvable, but no longer
    /// on the configured roster, falls back to the mode default. Neutralization: ignore
    /// <c>personaIdOverride</c> in <c>ResolveRunPersonaAsync</c> → the first leg reds.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-CHILD-2, <b>REGRESSION</b>, and the reason D7 exists. The child pool is a SECOND semaphore. With both
    /// permits of the shared pool held by parents that are blocked inside their own runs, a child dispatched on
    /// the shared pool could never start — and neither permit could ever be released, because a permit is
    /// released only after <c>RunAsync</c> RETURNS. That is a permanent deadlock reachable with exactly two
    /// concurrent parents, i.e. the configured cap.
    /// <para>
    /// Change <c>_childSlots</c> back to <c>_slots</c> and this fact does not fail an assertion — it TIMES OUT
    /// on the child's completion, which is what the deadlock actually looks like.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-CHILD-3, <b>REGRESSION</b>. <c>CancelAsync</c> is the cascade's mechanism: it cancels ONE in-flight
    /// dispatch by id and is a silent no-op for a run this process is not running (a child parked in a previous
    /// process), which is exactly why the orchestrator also settles such a row directly.
    /// </summary>
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

    /// <summary>
    /// T-CHILD-4, <b>REGRESSION</b>. Teardown is keyed on WORKSPACE OWNERSHIP, not on run id, so a child's run
    /// id is never entered in the chat→runs index. Deleting a child's stub chat mid-fan-out would otherwise call
    /// <c>TearDownWorkspaceAsync(childId)</c> — which routes through the provisioner and in worktree mode is a
    /// <c>git worktree remove</c> — against a directory the PARENT and its still-running siblings own.
    /// <para>
    /// The second half is the non-vacuity control: a PARENT's chat deletion does still tear its workspace down,
    /// so this cannot pass on a launcher whose teardown simply stopped working.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-CHILD-5, <b>REGRESSION</b> (§7.6 change 3). The hole the rest of §7.6 would leave open:
    /// <c>ResumeAsync</c> is a separate dispatch method that provisions at its OWN run id, and every child owns a
    /// stub chat — so a user opening a parked child's chat and pressing <b>Continue</b> would create a SECOND
    /// workspace at the child's id, diverging from the parent's and outliving it until the sweep. A resumed child
    /// re-enters the PARENT's directory and provisions nothing.
    /// </summary>
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

        // And the resumed child really did run, inside the parent's root. Assert.Single is the non-vacuity
        // control: a resume that dispatched nothing would satisfy both assertions above for free.
        // The parent's own launch DID plan (PlanContext is its context, not the child's), so the
        // resume-does-not-replan claim is T-G2-2's; the fact here is which ROOT the resumed child re-entered.
        Assert.Single(verifier.SeenWorkspaceRoots);
        Assert.Equal(SafeFolderPath.Canonicalize(parentRoot), verifier.SeenWorkspaceRoots[0]);
    }

    /// <summary>
    /// T-CHILD-6, <b>GUARD</b>. The degrade half of §7.6: a child whose parent ran UNISOLATED (or whose parent's
    /// workspace is already gone) gets a null root and writes the assistant folder, exactly as its parent does.
    /// Parent and child are always in the same isolation regime — the child must never provision one of its own
    /// to "fix" a parent that has none.
    /// </summary>
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

    /// <summary>
    /// Creates a chat and a run row in a given terminal/non-terminal state, plus a workspace directory whose
    /// <c>LastWriteTimeUtc</c> is <paramref name="ageDays"/> old — the two inputs the sweep's retention
    /// predicate reads.
    /// </summary>
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

    /// <summary>
    /// T-G4-13, <b>REGRESSION</b>. Batch 06 B12 — plan D3's retention rule and plan R5's mitigation in one
    /// predicate. A SETTLED run's workspace is kept only long enough for the user to answer the publish offer;
    /// anything non-terminal keeps the original 30-day floor because it may still be resumable, and deleting a
    /// resumable run's only copy of its work is the one mistake this sweep must not make.
    /// <para>
    /// Rows (c) and (d) are the non-vacuity controls: a sweep that deleted everything would pass on (a) and
    /// (b) alone.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// T-G4-14, <b>REGRESSION</b>. Plan R4 / B13: after Batch 06 a run's workspace holds the only copy of its
    /// un-promoted work, so a chat deletion must CANCEL a still-live dispatch before tearing that directory
    /// down rather than deleting it under a live writer. Teardown then happens off the synchronous event
    /// handler, because it may spawn a git process.
    /// </summary>
    [Fact]
    public async Task ChatDeleted_CancelsAnInFlightRunBeforeTearingDownItsWorkspace()
    {
        var planEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var planCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workspaces = new FakeRunWorkspaceService(_runsBase);

        // The planner parks the dispatch mid-run and reports whether ITS OWN token was cancelled — the only
        // in-process evidence that the run's CTS fired, since the teardown that follows is fire-and-forget and
        // says nothing about the cancel.
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

        // Both halves, each awaited rather than polled (xUnit1031: no .Result / .Wait() in a test body).
        await planCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await workspaces.TornDownOnce.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(handle.RunId, workspaces.TornDown);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// <b>REGRESSION</b> (Phase 3 consolidation pass — Batch 06 Lens A finding 4's other half). Plan R4 asks for
    /// "cancel first rather than deleting under a live writer", and CANCELLING IS ONLY HALF OF THAT:
    /// <c>Cancel()</c> returns immediately while the step is still inside a <c>write_file</c>, which is exactly
    /// when <c>git worktree remove</c> and the recursive delete both fail — the reachable trigger the finding
    /// named. The teardown now awaits the dispatch task <c>_inflight</c> has always held beside the CTS and
    /// nothing read.
    /// <para>
    /// Deterministic in both directions, and that rests on one measured fact: <c>AssistantChatService</c> raises
    /// <c>ChatsChanged</c> SYNCHRONOUSLY inside <c>DeleteAsync</c>, and the fake's <c>TearDownAsync</c> records
    /// its call before its first await — so in the unfixed build <c>TornDown</c> is already non-empty by the time
    /// <c>DeleteAsync</c> returns. Neutralization: put the <c>Cancel()</c>/fire-and-forget teardown pair back in
    /// <c>OnChatsChanged</c> and drop the await → the first assertion goes red.
    /// </para>
    /// <para>
    /// The bound itself (tear down anyway after a few seconds, because a wedged dispatch must not defer the
    /// cleanup forever) is NOT covered here: the timeout is a private constant and driving it would mean holding
    /// a real dispatch for its whole duration. Reasoned, stated, and left to the same startup sweep that already
    /// collects a teardown that failed.
    /// </para>
    /// </summary>
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
    // Batch 08 G4: the two steering asymmetries this launcher owns — chat delete REVOKES a pending pause,
    // shutdown deliberately does NOT. Not listed in the impl spec's §17 table for G4 (which names only the new
    // live-parity file); they live here because this is where a real launcher with a real dispatch already
    // exists, and lifting that fixture a third time would be worse than two facts on it.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Records every steering call in order, delegating to a real <see cref="RunSteeringStore"/>. The chat-delete
    /// revocation has no other observable: deleting the chat takes the run row with it by FK cascade, so
    /// "the run settled Cancelled rather than Paused" is not assertable — there is no row left to read, and the
    /// pause CAS would have matched zero rows even if the request HAD been honoured. The order the calls
    /// happened in is the claim, so the order is what this reads.
    /// </summary>
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

        // Deliberately NOT logged: the fan-out mark is dispatch bookkeeping, and these facts assert the ORDER
        // of the record/fire/revoke/consume calls. Logging it would make every one of them assert a shape it
        // is not about.
        public void BeginFanOut(Guid runId) => _inner.BeginFanOut(runId);

        public void EndFanOut(Guid runId) => _inner.EndFanOut(runId);

        public bool IsFanningOut(Guid runId) => _inner.IsFanningOut(runId);

        /// <summary>
        /// Not part of the interface: the run token's own cancellation, appended to the SAME log so the two
        /// orderings are comparable at all. Called from a <c>CancellationToken.Register</c> callback, which runs
        /// INSIDE <c>Cts.Cancel()</c>.
        /// </summary>
        public void NoteDispatchCancelled() => Add("cancelled");
    }

    /// <summary>
    /// §5.3 revocation site 3. Deleting the chat is TERMINAL intent — the run row goes with it and its workspace
    /// is about to be removed — so an unconsumed pause request must be revoked BEFORE the dispatch is cancelled.
    /// Without the revoke the unwinding loop reads that cancel as "the user asked to pause", which is the
    /// wrong-direction failure the hardening exists for: a run whose chat the user deleted comes back resumable.
    /// <para>
    /// The ordering is deterministic and rests on two measured facts: <c>AssistantChatService</c> raises
    /// <c>ChatsChanged</c> SYNCHRONOUSLY inside <c>DeleteAsync</c>, and the token registration below runs INSIDE
    /// <c>Cts.Cancel()</c> — so by the time <c>DeleteAsync</c> returns, both the revoke and the cancel are in the
    /// log, in the order the production line put them there. The assertion looks for a revoke AFTER the record
    /// rather than for "a revoke exists" so that it pins the DELETE's revoke specifically and cannot be
    /// satisfied by some other site's; since Batch 08 F3 removed the loop's blind clear-on-entry there is in
    /// fact only one revoke in this log, and the search still asserts it is the right one.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The deliberate asymmetry (§5.3), asserted rather than commented: <c>StopAsync</c> is app shutdown and it
    /// does NOT revoke, so a pause request that is still unconsumed when the shutdown token fires is honoured —
    /// the run comes back <see cref="AgentRunState.Paused"/> and RESUMABLE instead of <c>Cancelled</c>, which is
    /// the recoverable direction and the one the user asked for.
    /// <para>
    /// Deterministic because the consume happens INSIDE the dispatch task, and <c>StopAsync</c> awaits every
    /// dispatch task before returning — so reading the row after the await cannot race
    /// <c>ReleaseDispatch</c> (which is the last thing in the dispatch's <c>finally</c>, after the loop that
    /// consumed). Do not "simplify" that await away.
    /// </para>
    /// <para>
    /// The pause is recorded directly on the store rather than through <c>IAgentRunSteeringService</c> because
    /// the service also FIRES the cancel, and the whole point here is that the SHUTDOWN is what cancels.
    /// </para>
    /// </summary>
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

        // The row is Running with a step in flight — the only state a user pause is legal from, and the state
        // that makes the CAS below win. Asserted so a future change that parks the run somewhere else cannot
        // make this fact pass through the Planning hole (where the CAS loses and writes nothing).
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

    /// <summary>
    /// Holds a dispatch in the RESUME RAMP-UP — the window Batch 08 F3 lives in. <c>IExecutingRunStore.Register</c>
    /// is the last statement before <c>orchestrator.RunAsync</c>, and the resume's <c>RegisterDispatch</c> has
    /// already run by then (it is synchronous, before <c>Task.Run</c>), so a fact holding here has the row at
    /// <c>Running</c>, the panel's Pause live, and the new dispatch's sink installed and unused.
    /// <para>
    /// Armed explicitly, because the LAUNCH path registers here too and a launch must not be gated. Bounded:
    /// a gate nobody releases fails the fact instead of hanging the suite.
    /// </para>
    /// </summary>
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

        public Guid? GetChatId(Guid runId) => _inner.GetChatId(runId);
    }

    /// <summary>
    /// <b>Batch 08 F3, through the REAL launcher</b> — the shape the review executed and printed as
    /// <c>LPROBE1 … FINAL state=Cancelled … resumableFromPaused=False</c>. Continue, then Pause a beat later:
    /// the resume's CAS has the row at <c>Running</c> and its sink registered, so the pause is accepted and
    /// fires THAT dispatch's token. The dispatch must then honour it.
    /// <para>
    /// Before the fix <c>RunAsync</c> revoked any request for its own run id on entry — blindly, unable to tell
    /// a superseded dispatch's leftover from one recorded against itself milliseconds ago — so the cancel stood
    /// with nothing to explain it and the run settled <c>Cancelled</c> with <c>CompletedAt</c> stamped, after
    /// <c>PauseAsync</c> had already told the user it worked. The ownership rule now lives in
    /// <c>RegisterDispatch</c>, which is the instant ownership actually changes hands.
    /// </para>
    /// <para>
    /// The launcher's own ORDER is half the claim, which is why this is not only an orchestrator fact: if
    /// <c>RegisterDispatch</c> ever moved to after <c>Task.Run</c>, the pause below would be refused
    /// (registration-scoped) and the assertion that it was accepted reds.
    /// </para>
    /// </summary>
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
