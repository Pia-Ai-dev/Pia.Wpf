using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        public Task<IReadOnlyList<AssistantChatSearchHit>> SearchRankedAsync(string searchText, DateTime? fromDate,
            DateTime? toDate, Guid? providerId, Guid? excludeChatId, int limit, CancellationToken ct = default)
            => _inner.SearchRankedAsync(searchText, fromDate, toDate, providerId, excludeChatId, limit, ct);
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

        // Hoisted for the same reason, plus one of its own: only a seeded route returns a pending action, and
        // without one no fixture can drive the gate to a real approval park.
        public readonly IPluginService Plugins = Substitute.For<IPluginService>();
        public readonly IToolPermissionService Permissions = Substitute.For<IToolPermissionService>();

        /// <summary>
        /// The per-step resolver handed to every executor this harness builds; null puts every step on the run
        /// persona. Set it before the first NewExecutor call.
        /// </summary>
        public StepPersonaResolver? StepPersonas;

        /// <summary>The durable tool context every executor this harness builds shares, or null for the
        /// pre-store behaviour. Set it before the first NewExecutor call.</summary>
        public IAgentToolExchangeStore? Exchanges;

        /// <summary>Whether the composed turn offers tools. A step only ever produces tool exchanges when it
        /// had tools, and a turn that sends none drops the carried pairs — so a carry fixture must set this.</summary>
        public bool SupportsTools;

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
                    "system for " + ci.ArgAt<Persona>(0).Name,
                    SupportsTools ? new List<AITool> { AIFunctionFactory.Create(() => string.Empty, "noop") } : null,
                    SupportsTools, WebSearchActive: false));
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
            var titles = Substitute.For<IChatTitleService>();
            titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

            var engine = new BackgroundAssistantTurnRunner(
                Ai, Plugins, Permissions, Composer, Personas, Chats,
                titles, SettingsService, TokenMapFactory, Runs,
                new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance);
            return new HeadlessTurnExecutor(
                engine, Chats, SettingsService, Personas, Providers, Composer, titles, TokenMapFactory,
                ExecutorLog, timelineService: null, StepPersonas, exchangeStore: Exchanges);
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

    /// <summary>The granter is captured in BeginRunAsync but published per step, so the two must stay in step:
    /// a tool that mints an assignment reads it to learn nobody can be asked, and an empty one would name no
    /// granter in the consent audit line.</summary>
    [Fact]
    public async Task AStepExchange_CarriesTheRunsGranterOnTheAmbient()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("the goal");
        var jobId = Guid.NewGuid();
        run.TriggerKind = AgentRunTrigger.Schedule;
        run.TriggerRef = jobId;

        string? granter = null;
        h.OnTurn = _ => granter = TaskAmbient.Current?.UnattendedGranter;

        var ct = TestContext.Current.CancellationToken;
        var ctx = new RunContext("the goal", RunProfile.Interactive);
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, [], h.Provider);
        await executor.BeginRunAsync(run, ctx, ct);
        await executor.ExecuteStepAsync(
            run,
            new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s", Intent = "i", Status = AgentStepStatus.Pending },
            ctx, ct);

        Assert.Equal($"routine:{jobId}", granter);
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

    // ---- cross-step tool context ----

    private static AgentStep CarryStep(int ordinal) => new()
    {
        Id = Guid.NewGuid(), Ordinal = ordinal, Title = "s" + ordinal, Intent = "i" + ordinal,
        Status = AgentStepStatus.Pending,
    };

    private static ChatMessage ToolCall(string callId, string tool, string path) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, tool, new Dictionary<string, object?> { ["path"] = path })]);

    private static ChatMessage ToolResult(string callId, string result) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    /// <summary>What AiClientService yields for a turn that made tool calls: the marker, then the round's
    /// call/result pair, then the visible answer.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> DriveToolRounds(string answer, params ChatMessage[][] rounds)
    {
        await Task.Yield();
        for (var round = 0; round < rounds.Length; round++)
        {
            yield return new ToolRoundCompleted();
            yield return new ToolRoundExchange(round + 1, rounds[round]);
        }

        yield return new TextDelta(answer);
        yield return new Finished(null, "test-model");
    }

    /// <summary>Runs two steps against a stub whose FIRST turn made tool calls, and returns both requests.</summary>
    private static async Task<List<List<ChatMessage>>> RunTwoStepsAsync(
        DurabilityHarness h, AgentRun run, params ChatMessage[][] firstStepRounds)
    {
        var ct = TestContext.Current.CancellationToken;
        h.SupportsTools = true;
        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return ++h.Turns == 1 ? DriveToolRounds("read them", firstStepRounds) : DriveText("wrote them");
            });

        var ctx = new RunContext(run.Goal ?? "goal", RunProfile.Interactive);
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await executor.BeginRunAsync(run, ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(0), ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(1), ctx, ct);
        return captured;
    }

    private static IEnumerable<string> ResultBodies(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result as string ?? string.Empty);

    /// <summary>The finding: a step that read a file in step 1 and wrote a report in step 3 had nothing but its
    /// own prose to write from, and invented every row. The raw result must survive the boundary.</summary>
    [Fact]
    public async Task AStepsToolResult_ReachesTheNextStepsRequest()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");

        var captured = await RunTwoStepsAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "SKU-1001,Blue Widget,4,10,3.50")]);

        var second = Assert.IsType<List<ChatMessage>>(captured[1]);
        Assert.Contains("SKU-1001,Blue Widget,4,10,3.50", ResultBodies(second));
        Assert.Contains(second.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.Name == "read_file");
    }

    /// <summary>Carried context is model context only. _persisted is a different list of a different type, and
    /// the chat a person reads (and a resume re-seeds from) must not grow a tool row.</summary>
    [Fact]
    public async Task CarriedToolExchanges_DoNotReachThePersistedChat()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");

        await RunTwoStepsAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "SKU-1001,Blue Widget,4,10,3.50")]);

        var chat = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(chat!.Messages, m => m.Role == "tool");
        Assert.DoesNotContain(chat.Messages, m => (m.Content ?? string.Empty).Contains("SKU-1001"));
    }

    /// <summary>Past K, the oldest bodies go but the calls stay, and the placeholder says what to re-issue —
    /// otherwise a run's whole file history rides along forever.</summary>
    [Fact]
    public async Task PastTheKeptCount_TheOldestResultIsClearedAndNamesItsCall()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");

        var rounds = Enumerable.Range(0, AgentToolCarryover.KeptResults + 1)
            .Select(i => new[] { ToolCall("c" + i, "read_file", "f" + i + ".csv"), ToolResult("c" + i, "body " + i) })
            .ToArray();

        var captured = await RunTwoStepsAsync(h, run, rounds);
        var second = captured[1];

        Assert.Contains("[result cleared; call read_file on f0.csv again if you need it]", ResultBodies(second));
        Assert.DoesNotContain("body 0", ResultBodies(second));
        Assert.Contains("body 1", ResultBodies(second));
        Assert.Contains("body " + AgentToolCarryover.KeptResults, ResultBodies(second));
        Assert.Contains(second.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.CallId == "c0");
    }

    /// <summary>Every step's exchanges accumulate, not just the previous one's — the fabrication case was a
    /// read in step 1 and a write in step 3, two boundaries away.</summary>
    [Fact]
    public async Task EachStepsExchanges_AccumulateAcrossEveryLaterStep()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");
        var ct = TestContext.Current.CancellationToken;
        h.SupportsTools = true;

        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return ++h.Turns switch
                {
                    1 => DriveToolRounds("read it", [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")]),
                    2 => DriveToolRounds("searched", [ToolCall("c2", "search_files", "."), ToolResult("c2", "the search hits")]),
                    _ => DriveText("wrote it"),
                };
            });

        var ctx = new RunContext("inventory report", RunProfile.Interactive);
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await executor.BeginRunAsync(run, ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(0), ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(1), ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(2), ctx, ct);

        Assert.Contains("the csv rows", ResultBodies(captured[2]));
        Assert.Contains("the search hits", ResultBodies(captured[2]));
        Assert.DoesNotContain("the search hits", ResultBodies(captured[1]));
    }

    /// <summary>
    /// A turn that sends NO tools must not be handed tool_calls: a provider can reject the request outright,
    /// and the turn could not act on them anyway. The grace turn is the live case — it strips its tool list on
    /// purpose, after the budget is spent.
    /// </summary>
    [Fact]
    public async Task AToolFreeTurn_IsNotHandedTheCarriedExchanges()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");
        var ct = TestContext.Current.CancellationToken;
        h.SupportsTools = true;

        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return ++h.Turns == 1
                    ? DriveToolRounds("read it", [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")])
                    : DriveText("wrapping up");
            });

        var ctx = new RunContext("inventory report", RunProfile.Interactive);
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await executor.BeginRunAsync(run, ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(0), ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(1), ctx, ct);
        await executor.RunGraceTurnAsync(run, ctx, ct);

        // The premise: an ordinary later step DOES get the pair, so the grace turn's emptiness is the strip
        // and not a run that never carried anything.
        Assert.Contains("the csv rows", ResultBodies(captured[1]));
        Assert.DoesNotContain(captured[2].SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
    }

    /// <summary>The step instruction has to say what a cleared placeholder means, or the model treats it as an
    /// empty file rather than a prompt to read again.</summary>
    [Fact]
    public async Task TheStepInstruction_TellsTheModelToReReadAClearedResult()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");

        var captured = await RunTwoStepsAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "rows")]);

        Assert.Contains(captured[0], m => m.Role == ChatRole.User && m.Text.Contains(AgentToolCarryover.ReReadHint));
    }

    /// <summary>Both halves are gated: the workspace is where the vault write is refused, and the tool list is
    /// what proves the run actually has the tool the hint names.</summary>
    [Fact]
    public async Task TheStepInstruction_CarriesTheVaultTargetHint_OnlyInAWorkspace()
    {
        var inWorkspace = await StepInstructionAsync(withWorkspace: true, "noop", VaultTargetPolicy.CreateSourceToolName);
        var noWorkspace = await StepInstructionAsync(withWorkspace: false, "noop", VaultTargetPolicy.CreateSourceToolName);
        var noMemoryTool = await StepInstructionAsync(withWorkspace: true, "noop");

        Assert.Contains(VaultTargetPolicy.StepHint, inWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultTargetPolicy.StepHint, noWorkspace, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultTargetPolicy.StepHint, noMemoryTool, StringComparison.Ordinal);

        foreach (var instruction in new[] { inWorkspace, noWorkspace, noMemoryTool })
            Assert.Contains(AgentToolCarryover.ReReadHint, instruction, StringComparison.Ordinal);
    }

    /// <summary>The composed user message of one step's first turn, on a turn offering exactly
    /// <paramref name="toolNames"/>.</summary>
    private static async Task<string> StepInstructionAsync(bool withWorkspace, params string[] toolNames)
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("file the report");
        var ct = TestContext.Current.CancellationToken;

        h.Composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(_ => new AssistantTurnSetup(
                "system",
                [.. toolNames.Select(n => (AITool)AIFunctionFactory.Create(() => string.Empty, n))],
                SupportsTools: true, WebSearchActive: false));

        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return DriveText("done");
            });

        var workspaceRoot = withWorkspace ? Path.Combine(Path.GetTempPath(), "PiaWs_" + Guid.NewGuid().ToString("N")) : null;
        if (workspaceRoot is not null)
            Directory.CreateDirectory(workspaceRoot);
        try
        {
            var ctx = new RunContext("file the report", RunProfile.Interactive);
            var executor = h.NewExecutor();
            executor.Initialize(workspaceRoot, ["write_file"], h.Provider);
            await executor.BeginRunAsync(run, ctx, ct);
            await executor.ExecuteStepAsync(run, CarryStep(0), ctx, ct);
        }
        finally
        {
            if (workspaceRoot is not null)
                Directory.Delete(workspaceRoot, recursive: true);
        }

        return Assert.Single(captured[0], m => m.Role == ChatRole.User && m.Text.Contains("Execute step 1")).Text;
    }

    // ---- the durable twin: a resume gets back the exchanges the abandoned attempt produced ----

    private static AgentToolExchangeStore ExchangeStore(DurabilityHarness h) =>
        new(h.Ctx, NullLogger<AgentToolExchangeStore>.Instance);

    /// <summary>Runs step 0, then RESUMES with a FRESH executor — the launcher's new DI scope, sharing the
    /// harness's store — and runs step 1. Deliberately not driven through the orchestrator: a terminal settle
    /// purges the run's rows, which is the very thing a resume fixture needs to survive.</summary>
    private static async Task<List<List<ChatMessage>>> RunThenResumeAsync(
        DurabilityHarness h, AgentRun run, params ChatMessage[][] firstStepRounds)
    {
        var ct = TestContext.Current.CancellationToken;
        h.SupportsTools = true;
        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                return ++h.Turns == 1 ? DriveToolRounds("reply 1", firstStepRounds) : DriveText("reply 2");
            });

        var ctx = new RunContext(run.Goal ?? "goal", RunProfile.Interactive);
        var launch = h.NewExecutor();
        launch.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await launch.BeginRunAsync(run, ctx, ct);
        await launch.ExecuteStepAsync(run, CarryStep(0), ctx, ct);

        var resumed = h.NewExecutor();
        resumed.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await resumed.BeginRunAsync(run, ctx, ct);
        await resumed.ExecuteStepAsync(run, CarryStep(1), ctx, ct);
        return captured;
    }

    private static int IndexOfResult(List<ChatMessage> request, string body) =>
        request.FindIndex(m => m.Contents.OfType<FunctionResultContent>().Any(r => (r.Result as string) == body));

    private static int IndexOfProse(List<ChatMessage> request, string text) =>
        request.FindIndex(m => m.Role == ChatRole.Assistant && m.Text.Contains(text));

    /// <summary>The reported amnesia: a park discarded every call and result taken before it, so the resumed
    /// step had neither the file it wrote nor the data it read, and asked the user for both.</summary>
    [Fact]
    public async Task AResumeSeedsCarriedExchangesBeforeTheReplyTheyBelongTo()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");

        var captured = await RunThenResumeAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "SKU-1001,Blue Widget,4,10,3.50")]);

        var resumed = captured[1];
        Assert.Contains("SKU-1001,Blue Widget,4,10,3.50", ResultBodies(resumed));
        Assert.Contains(resumed.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.Name == "read_file");

        var pair = IndexOfResult(resumed, "SKU-1001,Blue Widget,4,10,3.50");
        var prose = IndexOfProse(resumed, "reply 1");
        Assert.True(prose >= 0, "the resumed request lost the step-0 reply");
        Assert.True(pair >= 0 && pair < prose,
            $"the re-seeded pair sat at {pair} and the reply it belongs to at {prose}");
    }

    /// <summary>The re-seed is a one-way arrow INTO the model context: the cloud-synced chat must not grow a
    /// tool row or a result body because a resume replayed one.</summary>
    [Fact]
    public async Task AReSeededExchange_DoesNotReachThePersistedChat()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");

        var captured = await RunThenResumeAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "SKU-1001,Blue Widget,4,10,3.50")]);
        Assert.Contains("SKU-1001,Blue Widget,4,10,3.50", ResultBodies(captured[1]));

        var chat = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(chat!.Messages, m => m.Role == "tool");
        Assert.DoesNotContain(chat.Messages, m => (m.Content ?? string.Empty).Contains("SKU-1001"));
    }

    /// <summary>A corrupt or unreachable store degrades ONE resume to prose-only — today's behaviour — instead
    /// of failing every resume from the second await BeginRunAsync now makes.</summary>
    [Fact]
    public async Task AStoreFaultOnResume_DegradesToProseOnly_AndDoesNotFailTheRun()
    {
        using var h = new DurabilityHarness();
        var faulting = Substitute.For<IAgentToolExchangeStore>();
        faulting.ReadCarriedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("read boom"));
        h.Exchanges = faulting;
        var run = await h.NewRunAsync("inventory report");

        var captured = await RunThenResumeAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "SKU-1001")]);

        var resumed = captured[1];
        Assert.DoesNotContain(resumed.SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
        Assert.True(IndexOfProse(resumed, "reply 1") >= 0, "the prose transcript was lost too");
        Assert.Contains(h.ExecutorLog.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("carried tool exchanges"));
    }

    private static string Shape(IEnumerable<List<ChatMessage>> requests) =>
        string.Join("\n--\n", requests.Select(r => string.Join("\n", r.Select(Describe))));

    private static string Describe(ChatMessage m) =>
        m.Role + ": " + string.Join("|", m.Contents.Select(c => c switch
        {
            FunctionCallContent call => "call " + call.CallId + " " + call.Name,
            FunctionResultContent result => "result " + result.CallId + " " + result.Result,
            TextContent text => "text " + text.Text,
            _ => c.GetType().Name,
        }));

    /// <summary>Wiring a store changes nothing on a path that never parks, so every existing carry fact keeps
    /// meaning what it meant.</summary>
    [Fact]
    public async Task ANullExchangeStore_LeavesEveryExistingPathByteForByteUnchanged()
    {
        using var baseline = new DurabilityHarness();
        var baseRun = await baseline.NewRunAsync("inventory report");
        var withoutStore = await RunTwoStepsAsync(baseline, baseRun,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")]);

        using var h = new DurabilityHarness();
        var inert = Substitute.For<IAgentToolExchangeStore>();
        inert.ReadCarriedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentToolExchangeRow>>([]));
        h.Exchanges = inert;
        var run = await h.NewRunAsync("inventory report");
        var withStore = await RunTwoStepsAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")]);

        Assert.Equal(Shape(withoutStore), Shape(withStore));
    }

    /// <summary>The compaction seam is downstream of the re-seed, so a tool-free turn still drops the pairs.</summary>
    [Fact]
    public async Task AToolFreeTurnAfterAResume_IsStillNotHandedTheReSeededExchanges()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");
        var ct = TestContext.Current.CancellationToken;

        var captured = await RunThenResumeAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")]);
        // The premise: the resumed step DID get the pair, so the grace turn's emptiness is the strip.
        Assert.Contains("the csv rows", ResultBodies(captured[1]));

        var ctx = new RunContext("inventory report", RunProfile.Interactive);
        var grace = h.NewExecutor();
        grace.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await grace.BeginRunAsync(run, ctx, ct);
        await grace.RunGraceTurnAsync(run, ctx, ct);

        Assert.DoesNotContain(captured[2].SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
    }

    /// <summary>Clearing still runs downstream of the re-seed, so the post-resume context budget is the
    /// pre-park one — re-seeding full bodies does not defeat it.</summary>
    [Fact]
    public async Task PastTheKeptCount_AResumedRunStillClearsTheOldestResult()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");

        var rounds = Enumerable.Range(0, AgentToolCarryover.KeptResults + 1)
            .Select(i => new[] { ToolCall("c" + i, "read_file", "f" + i + ".csv"), ToolResult("c" + i, "body " + i) })
            .ToArray();

        var captured = await RunThenResumeAsync(h, run, rounds);
        var resumed = captured[1];

        Assert.Contains("[result cleared; call read_file on f0.csv again if you need it]", ResultBodies(resumed));
        Assert.DoesNotContain("body 0", ResultBodies(resumed));
        Assert.Contains("body " + AgentToolCarryover.KeptResults, ResultBodies(resumed));
    }

    /// <summary>The detokenized rows must not outlive the run — and a park is not terminal, so it keeps them.</summary>
    [Fact]
    public async Task ATerminalSettle_PurgesTheRunsExchanges_ButAParkDoesNot()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");
        var ct = TestContext.Current.CancellationToken;
        h.SupportsTools = true;
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(_ => ++h.Turns == 1
                ? DriveToolRounds("reply 1",
                    [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")])
                : DriveText("reply 2"));

        var ctx = new RunContext("inventory report", RunProfile.Interactive);
        var executor = h.NewExecutor();
        executor.Initialize(workspaceRoot: null, ["write_file"], h.Provider);
        await executor.BeginRunAsync(run, ctx, ct);
        await executor.ExecuteStepAsync(run, CarryStep(0), ctx, ct);

        // A park never reaches EndRunAsync, which is exactly why the rows have to survive one.
        Assert.NotEmpty(await store.ReadCarriedAsync(run.Id, ct));

        await executor.EndRunAsync(run, ctx, cancelled: false, failed: false, ct);
        Assert.Empty(await store.ReadCarriedAsync(run.Id, ct));
    }

    /// <summary>A stale anchor must not be able to silently drop a group: the split is total, so it lands at
    /// the tail with the unanchored ones.</summary>
    [Fact]
    public async Task AnOrphanedAnchor_StillReachesTheModelContextAtTheTail()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");
        var ct = TestContext.Current.CancellationToken;

        var orphanStep = Guid.NewGuid();
        await store.RecordAsync(run.Id, orphanStep, 1,
            [ToolCall("c9", "read_file", "orphan.csv"), ToolResult("c9", "the orphan rows")], ct);
        await store.SealStepAsync(run.Id, orphanStep, Guid.NewGuid(), ct);

        var captured = await RunThenResumeAsync(h, run,
            [ToolCall("c1", "read_file", "inventory.csv"), ToolResult("c1", "the csv rows")]);
        var resumed = captured[1];

        Assert.Contains("the orphan rows", ResultBodies(resumed));
        Assert.True(IndexOfResult(resumed, "the orphan rows") > IndexOfProse(resumed, "reply 1"),
            "an orphaned group belongs at the tail, after every chat row");
    }

    // ---- the reported amnesia, reproduced through the REAL gate: park mid-step, resume, read the request ----

    /// <summary>Routes <paramref name="toolName"/> as a DEFERRED write — the only shape that reaches the gate —
    /// and every other call as a read, which short-circuits above it.</summary>
    private static void ArmApprovalPark(DurabilityHarness h, string toolName)
    {
        // Explicit rather than left to the substitute default: an External class is refused instead of parked.
        h.Plugins.IsMcpTool(Arg.Any<string>()).Returns(false);
        h.Plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var call = ci.Arg<FunctionCallContent>();
                if (!string.Equals(call.Name, toolName, StringComparison.Ordinal))
                    return ((object? Result, PluginToolCall? PendingAction)?)(ReadRows(call), null);

                return ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                    call.Name, Guid.NewGuid(), "files", "write a file", null,
                    () => Task.FromResult<object?>("written")));
            });
    }

    private static string ReadRows(FunctionCallContent call) => "rows of " + call.Arguments?["path"];

    /// <summary>One round put to the REAL gate, returning the call/result pair the production loop would have
    /// captured — including a park, whose advisory comes back as the result.</summary>
    private static async Task<ChatMessage[]> GateRoundAsync(
        ToolCallHandler handler, ToolLoopStopSignal stop, int round, string callId, string tool, string path)
    {
        var call = new FunctionCallContent(callId, tool, new Dictionary<string, object?> { ["path"] = path });
        var result = await handler(call, new ToolDispatchContext(round, stop));
        return
        [
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]),
        ];
    }

    /// <summary>One signal per round, and the round is yielded BEFORE the loop ends — that yield is the only
    /// source of a pre-park exchange. The visible text still flows past a park.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> DriveGatedRounds(
        ToolCallHandler handler, string answer, params (string CallId, string Tool, string Path)[] rounds)
    {
        await Task.Yield();
        for (var i = 0; i < rounds.Length; i++)
        {
            var (callId, tool, path) = rounds[i];
            var stop = new ToolLoopStopSignal();
            yield return new ToolRoundCompleted();
            yield return new ToolRoundExchange(i + 1, await GateRoundAsync(handler, stop, i + 1, callId, tool, path));
            if (stop.IsStopRequested)
                break;
        }

        yield return new TextDelta(answer);
        yield return new Finished(null, "test-model");
    }

    /// <summary>Step 0 reads and completes; step 1 reads and then parks on an ungranted write; a FRESH executor
    /// resumes it. The launch's result rides out so a fixture that stopped parking fails loudly.</summary>
    private static async Task<(List<List<ChatMessage>> Requests, StepTurnResult Parked)> ParkThenResumeAsync(
        DurabilityHarness h, AgentRun run)
    {
        var ct = TestContext.Current.CancellationToken;
        h.SupportsTools = true;
        ArmApprovalPark(h, "write_file");
        var captured = new List<List<ChatMessage>>();
        h.Ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                captured.Add([.. (IList<ChatMessage>)ci[0]]);
                var handler = ci.ArgAt<ToolCallHandler>(3);
                return ++h.Turns switch
                {
                    1 => DriveGatedRounds(handler, "reply 1", ("c0", "read_file", "inventory.csv")),
                    2 => DriveGatedRounds(handler, "reply 2",
                        ("c1", "read_file", "orders.csv"), ("c2", "write_file", "report.md")),
                    _ => DriveText("reply 3"),
                };
            });

        var ctx = new RunContext(run.Goal ?? "goal", RunProfile.Interactive);
        var step1 = CarryStep(1);
        var launch = h.NewExecutor();
        // grantedWrites EMPTY on purpose: nothing above the park arm in ToolAutonomy.Resolve may authorize it.
        launch.Initialize(workspaceRoot: null, [], h.Provider, policy: null, canPark: true);
        await launch.BeginRunAsync(run, ctx, ct);
        await launch.ExecuteStepAsync(run, CarryStep(0), ctx, ct);
        var parked = await launch.ExecuteStepAsync(run, step1, ctx, ct);

        var resumed = h.NewExecutor();
        resumed.Initialize(workspaceRoot: null, [], h.Provider, policy: null, canPark: true);
        await resumed.BeginRunAsync(run, ctx, ct);
        await resumed.ExecuteStepAsync(run, step1, ctx, ct);
        // Both facts below index the RESUMED request, and one of them asserts an absence.
        Assert.Equal(3, captured.Count);
        return (captured, parked);
    }

    private static int IndexOfStepInstruction(List<ChatMessage> request) =>
        request.FindIndex(m => m.Role == ChatRole.User && m.Text.StartsWith("Execute step ", StringComparison.Ordinal));

    /// <summary>Pinned where the amnesia actually bit: the abandoned attempt's exchanges must reach the REBUILT
    /// REQUEST, not merely the store — a row nobody sends is a row the model still does not have.</summary>
    [Fact]
    public async Task ParkedMidStep_TheResumedStepsRequestCarriesThePreParkToolExchange()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");

        var (requests, parked) = await ParkThenResumeAsync(h, run);

        Assert.Equal("write_file", parked.ApprovalRequiredTool);
        var resumed = requests[2];
        Assert.Contains("rows of inventory.csv", ResultBodies(resumed));
        Assert.Contains("rows of orders.csv", ResultBodies(resumed));
        Assert.Equal(2, resumed.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count(c => c.Name == "read_file"));
    }

    /// <summary>The negative control, and what makes the fact above a regression test: without the store the
    /// resumed step meets the prose alone, exactly as the reported run did.</summary>
    [Fact]
    public async Task ParkedMidStep_WithNoExchangeStore_TheResumedStepSeesProseAlone()
    {
        using var h = new DurabilityHarness();
        var run = await h.NewRunAsync("inventory report");

        var (requests, parked) = await ParkThenResumeAsync(h, run);

        Assert.Equal("write_file", parked.ApprovalRequiredTool);
        var resumed = requests[2];
        Assert.DoesNotContain(resumed.SelectMany(m => m.Contents), c => c is FunctionCallContent or FunctionResultContent);
        Assert.True(IndexOfProse(resumed, "reply 1") >= 0, "the prose transcript was lost too");
    }

    /// <summary>Order is the whole value: the completed step's pair sits before the reply it belongs to, and the
    /// abandoned attempt's at the tail, where ClearOldResults' newest-by-position window keeps it verbatim.</summary>
    [Fact]
    public async Task ParkedMidStep_TheReSeededExchangesReadInTheOrderTheyHappened()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");

        var (requests, _) = await ParkThenResumeAsync(h, run);
        var resumed = requests[2];

        var stepZero = IndexOfResult(resumed, "rows of inventory.csv");
        var prose = IndexOfProse(resumed, "reply 1");
        var prePark = IndexOfResult(resumed, "rows of orders.csv");
        var instruction = IndexOfStepInstruction(resumed);
        Assert.True(stepZero >= 0 && prose > stepZero && prePark > prose && instruction > prePark,
            $"step 0 at {stepZero}, reply at {prose}, pre-park at {prePark}, instruction at {instruction}");
    }

    /// <summary>The park/resume extension of CarriedToolExchanges_DoNotReachThePersistedChat: the payload is
    /// model context only, and the chat that syncs to the cloud must not grow a tool row because a run parked.</summary>
    [Fact]
    public async Task ParkedMidStep_ThePayloadNeverReachesTheCloudSyncedChat()
    {
        using var h = new DurabilityHarness();
        using var store = ExchangeStore(h);
        h.Exchanges = store;
        var run = await h.NewRunAsync("inventory report");

        var (requests, _) = await ParkThenResumeAsync(h, run);
        // The premise: the request DID carry both bodies, so the chat's emptiness is the guardrail holding.
        Assert.Contains("rows of inventory.csv", ResultBodies(requests[2]));
        Assert.Contains("rows of orders.csv", ResultBodies(requests[2]));

        var chat = await h.Chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        // The resume settled and rewrote the chat, so the absences below are about a chat that was written.
        Assert.Contains(chat!.Messages, m => (m.Content ?? string.Empty).Contains("reply 3"));
        Assert.DoesNotContain(chat.Messages, m => m.Role == "tool");
        Assert.DoesNotContain(chat.Messages, m => (m.Content ?? string.Empty).Contains("rows of "));
    }
}
