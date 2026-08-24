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
/// A headless multi-step run accumulates ONE chat across steps and keeps <c>TaskAmbient.TaskId == run.Id</c>
/// throughout, driven through the real orchestrator and real SQLite stores.
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
        // Runs inside RunExchangeAsync, where the run's TaskAmbient is live.
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
        // executing-run bracket is not exercised here — a throwaway index keeps the composition explicit.
        var engine = new BackgroundAssistantTurnRunner(
            ai, plugins, Substitute.For<IToolPermissionService>(), composer, personas, chats, titles,
            settings, TokenMapFactory, runs,
            new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
        var executor = new HeadlessTurnExecutor(
            engine, chats, settings, personas, providers, composer, titles, TokenMapFactory,
            NullLogger<HeadlessTurnExecutor>.Instance);

        // Bootstrap: the FK parent chat before the Planned run.
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

        // TaskAmbient.TaskId == run.Id for every exchange.
        Assert.Equal(3, ObservedTaskIds.Count);
        Assert.All(ObservedTaskIds, id => Assert.Equal(run.Id, id));

        // The headless path never offers Agent mode — suggestAgentModeEligible is always false.
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

    // ---- consent + MCP disable + provider override ----

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
            ai, plugins, Substitute.For<IToolPermissionService>(), composer, personas, chats, titles,
            settings, TokenMapFactory, runs,
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

        // MCP is OFFERED to unattended runs rather than stripped: it is denied inline unless granted, so both
        // tools reach the model.
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
            ai, plugins, Substitute.For<IToolPermissionService>(), composer, personas, chats, titles,
            settings, TokenMapFactory, runs,
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
        // Proves the executor→RunExchangeAsync relay carries a scope: the gate suite calls RunExchangeAsync
        // directly, so a forgotten argument HERE would be invisible there.
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
            ai, plugins, Substitute.For<IToolPermissionService>(), composer, personas, chats, titles,
            settings, TokenMapFactory, runs,
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

    // ---- per-step transcript durability (a budget pause used to lose the whole transcript) ----

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

        /// <summary>The executor's write seam. Counted as a WRITE — the merge must not cost a second one.</summary>
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

        /// <summary>The title-only writer, counted separately from <see cref="SaveCalls"/> because the auto-title
        /// path must issue NO full replace.</summary>
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
        // Deliberately NOT counted in GetCalls: the read-cost facts here are about the full-transcript read.
        public Task<Guid?> GetProviderIdAsync(Guid id, CancellationToken ct = default)
            => _inner.GetProviderIdAsync(id, ct);
        public Task<IReadOnlyList<SyncAssistantChat>> SearchAsync(string? searchText = null, DateTime? fromDate = null,
            DateTime? toDate = null, Guid? providerId = null, int offset = 0, int limit = 50, CancellationToken ct = default)
            => _inner.SearchAsync(searchText, fromDate, toDate, providerId, offset, limit, ct);
        public Task<int> CountAsync(string? searchText = null, DateTime? fromDate = null,
            DateTime? toDate = null, Guid? providerId = null, CancellationToken ct = default)
            => _inner.CountAsync(searchText, fromDate, toDate, providerId, ct);
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

        /// <summary>The executor's log, so a fixture can assert on the compaction diff LINE.</summary>
        public readonly CapturingLogger<HeadlessTurnExecutor> ExecutorLog = new();

        // Hoisted out of NewExecutor so a fixture can seed them and so a resume shares them with the launch —
        // which is what the container does anyway.
        public readonly IAssistantPromptComposer Composer = Substitute.For<IAssistantPromptComposer>();
        public readonly IPersonaService Personas = Substitute.For<IPersonaService>();
        public readonly IProviderService Providers = Substitute.For<IProviderService>();
        public readonly ISettingsService SettingsService = Substitute.For<ISettingsService>();
        public readonly AppSettings Settings = new();

        /// <summary>
        /// The per-step resolver handed to every executor this harness builds; null puts every step on the run
        /// persona. Set it before the first NewExecutor call.
        /// </summary>
        public StepPersonaResolver? StepPersonas;

        public int Turns;

        /// <summary>
        /// Runs on the run's own thread at the START of turn N — after BeginRunAsync seeded the transcript and
        /// before that turn's interim persist, which is the seam a "second writer" needs.
        /// </summary>
        public Action<int>? OnTurn;

        public DurabilityHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
            Runs = new AgentRunService(Ctx, NullLogger<AgentRunService>.Instance);
            Chats = new CountingChatService(new AssistantChatService(Ctx, Runs));

            // A prompt that NAMES the persona it was composed from, so a fixture can tell whose system message a
            // given step actually sent; a single-persona fixture still sees a constant string.
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
        /// A fresh executor, modelling the launcher's per-run DI scope. The persona-side substitutes are the
        /// HARNESS's, because a resume must meet the same persona store and composer the launch did.
        /// </summary>
        public HeadlessTurnExecutor NewExecutor()
        {
            var plugins = Substitute.For<IPluginService>();
            var titles = Substitute.For<IChatTitleService>();
            titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            var engine = new BackgroundAssistantTurnRunner(
                Ai, plugins, Substitute.For<IToolPermissionService>(), Composer, Personas, Chats,
                titles, SettingsService, TokenMapFactory, Runs,
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
        // The pause path deliberately skips EndRunAsync, which used to be the ONLY chat write a headless run ever
        // did — so a run parked after 2 of 4 steps left the DB holding just the goal.
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var planner = new FakePlanner(Steps(4));
        var budget = new RunProfile(MaxSteps: 2, MaxReplans: 0, WallClock: TimeSpan.FromMinutes(20));
        var savesBeforeRun = h.Chats.SaveCalls;

        await h.Orchestrator(planner).RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, budget,
            TestContext.Current.CancellationToken);

        var parked = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, parked!.State);

        // The parked transcript is DURABLE: goal + one reply per completed step + the grace turn's wrap-up, which
        // must be persisted here because a park never reaches EndRunAsync.
        var afterPause = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(4, afterPause!.Messages.Count);
        Assert.Equal("the goal", afterPause.Messages[0].Content);
        Assert.Equal("reply 1", afterPause.Messages[1].Content);
        Assert.Equal("reply 2", afterPause.Messages[2].Content);
        Assert.Equal("reply 3", afterPause.Messages[3].Content); // the grace turn's wrap-up

        // Cost control: exactly ONE chat write per TURN (no per-token/per-round rewrites) and no terminal
        // write on the pause path. Three turns here — two steps and the grace turn.
        Assert.Equal(3, h.Chats.SaveCalls - savesBeforeRun);

        // The interim rows carry the SAME Ids the step slices point at — stable Guids, not ordinals.
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

        // The resume LOADED the existing rows and appended — it neither erased nor duplicated them. "reply 3" is
        // the pre-pause wrap-up; the resumed run's own two steps are turns 4 and 5.
        var afterResume = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Equal(6, afterResume!.Messages.Count);
        Assert.Equal(new[] { "the goal", "reply 1", "reply 2", "reply 3", "reply 4", "reply 5" },
            afterResume.Messages.Select(m => m.Content).ToArray());
        Assert.Equal(preservedIds, afterResume.Messages.Take(4).Select(m => m.Id).ToList());
        Assert.Single(await h.Chats.GetAllIdsAsync(TestContext.Current.CancellationToken));
    }

    // A needs-goal park's chat holds only the model's clarification question, so a resumed run must seed the goal
    // into the model context even though the stored chat does not contain it.
    [Fact]
    public async Task NeedsGoalResume_ChatHoldsOnlyTheQuestion_ButTheModelContextStillStatesTheGoal()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("do something with ggg");

        var sent = await SeedChatAndRunOneStepAsync(h, run, "do something with ggg",
            Row("assistant", "what do you mean by ggg?"));

        var goalIndex = sent.FindIndex(m => m.Text == "do something with ggg");
        var questionIndex = sent.FindIndex(m => m.Text == "what do you mean by ggg?");
        Assert.True(goalIndex >= 0, "the goal must reach the model context on a needs-goal resume");
        Assert.True(questionIndex >= 0, "the model's own question must still be carried forward");
        Assert.True(goalIndex < questionIndex, "the goal must precede the question it was asked about");

        // The seeded goal is also durable now, so the next full persist writes it into the stored chat too.
        var persistedAfter = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(persistedAfter!.Messages, m => m.Role == "user" && m.Content == "do something with ggg");
        Assert.Contains(persistedAfter.Messages, m => m.Role == "assistant" && m.Content == "what do you mean by ggg?");
    }

    // When the answer arrives as a chat-composer user row the transcript still opens with the model's question, so
    // the resume-seed check must test what the chat OPENS with, not whether it contains any user row.
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
        Assert.True(goalIndex < questionIndex && questionIndex < answerIndex,
            "the three rows must read in the order they happened");

        var persistedAfter = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(persistedAfter!.Messages, m => m.Role == "user" && m.Content == "do something with ggg");
        Assert.Contains(persistedAfter.Messages, m => m.Role == "user" && m.Content == "I mean the nightly export job");
    }

    // Guards against always seeding: an ordinary resume, whose chat already opens with the goal, must not gain a
    // second copy of it.
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

    /// <summary>Seeds the chat, begins a fresh executor on a new DI scope the way a real resume gets one, and
    /// returns the messages that reached the provider for one step.</summary>
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
        // The interim save and the terminal full replace must write the SAME message Ids: the run's and each
        // step's First/LastMessageId slices point at them.
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
        // Interim persistence is bookkeeping: a store fault logs and the run continues, and the terminal replace
        // then re-writes the whole transcript, so nothing is permanently lost.
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
        // Pre-existing goal row + the one step reply + the grace-turn wrap-up, which is itself an interim save and
        // therefore subject to the same title/working-directory rule.
        Assert.Equal(3, parked.Messages.Count);
    }

    [Fact]
    public async Task BeginRunAsync_DoesNotInheritTheChatsWorkingSubpath_ParityWithLive()
    {
        // Unlike LiveTurnExecutor, a headless run must NOT inherit the chat's working subpath: every step runs with
        // WorkingSubpath null, so its writes land at the base root even when the chat row carries one.
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

    // The value Initialize was given must reach RunContext, because the verifier runs on the orchestrator thread —
    // outside any step's ambient — and resolves declared artifacts against the root the steps wrote into.
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

    // ---- the run's chat write merges the persisted rows INSIDE the store's gate hold ----

    [Fact]
    public async Task ForeignRowWrittenAfterBeginRun_SurvivesTheInterimAndTerminalWrites()
    {
        // BeginRunAsync takes ONE snapshot and every later write is a full replace from it, so a row another writer
        // added afterwards used to be DELETED silently — there is no FK from AgentSteps to the message rows.
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

        // Resolved against the ROWS, not a substring: nothing in production reads these ids, so this is the only
        // place a dangling-id symptom can be caught.
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
        // A merge implemented as "write, merge, re-write" would double the write count; it must be one read plus one
        // write inside a single gate hold. Three, not two: two steps plus the grace turn before the park.
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
        // The write lives inside PersistChatAsync's try: a chat deleted mid-run has no stored rows, the merge
        // absorbs nothing, and the run writes its own transcript exactly as before.
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

        // The run row is GONE, not Completed: AgentRuns.ChatId cascades on delete, so the orchestrator's terminal
        // SetStateAsync updates zero rows inside SafeSetState.
        Assert.Null(await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken));

        // Reaching this line proves RunAsync returned normally, and the count proves PersistChatAsync kept writing
        // after the chat vanished instead of aborting on the first failed merge.
        Assert.True(
            h.Chats.SaveCalls > savesBefore,
            "the run must keep persisting after its chat is deleted, not abort on the failed merge");
    }

    [Fact]
    public async Task Merge_DoesNotFeedForeignRowsIntoTheRunsModelContext()
    {
        // The run's plan is fixed at BeginRunAsync, so a foreign turn must reach the DURABLE transcript but NEVER
        // the model context — a mid-run user row must not appear among the exchange messages.
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
    /// Records the message list each turn was asked to send. The real turn runner sits between the executor and
    /// this stub, so the captured argument IS the request the provider would have seen, compaction included.
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
        // Compaction operates on the request copy only: the outgoing list may shrink, but the durable transcript
        // must be bit-for-bit what it would have been without compaction.
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

        // The log must say WHICH run lost context: the compactor itself holds no run id, so without this line a
        // support log can say context was dropped but not where.
        Assert.Contains(h.ExecutorLog.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains($"Headless run {run.Id}", StringComparison.Ordinal)
            && e.Message.Contains("compaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoConfiguredWindow_SendsTheWholeRequest_AndLogsNoCompactionDiff()
    {
        // Pins the MEANING of the diff line — "context WAS dropped on this step", not "compaction ran" — so it
        // fails if the log is ever moved outside the count-difference guard.
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

        // Keyed on the seam's own signature (run id AND "compaction"): the compactor logs through this same logger
        // instance, so a bare substring would couple this guard to the compactor's wording.
        Assert.DoesNotContain(h.ExecutorLog.Entries, e =>
            e.Message.Contains($"Headless run {run.Id}", StringComparison.Ordinal)
            && e.Message.Contains("compaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParkAndResumeUnderCompaction_TranscriptMatchesTheUncompactedBaseline()
    {
        // Resume re-seeds the transcript, so it is the path most likely to compact and the one where a persistence
        // leak would be least visible: with a tiny window and with none, the transcripts must be byte-identical.
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

    // ---- per-step persona, provider and PROMPT on the headless executor ----
    // The accumulating transcript already holds the RUN persona's system message at element 0, so reusing that
    // element sends the right label, the right provider and the WRONG instructions.

    /// <summary>A planner that degrades immediately, so the run takes the single-turn fallback path.</summary>
    private sealed class FallbackPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <summary>
    /// Roster membership is checked on the EXECUTOR side too, so a fixture that only stubbed the persona store
    /// would see every assignment ignored.
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

    // Two assigned steps then an unassigned one: the shape that catches both "the prompt never changes" and "the
    // prompt changes and then never changes back".
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
        // The system message is per STEP, and the accumulating transcript's element 0 stays the RUN persona's, so
        // an unassigned step later in the same run is unaffected.
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
        // A roster persona is chosen BECAUSE of its provider, so its PreferredProviderId wins for its own step
        // while every other step stays on the run's.
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
        // The attribution the panel and the transcript read. Row 0 is the goal, then one row per step in order.
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
        // The off switch: an executor built without a resolver — equivalently, a run whose roster is empty — keeps
        // every step on the run persona even for a step that still carries an AssignedPersonaId.
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
    public async Task APinnedRunPersona_ReplacesTheRunDefault_ButNotAnAssignedStepsSpecialist()
    {
        // A routine's pin reaches the PLAN through the orchestrator's argument; the step turns compose their
        // system prompt from the run default instead, so the pin has to arrive through Initialize as well.
        using var h = new DurabilityHarness();
        var analyst = RosterPersona("Analyst");
        var critic = RosterPersona("Critic");
        WithRoster(h, analyst, critic);
        var pinned = RosterPersona("Pinned");   // a job's pin is not roster-gated
        var captured = CaptureTurns(h);
        var run = await h.NewRunAsync("the goal");
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, grantedWrites: [], personaOverride: pinned);

        await h.Orchestrator(new FakePlanner(MixedSteps(analyst, critic)))
            .RunAsync(run, executor, pinned, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        Assert.Equal(3, captured.Count);
        Assert.Equal("system for Analyst", captured[0].Messages[0].Text);
        Assert.Equal("system for Critic", captured[1].Messages[0].Text);
        Assert.Equal("system for Pinned", captured[2].Messages[0].Text);
        await h.Personas.DidNotReceive().ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>());
    }

    [Fact]
    public async Task TheDegradeTurnUsesTheRunPersona()
    {
        // The single-turn fallback belongs to the RUN and to no step, so there is no assignment to read and the
        // run persona is the answer.
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

    // ---- Cancel classification: only a fired run token counts as a cancel ----

    private static async IAsyncEnumerable<ChatStreamItem> ThrowStream(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static void StubThrowing(DurabilityHarness h, Func<IAsyncEnumerable<ChatStreamItem>> stream)
    {
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(_ => stream());
    }

    [Fact]
    public async Task TransportOce_TokenNotCancelled_SettlesFailedNotCancelled()
    {
        // A TaskCanceledException out of the transport (an HTTP-layer timeout) while the run token is still live
        // must FAIL the step, not read as a user cancel and settle Cancelled.
        using var h = new DurabilityHarness();
        StubThrowing(h, () => ThrowStream(new TaskCanceledException("The operation was canceled.")));
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FakePlanner(Steps(1)))
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive,
                TestContext.Current.CancellationToken);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(final);
        Assert.Equal(AgentRunState.Failed, final!.State);
    }

    [Fact]
    public async Task UserCancelMidStep_SettlesCancelled()
    {
        // The other half of the distinction: when the run token actually fires mid-exchange (a user stop),
        // the OCE stays a cancel and the run settles Cancelled exactly as before.
        using var h = new DurabilityHarness();
        using var userCts = new CancellationTokenSource();
        StubThrowing(h, () => CancelThenThrow(userCts));
        var run = await h.NewRunAsync("the goal");

        await h.Orchestrator(new FakePlanner(Steps(1)))
            .RunAsync(run, h.NewExecutor(), h.Persona, h.Provider, RunProfile.Interactive, userCts.Token);

        var final = await h.Runs.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(final);
        Assert.Equal(AgentRunState.Cancelled, final!.State);
    }

    private static async IAsyncEnumerable<ChatStreamItem> CancelThenThrow(CancellationTokenSource cts)
    {
        await Task.Yield();
        cts.Cancel();
        throw new OperationCanceledException("cancelled");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
