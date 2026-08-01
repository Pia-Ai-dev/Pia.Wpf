using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;
// Microsoft.Extensions.AI also defines a ReasoningEffort; the planner's gate reads the Pia one.
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Services;

/// <summary>
/// Planner behavior (§13.3/§13.12): an <c>emit_plan</c> call parses into ordered Pending steps;
/// no-call retries once (firmer) then falls back to SingleTurn (R10); a semantically invalid plan
/// falls back without a retry. Plus the opt-in reason-then-emit split: it only fires on a provider whose
/// handler drops the configured reasoning effort under tools, it is tool-FREE (which is the whole point),
/// it can never hard-fail planning, and its tokens always reach <c>PlanResult.Usage</c> (I1).
/// </summary>
public sealed class AgentPlannerTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();

    // A REAL AppSettings: NSubstitute's auto-value for Task<AppSettings> is a completed task wrapping NULL
    // (AppSettings is a plain class, not substitutable), which would NRE inside the gate.
    private readonly AppSettings _appSettings = new();

    private static AiProvider Provider(
        AiProviderType type = AiProviderType.OpenAI,
        ReasoningEffort? effort = null,
        bool supportsTools = true,
        string name = "P")
        => new()
        {
            Name = name,
            Endpoint = "https://x",
            ProviderType = type,
            ReasoningEffort = effort,
            SupportsToolCalling = supportsTools,
        };

    private static Persona Persona() => new() { Name = "Pia", SystemPrompt = "sys" };
    private static RunContext Ctx() => new("build a thing", RunProfile.Interactive);

    /// <summary>
    /// The goal every test plans. It must NOT be a substring of the analysis wrapper that
    /// <c>BuildPlanMessages</c> composes ("--- Your analysis of this <b>goal</b> …"): with the literal
    /// "goal" as the goal, <c>Assert.Contains(goal, LastUserPrompt)</c> is satisfied by the wrapper alone
    /// and the "the goal survives into the analysis-seeded user message" guarantee has zero coverage.
    /// </summary>
    private const string Goal = "ship the widget catalogue";

    private AgentPlanner BuildPlanner() => BuildPlanner(AiProviderType.OpenAI, dropsEffortWithTools: false);

    private AgentPlanner BuildPlanner(AiProviderType handlerType, bool dropsEffortWithTools)
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(handlerType);
        handler.DropsReasoningEffortWithTools.Returns(dropsEffortWithTools);
        return new AgentPlanner(
            _ai, new AiProviderHandlerResolver([handler]), _settingsService, NullLogger<AgentPlanner>.Instance);
    }

    /// <summary>
    /// Builds the planner AND its provider together so the registered handler's ProviderType always matches
    /// the provider's. A mismatch would make <c>AiProviderHandlerResolver.Get</c> throw, the gate's catch-all
    /// swallow it, and every gate assertion below pass for the wrong reason.
    /// </summary>
    private (AgentPlanner Planner, AiProvider Provider) PlannerFor(
        AiProviderType type,
        bool dropsEffortWithTools,
        ReasoningEffort? effort,
        bool reasoningTurnEnabled,
        bool supportsTools = true)
    {
        _appSettings.AgentPlanReasoningTurnEnabled = reasoningTurnEnabled;
        return (BuildPlanner(type, dropsEffortWithTools), Provider(type, effort, supportsTools));
    }

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
    private readonly List<string> _userPrompts = new();

    /// <summary>The system prompt of the LAST planning attempt.</summary>
    private string LastPrompt => _systemPrompts[^1];

    /// <summary>The user message of the LAST planning attempt — where the analysis rides.</summary>
    private string LastUserPrompt => _userPrompts[^1];

    private void ReturnsPlan(Dictionary<string, object?>? emitArgs, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                return PlanStream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), emitArgs, usage);
            });
    }

    // ---- reasoning-turn plumbing: capture the ARGUMENTS, don't match on a literal null ----

    private readonly List<IList<ChatMessage>> _reasoningRequests = new();
    private bool _reasoningSawTools = true;   // must end up false — that is WHY the effort survives
    private bool _reasoningToolsCaptured;

    private void ReturnsReasoning(string text, UsageDetails? usage = null)
    {
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _reasoningRequests.Add(ci.ArgAt<IList<ChatMessage>>(0));
                _reasoningSawTools = ci.ArgAt<IList<AITool>?>(2) is not null;
                _reasoningToolsCaptured = true;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage });
            });
    }

    private void ThrowsFromReasoning(Exception ex)
    {
        _ai.GetChatResponseAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ChatResponse>(ex));
    }

    /// <summary>Received-count assertion for the tool-free reasoning turn. 0 == DidNotReceive.</summary>
    private void AssertReasoningTurns(int count)
    {
        _ = _ai.Received(count).GetChatResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    /// <summary>Received-count assertion for the constrained emit_plan turn.</summary>
    private void AssertConstrainedTurns(int count)
    {
        _ai.Received(count).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanAsync_EmitPlanCall_ProducesOrderedSteps()
    {
        ReturnsPlan(Steps(
            ("Gather", "collect the inputs", "notes"),
            ("Draft", "write the draft", null),
            ("Review", "check the draft", "final")));

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

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

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.Empty(result.Steps);
        AssertConstrainedTurns(2);
    }

    [Fact]
    public async Task PlanAsync_InvalidPlan_DuplicateTitles_FallsBackWithoutRetry()
    {
        ReturnsPlan(Steps(
            ("Same", "do a", null),
            ("Same", "do b", null))); // duplicate titles → semantic-invalid

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.Empty(result.Steps);
        AssertConstrainedTurns(1);
    }

    [Fact]
    public async Task PlanAsync_EmptyPlan_FallsBack()
    {
        ReturnsPlan(new Dictionary<string, object?> { ["steps"] = Array.Empty<object>() });

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
    }

    // ---- I1: the planning turn's spend must reach the ledger (it used to be discarded here) ----

    [Fact]
    public async Task PlanAsync_CapturesUsageFromFinished()
    {
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

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

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

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

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

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

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Null(result.Usage);
    }

    // ---- reason-then-emit: the opt-in two-call plan turn ----

    private const string Analysis = "Split this into fetching, transforming and reporting.";

    [Fact]
    public async Task PlanAsync_GateOn_AffectedHandler_ReasonsThenEmits()
    {
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(Analysis);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null), ("Draft", "write the draft", null)));

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(1);
        AssertConstrainedTurns(1);
        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(new[] { "Gather", "Draft" }, result.Steps.Select(s => s.Title).ToArray());
        // StartsWith, not Contains: the goal must LEAD the composed user message. Contains would also pass if
        // the goal were appended after the analysis block, which is a different (worse) prompt shape — and
        // BuildPlanMessages' system prompt never carries the goal, so losing it here means planning for
        // nothing.
        Assert.StartsWith(Goal, LastUserPrompt, StringComparison.Ordinal);
        Assert.Contains(Analysis, LastUserPrompt);
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurn_SendsNoTools_SoTheEffortSurvives()
    {
        // The premise the whole batch rests on: AiClientService computes hasTools from the tools argument,
        // so a tools:null call is the one shape that still carries the configured reasoning effort.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(Analysis);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.True(_reasoningToolsCaptured); // the call really happened; the assertion is not vacuous
        Assert.False(_reasoningSawTools);
        // The reasoning turn must not be told about emit_plan — no tool schema is even attached to it.
        Assert.DoesNotContain("emit_plan", _reasoningRequests[^1][0].Text ?? string.Empty);
        // …and it must actually ASK about something: the goal on the user message, the persona on the system
        // one. ReturnsReasoning hands back the canned analysis whatever was sent, so without these two the
        // reasoning turn could be spending a full provider round on an empty request.
        Assert.Equal(Goal, _reasoningRequests[^1][1].Text);
        Assert.Contains(Persona().SystemPrompt, _reasoningRequests[^1][0].Text ?? string.Empty);
    }

    [Fact]
    public async Task PlanAsync_GateOff_RunsOneTurn_AndTheUserMessageIsTheGoalVerbatim()
    {
        // The executable form of "no regression when OFF": same round count, byte-identical user message.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: false);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(0);
        AssertConstrainedTurns(1);
        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(Goal, LastUserPrompt);
    }

    [Fact]
    public async Task PlanAsync_GateOn_UnaffectedHandler_RunsOneTurn()
    {
        // The Responses-API case: the effort already survives tools, so a second turn buys nothing.
        var (planner, provider) = PlannerFor(
            AiProviderType.OpenAI, dropsEffortWithTools: false, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(0);
        AssertConstrainedTurns(1);
        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(Goal, LastUserPrompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReasoningEffort.None)]
    public async Task PlanAsync_GateOn_EffortNullOrNone_SkipsTheReasoningTurn(ReasoningEffort? effort)
    {
        // Nothing to boost: the extra round could only reason at the model default anyway.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, effort, reasoningTurnEnabled: true);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(0);
        AssertConstrainedTurns(1);
    }

    [Fact]
    public async Task PlanAsync_GateOn_ProviderWithoutToolCalling_SkipsTheReasoningTurn()
    {
        // Without tool calling the constrained turn already gets hasTools:false (so the effort IS sent) and
        // emit_plan is never attached — planning is heading for the SingleTurn degrade regardless.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true,
            supportsTools: false);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(0);
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurnThrows_StillProducesAValidPlan()
    {
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ThrowsFromReasoning(new LlmTimeoutException("P", 300));
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        AssertReasoningTurns(1); // the degrade is only interesting if the turn actually fired and failed
        AssertConstrainedTurns(1);
        Assert.Equal(Goal, LastUserPrompt); // degraded cleanly to today's single turn
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurnEmpty_StillProducesAValidPlan()
    {
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning("   ");
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        AssertReasoningTurns(1); // without this a gate that stopped firing would leave the test asserting nothing
        AssertConstrainedTurns(1);
        Assert.Equal(Goal, LastUserPrompt);
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurnEmpty_StillAccruesItsUsage()
    {
        // Separate from the test above on purpose: this is the accrual case most easily implemented wrong
        // (returning (null, null) once the text turns out to be useless). The round was still paid for.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning("   ", new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 });
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage!.InputTokenCount);
        Assert.Equal(4, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task PlanAsync_SumsUsageFromBothTurns()
    {
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(Analysis, new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 });
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(10, result.Usage!.InputTokenCount);
        Assert.Equal(4, result.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task PlanAsync_ReasoningUsage_ReachesTheSingleTurnDegradeResult()
    {
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(Analysis, new UsageDetails { InputTokenCount = 3, OutputTokenCount = 1 });
        ReturnsPlan(emitArgs: null, usage: new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 });

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.True(result.FallBackToSingleTurn);
        Assert.Equal(17, result.Usage!.InputTokenCount); // reasoning + 2 constrained attempts
        Assert.Equal(7, result.Usage.OutputTokenCount);
        Assert.Null(PlanResult.Fallback.Usage);          // the shared instance is never mutated
    }

    [Fact]
    public async Task PlanAsync_FirmRetry_ReusesTheSingleReasoningTurn()
    {
        // The firm retry exists because the model wrote prose instead of calling emit_plan, which a second
        // reasoning turn would not fix. Worst case stays 3 provider turns, not 4.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(Analysis);
        ReturnsPlan(emitArgs: null);

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(1);
        AssertConstrainedTurns(2);
        Assert.Contains(Analysis, LastUserPrompt);                   // the retry carries the SAME analysis
        Assert.StartsWith(Goal, LastUserPrompt, StringComparison.Ordinal); // …and still leads with the goal
    }

    [Fact]
    public async Task PlanAsync_CancellationDuringTheReasoningTurn_Rethrows()
    {
        // A cancel is not a degrade: it must not be swallowed into "plan single-turn instead".
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ThrowsFromReasoning(new OperationCanceledException(cts.Token));
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => planner.PlanAsync(Goal, Ctx(), Persona(), provider, cts.Token));

        AssertConstrainedTurns(0);
    }

    /// <summary>
    /// The two ends of the over-long analysis fixture. A homogeneous <c>new string('x', n)</c> cannot say
    /// WHICH 4000 chars survived: truncating from the tail (<c>text[^MaxAnalysisChars..]</c>) instead of the
    /// head would hand the plan turn a conclusion with the reasoning that produced it cut away, and still
    /// look truncated. These markers make the direction observable.
    /// </summary>
    private const string AnalysisHead = "HEAD-OF-ANALYSIS";
    private const string AnalysisTail = "TAIL-OF-ANALYSIS";

    [Fact]
    public async Task PlanAsync_LongAnalysis_IsTruncatedIntoThePlanTurn()
    {
        // The constrained turn passes no contextBudget, so an unbounded analysis could overflow a small
        // model's window and turn a WORKING plan turn into a failing one.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(AnalysisHead + new string('x', 10_000) + AnalysisTail);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.Contains("analysis truncated", LastUserPrompt);
        // Direction, not just size: the plan turn needs the OPENING of the analysis — the sub-problems and
        // the order they go in — so the head is what survives and the tail is what gets dropped.
        Assert.Contains(AnalysisHead, LastUserPrompt);
        Assert.DoesNotContain(AnalysisTail, LastUserPrompt);
        // Tight on purpose. MaxAnalysisChars is 4000 and goal + wrapper + truncation marker add 137, so a
        // correct run lands on exactly 4137 chars. The old bound of 5000 left enough slack that raising the
        // cap to 4800 (4937 chars) still passed, i.e. the window-overflow guard was unpinned above 4000.
        Assert.True(LastUserPrompt.Length < 4_300, $"user prompt was {LastUserPrompt.Length} chars");
    }

    [Fact]
    public async Task ReplanAsync_GateOn_StillRunsOneConstrainedTurn()
    {
        // PLAN-ONLY by decision: a replan already carries the completed-step summaries and the failure
        // detail, and it can run MaxReplans times, so doubling ITS cost multiplies over the run.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsPlan(Steps(("Recover", "retry the failed step", null)));

        var result = await planner.ReplanAsync(Ctx(), "boom", Persona(), provider, TestContext.Current.CancellationToken);

        AssertReasoningTurns(0);
        AssertConstrainedTurns(1);
        Assert.False(result.FallBackToSingleTurn);
    }

    [Fact]
    public async Task PlanAsync_GateOn_UnregisteredProviderType_StillPlans()
    {
        // Evaluating the gate of an optional optimization must never fail planning:
        // AiProviderHandlerResolver.Get throws NotSupportedException for a type with no handler.
        _appSettings.AgentPlanReasoningTurnEnabled = true;
        var planner = BuildPlanner(AiProviderType.OpenAI, dropsEffortWithTools: true);
        var provider = Provider(AiProviderType.Mistral, ReasoningEffort.High); // no Mistral handler registered
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        AssertReasoningTurns(0);
        AssertConstrainedTurns(1);
    }

    [Fact]
    public async Task PlanAsync_GateOn_SettingsUnavailable_StillPlans()
    {
        // The other half of the same guard: GetSettingsAsync does I/O and can fail.
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.Ollama);
        handler.DropsReasoningEffortWithTools.Returns(true);
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromException<AppSettings>(new System.IO.IOException("disk")));
        var planner = new AgentPlanner(
            _ai, new AiProviderHandlerResolver([handler]), _settingsService, NullLogger<AgentPlanner>.Instance);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        var result = await planner.PlanAsync(
            Goal, Ctx(), Persona(), Provider(AiProviderType.Ollama, ReasoningEffort.High),
            TestContext.Current.CancellationToken);

        Assert.False(result.FallBackToSingleTurn);
        AssertReasoningTurns(0);
        AssertConstrainedTurns(1);
    }

    [Fact]
    public async Task PlanAsync_GateOn_SettingsReadCancelled_Rethrows()
    {
        // The other side of the guard above: the gate's catch-all may swallow a settings read that FAILED,
        // but not one that was CANCELLED — that would let planning march on with a token the caller already
        // cancelled. Only ShouldReasonFirstAsync's own catch-when(IsCancellationRequested) prevents it: unlike
        // TryReasonAsync, its general catch just returns false and never calls ThrowIfCancellationRequested.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        // Hand-rolled like the test above, and for the same reason: BuildPlanner/PlannerFor/PlannerWithLog all
        // re-stub GetSettingsAsync to a successful task and would overwrite this cancelled one.
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.Ollama);
        handler.DropsReasoningEffortWithTools.Returns(true);
        _settingsService.GetSettingsAsync()
            .Returns(_ => Task.FromException<AppSettings>(new OperationCanceledException(cts.Token)));
        var planner = new AgentPlanner(
            _ai, new AiProviderHandlerResolver([handler]), _settingsService, NullLogger<AgentPlanner>.Instance);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planner.PlanAsync(
            Goal, Ctx(), Persona(), Provider(AiProviderType.Ollama, ReasoningEffort.High), cts.Token));

        AssertReasoningTurns(0);   // the gate never returned an answer…
        AssertConstrainedTurns(0); // …and planning did NOT proceed on the cancelled token
    }

    // ---- privacy: nothing this batch logs may put user content at a release-visible level ----

    /// <summary>
    /// Deliberately NOT a substring of any log line this class emits — "P" is a substring of
    /// "Plan reason-then-emit is ON…", so the default provider name would make the assertions below fail
    /// on correct code and teach the wrong lesson.
    /// </summary>
    private const string SecretProviderName = "my-secret-box";

    private (AgentPlanner Planner, AiProvider Provider, CapturingLogger Log) PlannerWithLog()
    {
        _appSettings.AgentPlanReasoningTurnEnabled = true;
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_appSettings));
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.Ollama);
        handler.DropsReasoningEffortWithTools.Returns(true);
        var log = new CapturingLogger();
        var planner = new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService, log);
        return (planner, Provider(AiProviderType.Ollama, ReasoningEffort.High, name: SecretProviderName), log);
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurn_LogsProviderTypeAndCost_ButNotTheNameGoalOrAnalysis()
    {
        // CLAUDE.md: a provider NAME is user-named, and goal/analysis text is user content. The cost line
        // must therefore identify the provider by TYPE, and the analysis may only go to SensitiveDebug.
        var (planner, provider, log) = PlannerWithLog();
        ReturnsReasoning(Analysis);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        var release = log.ReleaseVisible();
        Assert.Contains(release, m => m.Contains("Ollama", StringComparison.Ordinal));       // the type identifies it…
        Assert.Contains(release, m => m.Contains("doubled", StringComparison.Ordinal));      // …and the cost is stated
        Assert.DoesNotContain(release, m => m.Contains(SecretProviderName, StringComparison.Ordinal));
        Assert.DoesNotContain(release, m => m.Contains(Analysis, StringComparison.Ordinal));
        Assert.DoesNotContain(release, m => m.Contains(Goal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurnFails_LogsTheExceptionType_NotTheProviderName()
    {
        // LlmTimeoutException's MESSAGE embeds the provider name, so the degrade warning carries the
        // exception TYPE only and the detail goes to SensitiveDebug.
        var (planner, provider, log) = PlannerWithLog();
        ThrowsFromReasoning(new LlmTimeoutException(SecretProviderName, 300));
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        var release = log.ReleaseVisible();
        // Positive first: without it, deleting the warning outright would make the DoesNotContain pass.
        Assert.Contains(release, m => m.Contains(nameof(LlmTimeoutException), StringComparison.Ordinal));
        Assert.DoesNotContain(release, m => m.Contains(SecretProviderName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Captures every line with its level. <c>ReleaseVisible</c> is the subset a support log actually keeps:
    /// <c>SensitiveDebug</c> is <c>[Conditional("DEBUG")]</c> and the test build IS Debug, so an unfiltered
    /// "the analysis appears in no log line" assertion would be red against correct code.
    /// </summary>
    private sealed class CapturingLogger : ILogger<AgentPlanner>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public List<string> ReleaseVisible()
        {
            var lines = _entries.Where(e => e.Level >= LogLevel.Information).Select(e => e.Message).ToList();
            Assert.NotEmpty(lines); // stops every DoesNotContain here from passing on an empty log
            return lines;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Add((logLevel, formatter(state, exception)));
    }
}
