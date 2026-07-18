using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Planner behavior (§13.3/§13.12): an <c>emit_plan</c> call parses into ordered Pending steps;
/// no-call retries once (firmer) then falls back to SingleTurn (R10); a semantically invalid plan
/// falls back without a retry.
/// </summary>
public sealed class AgentPlannerTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();

    private static AiProvider Provider() => new() { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static RunContext Ctx() => new("build a thing", RunProfile.Interactive);

    private AgentPlanner BuildPlanner() => new(_ai, NullLogger<AgentPlanner>.Instance);

    // Drives one planning turn: invokes the captured toolHandler with a synthetic emit_plan call
    // (when emitArgs is set) then yields Finished — the loop drains the whole stream (R6).
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        Func<FunctionCallContent, Task<object?>>? handler, Dictionary<string, object?>? emitArgs)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs));
        await Task.Yield();
        yield return new Finished(null, "test-model");
    }

    private static Dictionary<string, object?> Steps(params (string Title, string Intent, string? Artifact)[] steps)
    {
        var arr = steps
            .Select(s => (object)new Dictionary<string, object?>
            {
                ["title"] = s.Title,
                ["intent"] = s.Intent,
                ["expectedArtifact"] = s.Artifact,
            })
            .ToArray();
        return new Dictionary<string, object?> { ["steps"] = arr };
    }

    private void ReturnsPlan(Dictionary<string, object?>? emitArgs)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => PlanStream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), emitArgs));
    }

    [Fact]
    public async Task PlanAsync_EmitPlanCall_ProducesOrderedSteps()
    {
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", "notes"),
            ("Draft", "write the draft", null),
            ("Review", "check the draft", "final")));

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal(new[] { 0, 1, 2 }, result.Steps.Select(s => s.Ordinal).ToArray());
        Assert.All(result.Steps, s => Assert.Equal(AgentStepStatus.Pending, s.Status));
        Assert.Equal("Gather", result.Steps[0].Title);
        Assert.Equal("collect the inputs", result.Steps[0].Intent);
        Assert.Equal("notes", result.Steps[0].ExpectedArtifact);
        Assert.Null(result.Steps[1].ExpectedArtifact);
    }

    [Fact]
    public async Task PlanAsync_NoCall_RetriesOnce_ThenSingleTurnFallback()
    {
        ReturnsPlan(emitArgs: null); // no emit_plan call on either attempt

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.Empty(result.Steps);
        _ai.Received(2).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanAsync_InvalidPlan_DuplicateTitles_FallsBackWithoutRetry()
    {
        ReturnsPlan(Steps(
            ("Same", "do a", null),
            ("Same", "do b", null))); // duplicate titles → semantic-invalid

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.Empty(result.Steps);
        _ai.Received(1).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanAsync_EmptyPlan_FallsBack()
    {
        ReturnsPlan(new Dictionary<string, object?> { ["steps"] = Array.Empty<object>() });

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
    }
}
