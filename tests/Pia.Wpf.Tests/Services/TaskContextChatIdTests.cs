using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Every surface that sets the ambient turn context carries the CHAT id. On a run surface
/// <see cref="TaskContext.TaskId"/> is the RUN id, so asserting the value — not just that one is
/// present — is the whole point: a reader keyed on TaskId would answer with a different id and
/// still look correct.
/// </summary>
public sealed class TaskContextChatIdTests : IDisposable
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly List<TaskContext?> _observed = [];

    public TaskContextChatIdTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    public void Dispose() => TaskAmbient.Current = null;

    private static AiProvider Provider() =>
        new() { Id = Guid.NewGuid(), Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI };

    // Runs inside the exchange, where the turn's ambient is live.
    private async IAsyncEnumerable<ChatStreamItem> ObserveThenReply()
    {
        _observed.Add(TaskAmbient.Current);
        await Task.Yield();
        yield return new TextDelta("ok");
        yield return new Finished(null, "test-model");
    }

    private ChatSession CreateSession(Guid chatId)
    {
        var session = new ChatSession(
            _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);
        session.Id = chatId;
        return session;
    }

    private void InteractiveAiReturnsAReply() =>
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => ObserveThenReply());

    [Fact]
    public async Task InteractiveTurn_CarriesTheChatId()
    {
        InteractiveAiReturnsAReply();
        var chatId = Guid.NewGuid();
        var session = CreateSession(chatId);

        var user = new AssistantMessage(ChatRole.User, "hi");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);

        await session.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = Provider(),
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        }, TestContext.Current.CancellationToken);

        var ambient = Assert.Single(_observed);
        Assert.NotNull(ambient);
        Assert.Equal(chatId, ambient!.Value.ChatId);
        Assert.Equal(chatId, ambient.Value.TaskId);
    }

    [Fact]
    public async Task StepTurn_CarriesTheChatId_NotTheRunId()
    {
        InteractiveAiReturnsAReply();
        var chatId = Guid.NewGuid();
        var session = CreateSession(chatId);
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));

        var spec = new StepTurnSpec(
            RunId: Guid.NewGuid(),
            Ordinal: 0,
            Intent: "do the thing",
            ExpectedArtifact: null,
            SystemPrompt: "system",
            Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", null),
            Provider: Provider(),
            Tools: null,
            SupportsTools: false,
            WebSearchActive: false,
            TokenizationEnabled: false);

        await session.RunStepTurnAsync(
            spec, new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        var ambient = Assert.Single(_observed);
        Assert.NotNull(ambient);
        Assert.Equal(chatId, ambient!.Value.ChatId);
        Assert.Equal(spec.RunId, ambient.Value.TaskId);
        Assert.NotEqual(spec.RunId, ambient.Value.ChatId);
    }

    [Fact]
    public async Task BackgroundTurn_CarriesTheChatId_NotTheRunId()
    {
        var ct = TestContext.Current.CancellationToken;
        InteractiveAiReturnsAReply();

        SyncAssistantChat? stub = null;
        var chats = Substitute.For<IAssistantChatService>();
        chats.SaveAsync(Arg.Do<SyncAssistantChat>(c => stub ??= c), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        AgentRun? created = null;
        var runs = Substitute.For<IAgentRunService>();
        runs.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var run = new AgentRun
                {
                    Id = Guid.NewGuid(),
                    ChatId = ci.Arg<AgentRunCreateRequest>().ChatId,
                    RunShape = RunShape.SingleTurn,
                    State = AgentRunState.Running,
                };
                created = run;
                return Task.FromResult(run);
            });

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
        personas.GetPersonasAsync().Returns(Task.FromResult<IReadOnlyList<Persona>>([]));
        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        var runner = new BackgroundAssistantTurnRunner(
            _ai, _plugins, _permissions, composer, personas, chats, titles, settings,
            TokenMapFactory, runs, new ExecutingRunStore(),
            NullLogger<BackgroundAssistantTurnRunner>.Instance);

        var result = await runner.RunAsync(
            new BackgroundTurnRequest { Prompt = "go", Provider = Provider() }, ct);

        Assert.True(result.Succeeded);
        Assert.NotNull(stub);
        Assert.NotNull(created);

        var ambient = Assert.Single(_observed);
        Assert.NotNull(ambient);
        Assert.Equal(stub!.Id, ambient!.Value.ChatId);
        Assert.Equal(created!.Id, ambient.Value.TaskId);
        Assert.NotEqual(created.Id, ambient.Value.ChatId);
    }

    [Fact]
    public async Task HeadlessTurn_CarriesTheChatId_NotTheRunId()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var sqlite = new SqliteContext(Path.Combine(dir, "history.db"));
        using var runs = new AgentRunService(sqlite, NullLogger<AgentRunService>.Instance);
        using var chats = new AssistantChatService(sqlite, runs);

        var provider = Provider();
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };

        // The headless engine passes a context budget, so the stub has to name that argument too.
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(_ => ObserveThenReply());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
                Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        ITokenMapService TokenMapFactory() => Substitute.For<ITokenMapService>();

        var engine = new BackgroundAssistantTurnRunner(
            _ai, _plugins, _permissions, composer, personas, chats, titles, settings,
            TokenMapFactory, runs, new ExecutingRunStore(),
            NullLogger<BackgroundAssistantTurnRunner>.Instance);
        var executor = new HeadlessTurnExecutor(
            engine, chats, settings, personas, providers, composer, titles, TokenMapFactory,
            NullLogger<HeadlessTurnExecutor>.Instance);

        // The FK parent chat before the run row.
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
        }, ct);
        var run = await runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User, Goal: "the goal"), ct);

        var runContext = new RunContext("the goal", RunProfile.Scheduled);
        await executor.BeginRunAsync(run, runContext, ct);
        await executor.RunSingleTurnFallbackAsync(run, runContext, ct);

        var ambient = Assert.Single(_observed);
        Assert.NotNull(ambient);
        Assert.Equal(chatId, ambient!.Value.ChatId);
        Assert.Equal(run.Id, ambient.Value.TaskId);
        Assert.NotEqual(run.Id, ambient.Value.ChatId);

        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
