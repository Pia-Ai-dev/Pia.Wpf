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
/// hermes #9's SCOPING half on the live path: which turn shapes <c>LiveTurnExecutor.BuildSpec</c> puts
/// <c>emit_step_result</c> in front of. <c>ChatSessionStepOutcomeSignalTests</c> hand-builds its
/// <c>StepTurnSpec</c>, so it never touches the production line that decides this — the same gap
/// <c>LiveTurnExecutorPlannedRunTests</c> closes for the autonomy policy, for the same reason: the
/// augmentation is a single line in <c>BuildSpec</c> and dropping it COMPILES, leaving every interactive run
/// silently back on the old text/exception premise.
/// </summary>
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

    /// <summary>The R10 planner-degrade turn produces no <c>AgentStep</c> row, so there is no Done/Failed for
    /// a declaration to decide — it is deliberately not offered the tool, matching the headless executor.</summary>
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

    /// <summary>
    /// <b>GUARD</b>. A step that resolved its OWN persona (Batch 07 G6) runs on a different
    /// <c>AssistantTurnSetup</c>, and it is still offered the tool. Augmenting <c>_turnSetup</c> instead of
    /// the resolved one compiles, keeps the two facts above green, and strands exactly the specialist steps
    /// on the old premise. Non-vacuity: the specialist's own marker tool must be in the same list, so a
    /// fixture whose step persona quietly degraded to the run default cannot pass.
    /// </summary>
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
                Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
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
        // 18 G5 rides the same choke point, so the same guard covers it: a specialist step that could declare an
        // outcome but could not ASK would be the one shape where the two tools silently disagree.
        Assert.True(AgentStepTools.OffersRequestUserInputTool(_lastTools),
            "a step running on its own persona must still be offered request_user_input");
    }

    // ============================================================================================
    // 18 G5 (D3/D7, owner Q5) — the SAME scoping question for the mid-plan ask tool. It matters most
    // on THIS path: the interactive symptom was recorded by the owner as inferred rather than
    // observed (18 D7), so "interactive should work the same way" is only true if BuildSpec really
    // offers it here too. Dropping the augmentation line COMPILES.
    // ============================================================================================

    private static AgentRun ChildRun() =>
        new() { Id = Guid.NewGuid(), ChatId = Guid.NewGuid(), Goal = "the goal", ParentRunId = Guid.NewGuid() };

    /// <summary>An interactive Planned step of a ROOT run is offered the ask tool, and the run's cached tool list
    /// survives untouched — an in-place add would leak a step-only tool into every later chat turn on this
    /// session, which is the very list an ordinary Send uses.</summary>
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

    /// <summary>
    /// <b>OWNER Q1 on the live path.</b> A step of a DELEGATED run is not offered the ask tool — while still
    /// being offered <c>emit_step_result</c>, which is the channel a blocked child is redirected to. The second
    /// assertion is the load-bearing one: withholding BOTH would strand every delegated step on the pre-hermes-#9
    /// text heuristic, which is a strictly worse failure than not being able to ask.
    /// <para>
    /// No live run is a child today (the fan-out dispatches children headlessly), so this measures a property of
    /// <c>BuildSpec</c> rather than a shape production reaches — which is exactly why it is worth pinning: the day
    /// that changes, nothing else would notice.
    /// </para>
    /// </summary>
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

    /// <summary>The R10 planner-degrade turn owns no <c>AgentStep</c> row, so there is nothing to put back to
    /// Pending and nothing for a resume to re-run — it is deliberately not offered the ask tool either, matching
    /// both the declaration tool above it and the headless executor.</summary>
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
