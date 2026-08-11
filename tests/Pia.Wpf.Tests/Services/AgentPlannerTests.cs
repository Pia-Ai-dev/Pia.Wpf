using System.Text.Json;
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

    // Must not be a substring of the analysis wrapper the planner composes, or Contains would pass on the wrapper alone.
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

    // Pairs planner and provider so their types match: a mismatch is swallowed by the gate's catch-all and
    // would make every gate assertion pass for the wrong reason.
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

    // The usage rides on the yielded Finished item, which is the only place a provider ever reports it.
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        ToolCallHandler? handler, Dictionary<string, object?>? emitArgs,
        UsageDetails? usage = null)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs), new ToolDispatchContext(1));
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

    // Member names are wire literals rather than nameof, since that is what a provider actually sends.
    private static Dictionary<string, object?> Decline(string? question = Question, bool cannotGround = true) =>
        new() { ["cannotGround"] = cannotGround, ["question"] = question, ["steps"] = null };

    private const string Question = "do you mean the printed catalogue or the web one?";

    private readonly List<string> _systemPrompts = new();
    private readonly List<string> _userPrompts = new();

    // Captured because prompt text alone cannot reveal a schema difference the model actually reads.
    private readonly List<IList<AITool>?> _toolSets = new();

    // Reads the schema off the argument the planner passed, so sending the wrong tool is still caught.
    private JsonElement ToolSchemaOfTurn(int turn)
    {
        var tools = _toolSets[turn];
        Assert.NotNull(tools);
        return Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools!)).JsonSchema;
    }

    // A union type comes back as an array, so it is flattened to "array|null" rather than throwing.
    private static string SchemaType(JsonElement node)
    {
        var type = node.GetProperty("type");
        return type.ValueKind == JsonValueKind.Array
            ? string.Join("|", type.EnumerateArray().Select(e => e.GetString()))
            : type.GetString() ?? string.Empty;
    }

    private string LastPrompt => _systemPrompts[^1];

    private string LastUserPrompt => _userPrompts[^1];

    // One entry per constrained turn: the first attempt, then the firm retry, with the last entry reused beyond.
    private void ReturnsPlanTurns(UsageDetails? usage, params Dictionary<string, object?>?[] turns)
    {
        var served = 0;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                _toolSets.Add(ci.ArgAt<IList<AITool>?>(2));
                var args = turns[Math.Min(served++, turns.Length - 1)];
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), args, usage);
            });
    }

    private void ReturnsPlan(Dictionary<string, object?>? emitArgs, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var messages = ci.ArgAt<IList<ChatMessage>>(0);
                _systemPrompts.Add(messages[0].Text ?? string.Empty);
                _userPrompts.Add(messages[1].Text ?? string.Empty);
                _toolSets.Add(ci.ArgAt<IList<AITool>?>(2));
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), emitArgs, usage);
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

    private void AssertReasoningTurns(int count)
    {
        _ = _ai.Received(count).GetChatResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    private void AssertConstrainedTurns(int count)
    {
        _ai.Received(count).GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
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
    public async Task PlanAsync_SystemPromptIncludesGroupByFileRule()
    {
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("Group by logical change, not by file", LastPrompt);
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

    // ---- decline: the plan turn may refuse to ground the goal, distinct from the single-turn degrade ----

    // A decline and the no-plan degrade both deserialize to empty steps, so they have to stay distinguishable.
    [Fact]
    public async Task PlanAsync_Decline_IsTheThirdOutcome_NotTheSingleTurnDegrade()
    {
        ReturnsPlan(Decline());

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.CannotGroundGoal);
        Assert.False(result.FallBackToSingleTurn);
        Assert.Empty(result.Steps);
        Assert.Equal(Question, result.ClarificationQuestion);
    }

    // The firm retry's text only makes sense for silence, not for a model that already called emit_plan.
    [Fact]
    public async Task PlanAsync_Decline_ShortCircuitsTheFirmRetry()
    {
        ReturnsPlan(Decline());

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        AssertConstrainedTurns(1);
        Assert.True(result.CannotGroundGoal);
    }

    [Fact]
    public async Task PlanAsync_SilentThenDeclines_HonoursTheDecline_OnTheFirmRetry()
    {
        ReturnsPlanTurns(usage: null, null, Decline());

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        AssertConstrainedTurns(2);
        Assert.True(result.CannotGroundGoal);
        Assert.False(result.FallBackToSingleTurn);
        Assert.Equal(Question, result.ClarificationQuestion);
    }

    [Fact]
    public async Task PlanAsync_Decline_CarriesTheTurnsUsage()
    {
        ReturnsPlan(Decline(), new UsageDetails { InputTokenCount = 9, OutputTokenCount = 4 });

        var single = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(single.CannotGroundGoal);
        Assert.NotNull(single.Usage);
        Assert.Equal(9, single.Usage!.InputTokenCount);
        Assert.Equal(4, single.Usage.OutputTokenCount);

        _systemPrompts.Clear();
        _userPrompts.Clear();
        _toolSets.Clear(); // keep the three per-turn captures index-aligned across the re-stub
        ReturnsPlanTurns(new UsageDetails { InputTokenCount = 9, OutputTokenCount = 4 }, null, Decline());

        var afterRetry = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(afterRetry.CannotGroundGoal);
        Assert.Equal(18, afterRetry.Usage!.InputTokenCount);  // the silent attempt plus the declining retry
        Assert.Equal(8, afterRetry.Usage.OutputTokenCount);
    }

    // The flag is the discriminator, not the question text.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PlanAsync_DeclineWithNoQuestion_StillDeclines_AndNeverDegrades(string? question)
    {
        ReturnsPlan(Decline(question));

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.CannotGroundGoal);
        Assert.False(result.FallBackToSingleTurn);
        AssertConstrainedTurns(1);
        Assert.Null(result.ClarificationQuestion);  // blank normalizes to null, so one nullness test covers all three rows
    }

    // A plan the model disowned in the same breath must never run.
    [Fact]
    public async Task PlanAsync_DeclinesAndAlsoEmitsSteps_TheDeclineWins()
    {
        var contradictory = Steps(("Gather", "collect the inputs", null), ("Draft", "write the draft", null));
        contradictory["cannotGround"] = true;
        contradictory["question"] = Question;
        ReturnsPlan(contradictory);

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.True(result.CannotGroundGoal);
        Assert.Empty(result.Steps);
    }

    // Without the decline on the retry too, the retry becomes a demand for a plan with no alternative.
    [Fact]
    public async Task PlanAsync_PlanPrompt_OffersTheDecline_OnTheFirstTurnAndTheFirmRetry()
    {
        ReturnsPlan(emitArgs: null); // silent on both turns, so both prompts get recorded

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Equal(2, _systemPrompts.Count);
        Assert.All(_systemPrompts, p =>
        {
            Assert.Contains("cannotGround", p, StringComparison.Ordinal);
            Assert.Contains("question", p, StringComparison.Ordinal);
            Assert.Contains("do NOT invent steps", p, StringComparison.Ordinal);
            // A gate that refuses goals the model could have planned is worse than no gate.
            Assert.Contains("however terse, gets a plan", p, StringComparison.Ordinal);
        });
        Assert.Contains("You did not call emit_plan", _systemPrompts[^1], StringComparison.Ordinal);
        // Non-vacuity: a silent model still degrades, so the prompt text alone does not turn silence into a decline.
        Assert.True(result.FallBackToSingleTurn);
        Assert.False(result.CannotGroundGoal);
    }

    // Annotating the steps parameter as nullable must not propagate nullability down into the item properties.
    [Fact]
    public async Task PlanAsync_PlanTool_OffersTheDecline_ButKeepsTheStepItemsStrict()
    {
        ReturnsPlan(Steps(("Do", "do the thing", null)));

        var result = await BuildPlanner().PlanAsync(Goal, Ctx(), Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Single(result.Steps); // an ordinary successful plan, so nothing below is read off a degrade
        var schema = ToolSchemaOfTurn(0);
        var props = schema.GetProperty("properties");

        Assert.True(props.TryGetProperty("cannotGround", out var flag));
        Assert.Equal("boolean", SchemaType(flag));
        Assert.True(props.TryGetProperty("question", out _));
        // Optional-when-declining: with all three members defaulted the generator emits no top-level "required".
        Assert.False(schema.TryGetProperty("required", out _));

        var steps = props.GetProperty("steps");
        Assert.Equal("array", SchemaType(steps));
        var items = steps.GetProperty("items");
        Assert.Equal("object", SchemaType(items));
        var itemProps = items.GetProperty("properties");
        Assert.Equal("string", SchemaType(itemProps.GetProperty("title")));
        Assert.Equal("string", SchemaType(itemProps.GetProperty("intent")));
        Assert.Equal(
            new[] { "title", "intent" },
            items.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // The clarification question and the goal are both user-derived payload, so neither may reach a support log.
    [Fact]
    public async Task PlanAsync_Decline_LogsThatItDeclined_ButNeverTheQuestion()
    {
        var (planner, provider, log) = PlannerWithLog();
        ReturnsReasoning(Analysis);
        ReturnsPlan(Decline());

        var result = await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.True(result.CannotGroundGoal);
        var release = log.ReleaseVisible();
        Assert.Contains(release, m => m.Contains("DECLINED", StringComparison.Ordinal));
        Assert.DoesNotContain(release, m => m.Contains(Question, StringComparison.Ordinal));
        Assert.DoesNotContain(release, m => m.Contains(Goal, StringComparison.Ordinal));
    }

    // A replan never offers the decline, so a model that invents the member reads as a no-steps turn.
    [Fact]
    public async Task ReplanAsync_DeclineMember_IsNotHonoured_AndNeitherThePromptNorTheToolEverOffersIt()
    {
        ReturnsPlan(Decline());

        var revised = await BuildPlanner().ReplanAsync(Ctx(), "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.False(revised.CannotGroundGoal);
        Assert.True(revised.FallBackToSingleTurn); // the replan's own single-turn degrade
        Assert.Null(revised.ClarificationQuestion);
        AssertConstrainedTurns(2);                 // and it did go through the replan's firm retry
        Assert.All(_systemPrompts, p => Assert.DoesNotContain("cannotGround", p, StringComparison.Ordinal));

        // BOTH replan turns, first attempt and firm retry: the schema the model was handed cannot say "decline".
        Assert.Equal(2, _toolSets.Count);
        for (var turn = 0; turn < _toolSets.Count; turn++)
        {
            var schema = ToolSchemaOfTurn(turn);
            var props = schema.GetProperty("properties");
            Assert.False(props.TryGetProperty("cannotGround", out _));
            Assert.False(props.TryGetProperty("question", out _));
            // …and it is otherwise the original tool verbatim: steps required, items strict.
            Assert.Equal(
                new[] { "steps" },
                schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal("array", SchemaType(props.GetProperty("steps")));
            Assert.Equal("object", SchemaType(props.GetProperty("steps").GetProperty("items")));
        }
    }

    // ---- the planning turn's spend must reach the ledger (it used to be discarded here) ----

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

    // ---- a resumed run's replan judge must be told the pre-pause steps already ran ----

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

        // The block rides the USER message because a step title or intent can be raw user keystrokes and
        // TokenizeMessages rewrites ChatRole.User text only.
        Assert.Contains("Completed so far", LastUserPrompt);
        Assert.Contains("[ok] Early: ran before the pause", LastUserPrompt);
        Assert.Contains(CompletedStepSummary.EarlierSegmentNote, LastUserPrompt); // ran, text just unavailable
        Assert.Contains("do NOT repeat these steps", LastUserPrompt);

        // The block must not ALSO be in the System prompt, where the tokenizer would never see it.
        Assert.DoesNotContain("Completed so far", LastPrompt);
        Assert.DoesNotContain("ran before the pause", LastPrompt);
    }

    /// <summary>Told nothing about a skipped row, the replanner can emit a fresh step for the very work the user deleted.</summary>
    [Fact]
    public async Task ReplanAsync_SkippedSteps_AreListedAsRemoved_OnTheUserMessage()
    {
        ReturnsPlan(Steps(("Recover", "finish the goal", null)));
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        ctx.SetSkippedTitles(["Delete the old backups"]);

        await BuildPlanner().ReplanAsync(ctx, "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("The user REMOVED these steps from the plan", LastUserPrompt);
        Assert.Contains("- Delete the old backups", LastUserPrompt);
        Assert.DoesNotContain("Delete the old backups", LastPrompt); // never the System prompt
    }

    /// <summary>Non-vacuity for the fact above: with no skipped steps the prohibition must not appear at all,
    /// so an unconditional block could not pass both.</summary>
    [Fact]
    public async Task ReplanAsync_NoSkippedSteps_SaysNothingAboutRemovedWork()
    {
        ReturnsPlan(Steps(("Recover", "finish the goal", null)));
        var ctx = new RunContext("build a thing", RunProfile.Interactive);

        await BuildPlanner().ReplanAsync(ctx, "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("REMOVED", LastUserPrompt);
        Assert.DoesNotContain("REMOVED", LastPrompt);
    }

    [Fact]
    public async Task ReplanAsync_SystemPromptIncludesGroupByFileRule()
    {
        ReturnsPlan(Steps(("Recover", "finish the goal", null)));

        await BuildPlanner().ReplanAsync(Ctx(), "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        Assert.Contains("Group by logical change, not by file", LastPrompt);
    }

    [Fact]
    public async Task ReplanAsync_LiveStep_CarriesNoEarlierSegmentNote()
    {
        ReturnsPlan(Steps(("Recover", "finish the goal", null)));
        var ctx = new RunContext("build a thing", RunProfile.Interactive);
        ctx.RecordStep(new AgentStep { Ordinal = 0, Title = "Live", Intent = "ran in this segment" },
            new StepTurnResult(true, false, null, "visible", null, Guid.NewGuid(), Guid.NewGuid()));

        await BuildPlanner().ReplanAsync(ctx, "boom", Persona(), Provider(), TestContext.Current.CancellationToken);

        // Read off the USER message — see the sibling fact above for the argument.
        Assert.Contains("[ok] Live: ran in this segment", LastUserPrompt);
        Assert.DoesNotContain(CompletedStepSummary.EarlierSegmentNote, LastUserPrompt);
        Assert.DoesNotContain("ran in this segment", LastPrompt);
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
        // StartsWith, not Contains: the goal must LEAD the message, and the system prompt never carries it,
        // so a goal appended after the analysis block would mean planning for nothing.
        Assert.StartsWith(Goal, LastUserPrompt, StringComparison.Ordinal);
        Assert.Contains(Analysis, LastUserPrompt);
    }

    [Fact]
    public async Task PlanAsync_ReasoningTurn_SendsNoTools_SoTheEffortSurvives()
    {
        // AiClientService computes hasTools from the tools argument, so a tools:null call is the one shape
        // that still carries the configured reasoning effort.
        var (planner, provider) = PlannerFor(
            AiProviderType.Ollama, dropsEffortWithTools: true, ReasoningEffort.High, reasoningTurnEnabled: true);
        ReturnsReasoning(Analysis);
        ReturnsPlan(Steps(("Gather", "collect the inputs", null)));

        await planner.PlanAsync(Goal, Ctx(), Persona(), provider, TestContext.Current.CancellationToken);

        Assert.True(_reasoningToolsCaptured); // the call really happened; the assertion is not vacuous
        Assert.False(_reasoningSawTools);
        // The reasoning turn must not be told about emit_plan — no tool schema is even attached to it.
        Assert.DoesNotContain("emit_plan", _reasoningRequests[^1][0].Text ?? string.Empty);
        // The stub answers whatever was sent, so without these two the reasoning turn could be spending a
        // full provider round on an empty request.
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

    /// <summary>Distinct ends, because a homogeneous filler string cannot say WHICH half of the analysis survived truncation.</summary>
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
        // Tight on purpose: a correct run lands on exactly 4137 chars, and a looser bound would let the cap
        // be raised without failing here.
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
        // The gate's catch-all may swallow a settings read that FAILED, but not one that was CANCELLED —
        // that would let planning march on with a token the caller already cancelled.
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

    // ---- privacy: no log line may put user content at a release-visible level ----

    /// <summary>Deliberately not a substring of any log line this class emits, unlike the default provider name.</summary>
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

    /// <summary><c>SensitiveDebug</c> lines are present in this Debug build, so only the release-visible subset can be asserted over.</summary>
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
