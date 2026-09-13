using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The persona chosen for an interview is what picks the model — that is the whole point of offering it, so a
/// user can keep the interview on a private one. It routes three ways at once: the provider it pins, the
/// <c>X-Pia-Persona</c> id, and the model-type hint the cloud routes on.
/// </summary>
public sealed class AdvancedCreationPersonaRoutingTests
{
    private static readonly Guid PersonaProvider = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private static Persona APersona(Guid? preferredProvider, string? modelType) => new()
    {
        Name = "Private one",
        SystemPrompt = "irrelevant here",
        PreferredProviderId = preferredProvider,
        ModelType = modelType,
    };

    private static AiProvider AProvider(Guid id, string name) => new() { Id = id, Name = name, Endpoint = "https://example.invalid" };

    private static async IAsyncEnumerable<ChatStreamItem> Reply(string text)
    {
        yield return new TextDelta(text);
        await Task.CompletedTask;
    }

    private AdvancedCreationService CreateSut(AiProvider modeDefault)
    {
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(modeDefault);
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>())
            // A question set, not an empty one: "ask" with no questions is a reply the parser refuses.
            .Returns(_ => Reply(
                """{"state":"ask","summary":"s","questions":[{"id":"q","kind":"text","label":"L","optional":false}]}"""));

        return new AdvancedCreationService(
            _providers, _ai, NullLogger<AdvancedCreationService>.Instance);
    }

    [Fact]
    public async Task APersonaPin_BeatsTheModeDefault()
    {
        var pinned = AProvider(PersonaProvider, "the persona's own");
        _providers.GetProviderAsync(PersonaProvider).Returns(pinned);
        var sut = CreateSut(AProvider(Guid.NewGuid(), "mode default"));
        var session = new AdvancedCreationSession(AdvancedCreationModes.Persona())
        {
            Persona = APersona(PersonaProvider, "private"),
        };

        await sut.StartAsync(session, "design me something", TestContext.Current.CancellationToken);

        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Is<AiProvider>(p => p.Id == PersonaProvider),
            Arg.Any<IList<AITool>?>(), Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    [Fact]
    public async Task ThePersonasIdAndModelType_RideTheTurn()
    {
        var persona = APersona(preferredProvider: null, modelType: "private");
        var sut = CreateSut(AProvider(Guid.NewGuid(), "mode default"));
        var session = new AdvancedCreationSession(AdvancedCreationModes.Template()) { Persona = persona };

        await sut.StartAsync(session, "design me something", TestContext.Current.CancellationToken);

        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Is<Guid?>(id => id == persona.Id), Arg.Is<string?>(t => t == "private"),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    /// <summary>No persona is the default row, and it must leave the turn exactly as it was before the picker
    /// existed — no header, no routing hint.</summary>
    [Fact]
    public async Task NoPersona_SendsNoRoutingAtAll()
    {
        var sut = CreateSut(AProvider(Guid.NewGuid(), "mode default"));
        var session = new AdvancedCreationSession(AdvancedCreationModes.Routine([]));

        await sut.StartAsync(session, "design me something", TestContext.Current.CancellationToken);

        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
            Arg.Is<Guid?>(id => id == null), Arg.Is<string?>(t => t == null),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }

    /// <summary>A blank hint is normalized rather than sent empty, the way every other read path does it.</summary>
    [Fact]
    public async Task ABlankModelType_IsSentAsTheDefaultHint()
    {
        var sut = CreateSut(AProvider(Guid.NewGuid(), "mode default"));
        var session = new AdvancedCreationSession(AdvancedCreationModes.Template())
        {
            Persona = APersona(preferredProvider: null, modelType: "   "),
        };

        await sut.StartAsync(session, "design me something", TestContext.Current.CancellationToken);

        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
            Arg.Is<string?>(t => t == Persona.DefaultModelType),
            Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>());
    }
}
