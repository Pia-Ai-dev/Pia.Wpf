using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>End-to-end park/ask/refusal behavior lives in <c>MidPlanAskTests</c>; this file covers
/// <see cref="UserInputRequestStore"/> and <see cref="AgentStepTools"/> in isolation.</summary>
public sealed class UserInputRequestSignalTests
{
    private static Dictionary<string, object?> Args(object? question)
    {
        var d = new Dictionary<string, object?>();
        if (question is not null) d["question"] = question;
        return d;
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    private static UserInputRequestStore RootStore() => new(canAsk: true);

    private static UserInputRequestStore DelegatedStore() => new(canAsk: false);

    /// <summary>Asserted against the literal so a renamed constant can't silently change this provider-visible tool name.</summary>
    [Fact]
    public void TheToolIsNamedRequestUserInput() =>
        Assert.Equal("request_user_input", AgentStepTools.RequestUserInputToolName);

    /// <summary>Delegated (child) steps must not ask — a parked child run has no surface to show the question.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanRequestUserInput_IsTrueOnlyForARootRun(bool isChild) =>
        Assert.Equal(!isChild, AgentStepTools.CanRequestUserInput(isChild ? Guid.NewGuid() : null));

    /// <summary>The list must be copied — <c>setup.Tools</c> is the same cached instance the run's ordinary chat turns use, and mutating it in place would leak a step-only tool into every later turn.</summary>
    [Fact]
    public void WithRequestUserInputTool_DoesNotMutateTheOriginalList()
    {
        var original = new List<AITool> { AIFunctionFactory.Create(() => "ok", "unrelated", "d") };
        var setup = new AssistantTurnSetup("sys", original, SupportsTools: true, WebSearchActive: false);

        var augmented = AgentStepTools.WithRequestUserInputTool(setup);

        Assert.True(AgentStepTools.OffersRequestUserInputTool(augmented.Tools));
        Assert.False(AgentStepTools.OffersRequestUserInputTool(original));
        Assert.Single(original);
    }

    /// <summary>A <c>SupportsTools=false</c> setup carries neither a tools list nor a handler, so there is nothing to augment.</summary>
    [Fact]
    public void WithRequestUserInputTool_IsANoOpWhenTheTurnHasNoTools()
    {
        var setup = new AssistantTurnSetup("sys", null, SupportsTools: false, WebSearchActive: false);

        var augmented = AgentStepTools.WithRequestUserInputTool(setup);

        Assert.Same(setup.Tools, augmented.Tools);
        Assert.False(AgentStepTools.OffersRequestUserInputTool(augmented.Tools));
    }

    /// <summary>The null arm matters because a <c>SupportsTools=false</c> setup carries a null list and the executors feed it straight in.</summary>
    [Fact]
    public void OffersRequestUserInputTool_IsFalseForNullAndForAnUnrelatedList()
    {
        Assert.False(AgentStepTools.OffersRequestUserInputTool(null));
        Assert.False(AgentStepTools.OffersRequestUserInputTool(
            [AIFunctionFactory.Create(() => "ok", "write_file", "d")]));
    }

    [Fact]
    public void BothStepToolsCanBeOfferedOnOneTurn_AndNeitherHidesTheOther()
    {
        var setup = new AssistantTurnSetup("sys", new List<AITool>(), SupportsTools: true, WebSearchActive: false);

        var augmented = AgentStepTools.WithRequestUserInputTool(AgentStepTools.WithStepResultTool(setup));

        Assert.True(AgentStepTools.OffersStepResultTool(augmented.Tools));
        Assert.True(AgentStepTools.OffersRequestUserInputTool(augmented.Tools));
        Assert.Equal(2, augmented.Tools!.Count);
    }

    [Fact]
    public void Record_CapturesTheQuestion_AndTellsTheModelToStop()
    {
        var store = RootStore();

        var reply = store.Record(Args("Which environment — staging or production?"));

        Assert.Equal("Which environment — staging or production?", store.Question);
        Assert.Equal(UserInputRequestStore.Accepted, reply);
        Assert.Equal(1, store.AcceptedCalls);
        Assert.Equal(0, store.RefusedCalls);
    }

    /// <summary><c>Microsoft.Extensions.AI</c> hands tool arguments through as deserialized JSON, so a <see cref="JsonElement"/> question is the encoding that actually shows up in production.</summary>
    [Fact]
    public void Record_ReadsAJsonElementQuestion()
    {
        var store = RootStore();

        store.Record(Args(Json("\"which repo?\"")));

        Assert.Equal("which repo?", store.Question);
    }

    /// <summary>First call wins — the opposite of <c>StepOutcomeStore.Claim</c>'s last-wins — since the park must show the question that actually stopped the run.</summary>
    [Fact]
    public void Record_FirstCallWins_AndTheSecondIsCountedButNotShown()
    {
        var store = RootStore();

        var first = store.Record(Args("first question"));
        var second = store.Record(Args("second question"));

        Assert.Equal("first question", store.Question);
        Assert.Equal(UserInputRequestStore.Accepted, first);
        Assert.Equal(UserInputRequestStore.AlreadyAsked, second);
        Assert.Equal(2, store.AcceptedCalls);
    }

    /// <summary>Parking on a blank question is worse than not asking: the chat post no-ops on blank text and the Flow card would be left with no question shown anywhere.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_WithoutAUsableQuestion_RecordsNothing(string? question)
    {
        var store = RootStore();

        var reply = store.Record(Args(question));

        Assert.Null(store.Question);
        Assert.Equal(UserInputRequestStore.NeedsAQuestion, reply);
        Assert.Equal(0, store.AcceptedCalls);
        Assert.Equal(1, store.RefusedCalls);
    }

    /// <summary>The refusal is checked before the question is even read, so no state exists for a later change to accidentally act on.</summary>
    [Fact]
    public void Record_OnADelegatedStep_RefusesAndRecordsNothing()
    {
        var store = DelegatedStore();

        var reply = store.Record(Args("please tell me which repo"));

        Assert.Null(store.Question);
        Assert.Equal(UserInputRequestStore.RefusedForDelegatedStep, reply);
        Assert.Equal(0, store.AcceptedCalls);
        Assert.Equal(1, store.RefusedCalls);
    }

    /// <summary>The refusal must name the working channel, not merely say no — a bare "no" reads as prose, which the text fallback treats as a declared success and the parent replans on a lie.</summary>
    [Fact]
    public void TheDelegatedRefusal_PointsAtEmitStepResult() =>
        Assert.Contains(
            AgentStepTools.EmitStepResultToolName,
            UserInputRequestStore.RefusedForDelegatedStep,
            StringComparison.Ordinal);

    /// <summary>Head-capped because the question is re-seeded into the model's transcript on every later segment of the run, so an unbounded one would be paid for repeatedly.</summary>
    [Fact]
    public void Record_CapsALongQuestion_KeepingTheHead()
    {
        var store = RootStore();

        store.Record(Args(new string('q', 5000)));

        Assert.NotNull(store.Question);
        Assert.StartsWith("qqq", store.Question, StringComparison.Ordinal);
        Assert.EndsWith("…", store.Question, StringComparison.Ordinal);
        Assert.True(store.Question!.Length < 5000);
    }

    /// <summary>Newlines survive trimming, deliberately unlike <c>StepOutcomeStore</c>: that store flattens because its text is rendered as a fact line inside a later prompt, where an embedded newline could forge a surrounding line, but a question is never fenced into a prompt.</summary>
    [Fact]
    public void Record_TrimsButKeepsInternalNewlines()
    {
        var store = RootStore();

        store.Record(Args("  Which one?\nA or B?  "));

        Assert.Equal("Which one?\nA or B?", store.Question);
    }

    /// <summary>Repeat asks are counted but never refused — there is deliberately no limit here.</summary>
    [Fact]
    public void RepeatAsks_AreCounted_ButNeverRefusedForBeingRepeats()
    {
        var store = RootStore();

        for (var i = 0; i < 25; i++)
            store.Record(Args($"question {i}"));

        Assert.Equal(25, store.AcceptedCalls);
        Assert.Equal(0, store.RefusedCalls);
        Assert.Equal("question 0", store.Question);
    }
}
