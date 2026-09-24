using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The one-shot "Draft with AI" buttons offer no picker, so they run as whoever the Assistant is currently
/// set to — otherwise a user who keeps the Assistant on a private persona still has the draft leave on the
/// default model. It routes three ways at once: the provider the persona pins, the <c>X-Pia-Persona</c> id,
/// and the model-type hint the cloud routes on.
/// </summary>
public sealed class SparkleDraftPersonaRoutingTests
{
    private static readonly Guid PersonaProvider = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string RoutineJson = """
        {"name":"N","goal":"G","recurrence":"daily","dayOfWeek":"","timeOfDay":"08:00",
         "effort":"low","needsWebSearch":false,"tools":[]}
        """;

    private const string PersonaJson = """
        {"name":"N","tagline":"T","systemPrompt":"You are N.","guardrails":"","outputFormat":"",
         "archetype":"analyst","emoji":"📊","accentColor":"#2962FF","expertise":["x"]}
        """;

    private const string TemplateJson = """{"name":"N","description":"D","prompt":"Rewrite it."}""";

    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private static Persona APersona(Guid? preferredProvider, string? modelType) => new()
    {
        Name = "Private one",
        SystemPrompt = "irrelevant here",
        PreferredProviderId = preferredProvider,
        ModelType = modelType,
    };

    // Typed explicitly: PiaCloud is the enum's zero value, and it is the one branch that carries no persona.
    private static AiProvider AProvider(Guid id, string name, AiProviderType type = AiProviderType.OpenAI) =>
        new() { Id = id, Name = name, Endpoint = "https://example.invalid", ProviderType = type };

    private static async IAsyncEnumerable<ChatStreamItem> Reply(string text)
    {
        yield return new TextDelta(text);
        await Task.CompletedTask;
    }

    private TextOptimizationService CreateSut(Persona? assistantPersona, string reply)
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(assistantPersona!);
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>())
            .Returns(AProvider(Guid.NewGuid(), "mode default"));
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>())
            .Returns(_ => Reply(reply));

        return new TextOptimizationService(
            Substitute.For<ITemplateService>(), _providers, Substitute.For<IHistoryService>(), _ai,
            _personas, _settings, NullLogger<TextOptimizationService>.Instance);
    }

    /// <summary>The Assistant's persona is read for its own mode, not for the mode the draft happens to
    /// default its provider to — a template draft defaults to Optimize but is still drafted by the Assistant.</summary>
    [Fact]
    public async Task TheAssistantsPersona_IsTheOneResolved_EvenForATemplateDraft()
    {
        var sut = CreateSut(APersona(preferredProvider: null, modelType: "private"), TemplateJson);

        await sut.GenerateTemplateDraftAsync("terse and formal");

        await _personas.Received(1).ResolveActiveAsync(WindowMode.Assistant, Arg.Any<UserOperatingMode>());
    }

    [Fact]
    public async Task APersonaPin_BeatsTheModeDefault()
    {
        var pinned = AProvider(PersonaProvider, "the persona's own");
        var sut = CreateSut(APersona(PersonaProvider, "private"), RoutineJson);
        _providers.GetProviderAsync(PersonaProvider).Returns(pinned);

        await sut.GenerateRoutineDraftAsync("tidy my inbox", []);

        _ai.Received().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Is<AiProvider>(p => p.Id == PersonaProvider),
            Arg.Any<IList<AITool>?>(), Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    [Fact]
    public async Task ARoutineDraft_CarriesThePersonasIdAndModelType()
    {
        var persona = APersona(preferredProvider: null, modelType: "private");
        var sut = CreateSut(persona, RoutineJson);

        await sut.GenerateRoutineDraftAsync("tidy my inbox", []);

        _ai.Received().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Is<Guid?>(id => id == persona.Id), Arg.Is<string?>(t => t == "private"),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    [Fact]
    public async Task APersonaDraft_CarriesThePersonasIdAndModelType()
    {
        var persona = APersona(preferredProvider: null, modelType: "private");
        var sut = CreateSut(persona, PersonaJson);

        await sut.GeneratePersonaDraftAsync("a tax advisor");

        _ai.Received().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Is<Guid?>(id => id == persona.Id), Arg.Is<string?>(t => t == "private"),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    [Fact]
    public async Task ATemplateDraft_CarriesThePersonasIdAndModelType()
    {
        var persona = APersona(preferredProvider: null, modelType: "private");
        var sut = CreateSut(persona, TemplateJson);

        await sut.GenerateTemplateDraftAsync("terse and formal");

        _ai.Received().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Is<Guid?>(id => id == persona.Id), Arg.Is<string?>(t => t == "private"),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    /// <summary>A blank hint is normalized rather than sent empty, the way every other read path does it.</summary>
    [Fact]
    public async Task ABlankModelType_IsSentAsTheDefaultHint()
    {
        var sut = CreateSut(APersona(preferredProvider: null, modelType: "   "), RoutineJson);

        await sut.GenerateRoutineDraftAsync("tidy my inbox", []);

        _ai.Received().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
            Arg.Is<string?>(t => t == Persona.DefaultModelType),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    /// <summary>The Pia Cloud prompt route reads no persona header, so a template draft there is the one
    /// draft the persona cannot reach. Pinned so the gap stays visible rather than being read as working.</summary>
    [Fact]
    public async Task ATemplateDraftOnPiaCloud_CannotCarryThePersona()
    {
        var sut = CreateSut(APersona(preferredProvider: null, modelType: "private"), TemplateJson);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Optimize)
            .Returns(AProvider(Guid.NewGuid(), "cloud", AiProviderType.PiaCloud));
        _ai.GeneratePromptViaPiaCloudAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("Rewrite it.");

        var draft = await sut.GenerateTemplateDraftAsync("terse and formal");

        Assert.Equal("Rewrite it.", draft.Prompt);
        // The prompt route takes no persona argument at all, so the proof is that the draft never reaches
        // the chat path — the only one that carries the id and the model-type hint.
        _ai.DidNotReceive().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    /// <summary>A caller that pins a provider outranks the persona, so the picker in the routines editor
    /// still decides the model while the persona keeps deciding the routing.</summary>
    [Fact]
    public async Task AnExplicitProviderPin_OutranksThePersonasOwn()
    {
        var pinned = AProvider(Guid.NewGuid(), "the caller's");
        var persona = APersona(PersonaProvider, "private");
        var sut = CreateSut(persona, RoutineJson);
        _providers.GetProviderAsync(pinned.Id).Returns(pinned);
        _providers.GetProviderAsync(PersonaProvider).Returns(AProvider(PersonaProvider, "the persona's own"));

        await sut.GenerateRoutineDraftAsync("tidy my inbox", [], pinned.Id);

        _ai.Received().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Is<AiProvider>(p => p.Id == pinned.Id),
            Arg.Any<IList<AITool>?>(), Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Is<Guid?>(id => id == persona.Id), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }
}
