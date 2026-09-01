using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The step-tool augmentation is a single line in <c>LiveTurnExecutor.BuildSpec</c> and dropping it
/// compiles; <c>ChatSessionStepOutcomeSignalTests</c> hand-builds its spec, so nothing else covers it.</summary>
public sealed class LiveTurnExecutorStepToolScopeTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    private IList<AITool>? _lastTools;

    public LiveTurnExecutorStepToolScopeTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                _lastTools = ci.ArgAt<IList<AITool>?>(2);
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

    /// <summary>The run-level tool list — the cached instance the augmentation must copy rather than mutate,
    /// because it is the very list this session's ordinary chat turns use.</summary>
    private readonly IList<AITool> _runTools =
        [AIFunctionFactory.Create(() => "ok", "unrelated_tool", "not the step-result tool")];

    private LiveTurnExecutor BuildExecutor(ChatSession session, StepPersonaResolver? stepPersonas, Persona? runPersona)
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
                new AssistantTurnSetup("system", _runTools, SupportsTools: true, WebSearchActive: false),
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

    /// <summary>An interactive Planned step is offered the tool, and the run's cached tool list survives
    /// untouched — an in-place add would leak a step-only tool into every later chat turn on this session.</summary>
    [Fact]
    public async Task AStepTurn_IsOfferedTheTool()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, stepPersonas: null, runPersona: null);

        await executor.ExecuteStepAsync(
            Run(), new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S", Intent = "do it" },
            new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(AgentStepTools.OffersStepResultTool(_lastTools));
        Assert.False(AgentStepTools.OffersStepResultTool(_runTools),
            "the session's cached tool list must not have been mutated");
    }

    /// <summary>The planner-degrade turn produces no <c>AgentStep</c> row, so there is no Done/Failed for a
    /// declaration to decide — it is deliberately not offered the tool, matching the headless executor.</summary>
    [Fact]
    public async Task TheFallbackTurn_IsNotOfferedTheTool()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, stepPersonas: null, runPersona: null);

        await executor.RunSingleTurnFallbackAsync(
            Run(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.NotNull(_lastTools); // the turn really ran
        Assert.False(AgentStepTools.OffersStepResultTool(_lastTools));
    }

    /// <summary>A step that resolved its own persona runs on a different <c>AssistantTurnSetup</c>; augmenting
    /// <c>_turnSetup</c> instead compiles, keeps the facts above green, and strands the specialist steps.</summary>
    [Fact]
    public async Task AStepWithItsOwnPersona_IsStillOfferedTheTool()
    {
        var runPersona = new Persona { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "sys" };
        var specialist = new Persona { Id = Guid.NewGuid(), Name = "Specialist", SystemPrompt = "spec" };

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
                Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new AssistantTurnSetup(
                "specialist system",
                [AIFunctionFactory.Create(() => "ok", "specialist_only_tool", "only the specialist has this")],
                SupportsTools: true,
                WebSearchActive: false));

        var resolver = new StepPersonaResolver(
            personas, providers, composer, settingsService, NullLogger<StepPersonaResolver>.Instance);

        var session = CreateSession();
        var executor = BuildExecutor(session, resolver, runPersona);

        await executor.ExecuteStepAsync(
            Run(),
            new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S", Intent = "do it", AssignedPersonaId = specialist.Id },
            new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.Contains(_lastTools!, t => t.Name == "specialist_only_tool"); // the step really re-resolved
        Assert.True(AgentStepTools.OffersStepResultTool(_lastTools),
            "a step running on its own persona must still be offered emit_step_result");
        // A specialist step that could declare an outcome but not ask would be where the two tools disagree.
        Assert.True(AgentStepTools.OffersRequestUserInputTool(_lastTools),
            "a step running on its own persona must still be offered request_user_input");
    }

    // The mid-plan ask tool must be scoped by BuildSpec the same way emit_step_result is.

    private static AgentRun ChildRun() =>
        new() { Id = Guid.NewGuid(), ChatId = Guid.NewGuid(), Goal = "the goal", ParentRunId = Guid.NewGuid() };

    /// <summary>An in-place add would leak a step-only tool into the session's cached list, which is the very
    /// list an ordinary Send uses.</summary>
    [Fact]
    public async Task AStepTurn_IsOfferedTheAskTool()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, stepPersonas: null, runPersona: null);

        await executor.ExecuteStepAsync(
            Run(), new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S", Intent = "do it" },
            new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(AgentStepTools.OffersRequestUserInputTool(_lastTools));
        Assert.False(AgentStepTools.OffersRequestUserInputTool(_runTools),
            "the session's cached tool list must not have been mutated");
    }

    /// <summary>A delegated step is not offered the ask tool but keeps <c>emit_step_result</c> — withholding both would strand it on the older text-heuristic fallback, which is worse than not being able to ask.</summary>
    [Fact]
    public async Task ADelegatedStep_IsNotOfferedTheAskTool_ButKeepsTheDeclarationTool()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, stepPersonas: null, runPersona: null);

        await executor.ExecuteStepAsync(
            ChildRun(), new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S", Intent = "do it" },
            new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.False(AgentStepTools.OffersRequestUserInputTool(_lastTools));
        Assert.True(AgentStepTools.OffersStepResultTool(_lastTools),
            "a delegated step must still be able to DECLARE the block it may not ask about");
    }

    /// <summary>The planner-degrade turn owns no <c>AgentStep</c> row, so it is not offered the ask tool either, matching the declaration tool above it and the headless executor.</summary>
    [Fact]
    public async Task TheFallbackTurn_IsNotOfferedTheAskTool()
    {
        var session = CreateSession();
        var executor = BuildExecutor(session, stepPersonas: null, runPersona: null);

        await executor.RunSingleTurnFallbackAsync(
            Run(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.NotNull(_lastTools); // the turn really ran
        Assert.False(AgentStepTools.OffersRequestUserInputTool(_lastTools));
    }
}
