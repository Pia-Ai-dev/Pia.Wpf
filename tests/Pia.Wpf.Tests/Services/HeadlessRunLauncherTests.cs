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

    // Gate that a FakePlanner blocks on, letting a test hold a run inside the orchestrator to probe
    // concurrency / shutdown.
    private sealed class FakePlanner : IAgentPlanner
    {
        private readonly Func<Task>? _onPlan;
        public int Concurrent;
        public int MaxConcurrent;
        private readonly object _lock = new();

        public FakePlanner(Func<Task>? onPlan = null) => _onPlan = onPlan;

        public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            lock (_lock) { Concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, Concurrent); }
            try
            {
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

    private (HeadlessRunLauncher Launcher, FakePlanner Planner) BuildLauncher(
        Func<Task>? onPlan = null, bool nullDefaultProvider = false, ToolProbe? probe = null)
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
        settings.GetSettingsAsync().Returns(new AppSettings());

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
        services.AddSingleton<IAgentVerifier>(new FakeVerifier());
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase);
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

        try { Directory.Delete(Path.Combine(_runsBase, handle.RunId.ToString()), true); } catch { }
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
}
