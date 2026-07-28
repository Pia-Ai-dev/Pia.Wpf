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
/// Headless multi-step run (§13.6/§13.12): the executor accumulates ONE chat across steps (goal +
/// one assistant reply per step), and <c>TaskAmbient.TaskId == run.Id</c> throughout the run (R9).
/// Driven through the real orchestrator + real SQLite stores.
/// </summary>
public sealed class HeadlessTurnExecutorTests
{
    private static readonly List<Guid?> ObservedTaskIds = new();

    private sealed class FakePlanner : IAgentPlanner
    {
        private readonly IReadOnlyList<AgentStep> _steps;
        public FakePlanner(IReadOnlyList<AgentStep> steps) => _steps = steps;
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(_steps, false));
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(string answer)
    {
        // Runs inside RunExchangeAsync — the run's TaskAmbient is live here (R9 probe).
        ObservedTaskIds.Add(TaskAmbient.Current?.TaskId);
        await Task.Yield();
        yield return new TextDelta(answer);
        yield return new Finished(null, "test-model");
    }

    [Fact]
    public async Task MultiStep_AccumulatesOneChat_TaskIdIsRunId()
    {
        ObservedTaskIds.Clear();
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var ctx = new SqliteContext(Path.Combine(dir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Drive("reply"));

        var plugins = Substitute.For<IPluginService>();
        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        var engine = new BackgroundAssistantTurnRunner(
            ai, plugins, composer, personas, chats, titles, settings, TokenMapFactory, runs,
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
        var executor = new HeadlessTurnExecutor(
            engine, chats, settings, personas, providers, composer, titles, TokenMapFactory,
            NullLogger<HeadlessTurnExecutor>.Instance);

        // Bootstrap: FK parent chat + Planned run (R1 ordering).
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "stub",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [],
        }, TestContext.Current.CancellationToken);
        var run = await runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "the goal"), TestContext.Current.CancellationToken);

        var planner = new FakePlanner(new List<AgentStep>
        {
            new() { Ordinal = 0, Title = "A", Intent = "ia", Status = AgentStepStatus.Pending },
            new() { Ordinal = 1, Title = "B", Intent = "ib", Status = AgentStepStatus.Pending },
            new() { Ordinal = 2, Title = "C", Intent = "ic", Status = AgentStepStatus.Pending },
        });
        var orchestrator = new AgentRunOrchestrator(runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        await orchestrator.RunAsync(run, executor, persona, provider, RunProfile.Interactive, TestContext.Current.CancellationToken);

        // TaskAmbient.TaskId == run.Id for every exchange (R9).
        Assert.Equal(3, ObservedTaskIds.Count);
        Assert.All(ObservedTaskIds, id => Assert.Equal(run.Id, id));

        // R7/G1: the headless path never offers Agent mode — suggestAgentModeEligible is always false.
        composer.DidNotReceive().PrepareTurn(
            Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(),
            suggestAgentModeEligible: true);

        // Exactly one accumulated chat: goal + 3 assistant replies.
        var ids = await chats.GetAllIdsAsync(TestContext.Current.CancellationToken);
        Assert.Single(ids);
        var persisted = await chats.GetAsync(chatId, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(4, persisted!.Messages.Count);
        Assert.Equal(1, persisted.Messages.Count(m => m.Role == "user"));
        Assert.Equal(3, persisted.Messages.Count(m => m.Role == "assistant"));

        // The run's transcript slice was pinned (SetRunMessageRange) and it Completed.
        var finalRun = await runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, finalRun!.State);
        Assert.NotNull(finalRun.FirstMessageId);
        Assert.NotNull(finalRun.LastMessageId);

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    // ---- C2: consent + MCP disable + provider override ----

    private sealed class SingleStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                new List<AgentStep> { new() { Ordinal = 0, Title = "A", Intent = "ia", Status = AgentStepStatus.Pending } },
                false));
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    [Fact]
    public async Task BeginRun_OffersMcpTools_HonorsProviderOverride_AndExecutesGrantedWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var runRoot = Path.Combine(dir, "runroot");
        Directory.CreateDirectory(runRoot);
        using var ctx = new SqliteContext(Path.Combine(dir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        var defaultProvider = new AiProvider { Id = Guid.NewGuid(), Name = "Default", Endpoint = "https://d", ProviderType = AiProviderType.OpenAI };
        var overrideProvider = new AiProvider { Id = Guid.NewGuid(), Name = "Override", Endpoint = "https://o", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };

        var mcpTool = AIFunctionFactory.Create((string q) => "x", "mcp_search", "mcp");
        var normalTool = AIFunctionFactory.Create((string q) => "y", "write_file", "write");

        IList<AITool>? capturedTools = null;
        AiProvider? capturedProvider = null;
        var toolCalls = new List<FunctionCallContent> { new(Guid.NewGuid().ToString(), "write_file", new Dictionary<string, object?>()) };

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedTools = ci.ArgAt<IList<AITool>?>(2);
                capturedProvider = ci.ArgAt<AiProvider>(1);
                return DriveWithTool(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), toolCalls);
            });

        var executed = false;
        var plugins = Substitute.For<IPluginService>();
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, new PluginToolCall("write_file", Guid.NewGuid(), "files", "d", null, () =>
            {
                executed = true;
                return Task.FromResult<object?>("written");
            })));

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool> { mcpTool, normalTool }, SupportsTools: true, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(defaultProvider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        var engine = new BackgroundAssistantTurnRunner(
            ai, plugins, composer, personas, chats, titles, settings, TokenMapFactory, runs,
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
        var executor = new HeadlessTurnExecutor(
            engine, chats, settings, personas, providers, composer, titles, TokenMapFactory,
            NullLogger<HeadlessTurnExecutor>.Instance);

        // Seed workspace root + grants + provider override (the launcher's job).
        executor.Initialize(runRoot, new[] { "write_file" }, overrideProvider);

        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "stub",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), Messages = [],
        }, TestContext.Current.CancellationToken);
        var run = await runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal"), TestContext.Current.CancellationToken);

        var orchestrator = new AgentRunOrchestrator(runs, new SingleStepPlanner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);
        await orchestrator.RunAsync(run, executor, persona, defaultProvider, RunProfile.Interactive, TestContext.Current.CancellationToken);

        // MCP is now OFFERED to unattended runs (Phase-2 gate): no longer stripped — instead denied inline
        // unless granted. Both tools reach the model.
        Assert.NotNull(capturedTools);
        Assert.Contains(capturedTools!, t => t.Name == "mcp_search");
        Assert.Contains(capturedTools!, t => t.Name == "write_file");

        // Provider override honored — the default was never resolved.
        Assert.Equal(overrideProvider.Id, capturedProvider!.Id);
        await providers.DidNotReceive().GetDefaultProviderForModeAsync(Arg.Any<WindowMode>());

        // Granted write executed.
        Assert.True(executed);

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task UngrantedWrite_IsDenied_InHeadlessRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var runRoot = Path.Combine(dir, "runroot");
        Directory.CreateDirectory(runRoot);
        using var ctx = new SqliteContext(Path.Combine(dir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);

        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var toolCalls = new List<FunctionCallContent> { new(Guid.NewGuid().ToString(), "delete_file", new Dictionary<string, object?>()) };

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => DriveWithTool(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), toolCalls));

        var executed = false;
        var plugins = Substitute.For<IPluginService>();
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, new PluginToolCall("delete_file", Guid.NewGuid(), "files", "d", null, () =>
            {
                executed = true;
                return Task.FromResult<object?>("deleted");
            })));

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        var engine = new BackgroundAssistantTurnRunner(
            ai, plugins, composer, personas, chats, titles, settings, TokenMapFactory, runs,
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
        var executor = new HeadlessTurnExecutor(
            engine, chats, settings, personas, providers, composer, titles, TokenMapFactory,
            NullLogger<HeadlessTurnExecutor>.Instance);

        // Only write_file granted — delete_file is not.
        executor.Initialize(runRoot, new[] { "write_file" }, provider);

        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "stub",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), Messages = [],
        }, TestContext.Current.CancellationToken);
        var run = await runs.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal"), TestContext.Current.CancellationToken);

        var orchestrator = new AgentRunOrchestrator(runs, new SingleStepPlanner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);
        await orchestrator.RunAsync(run, executor, persona, provider, RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.False(executed);

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static async IAsyncEnumerable<ChatStreamItem> DriveWithTool(
        Func<FunctionCallContent, Task<object?>>? handler, IReadOnlyList<FunctionCallContent> toolCalls)
    {
        if (handler is not null)
            foreach (var call in toolCalls)
                await handler(call);
        await Task.Yield();
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }

    // ---- E2: per-step transcript durability (a budget pause used to lose the whole transcript) ----

    private static async IAsyncEnumerable<ChatStreamItem> DriveText(string answer)
    {
        await Task.Yield();
        yield return new TextDelta(answer);
        yield return new Finished(null, "test-model");
    }

    /// <summary>
    /// Real <see cref="AssistantChatService"/> plus a call counter and fault injection on
    /// <c>SaveAsync</c> — the seam the interim (per-step) persist writes through.
    /// </summary>
    private sealed class CountingChatService : IAssistantChatService
    {
        private readonly IAssistantChatService _inner;

        public CountingChatService(IAssistantChatService inner)
        {
            _inner = inner;
            _inner.ChatsChanged += (s, e) => ChatsChanged?.Invoke(s, e);
        }

        public event EventHandler<AssistantChatChangedEventArgs>? ChatsChanged;

        public int SaveCalls { get; private set; }

        /// <summary>Throw on the next N SaveAsync calls (models a transient store fault mid-run).</summary>
        public int FailNextSaves { get; set; }

        public Task SaveAsync(SyncAssistantChat chat, CancellationToken ct = default)
        {
            SaveCalls++;
            if (FailNextSaves > 0)
            {
                FailNextSaves--;
                throw new InvalidOperationException("save boom");
            }
            return _inner.SaveAsync(chat, ct);
        }

        public Task SaveFromRemoteAsync(SyncAssistantChat chat, CancellationToken ct = default) => _inner.SaveFromRemoteAsync(chat, ct);

        /// <summary>W2a: the title-only writer. Counted separately from <see cref="SaveCalls"/> — the point of
        /// the change is that the auto-title path issues NO full replace.</summary>
        public int SetTitleCalls { get; private set; }

        public Task<bool> SetTitleAsync(Guid chatId, string title, CancellationToken ct = default)
        {
            SetTitleCalls++;
            return _inner.SetTitleAsync(chatId, title, ct);
        }

        /// <summary>W2b: counted so the rebase can be shown to add GetAsync calls, not SaveAsync calls.</summary>
        public int GetCalls { get; private set; }

        public Task<SyncAssistantChat?> GetAsync(Guid id, CancellationToken ct = default)
        {
            GetCalls++;
            return _inner.GetAsync(id, ct);
        }
        public Task<IReadOnlyList<SyncAssistantChat>> SearchAsync(string? searchText = null, DateTime? fromDate = null,
            DateTime? toDate = null, Guid? providerId = null, int offset = 0, int limit = 50, CancellationToken ct = default)
            => _inner.SearchAsync(searchText, fromDate, toDate, providerId, offset, limit, ct);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
        public Task DeleteFromRemoteAsync(Guid id, CancellationToken ct = default) => _inner.DeleteFromRemoteAsync(id, ct);
        public Task TouchLastAccessedAsync(Guid id, CancellationToken ct = default) => _inner.TouchLastAccessedAsync(id, ct);
        public Task<IReadOnlyList<Guid>> EvictOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default) => _inner.EvictOlderThanAsync(cutoffUtc, ct);
        public Task<IReadOnlyList<Guid>> DeleteAllAsync(CancellationToken ct = default) => _inner.DeleteAllAsync(ct);
        public Task<DateTime?> GetMaxUpdatedAtAsync(CancellationToken ct = default) => _inner.GetMaxUpdatedAtAsync(ct);
        public Task<IReadOnlyList<Guid>> GetAllIdsAsync(CancellationToken ct = default) => _inner.GetAllIdsAsync(ct);
    }

    /// <summary>Everything a headless run needs, wired to one temp SQLite file.</summary>
    private sealed class DurabilityHarness : IDisposable
    {
        private readonly string _dir;
        public readonly SqliteContext Ctx;
        public readonly AgentRunService Runs;
        public readonly CountingChatService Chats;
        public readonly AiProvider Provider = new() { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        public readonly Persona Persona = new() { Name = "Pia", SystemPrompt = "sys" };
        public readonly IAiClientService Ai = Substitute.For<IAiClientService>();
        public int Turns;

        /// <summary>
        /// Runs on the run's own thread at the START of turn N, i.e. AFTER BeginRunAsync seeded the executor's
        /// transcript and BEFORE that turn's interim persist. The seam a "second writer" needs (W2).
        /// </summary>
        public Action<int>? OnTurn;

        public DurabilityHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new CountingChatService(new AssistantChatService(Ctx, Runs));

            // One distinct reply per step turn, so the persisted transcript is order-verifiable.
            Ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var turn = ++Turns;
                    OnTurn?.Invoke(turn);
                    return DriveText("reply " + turn);
                });
        }

        /// <summary>A fresh executor — models the launcher's per-run (and per-resume) DI scope.</summary>
        public HeadlessTurnExecutor NewExecutor()
        {
            var plugins = Substitute.For<IPluginService>();
            var composer = Substitute.For<IAssistantPromptComposer>();
            composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
            var personas = Substitute.For<IPersonaService>();
            personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(Persona);
            var providers = Substitute.For<IProviderService>();
            providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider);
            var titles = Substitute.For<IChatTitleService>();
            titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            var engine = new BackgroundAssistantTurnRunner(
                Ai, plugins, composer, personas, Chats, titles, settings, TokenMapFactory, Runs,
                NullLogger<BackgroundAssistantTurnRunner>.Instance);
            return new HeadlessTurnExecutor(
                engine, Chats, settings, personas, providers, composer, titles, TokenMapFactory,
                NullLogger<HeadlessTurnExecutor>.Instance);
        }

        public async Task<AgentRun> NewRunAsync(string goal)
        {
            var chatId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await Chats.SaveAsync(new SyncAssistantChat
            {
                Id = chatId, SchemaVersion = 1, Title = "stub",
                CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
                WindowMode = WindowMode.Assistant.ToString(), Messages = [],
            }, TestContext.Current.CancellationToken);
            return await Runs.CreateAsync(
                new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal),
                TestContext.Current.CancellationToken);
        }

        public AgentRunOrchestrator Orchestrator(IAgentPlanner planner) =>
            new(Runs, planner, new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance);

        public void Dispose()
        {
            Runs.Dispose();
            Ctx.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    private static List<AgentStep> Steps(int count)
    {
        var steps = new List<AgentStep>();
        for (var i = 0; i < count; i++)
            steps.Add(new AgentStep { Ordinal = i, Title = "s" + i, Intent = "i" + i, Status = AgentStepStatus.Pending });
        return steps;
    }

    [Fact]
    public async Task ParkedAtBudget_BothStepRepliesArePersisted_AndTheResumeAppendsWithoutErasingThem()
    {
        // E2: the pause path deliberately skips EndRunAsync, which used to be the ONLY chat write a
        // headless run ever did — so a run parked after 2 of 4 steps left the DB holding just the goal.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(4));
        var budget = new RunProfile(MaxSteps: 2, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));
        var savesBeforeRun = h.Chats.SaveCalls;

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken);

        var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);

        // The parked transcript is DURABLE: goal + one reply per completed step.
        var afterPause = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(3, afterPause!.Messages.Count);
        Assert.Equal("the goal", afterPause.Messages[0].Content);
        Assert.Equal("reply 1", afterPause.Messages[1].Content);
        Assert.Equal("reply 2", afterPause.Messages[2].Content);

        // Cost control: exactly ONE chat write per completed step (no per-token/per-round rewrites) and
        // no terminal write on the pause path.
        Assert.Equal(2, h.Chats.SaveCalls - savesBeforeRun);

        // The interim rows carry the SAME Ids the step slices point at (R3 — stable Guids, not ordinals).
        var doneSteps = parked.Plan.Where(s => s.Status == AgentStepStatus.Done).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, doneSteps.Count);
        Assert.Equal(afterPause.Messages[1].Id, doneSteps[0].FirstMessageId);
        Assert.Equal(afterPause.Messages[2].Id, doneSteps[1].LastMessageId);
        var preservedIds = afterPause.Messages.Select(m => m.Id).ToList();

        // ---- resume: a FRESH executor (new DI scope), same run, fresh budget ----
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, TestContext.Current.CancellationToken));
        await h.Orchestrator(planner).RunAsync(parked, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken, resume: true);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);

        // D2: the resume LOADED the existing rows and appended — it neither erased nor duplicated them.
        var afterResume = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(5, afterResume!.Messages.Count);
        Assert.Equal(new[] { "the goal", "reply 1", "reply 2", "reply 3", "reply 4" },
            afterResume.Messages.Select(m => m.Content).ToArray());
        Assert.Equal(preservedIds, afterResume.Messages.Take(3).Select(m => m.Id).ToList());
        Assert.Single(await h.Chats.GetAllIdsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InterimAndTerminalSaves_AgreeOnMessageIds()
    {
        // The interim save and the terminal full replace must write the SAME message Ids: the run's and
        // each step's First/LastMessageId slices point at them (R3).
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner(Steps(2));

        var executor = h.NewExecutor();
        var orchestrator = h.Orchestrator(planner);
        await orchestrator.RunAsync(run, executor, h.Persona, h.Provider, RunProfile.Interactive,
            TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        var persisted = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.Equal(3, persisted!.Messages.Count);

        // Every step slice resolves to a live row, and the run-level slice spans the assistant replies.
        var ids = persisted.Messages.Select(m => m.Id).ToHashSet();
        foreach (var step in final.Plan)
        {
            Assert.Contains(step.FirstMessageId!.Value, ids);
            Assert.Contains(step.LastMessageId!.Value, ids);
        }
        Assert.Equal(persisted.Messages[1].Id, final.FirstMessageId);
        Assert.Equal(persisted.Messages[2].Id, final.LastMessageId);
    }

    [Fact]
    public async Task InterimPersistThrows_DoesNotFailTheStepOrTheRun_AndTheTerminalSaveRecovers()
    {
        // Guardrail 1: interim persistence is bookkeeping. A store fault logs and the run continues; the
        // terminal replace then re-writes the whole transcript, so nothing is permanently lost.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("goal");
        var planner = new FakePlanner(Steps(2));
        h.Chats.FailNextSaves = 2; // both interim saves throw; the terminal one succeeds

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);
        Assert.All(final.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));

        var persisted = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(3, persisted!.Messages.Count);
        Assert.Equal("reply 2", persisted.Messages[2].Content);
    }

    [Fact]
    public async Task InterimSave_KeepsTheExistingTitleAndWorkingDirectory()
    {
        // A mid-run save must not downgrade the chat's title to derived-from-goal, nor null the per-chat
        // working directory an interactive chat carries — the terminal save still owns the final title.
        using var h = new DurabilityHarness();
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await h.Chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "A nice earlier title",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), WorkingDirectory = "projects/alpha",
            Messages =
            [
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "the goal", Timestamp = now },
            ],
        }, TestContext.Current.CancellationToken);
        var run = await h.Runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "the goal"),
            TestContext.Current.CancellationToken);

        var planner = new FakePlanner(Steps(3));
        var budget = new RunProfile(MaxSteps: 1, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken);

        var parked = await h.Chats.GetAsync(chatId, TestContext.Current.CancellationToken);
        Assert.Equal("A nice earlier title", parked!.Title);
        Assert.Equal("projects/alpha", parked.WorkingDirectory);
        Assert.Equal(2, parked.Messages.Count); // pre-existing goal row + the one step reply
    }

    [Fact]
    public async Task BeginRunAsync_DoesNotInheritTheChatsWorkingSubpath_ParityWithLive()
    {
        // Executor parity (guardrail 3) with LiveTurnExecutor.BeginRunAsync, which DOES hand its chat's
        // working subpath to the run context so the verifier's artifact probe stats the right root. A
        // headless run deliberately must NOT: every step runs with TaskContext.WorkingSubpath: null, so its
        // writes land at the base root even when the chat row carries a WorkingDirectory. Asserted so a
        // future "just copy the row's value" tweak cannot point the probe at a folder nothing wrote to.
        using var h = new DurabilityHarness();
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await h.Chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId, SchemaVersion = 1, Title = "t",
            CreatedAt = now, UpdatedAt = now, LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(), WorkingDirectory = "projects/alpha",
            Messages = [],
        }, TestContext.Current.CancellationToken);
        var run = await h.Runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "the goal"),
            TestContext.Current.CancellationToken);

        var ctx = new RunContext("the goal", RunProfile.Interactive) { WorkingSubpath = "projects/alpha" };
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, ["write_file"], h.Provider);

        await executor.BeginRunAsync(run, ctx, TestContext.Current.CancellationToken);

        Assert.Null(ctx.WorkingSubpath);
    }

    // ---- W2b: the run's chat write rebases on the persisted rows before every write ----

    [Fact]
    public async Task ForeignRowWrittenAfterBeginRun_SurvivesTheInterimAndTerminalWrites()
    {
        // W2 direction B: BeginRunAsync takes ONE snapshot of the chat and every later write is a full
        // replace from it, so a row another writer added afterwards used to be DELETED — silently, because
        // there is no FK from AgentSteps to the message rows. The rebase absorbs it instead.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(2));
        var foreignId = Guid.NewGuid();

        h.OnTurn = turn =>
        {
            if (turn != 1) return;
            // A second writer (a live session's full replace) appends a row mid-run.
            var stored = h.Chats.GetAsync(run.ChatId).GetAwaiter().GetResult()!;
            stored.Messages.Add(new SyncAssistantChatMessage
            {
                Id = foreignId,
                Role = "user",
                Content = "typed by the user mid-run",
                Timestamp = DateTime.UtcNow,
            });
            h.Chats.SaveAsync(stored).GetAwaiter().GetResult();
        };

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        var final = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        var contents = final!.Messages.Select(m => m.Content).ToList();
        Assert.Contains("typed by the user mid-run", contents);   // the foreign row survived BOTH writes
        Assert.Contains("the goal", contents);                    // and the run's own rows are all still there
        Assert.Contains("reply 1", contents);
        Assert.Contains("reply 2", contents);

        // The run's own message Ids are unchanged, and every step slice still resolves to a live row —
        // resolved against the ROWS, not against a substring (nothing in production reads these ids, so this
        // is the only place the dangling-id symptom can be caught).
        var runRow = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        var ids = final.Messages.Select(m => m.Id).ToHashSet();
        Assert.Equal(AgentRunState.Completed, runRow!.State);
        foreach (var step in runRow.Plan)
        {
            Assert.Contains(step.FirstMessageId!.Value, ids);
            Assert.Contains(step.LastMessageId!.Value, ids);
        }
        Assert.Contains(runRow.FirstMessageId!.Value, ids);
        Assert.Contains(runRow.LastMessageId!.Value, ids);
    }

    [Fact]
    public async Task Rebase_AddsReadsNotWrites_SaveCallsPerParkedTwoStepRunIsStillTwo()
    {
        // The cost guard for W2b: if the rebase were ever implemented as "write, merge, re-write", SaveCalls
        // would double. It must be a READ before the single write.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(4));
        var budget = new RunProfile(MaxSteps: 2, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));
        var savesBefore = h.Chats.SaveCalls;
        var getsBefore = h.Chats.GetCalls;

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, h.Chats.SaveCalls - savesBefore);
        // One GetAsync for BeginRunAsync's seeding + one per write. Asserted as a lower bound so the exact
        // read cadence stays free to change; the WRITE count above is the one that is pinned.
        Assert.True(h.Chats.GetCalls - getsBefore >= 3,
            $"expected the rebase to add reads, saw {h.Chats.GetCalls - getsBefore}");
    }

    [Fact]
    public async Task ChatDeletedMidRun_RebaseIsANoOp_AndTheRunStillCompletes()
    {
        // Guardrail 1: the rebase lives inside PersistChatAsync's try. A chat deleted mid-run reads back null,
        // the rebase absorbs nothing, and the run writes its own transcript exactly as before.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(2));

        h.OnTurn = turn =>
        {
            if (turn != 1) return;
            h.Chats.DeleteAsync(run.ChatId).GetAwaiter().GetResult();
        };

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        var runRow = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, runRow!.State);
    }

    [Fact]
    public async Task Rebase_DoesNotFeedForeignRowsIntoTheRunsModelContext()
    {
        // Executor parity: the run's plan is fixed at BeginRunAsync, so a foreign turn must reach the DURABLE
        // transcript but NEVER the model context. The exchange messages are [system, goal, ...replies,
        // step instruction] — a mid-run user row must not appear among them.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(2));
        var seenByModel = new List<string>();

        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var turn = ++h.Turns;
                foreach (var m in (IList<ChatMessage>)ci[0])
                    seenByModel.Add(m.Text ?? string.Empty);
                if (turn == 1)
                {
                    var stored = h.Chats.GetAsync(run.ChatId).GetAwaiter().GetResult()!;
                    stored.Messages.Add(new SyncAssistantChatMessage
                    {
                        Id = Guid.NewGuid(),
                        Role = "user",
                        Content = "FOREIGN CHATTER",
                        Timestamp = DateTime.UtcNow,
                    });
                    h.Chats.SaveAsync(stored).GetAwaiter().GetResult();
                }
                return DriveText("reply " + turn);
            });

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("FOREIGN CHATTER", seenByModel);
        // ...while still reaching the durable transcript.
        var final = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(final!.Messages, m => m.Content == "FOREIGN CHATTER");
    }

    /// <summary>A step reply long enough that a handful of them blows a small context window.</summary>
    private static string LongReply(int turn) => $"reply {turn}: " + new string('x', 8_000);

    /// <summary>
    /// Re-stubs the harness AI with long replies and records the message list each turn was actually
    /// asked to send. The real BackgroundAssistantTurnRunner sits between the executor and this stub,
    /// so the captured argument IS the request the provider would have seen — compaction included.
    /// </summary>
    private static List<List<ChatMessage>> CaptureLongReplyRequests(DurabilityHarness h)
    {
        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return DriveText(LongReply(++h.Turns));
            });
        return captured;
    }

    [Fact]
    public async Task CompactionShrinksTheRequest_ButPersistedTranscriptKeepsEveryStepReply()
    {
        // THE HARD-GUARDRAIL TEST. Compaction operates on the request copy only: the outgoing list may
        // shrink, but _persisted — and therefore the E2 per-step durable transcript — must be
        // bit-for-bit what it would have been without compaction.
        using var h = new DurabilityHarness();
        h.Provider.MaxContextWindowTokens = 4_000;
        h.Provider.MaxOutputTokens = 1_000;
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(6));
        var captured = CaptureLongReplyRequests(h);

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            new RunProfile(MaxSteps: 6, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, captured.Count);

        // The last step's request would carry system + goal + 5 prior replies + the step instruction
        // if nothing compacted. It must be smaller than that.
        var last = captured[^1];
        Assert.True(last.Count < 8,
            $"the final step's request must be compacted, but it still carried {last.Count} messages");

        // ...and the pin held: the system prompt and the run goal are still the first two messages.
        Assert.Equal(ChatRole.System, last[0].Role);
        Assert.Equal(ChatRole.User, last[1].Role);
        Assert.Equal("the goal", last[1].Text);

        // The durable transcript is UNAFFECTED: goal + one verbatim reply per step.
        var persisted = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(7, persisted!.Messages.Count);
        Assert.Equal("the goal", persisted.Messages[0].Content);
        for (var turn = 1; turn <= 6; turn++)
            Assert.Equal(LongReply(turn), persisted.Messages[turn].Content);
    }

    [Fact]
    public async Task ParkAndResumeUnderCompaction_TranscriptMatchesTheUncompactedBaseline()
    {
        // Resume growth enters through the transcript re-seed into _messages, so resume is the path
        // most likely to compact — and the path where a persistence leak would be least visible.
        // Asserts the PERSISTED FACT rather than the mechanism: the same park/resume scenario run with
        // a tiny window and with no window at all must leave byte-identical transcripts.
        var budget = new RunProfile(MaxSteps: 2, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));

        async Task<List<string>> ParkAndResumeAsync(int? window, int? maxOutput)
        {
            using var h = new DurabilityHarness();
            h.Provider.MaxContextWindowTokens = window;
            h.Provider.MaxOutputTokens = maxOutput;
            var run = await h.NewRunAsync("the goal");
            var planner = new FakePlanner(Steps(4));
            CaptureLongReplyRequests(h);

            await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget,
                TestContext.Current.CancellationToken);

            var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
            Assert.Equal(AgentRunState.WaitingForInput, parked!.State);

            Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, TestContext.Current.CancellationToken));
            await h.Orchestrator(planner).RunAsync(parked, h.NewExecutor(), h.Persona, h.Provider, budget,
                TestContext.Current.CancellationToken, resume: true);

            var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
            Assert.Equal(AgentRunState.Completed, final!.State);

            var chat = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
            return chat!.Messages.Select(m => $"{m.Role}:{m.Content}").ToList();
        }

        var compacted = await ParkAndResumeAsync(4_000, 1_000);
        var baseline = await ParkAndResumeAsync(null, null);

        Assert.Equal(baseline.Count, compacted.Count);
        Assert.Equal(baseline, compacted);
    }
}
