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
/// The persona's model type reaches an interactive step turn as <c>metadata.pia_persona_type</c>.
/// <c>StepTurnSpec.ModelType</c> is trailing and defaulted, so dropping it from
/// <c>LiveTurnExecutor.BuildSpec</c> or from <c>ChatSession.RunStepTurnAsync</c>'s relay compiles and
/// silently routes every step on the mode default — a persona picked FOR its model reaching the proxy as
/// a system prompt and nothing else.
/// </summary>
public sealed class LiveTurnExecutorPersonaRoutingTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    private string? _lastModelType;
    private bool _turnRan;

    public LiveTurnExecutorPersonaRoutingTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                _lastModelType = ci.ArgAt<string?>(6);
                _turnRan = true;
                return Reply();
            });
    }

    private static async IAsyncEnumerable<ChatStreamItem> Reply()
    {
        await Task.Yield();
        yield return new TextDelta("done");
    }

    private static AiProvider Provider() =>
        new() { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => false);

    private LiveTurnExecutor BuildExecutor(
        ChatSession session, string? runModelType, StepPersonaResolver? stepPersonas = null, Persona? runPersona = null)
    {
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            return new LiveTurnExecutor(
                session,
                _ => false,
                new PersonaAttribution(runPersona?.Id ?? Guid.NewGuid(), runPersona?.Name ?? "Pia", "🤖"),
                Provider(),
                new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false,
                    PersonaId: runPersona?.Id, ModelType: runModelType),
                tokenizationEnabled: false,
                stepPersonas: stepPersonas,
                runPersona: runPersona);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }

    private static AgentRun Run() => new() { Id = Guid.NewGuid(), ChatId = Guid.NewGuid(), Goal = "the goal" };

    [Fact]
    public async Task AStepTurn_SendsTheRunPersonaModelType()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, runModelType: "code");

        await executor.ExecuteStepAsync(
            Run(), new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S", Intent = "do it" },
            new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(_turnRan);
        Assert.Equal("code", _lastModelType);
    }

    /// <summary>The degrade turn is the run's, so it routes on the run persona's type too.</summary>
    [Fact]
    public async Task TheFallbackTurn_SendsTheRunPersonaModelType()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, runModelType: "code");

        await executor.RunSingleTurnFallbackAsync(
            Run(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(_turnRan);
        Assert.Equal("code", _lastModelType);
    }

    /// <summary>A step on its own persona routes on THAT persona's type — reading the run's would defeat the
    /// point of assigning a specialist chosen for its model.</summary>
    [Fact]
    public async Task AStepWithItsOwnPersona_SendsThatPersonaModelType()
    {
        var runPersona = new Persona { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "sys", ModelType = "general" };
        var specialist = new Persona { Id = Guid.NewGuid(), Name = "Specialist", SystemPrompt = "spec", ModelType = "code" };

        var settingsService = Substitute.For<ISettingsService>();
        var settings = new AppSettings();
        settings.SetAgentPersonaRoster(UserOperatingMode.Personal, [specialist.Id]);
        settingsService.GetSettingsAsync().Returns(settings);

        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns([specialist]);
        personas.GetPersonaAsync(specialist.Id).Returns(specialist);

        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(Provider());

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Is<Persona>(p => p.Id == specialist.Id), Arg.Any<AiProvider>(),
                Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>(), unattended: Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("specialist system", null, SupportsTools: false, WebSearchActive: false,
                PersonaId: specialist.Id, ModelType: specialist.ModelType));

        var resolver = new StepPersonaResolver(
            personas, providers, composer, settingsService, NullLogger<StepPersonaResolver>.Instance);

        var session = CreateSession();
        var executor = BuildExecutor(session, runModelType: "general", stepPersonas: resolver, runPersona: runPersona);

        await executor.ExecuteStepAsync(
            Run(),
            new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S", Intent = "do it", AssignedPersonaId = specialist.Id },
            new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(_turnRan);
        Assert.Equal("code", _lastModelType);
    }
}
