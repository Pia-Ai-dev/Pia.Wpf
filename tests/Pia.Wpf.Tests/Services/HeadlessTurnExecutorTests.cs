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
            engine, chats, settings, personas, providers, composer, titles, plugins, TokenMapFactory,
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
        var orchestrator = new AgentRunOrchestrator(runs, planner, NullLogger<AgentRunOrchestrator>.Instance);

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
    public async Task BeginRun_StripsMcpTools_AndHonorsProviderOverride_AndGrantedWrites()
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
        plugins.IsMcpTool("mcp_search").Returns(true);
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
            engine, chats, settings, personas, providers, composer, titles, plugins, TokenMapFactory,
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

        var orchestrator = new AgentRunOrchestrator(runs, new SingleStepPlanner(), NullLogger<AgentRunOrchestrator>.Instance);
        await orchestrator.RunAsync(run, executor, persona, defaultProvider, RunProfile.Interactive, TestContext.Current.CancellationToken);

        // MCP tool stripped from the executor's tool list (G-2 capability removal).
        Assert.NotNull(capturedTools);
        Assert.DoesNotContain(capturedTools!, t => t.Name == "mcp_search");
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
            engine, chats, settings, personas, providers, composer, titles, plugins, TokenMapFactory,
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

        var orchestrator = new AgentRunOrchestrator(runs, new SingleStepPlanner(), NullLogger<AgentRunOrchestrator>.Instance);
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
}
