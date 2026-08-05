using System.IO;
using Microsoft.Extensions.AI;
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
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

        // The executor uses this runner only as its per-step RunExchangeAsync engine, never RunAsync, so the
        // A2 bracket is not exercised here — a throwaway index keeps the composition explicit.
        var engine = new BackgroundAssistantTurnRunner(
            ai, plugins, composer, personas, chats, titles, settings, TokenMapFactory, runs,
            new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                capturedTools = ci.ArgAt<IList<AITool>?>(2);
                capturedProvider = ci.ArgAt<AiProvider>(1);
                return DriveWithTool(ci.ArgAt<ToolCallHandler?>(3), toolCalls);
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
            new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => DriveWithTool(ci.ArgAt<ToolCallHandler?>(3), toolCalls));

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
            new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
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

    [Fact]
    public async Task HeadlessStep_RecordsItsGateDecisions_AttributedToTheRunAndStep()
    {
        // Batch 03: proves the executor→RunExchangeAsync relay actually carries a scope. Everything below the
        // relay is covered by the gate suite; a forgotten argument HERE would be invisible there, because
        // those facts call RunExchangeAsync directly.
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var ctx = new SqliteContext(Path.Combine(dir, "history.db"));
        using var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        var chats = new AssistantChatService(ctx, runs);
        var timeline = new RecordingTimelineService();

        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var toolCalls = new List<FunctionCallContent>
        {
            new(Guid.NewGuid().ToString(), "write_file", new Dictionary<string, object?>()),
            new(Guid.NewGuid().ToString(), "delete_file", new Dictionary<string, object?>()),
        };

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => DriveWithTool(ci.ArgAt<ToolCallHandler?>(3), toolCalls));

        var plugins = Substitute.For<IPluginService>();
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((object?)null, (PluginToolCall?)new PluginToolCall(
                ci.ArgAt<FunctionCallContent>(0).Name, Guid.NewGuid(), "files", "d", null,
                () => Task.FromResult<object?>("ok"))));

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
            new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
        var executor = new HeadlessTurnExecutor(
            engine, chats, settings, personas, providers, composer, titles, TokenMapFactory,
            NullLogger<HeadlessTurnExecutor>.Instance, timeline);

        executor.Initialize(null, new[] { "write_file" }, provider);

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

        var rows = timeline.Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(run.Id, r.RunId);
            // The step id really travels: HeadlessTurnExecutor used to discard it at ExecuteStepAsync.
            Assert.NotNull(r.StepId);
            Assert.Equal(ToolGateSurface.Unattended, r.Surface);
        });
        Assert.Equal(ToolGateDecision.GrantedByName, rows[0].Decision);
        Assert.Equal(ToolGateDecision.DeniedNotGranted, rows[1].Decision);

        try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private static async IAsyncEnumerable<ChatStreamItem> DriveWithTool(
        ToolCallHandler? handler, IReadOnlyList<FunctionCallContent> toolCalls)
    {
        if (handler is not null)
            foreach (var call in toolCalls)
                await handler(call, new ToolDispatchContext(1));
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
    /// Real <see cref="AssistantChatService"/> plus a call counter and fault injection on the WRITE seams
    /// (<c>SaveAsync</c> and the merging <c>SaveMergedAsync</c> the interim/per-step persist uses).
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

        /// <summary>Every full-chat replace, whichever save seam issued it — the per-step write cost.</summary>
        public int SaveCalls { get; private set; }

        /// <summary>Throw on the next N save calls (models a transient store fault mid-run).</summary>
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

        /// <summary>W2b: the executor's write seam. Counted as a WRITE — the merge must not cost a second one.</summary>
        public Task<int> SaveMergedAsync(SyncAssistantChat chat, CancellationToken ct = default)
        {
            SaveCalls++;
            if (FailNextSaves > 0)
            {
                FailNextSaves--;
                throw new InvalidOperationException("save boom");
            }
            return _inner.SaveMergedAsync(chat, ct);
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

        /// <summary>The executor's log, so a fixture can assert on the compaction diff LINE (D-A').</summary>
        public readonly CapturingLogger<HeadlessTurnExecutor> ExecutorLog = new();

        // Hoisted out of NewExecutor so a fixture can seed them and so a resume shares them with the launch —
        // which is what the container does anyway. Batch 07's per-step persona facts need all four.
        public readonly IAssistantPromptComposer Composer = Substitute.For<IAssistantPromptComposer>();
        public readonly IPersonaService Personas = Substitute.For<IPersonaService>();
        public readonly IProviderService Providers = Substitute.For<IProviderService>();
        public readonly ISettingsService SettingsService = Substitute.For<ISettingsService>();
        public readonly AppSettings Settings = new();

        /// <summary>
        /// Batch 07 G6: the per-step resolver handed to every executor this harness builds, or null for the
        /// pre-batch behaviour (every step on the run persona). Set it before the first NewExecutor call.
        /// </summary>
        public StepPersonaResolver? StepPersonas;

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

            // A prompt that NAMES the persona it was composed from, so a fixture can tell whose system message
            // a given step actually sent. Every pre-Batch-07 fixture in this file only ever sees one persona,
            // so its prompt is a constant string exactly as before.
            Composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                    Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(ci => new AssistantTurnSetup(
                    "system for " + ci.ArgAt<Persona>(0).Name, null, SupportsTools: false, WebSearchActive: false));
            Personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(Persona);
            Providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider);
            SettingsService.GetSettingsAsync().Returns(_ => Task.FromResult(Settings));

            // One distinct reply per step turn, so the persisted transcript is order-verifiable.
            Ai.GetChatCompletionWithToolsAsync(
                    Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                    Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                    contextBudget: Arg.Any<AgentContextBudget?>())
                .Returns(_ =>
                {
                    var turn = ++Turns;
                    OnTurn?.Invoke(turn);
                    return DriveText("reply " + turn);
                });
        }

        /// <summary>
        /// A fresh executor — models the launcher's per-run (and per-resume) DI scope. The four persona-side
        /// substitutes are the HARNESS's, not locals: a resume must meet the same persona store and the same
        /// composer the launch did, which is what the container gives it, and Batch 07's per-step facts need to
        /// seed them from the fixture.
        /// </summary>
        public HeadlessTurnExecutor NewExecutor()
        {
            var plugins = Substitute.For<IPluginService>();
            var titles = Substitute.For<IChatTitleService>();
            titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            var engine = new BackgroundAssistantTurnRunner(
                Ai, plugins, Composer, Personas, Chats, titles, SettingsService, TokenMapFactory, Runs,
                new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
            return new HeadlessTurnExecutor(
                engine, Chats, SettingsService, Personas, Providers, Composer, titles, TokenMapFactory,
                ExecutorLog, timelineService: null, StepPersonas);
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

        // The parked transcript is DURABLE: goal + one reply per completed step + T2-18's grace turn, which
        // spends one tool-free wrap-up round before the park and persists it here (a park never reaches
        // EndRunAsync, so a wrap-up not written at pause time would never be written at all).
        var afterPause = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(4, afterPause!.Messages.Count);
        Assert.Equal("the goal", afterPause.Messages[0].Content);
        Assert.Equal("reply 1", afterPause.Messages[1].Content);
        Assert.Equal("reply 2", afterPause.Messages[2].Content);
        Assert.Equal("reply 3", afterPause.Messages[3].Content); // the grace turn's wrap-up

        // Cost control: exactly ONE chat write per TURN (no per-token/per-round rewrites) and no terminal
        // write on the pause path. Three turns here — two steps and the grace turn.
        Assert.Equal(3, h.Chats.SaveCalls - savesBeforeRun);

        // The interim rows carry the SAME Ids the step slices point at (R3 — stable Guids, not ordinals).
        var doneSteps = parked.Plan.Where(s => s.Status == AgentStepStatus.Done).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, doneSteps.Count);
        Assert.Equal(afterPause.Messages[1].Id, doneSteps[0].FirstMessageId);
        Assert.Equal(afterPause.Messages[2].Id, doneSteps[1].LastMessageId);
        // All FOUR rows, wrap-up included: the resume must append to them, not replace them.
        var preservedIds = afterPause.Messages.Select(m => m.Id).ToList();

        // ---- resume: a FRESH executor (new DI scope), same run, fresh budget ----
        Assert.True(await h.Runs.TryBeginResumeAsync(run.Id, TestContext.Current.CancellationToken));
        await h.Orchestrator(planner).RunAsync(parked, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken, resume: true);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, final!.State);

        // D2: the resume LOADED the existing rows and appended — it neither erased nor duplicated them.
        // "reply 3" is the pre-pause wrap-up; the resumed run's own two steps are turns 4 and 5.
        var afterResume = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(6, afterResume!.Messages.Count);
        Assert.Equal(new[] { "the goal", "reply 1", "reply 2", "reply 3", "reply 4", "reply 5" },
            afterResume.Messages.Select(m => m.Content).ToArray());
        Assert.Equal(preservedIds, afterResume.Messages.Take(4).Select(m => m.Id).ToList());
        Assert.Single(await h.Chats.GetAllIdsAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 18 G3 adversarial review, finding 2: <c>AgentRunOrchestrator.SafePostClarificationQuestionAsync</c>
    /// posts ONLY the plan turn's clarification question into a <c>needs-goal</c> park's chat — a decline
    /// returns before this executor's own per-step persist ever runs, so the goal itself never reaches that
    /// chat row. Before this fix, <see cref="BeginRunAsync"/>'s resume-seed heuristic read "the chat has rows"
    /// as "the goal is already in the transcript" (true before 18 G3, false the moment a chat can hold an
    /// assistant-only row) and a resumed run's model context opened on the model's OWN question with no
    /// record of what was asked.
    /// <para>
    /// This is the PANEL/Flow answer shape: the user pressed Continue (or typed into the panel's nudge box),
    /// so nothing was appended to the chat and it still holds exactly one assistant row. The sibling fact
    /// below is the CHAT-composer shape, where the answer is itself a user row.
    /// </para>
    /// <para>
    /// Drives <see cref="HeadlessTurnExecutor.BeginRunAsync"/> and
    /// <see cref="HeadlessTurnExecutor.ExecuteStepAsync"/> directly (the same style as
    /// <see cref="BeginRunAsync_PublishesTheWorkspaceRootOntoTheRunContext"/>) rather than through the
    /// orchestrator's resume path: what is under test is the executor's own SEEDING contract, and going
    /// through 18 G4's re-plan would put a whole planner and drain loop between the seed and the assertion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NeedsGoalResume_ChatHoldsOnlyTheQuestion_ButTheModelContextStillStatesTheGoal()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("do something with ggg");

        // Exactly what SafePostClarificationQuestionAsync leaves behind for a needs-goal park: one assistant
        // row, the model's question, and nothing else — never the goal.
        var sent = await SeedChatAndRunOneStepAsync(h, run, "do something with ggg",
            Row("assistant", "what do you mean by ggg?"));

        var goalIndex = sent.FindIndex(m => m.Text == "do something with ggg");
        var questionIndex = sent.FindIndex(m => m.Text == "what do you mean by ggg?");
        Assert.True(goalIndex >= 0, "the goal must reach the model context on a needs-goal resume");
        Assert.True(questionIndex >= 0, "the model's own question must still be carried forward");
        Assert.True(goalIndex < questionIndex, "the goal must precede the question it was asked about");

        // The seeded goal is also durable now (it feeds _persisted, not only _messages), so the NEXT full
        // replace (this step's own interim persist) writes it into the stored chat going forward.
        var persistedAfter = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(persistedAfter!.Messages, m => m.Role == "user" && m.Content == "do something with ggg");
        Assert.Contains(persistedAfter.Messages, m => m.Role == "assistant" && m.Content == "what do you mean by ggg?");
    }

    /// <summary>
    /// 18 G4 adversarial review, finding 1 — the OTHER answer surface, and the one the fix above did not
    /// cover. When the user answers a <c>needs-goal</c> park in the CHAT COMPOSER (18 G6:
    /// <c>ChatSessionManager.TryAnswerParkedRunAsync</c> adds the answer as a <c>ChatRole.User</c> row and
    /// AWAITS <c>AppendAnswerDurablyAsync</c> before it calls <c>ResumeAsync</c>), the chat this executor
    /// reads back is <c>[assistant question, user answer]</c> — a transcript that contains a user row and
    /// still does not contain the GOAL, because a decline returns before any per-step persist.
    /// <para>
    /// A seed test of the form "does the chat contain a user row anywhere" therefore passes the panel case
    /// and fails this one: it would take the resume branch, and every step turn of the re-planned run would
    /// run on the model's own question plus the reply, with the goal stated nowhere — and the goal would
    /// never reach the stored transcript either, so a person reading the chat later would not see it. The
    /// predicate asks whether the transcript OPENS with a user row instead; here it opens with the question.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> change <c>BeginRunAsync</c>'s <c>chat.Messages[0]</c> test back to
    /// <c>chat.Messages.Any(...)</c> → the goal assertion below reds while the fact above stays green, which
    /// is exactly the asymmetry that made this reachable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NeedsGoalResume_AnsweredInTheChat_StillStatesTheGoal_AndKeepsTheAnswer()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("do something with ggg");

        var sent = await SeedChatAndRunOneStepAsync(h, run, "do something with ggg",
            Row("assistant", "what do you mean by ggg?"),
            Row("user", "I mean the nightly export job"));

        var goalIndex = sent.FindIndex(m => m.Text == "do something with ggg");
        var questionIndex = sent.FindIndex(m => m.Text == "what do you mean by ggg?");
        var answerIndex = sent.FindIndex(m => m.Text == "I mean the nightly export job");
        Assert.True(goalIndex >= 0, "the goal must reach the model context even when the answer is a user row");
        Assert.True(questionIndex >= 0, "the model's own question must still be carried forward");
        Assert.True(answerIndex >= 0, "the user's typed answer must still be carried forward");
        // Chronological, and it is the whole story: goal, the question it provoked, the answer.
        Assert.True(goalIndex < questionIndex && questionIndex < answerIndex,
            "the three rows must read in the order they happened");

        var persistedAfter = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(persistedAfter!.Messages, m => m.Role == "user" && m.Content == "do something with ggg");
        Assert.Contains(persistedAfter.Messages, m => m.Role == "user" && m.Content == "I mean the nightly export job");
    }

    /// <summary>
    /// The CONTROL for the two facts above, and what keeps them from being "always seed the goal": an
    /// ordinary resume — a chat that OPENS with the goal, because this executor's own fresh launch wrote it
    /// there — must not gain a second copy of it. Duplicating the goal on every resume would grow the
    /// transcript once per park and re-state the goal to the model as if it were a new instruction.
    /// </summary>
    [Fact]
    public async Task OrdinaryResume_ChatAlreadyOpensWithTheGoal_DoesNotSeedItTwice()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");

        var sent = await SeedChatAndRunOneStepAsync(h, run, "the goal",
            Row("user", "the goal"),
            Row("assistant", "reply 1"));

        Assert.Equal(1, sent.Count(m => m.Text == "the goal"));

        var persistedAfter = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(1, persistedAfter!.Messages.Count(m => m.Content == "the goal"));
    }

    /// <summary>A stored chat row, timestamped in call order so the store's chronological merge keeps it.</summary>
    private static SyncAssistantChatMessage Row(string role, string content) => new()
    {
        Id = Guid.NewGuid(),
        Role = role,
        Content = content,
        Timestamp = DateTime.UtcNow,
    };

    /// <summary>
    /// Puts <paramref name="rows"/> into the run's chat, begins a FRESH executor on it (a new DI scope —
    /// exactly what a real resume gets) and runs one step, returning the messages that reached the provider.
    /// </summary>
    private static async Task<List<ChatMessage>> SeedChatAndRunOneStepAsync(
        DurabilityHarness h, AgentRun run, string goal, params SyncAssistantChatMessage[] rows)
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = await h.Chats.GetAsync(run.ChatId, ct);
        chat!.Messages = [.. rows];
        await h.Chats.SaveAsync(chat, ct);

        var ctx = new RunContext(goal, RunProfile.Interactive);
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await executor.BeginRunAsync(run, ctx, ct);

        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return DriveText("reply");
            });

        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s", Intent = "i", Status = AgentStepStatus.Pending };
        await executor.ExecuteStepAsync(run, step, ctx, ct);
        return Assert.Single(captured);
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
        // Pre-existing goal row + the one step reply + T2-18's grace-turn wrap-up, which is itself an interim
        // save and therefore subject to the same title/working-directory rule.
        Assert.Equal(3, parked.Messages.Count);
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

    // REGRESSION (T-G1-6, Batch 06 B3): the value Initialize was given must reach RunContext so the
    // verifier — which runs on the orchestrator thread, outside any step's ambient — can resolve
    // declared artifacts against the root the steps actually wrote into. Delete the assignment in
    // BeginRunAsync to see this go red.
    [Fact]
    public async Task BeginRunAsync_PublishesTheWorkspaceRootOntoTheRunContext()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var ctx = new RunContext("the goal", RunProfile.Interactive);
        var executor = h.NewExecutor();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "PiaTests_workspace_" + Guid.NewGuid().ToString("N"));
        executor.Initialize(workspaceRoot, ["write_file"], h.Provider);

        await executor.BeginRunAsync(run, ctx, TestContext.Current.CancellationToken);

        Assert.Equal(workspaceRoot, ctx.WorkspaceRoot);
        Assert.Null(ctx.WorkingSubpath);
    }

    // ---- W2b: the run's chat write merges the persisted rows INSIDE the store's gate hold ----

    [Fact]
    public async Task ForeignRowWrittenAfterBeginRun_SurvivesTheInterimAndTerminalWrites()
    {
        // W2 direction B: BeginRunAsync takes ONE snapshot of the chat and every later write is a full
        // replace from it, so a row another writer added afterwards used to be DELETED — silently, because
        // there is no FK from AgentSteps to the message rows. SaveMergedAsync absorbs it instead.
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
    public async Task Merge_IsNotASecondWrite_SaveCallsPerParkedRunIsOnePerTurn()
    {
        // The cost guard for W2b: if the merge were ever implemented as "write, merge, re-write", the write
        // count would double. It must be one read + one write inside a single gate hold, so the per-turn
        // write cost is unchanged. SaveCalls counts BOTH save seams (see CountingChatService).
        //
        // THREE, not two, since T2-18: two steps plus the grace turn spent before the budget park. The
        // invariant this guards is one write per TURN — which is why the number moved when a turn was added
        // and the name no longer carries it.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(4));
        var budget = new RunProfile(MaxSteps: 2, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));
        var savesBefore = h.Chats.SaveCalls;

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, h.Chats.SaveCalls - savesBefore);
    }

    [Fact]
    public async Task ChatDeletedMidRun_MergeIsANoOp_AndTheRunStillCompletes()
    {
        // Guardrail 1: the write lives inside PersistChatAsync's try. A chat deleted mid-run has no stored
        // rows, the merge absorbs nothing, and the run writes its own transcript exactly as before.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(2));
        var savesBefore = h.Chats.SaveCalls;

        h.OnTurn = turn =>
        {
            if (turn != 1) return;
            h.Chats.DeleteAsync(run.ChatId).GetAwaiter().GetResult();
        };

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            RunProfile.Interactive, TestContext.Current.CancellationToken);

        // The run row is GONE, not Completed — and asserting Completed here was unachievable by design,
        // not a production defect. AgentRuns declares
        // FOREIGN KEY (ChatId) REFERENCES AssistantChats(Id) ON DELETE CASCADE, Microsoft.Data.Sqlite
        // enables foreign keys by default, and DurabilityHarness.NewRunAsync inserts the chat row before
        // the run row (it has to, or the run INSERT would trip the constraint). So deleting the chat
        // mid-run cascades the run row away, and the orchestrator's terminal SetStateAsync updates zero
        // rows inside SafeSetState — which is exactly the failure-isolated bookkeeping it promises.
        Assert.Null(await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken));

        // What the guardrail is actually about: RunAsync returned normally (reaching this line at all
        // proves it did not throw or hang), and PersistChatAsync kept writing after the chat vanished
        // instead of aborting the run on the first failed merge.
        Assert.True(
            h.Chats.SaveCalls > savesBefore,
            "the run must keep persisting after its chat is deleted, not abort on the failed merge");
    }

    [Fact]
    public async Task Merge_DoesNotFeedForeignRowsIntoTheRunsModelContext()
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
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

        // D-A': and the log says WHICH run lost context - the compactor itself holds no run id, so
        // without this line a support log can say context was dropped but not where. Keyed on the RUN ID
        // (which the compactor's own line structurally cannot carry) AND on "compaction" (which the four
        // other "Headless run {RunId}" lines in this executor do not carry), so neither side can be
        // reworded into satisfying it by accident. Not vacuous: the assertion above already proved a
        // request shrank at this 4000/1000 window.
        Assert.Contains(h.ExecutorLog.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains($"Headless run {run.Id}", StringComparison.Ordinal)
            && e.Message.Contains("compaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoConfiguredWindow_SendsTheWholeRequest_AndLogsNoCompactionDiff()
    {
        // A GUARD, NOT A REGRESSION TEST: this passes before the D-A' change too, because before it there
        // was no diff line at all. What it pins is the MEANING of the line - "context WAS dropped on this
        // step", not "compaction ran" - and it is what fails if someone later moves the log outside the
        // count-difference guard. On an unconfigured provider (every provider after upgrade)
        // AgentContextBudget.From returns null, CompactAsync returns at its budget guard before it logs
        // anything, and the seam's count comparison finds no difference.
        using var h = new DurabilityHarness(); // Provider leaves the window and max output unset.
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(6));
        var captured = CaptureLongReplyRequests(h);

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider,
            new RunProfile(MaxSteps: 6, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20)),
            TestContext.Current.CancellationToken);

        // system + goal + 5 prior replies + the step instruction - the full 8 the compacted run cut down.
        Assert.Equal(6, captured.Count);
        Assert.Equal(8, captured[^1].Count);

        // Keyed on the SEAM's own signature (run id AND "compaction"), never on a bare "compaction":
        // the compactor logs through this same logger instance, so a bare substring would couple this
        // guard to the compactor's wording and level.
        Assert.DoesNotContain(h.ExecutorLog.Entries, e =>
            e.Message.Contains($"Headless run {run.Id}", StringComparison.Ordinal)
            && e.Message.Contains("compaction", StringComparison.Ordinal));
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

    // ---------------------------------------------------------------------------------------------------
    // Batch 07 G6 — per-step persona, provider and PROMPT on the headless executor.
    //
    // The prompt is the half that is easy to ship broken: the executor's accumulating transcript already
    // holds the RUN persona's system message at element 0, so an implementation that resolves a per-step
    // persona and then reuses that element sends the right label, the right provider and the WRONG
    // instructions — a feature that looks correct in the panel and is inert in the model.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>A planner that degrades immediately, so the run takes the R10 single-turn fallback path.</summary>
    private sealed class FallbackPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <summary>
    /// Puts <paramref name="roster"/> on the harness's configured roster and gives the harness a real
    /// <see cref="StepPersonaResolver"/> over its own substitutes. Roster membership is checked on the
    /// EXECUTOR side too, so a fixture that only stubs the persona store would see every assignment ignored.
    /// </summary>
    private static void WithRoster(DurabilityHarness h, params Persona[] roster)
    {
        h.Settings.SetAgentPersonaRoster(UserOperatingMode.Personal, roster.Select(p => p.Id).ToList());
        h.Personas.GetPersonasAsync().Returns(roster.ToList());
        foreach (var p in roster)
            h.Personas.GetPersonaAsync(p.Id).Returns(p);
        h.StepPersonas = new StepPersonaResolver(
            h.Personas, h.Providers, h.Composer, h.SettingsService, NullLogger<StepPersonaResolver>.Instance);
    }

    /// <summary>Records the (messages, provider) pair each turn was actually sent, in order.</summary>
    private static List<(List<ChatMessage> Messages, AiProvider Provider)> CaptureTurns(DurabilityHarness h)
    {
        var captured = new List<(List<ChatMessage>, AiProvider)>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add(([.. (IList<ChatMessage>)ci[0]], (AiProvider)ci[1]));
                return DriveText("reply " + ++h.Turns);
            });
        return captured;
    }

    private static Persona RosterPersona(string name, Guid? preferredProviderId = null) =>
        new() { Id = Guid.NewGuid(), Name = name, SystemPrompt = "you are " + name, PreferredProviderId = preferredProviderId };

    /// <summary>
    /// A three-step plan whose first two steps are assigned to two DIFFERENT roster personas and whose third
    /// is unassigned — the shape that catches both "the prompt never changes" and "the prompt changes and then
    /// never changes back".
    /// </summary>
    private static List<AgentStep> MixedSteps(Persona first, Persona second)
    {
        var steps = Steps(3);
        steps[0].AssignedPersonaId = first.Id;
        steps[1].AssignedPersonaId = second.Id;
        return steps;   // steps[2].AssignedPersonaId stays null ⇒ the run persona
    }

    [Fact]
    public async Task AnAssignedStep_SendsThatPersonasSystemPrompt_AndTheRunPersonaSurvivesForTheNextStep()
    {
        // REGRESSION for the §0.1 headline: the system message is per STEP, and the accumulating transcript's
        // element 0 stays the RUN persona's, so an unassigned step later in the same run is unaffected.
        using var h = new DurabilityHarness();
        var analyst = RosterPersona("Analyst");
        var critic = RosterPersona("Critic");
        WithRoster(h, analyst, critic);
        var captured = CaptureTurns(h);
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FakePlanner(MixedSteps(analyst, critic)))
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        Assert.Equal(3, captured.Count);
        Assert.All(captured, c => Assert.Equal(ChatRole.System, c.Messages[0].Role));
        Assert.Equal("system for Analyst", captured[0].Messages[0].Text);
        Assert.Equal("system for Critic", captured[1].Messages[0].Text);
        // The one that would go red if step 1 had mutated the shared transcript instead of its own copy.
        Assert.Equal("system for Pia", captured[2].Messages[0].Text);

        // The goal is still message 1 of every request: only element 0 was swapped.
        Assert.All(captured, c => Assert.Equal("the goal", c.Messages[1].Text));
    }

    [Fact]
    public async Task AnAssignedStep_RunsOnThatPersonasProvider()
    {
        // REGRESSION: D5 — a roster persona was chosen BECAUSE of its provider, so its PreferredProviderId
        // wins for its own step while every other step stays on the run's.
        using var h = new DurabilityHarness();
        var fast = new AiProvider { Id = Guid.NewGuid(), Name = "fast", Endpoint = "https://y", ProviderType = AiProviderType.OpenAI };
        var analyst = RosterPersona("Analyst", preferredProviderId: fast.Id);
        var critic = RosterPersona("Critic");
        WithRoster(h, analyst, critic);
        h.Providers.GetProviderAsync(fast.Id).Returns(fast);
        var captured = CaptureTurns(h);
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FakePlanner(MixedSteps(analyst, critic)))
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        Assert.Equal(fast.Id, captured[0].Provider.Id);       // the assigned persona's own provider
        Assert.Equal(h.Provider.Id, captured[1].Provider.Id); // no preference ⇒ the Assistant-mode default
        Assert.Equal(h.Provider.Id, captured[2].Provider.Id); // unassigned ⇒ the run's
    }

    [Fact]
    public async Task AnAssignedStep_StampsThatPersonaOnItsPersistedMessage()
    {
        // REGRESSION: the attribution the panel and the transcript read. Row 0 is the goal, then one row per
        // step in order.
        using var h = new DurabilityHarness();
        var analyst = RosterPersona("Analyst");
        var critic = RosterPersona("Critic");
        WithRoster(h, analyst, critic);
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FakePlanner(MixedSteps(analyst, critic)))
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        var chat = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(4, chat!.Messages.Count);
        Assert.Equal(analyst.Id, chat.Messages[1].Persona!.Id);
        Assert.Equal("Analyst", chat.Messages[1].Persona!.Name);
        Assert.Equal(critic.Id, chat.Messages[2].Persona!.Id);
        Assert.Equal(h.Persona.Id, chat.Messages[3].Persona!.Id);

        // The CHAT ROW's provider stays the run's even though two steps ran elsewhere: a chat has one
        // provider, and it is what a later interactive turn on this chat would resume on.
        Assert.Equal(h.Provider.Id, chat.ProviderId);
    }

    [Fact]
    public async Task NoResolver_LeavesAnAssignedStepOnTheRunPersona()
    {
        // GUARD for the batch's off switch at this seam: the executor the container builds without a resolver
        // (and, equivalently, any run whose roster is empty) behaves exactly as it did before Batch 07 even
        // for a step that already carries an AssignedPersonaId — a persisted plan from a roster since cleared.
        using var h = new DurabilityHarness();
        var analyst = RosterPersona("Analyst");
        h.Personas.GetPersonaAsync(analyst.Id).Returns(analyst);
        h.StepPersonas = null;
        var captured = CaptureTurns(h);
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FakePlanner(MixedSteps(analyst, analyst)))
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        Assert.Equal(3, captured.Count);
        Assert.All(captured, c => Assert.Equal("system for Pia", c.Messages[0].Text));
        Assert.All(captured, c => Assert.Equal(h.Provider.Id, c.Provider.Id));
        await h.Personas.DidNotReceive().GetPersonaAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task TheDegradeTurnUsesTheRunPersona()
    {
        // GUARD: the R10 single-turn fallback belongs to the RUN and to no step, so it must not pick up a
        // persona at all — there is no step to read an assignment from, and the run persona is the answer.
        using var h = new DurabilityHarness();
        WithRoster(h, RosterPersona("Analyst"));
        var captured = CaptureTurns(h);
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FallbackPlanner())
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        Assert.Single(captured);
        Assert.Equal("system for Pia", captured[0].Messages[0].Text);
        Assert.Equal(h.Provider.Id, captured[0].Provider.Id);
    }
}
