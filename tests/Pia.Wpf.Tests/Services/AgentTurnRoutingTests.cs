using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Services;

/// <summary>The persona-less spine turns route as Assistant mode so the group's persona-type mapping applies.</summary>
public sealed class AgentTurnRoutingTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _appSettings = new();

    private readonly List<string?> _toolTurnModes = new();
    private readonly List<string?> _toolTurnModelTypes = new();
    private readonly List<Guid?> _toolTurnPersonaIds = new();
    private string? _reasoningMode;
    private string? _reasoningModelType;

    public AgentTurnRoutingTests()
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<AgentContextBudget?>())
            .Returns(ci =>
            {
                _toolTurnModes.Add(ci.ArgAt<string?>(4));
                _toolTurnPersonaIds.Add(ci.ArgAt<Guid?>(5));
                _toolTurnModelTypes.Add(ci.ArgAt<string?>(6));
                return EmptyStream();
            });

        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _reasoningMode = ci.ArgAt<string?>(3);
                _reasoningModelType = ci.ArgAt<string?>(5);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "analysis")));
            });
    }

    // No emit_plan / emit_verdict call: both callers degrade cleanly, and routing is what is under test.
    private static async IAsyncEnumerable<ChatStreamItem> EmptyStream()
    {
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private static AiProvider Provider(ReasoningEffort? effort = null) => new()
    {
        Name = "P",
        Endpoint = "https://x",
        ProviderType = AiProviderType.OpenAI,
        ReasoningEffort = effort,
        SupportsToolCalling = true,
    };

    private static Persona RunPersona() => new() { Name = "Experienced Coder", SystemPrompt = "sys" };

    private static RunContext Ctx()
    {
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        ctx.RecordStep(
            new AgentStep { Ordinal = 0, Title = "A", Intent = "ia" },
            new StepTurnResult(true, false, null, "step result text", null, Guid.NewGuid(), Guid.NewGuid()));
        // Two steps: AgentVerifier skips a lone step that named no artifact, so the verify turn whose
        // routing this asserts would never be sent.
        ctx.RecordStep(
            new AgentStep { Ordinal = 1, Title = "B", Intent = "ib" },
            new StepTurnResult(true, false, null, "second step result text", null, Guid.NewGuid(), Guid.NewGuid()));
        return ctx;
    }

    private AgentPlanner BuildPlanner(bool dropsEffortWithTools = false)
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(dropsEffortWithTools);
        return new AgentPlanner(
            _ai, new AiProviderHandlerResolver([handler]), _settingsService, NullLogger<AgentPlanner>.Instance);
    }

    private AgentVerifier BuildVerifier() => new(_ai, _settingsService, NullLogger<AgentVerifier>.Instance);

    // The literals, never AgentTurnRouting's own constants: asserting the constant the call site passes
    // would still pass with the routing gutted.
    private void AssertSpineRouting(int expectedTurns, string expectedModelType)
    {
        Assert.Equal(expectedTurns, _toolTurnModes.Count);
        Assert.All(_toolTurnModes, m => Assert.Equal("Assistant", m));
        Assert.All(_toolTurnModelTypes, t => Assert.Equal(expectedModelType, t));
    }

    [Fact]
    public async Task PlanAsync_RoutesAsAssistantModeSpineTraffic()
    {
        await BuildPlanner().PlanAsync(
            "ship the widget catalogue", Ctx(), RunPersona(), Provider(), TestContext.Current.CancellationToken);

        AssertSpineRouting(expectedTurns: 2, expectedModelType: "plan"); // first attempt + firm retry
    }

    [Fact]
    public async Task ReplanAsync_RoutesAsAssistantModeSpineTraffic()
    {
        await BuildPlanner().ReplanAsync(
            Ctx(), "boom", RunPersona(), Provider(), TestContext.Current.CancellationToken);

        AssertSpineRouting(expectedTurns: 2, expectedModelType: "plan");
    }

    [Fact]
    public async Task VerifyAsync_RoutesAsAssistantModeSpineTraffic()
    {
        await BuildVerifier().VerifyAsync(
            Ctx(), RunPersona(), Provider(), TestContext.Current.CancellationToken);

        AssertSpineRouting(expectedTurns: 2, expectedModelType: "fast");
    }

    [Fact]
    public async Task PlanReasoningTurn_RoutesAsAssistantModeSpineTraffic()
    {
        _appSettings.AgentPlanReasoningTurnEnabled = true;

        await BuildPlanner(dropsEffortWithTools: true).PlanAsync(
            "ship the widget catalogue", Ctx(), RunPersona(), Provider(ReasoningEffort.High),
            TestContext.Current.CancellationToken);

        Assert.Equal("Assistant", _reasoningMode);
        Assert.Equal("plan", _reasoningModelType);
    }

    /// <summary>
    /// No <c>X-Pia-Persona</c>: the spine turns must not drag a persona's bound KBs and connectors into
    /// planning. Assistant mode alone already opens the user's own KB gate.
    /// </summary>
    [Fact]
    public async Task SpineTurns_SendNoPersonaId()
    {
        await BuildPlanner().PlanAsync(
            "ship the widget catalogue", Ctx(), RunPersona(), Provider(), TestContext.Current.CancellationToken);

        Assert.All(_toolTurnPersonaIds, Assert.Null);
    }
}
