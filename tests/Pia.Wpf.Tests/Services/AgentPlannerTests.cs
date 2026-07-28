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
    // (when emitArgs is set) then yields Finished — the loop drains the whole stream (R6). The usage
    // rides on the yielded Finished item, which is the ONLY place a provider reports it (I1).
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        Func<FunctionCallContent, Task<object?>>? handler, Dictionary<string, object?>? emitArgs,
        UsageDetails? usage = null)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs));
        await Task.Yield();
        yield return new Finished(usage, "test-model");
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

    private readonly List<string> _systemPrompts = new();

    /// <summary>The system prompt of the LAST planning attempt.</summary>
    private string LastPrompt => _systemPrompts[^1];

    private void ReturnsPlan(Dictionary<string, object?>? emitArgs, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _systemPrompts.Add(ci.ArgAt<IList<ChatMessage>>(0)[0].Text ?? string.Empty);
                return PlanStream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), emitArgs, usage);
            });
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

    // ---- I1: the planning turn's spend must reach the ledger (it used to be discarded here) ----

    [Fact]
    public async Task PlanAsync_CapturesUsageFromFinished()
    {
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        Assert.NotNull(result.Usage);
        Assert.Equal(7, result.Usage!.InputTokenCount);
        Assert.Equal(3, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task PlanAsync_NoCall_Degrades_ButCarriesBothAttemptsUsage()
    {
        // No emit_plan on either attempt → SingleTurn degrade. Both rounds were still paid for, so the
        // fallback result must carry the SUM (the firm retry's usage is the one most easily lost).
        ReturnsPlan(emitArgs: null, usage: new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.NotNull(result.Usage);
        Assert.Equal(14, result.Usage!.InputTokenCount);  // 2 attempts × 7
        Assert.Equal(6, result.Usage.OutputTokenCount);   // 2 attempts × 3
        Assert.NotSame(PlanResult.Fallback, result);      // the shared instance is never mutated
        Assert.Null(PlanResult.Fallback.Usage);
    }

    [Fact]
    public async Task PlanAsync_InvalidPlan_Degrades_ButCarriesTheAttemptUsage()
    {
        // Semantically invalid (duplicate titles) → fallback WITHOUT a retry, but the one attempt spent.
        ReturnsPlan(Steps(("Same", "do a", null), ("Same", "do b", null)),
            new UsageDetails { InputTokenCount = 5, OutputTokenCount = 2 });

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.Equal(5, result.Usage!.InputTokenCount);
        Assert.Equal(2, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task ReplanAsync_CapturesUsage_OnSuccessAndOnDegrade()
    {
        ReturnsPlan(Steps(("Recover", "retry the failed step", null)),
            new UsageDetails { InputTokenCount = 11, OutputTokenCount = 4 });
        var planner = BuildPlanner();

        var revised = await planner.ReplanAsync(Ctx(), "boom", Persona(), Provider(), TestContext.Current.CancellationToken);
        Assert.False(revised.FallBackToSingleTurn);
        Assert.Equal(11, revised.Usage!.InputTokenCount);
        Assert.Equal(4, revised.Usage.OutputTokenCount);

        ReturnsPlan(emitArgs: null, usage: new UsageDetails { InputTokenCount = 11, OutputTokenCount = 4 });
        var degraded = await planner.ReplanAsync(Ctx(), "boom", Persona(), Provider(), TestContext.Current.CancellationToken);
        Assert.True(degraded.FallBackToSingleTurn);
        Assert.Equal(22, degraded.Usage!.InputTokenCount); // replan + its firm retry
        Assert.Equal(8, degraded.Usage.OutputTokenCount);
    }

    // ---- E2: a resumed run's replan judge must be told the pre-pause steps already ran ----

    [Fact]
    public async Task ReplanAsync_SeededPrePauseStep_IsPresentedAsExecuted_NotAsMissing()
    {
        ReturnsPlan(Steps(("Recover", "finish the goal", null)));
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        ctx.SeedCompletedSteps(new[]
        {
            new CompletedStepSummary(0, "Early", "ran before the pause", Succeeded: true, VisibleText: string.Empty,
                ExpectedArtifact: "early.md", FromEarlierSegment: true),
        });

        await BuildPlanner().ReplanAsync(ctx, "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("Completed so far", LastPrompt);
        Assert.Contains("[ok] Early: ran before the pause", LastPrompt);
        Assert.Contains(CompletedStepSummary.EarlierSegmentNote, LastPrompt); // ran, text just unavailable
        Assert.Contains("do NOT repeat these steps", LastPrompt);
    }

    [Fact]
    public async Task ReplanAsync_LiveStep_CarriesNoEarlierSegmentNote()
    {
        ReturnsPlan(Steps(("Recover", "finish the goal", null)));
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        ctx.RecordStep(new AgentStep { Ordinal = 0, Title = "Live", Intent = "ran in this segment" },
            new StepTurnResult(true, false, null, "visible", null, Guid.NewGuid(), Guid.NewGuid()));

        await BuildPlanner().ReplanAsync(ctx, "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("[ok] Live: ran in this segment", LastPrompt);
        Assert.DoesNotContain(CompletedStepSummary.EarlierSegmentNote, LastPrompt);
    }

    [Fact]
    public async Task PlanAsync_ProviderReportsNoUsage_LeavesUsageNull()
    {
        // A provider that never reports usage must not fabricate a zero-token ledger write.
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await BuildPlanner().PlanAsync("goal", Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Null(result.Usage);
    }
}
