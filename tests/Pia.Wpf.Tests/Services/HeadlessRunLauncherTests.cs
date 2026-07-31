using System.IO;
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

        public FakePlanner(Func<Task>? onPlan = null) => _onPlan = onPlan;

        public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanContext = ctx;
            lock (_lock) { Concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, Concurrent); }
            try
            {
                if (OnPlanWithToken is not null) await OnPlanWithToken(ct);
                if (_onPlan is not null) await _onPlan();
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
    private (HeadlessRunLauncher Launcher, FakePlanner Planner) BuildLauncher(
        Func<Task>? onPlan = null, bool nullDefaultProvider = false, ToolProbe? probe = null,
        AppSettings? appSettings = null, FakeVerifier? verifier = null,
        FakeRunWorkspaceService? workspaces = null, string? runsBaseOverride = null)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new FakePlanner(onPlan);

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => probe is null
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
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(nullDefaultProvider ? (AiProvider?)null : provider);
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
        services.AddSingleton<IExecutingRunStore>(_executing);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance,
            runsBaseDirOverride: runsBaseOverride ?? _runsBase, workspaces: workspaces);
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
    private async Task<AgentRun> ParkRunWithPendingStepAsync(string? policyJson)
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
                PolicyJson: policyJson), ct);
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

        Assert.True(await launcher.ResumeAsync(parked.Id, TestContext.Current.CancellationToken));
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
        Assert.True(await deleteLauncher.ResumeAsync(parked.Id, TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(deleteLauncher, parked.Id);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("not granted", deleteProbe.GateResult ?? string.Empty);

        var writeProbe = new ToolProbe("write_file");
        var (writeLauncher, _) = BuildLauncher(probe: writeProbe);
        var parked2 = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await writeLauncher.ResumeAsync(parked2.Id, TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(writeLauncher, parked2.Id);

        Assert.False(writeProbe.Executed); // the floor is a FALLBACK, never an addition to a known set
        // Positive counter-assertion: the gate was actually consulted and refused, so this leg cannot pass
        // just because the resume never dispatched a step.
        Assert.Contains("not granted", writeProbe.GateResult ?? string.Empty);

        var grantProbe = new ToolProbe("create_todo");
        var (grantLauncher, _) = BuildLauncher(probe: grantProbe);
        var parked3 = await ParkRunWithPendingStepAsync(narrowEnvelope);
        Assert.True(await grantLauncher.ResumeAsync(parked3.Id, TestContext.Current.CancellationToken));
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
        Assert.True(await deleteLauncher.ResumeAsync(parked.Id, TestContext.Current.CancellationToken));
        await AwaitRunSettledAsync(deleteLauncher, parked.Id);

        Assert.False(deleteProbe.Executed);
        Assert.Contains("not granted", deleteProbe.GateResult ?? string.Empty); // the gate refused it — not "never asked"

        var writeProbe = new ToolProbe("write_file");
        var (writeLauncher, _) = BuildLauncher(probe: writeProbe);
        var parked2 = await ParkRunWithPendingStepAsync(policyJson: "{\"totally\":\"foreign\"}");
        Assert.True(await writeLauncher.ResumeAsync(parked2.Id, TestContext.Current.CancellationToken));
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
        Assert.True(await launcher.ResumeAsync(parked.Id, TestContext.Current.CancellationToken));
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
        Assert.True(await launcher.ResumeAsync(parked.Id, TestContext.Current.CancellationToken));
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
        Assert.True(await launcher.ResumeAsync(parked.Id, TestContext.Current.CancellationToken));
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

        var resumed = await launcher.ResumeAsync(run.Id, TestContext.Current.CancellationToken);

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
}
