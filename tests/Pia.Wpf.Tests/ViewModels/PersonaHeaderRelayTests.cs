using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The <c>X-Pia-Persona</c> relay, end to end on the client side of it: the composer puts the resolved
/// persona on the turn setup, and every turn path passes it down to <see cref="IAiClientService"/>.
/// <para>
/// Written because the transport test (<c>PiaCloudChatClientPersonaHeaderTests</c>) only pins the last hop
/// — it constructs the client with an explicit id — while every other mock in the suite widened to
/// <c>Arg.Any&lt;Guid?&gt;()</c>. Without these assertions any of the relay arguments could be reverted to
/// <c>null</c> and the whole suite would stay green with the header silently never sent.
/// </para>
/// </summary>
public sealed class PersonaHeaderRelayTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    /// <summary>Every managedPersonaId the AI client was called with, in call order (argument index 5).</summary>
    private readonly List<Guid?> _relayed = [];

    public PersonaHeaderRelayTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _relayed.Add(ci.ArgAt<Guid?>(5));
                return Stream(new TextDelta("ok"), new Finished(null, "m"));
            });
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private static AiProvider Provider() => new()
    {
        Name = "Test",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
    };

    private static ChatTurnRequest BuildRequest(ChatSession session, Guid? personaId)
    {
        var user = new AssistantMessage(ChatRole.User, "hi");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        return new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = Provider(),
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false, PersonaId: personaId),
            AtCommands = [],
            TokenizationEnabled = false,
        };
    }

    [Fact]
    public void PrepareTurn_PutsTheResolvedPersonaIdOnTheTurnSetup()
    {
        // The single place the persona is known on every turn path — if it is dropped here, all three
        // relays below carry null however correct their own wiring is.
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(TargetLanguage.EN);
        var plugins = Substitute.For<IPluginService>();
        plugins.GetAllTools().Returns([]);
        plugins.GetCombinedSystemPromptAdditions().Returns(string.Empty);
        var composer = new AssistantPromptComposer(localization, plugins);
        var persona = new Persona
        {
            Name = "Brandvoice",
            SystemPrompt = "You are the brand voice editor.",
            ToolScope = PersonaToolScope.Full,
            IsManaged = true,
        };

        var setup = composer.PrepareTurn(persona, Provider(), [], tokenizationEnabled: false);

        Assert.Equal(persona.Id, setup.PersonaId);
    }

    [Fact]
    public async Task InteractiveTurn_SendsTheTurnSetupsPersonaId()
    {
        // The primary path: what the user sees when they pick a persona in the chat picker.
        var personaId = Guid.NewGuid();
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session, personaId), CancellationToken.None);

        Assert.Equal(personaId, Assert.Single(_relayed));
    }

    [Fact]
    public async Task InteractiveTurn_WithNoResolvedPersona_SendsNull()
    {
        // Null ⇒ the transport omits the header entirely. Pinned so "always send something" never creeps in.
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session, personaId: null), CancellationToken.None);

        Assert.Null(Assert.Single(_relayed));
    }

    [Fact]
    public async Task StepTurn_SendsTheStepsOwnPersonaId()
    {
        // A planned step can run under a different persona than the run default (Batch 07), so the header
        // follows the STEP's attribution rather than the run's turn setup — which this path never even sees.
        var stepPersonaId = Guid.NewGuid();
        var spec = new StepTurnSpec(
            RunId: Guid.NewGuid(),
            Ordinal: 0,
            Intent: "do the thing",
            ExpectedArtifact: "artifact",
            SystemPrompt: "system",
            Persona: new PersonaAttribution(stepPersonaId, "Analyst", "🔍"),
            Provider: Provider(),
            Tools: null,
            SupportsTools: false,
            WebSearchActive: false,
            TokenizationEnabled: false);

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));

        var result = await session.RunStepTurnAsync(spec, new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(stepPersonaId, Assert.Single(_relayed));
    }
}
