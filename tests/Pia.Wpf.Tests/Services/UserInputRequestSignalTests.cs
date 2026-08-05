using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// <b>Batch 18 G5 (D3/D4/D6, owner Q1/Q5) — the pieces in isolation:</b> <see cref="UserInputRequestStore"/>'s
/// argument handling and <see cref="AgentStepTools"/>' second tool-list augmentation. The end-to-end facts (a run
/// that really parks, the question that really reaches the chat, the delegated refusal driven through a real
/// launcher) live in <c>MidPlanAskTests</c>; this file covers the decisions that are invisible from there.
/// <para>
/// The shape deliberately mirrors <c>StepOutcomeSignalTests</c>, because the two tools are siblings — same choke
/// point, same pre-route interception, same per-step sink lifetime — and a reader who knows one should be able to
/// read the other without re-learning the layout.
/// </para>
/// <para>
/// <b>What this file does NOT test, and why.</b> There is no "the run may only ask N times" fact anywhere, in any
/// file. 18 D4 is "model declares, no cap": the owner was shown the stall risk (spec §5) and chose it, so a test
/// asserting a limit would be asserting a decision that was made the other way. What IS asserted is that repeat
/// asks are COUNTED (<see cref="UserInputRequestStore.AcceptedCalls"/> /
/// <see cref="UserInputRequestStore.RefusedCalls"/>) — counting is not capping, and it is what lets a cap be a
/// measured follow-up rather than a guess.
/// </para>
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are written, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
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

    // ------------------------------------------------------------------ the tool's identity and scope

    /// <summary>
    /// The wire NAME, as a literal. Both interception seams and the tool schema key on this one constant, and a
    /// test that referenced the constant could not catch the constant itself being changed — the same discipline
    /// <c>StepOutcomeSignalTests.TheToolIsNamedEmitStepResult</c> applies to the sibling. Owner Q5 chose this
    /// name; a rename is a provider-visible contract change and should red here first.
    /// </summary>
    [Fact]
    public void TheToolIsNamedRequestUserInput() =>
        Assert.Equal("request_user_input", AgentStepTools.RequestUserInputToolName);

    /// <summary>
    /// <b>OWNER Q1, the predicate.</b> A ROOT run may ask; a DELEGATED one may not. Asserted on the predicate
    /// directly as well as end-to-end, because it is the single line both executors and the store all key on, and
    /// a mis-signed change here would be visible nowhere else until a child run parked behind a card nobody can
    /// see (spec §4.5's <c>:170</c> filter).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanRequestUserInput_IsTrueOnlyForARootRun(bool isChild) =>
        Assert.Equal(!isChild, AgentStepTools.CanRequestUserInput(isChild ? Guid.NewGuid() : null));

    /// <summary>
    /// The augmentation copies the list, exactly as <c>WithStepResultTool</c> does and for the identical reason:
    /// an executor's <c>AssistantTurnSetup</c> is resolved once and cached for the whole run — on the live path it
    /// is the very instance the session's ordinary chat turns use — so mutating <c>setup.Tools</c> in place would
    /// leak a step-only tool into every later turn.
    /// </summary>
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

    /// <summary>
    /// A <c>SupportsTools=false</c> setup is returned untouched: the exchange engines pass neither tools nor a
    /// tool handler in that case, so there would be nothing to offer it to and nothing to intercept. Such a step
    /// simply cannot ask, which is correct — and it is also why the store is armed from the RESOLVED list rather
    /// than from a flag.
    /// </summary>
    [Fact]
    public void WithRequestUserInputTool_IsANoOpWhenTheTurnHasNoTools()
    {
        var setup = new AssistantTurnSetup("sys", null, SupportsTools: false, WebSearchActive: false);

        var augmented = AgentStepTools.WithRequestUserInputTool(setup);

        Assert.Same(setup.Tools, augmented.Tools);
        Assert.False(AgentStepTools.OffersRequestUserInputTool(augmented.Tools));
    }

    /// <summary>Null and an unrelated list are both "not offered" — the null arm matters because a
    /// <c>SupportsTools=false</c> setup carries a null list and the executors feed it straight in.</summary>
    [Fact]
    public void OffersRequestUserInputTool_IsFalseForNullAndForAnUnrelatedList()
    {
        Assert.False(AgentStepTools.OffersRequestUserInputTool(null));
        Assert.False(AgentStepTools.OffersRequestUserInputTool(
            [AIFunctionFactory.Create(() => "ok", "write_file", "d")]));
    }

    /// <summary>
    /// <b>18 D6, asserted as a NEGATIVE.</b> Adding the ask tool must not disturb the declaration tool: the two
    /// are separate channels and the outcome bool is untouched. A change that folded them together (one method,
    /// one flag) would red here — and the whole reason G5 stayed small is that nothing rippled into
    /// <c>StepOutcomeStore</c>, <c>StepTurnResult.Succeeded</c>, <c>ReplanAsync</c>, the step chips or
    /// <c>AgentVerifier</c>'s <c>[declared]</c> vocabulary.
    /// </summary>
    [Fact]
    public void BothStepToolsCanBeOfferedOnOneTurn_AndNeitherHidesTheOther()
    {
        var setup = new AssistantTurnSetup("sys", new List<AITool>(), SupportsTools: true, WebSearchActive: false);

        var augmented = AgentStepTools.WithRequestUserInputTool(AgentStepTools.WithStepResultTool(setup));

        Assert.True(AgentStepTools.OffersStepResultTool(augmented.Tools));
        Assert.True(AgentStepTools.OffersRequestUserInputTool(augmented.Tools));
        Assert.Equal(2, augmented.Tools!.Count);
    }

    // ------------------------------------------------------------------ the store

    /// <summary>The happy path: the question is captured, the model is told the run will stop, and the ask is
    /// counted.</summary>
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

    /// <summary>
    /// <c>Microsoft.Extensions.AI</c> hands tool arguments through as deserialized JSON, so the
    /// <see cref="JsonElement"/> encoding is the one that actually shows up in production. Reused from
    /// <c>StepOutcomeStore.ReadString</c> rather than re-implemented, which is what this row protects.
    /// </summary>
    [Fact]
    public void Record_ReadsAJsonElementQuestion()
    {
        var store = RootStore();

        store.Record(Args(Json("\"which repo?\"")));

        Assert.Equal("which repo?", store.Question);
    }

    /// <summary>
    /// <b>FIRST call wins</b> — the opposite of <c>StepOutcomeStore.Claim</c>'s last-wins, and matching
    /// <c>ToolApprovalStore.PendingToolName</c>. The park carries ONE question and that question is what the
    /// person reads in the run's chat, so it must be the one that actually stopped the run; a second call is one
    /// the model made after being told the run was parking. The count still moves, which is how "the model kept
    /// going" stays visible.
    /// </summary>
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

    /// <summary>
    /// <b>GUARD.</b> A blank/missing question records NOTHING and asks for a retry. Parking on an empty question
    /// is the one outcome worse than not asking at all: the chat post no-ops on blank text and the Flow card is
    /// token-keyed by rule (§4.4), so the person would find a run stopped and waiting with the question nowhere.
    /// Same shape <c>StepOutcomeStore.Record</c> uses for a missing <c>succeeded</c>.
    /// </summary>
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

    /// <summary>
    /// <b>OWNER Q1 at the sink.</b> A delegated step's ask records nothing, whatever it wrote — the refusal is
    /// checked BEFORE the question is even read, so there is no state a later change could accidentally act on.
    /// </summary>
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

    /// <summary>
    /// The refusal must NAME the channel that works, not merely say no. A delegated step told only "no" writes
    /// prose instead, and prose is exactly what §1.3's text fallback reads as a declared success — the block is
    /// then swallowed and the parent replans on a lie. Asserting the substring rather than the whole sentence:
    /// the wording is prompt copy and may be tuned, the REDIRECT may not silently disappear.
    /// </summary>
    [Fact]
    public void TheDelegatedRefusal_PointsAtEmitStepResult() =>
        Assert.Contains(
            AgentStepTools.EmitStepResultToolName,
            UserInputRequestStore.RefusedForDelegatedStep,
            StringComparison.Ordinal);

    /// <summary>
    /// The question is head-capped. It becomes a durable chat row AND is re-seeded into the model's transcript on
    /// every later segment of the run, so an unbounded one is paid for on every later turn. Same number as
    /// <c>RunClarifications.MaxAnswerChars</c> — one bound for both halves of the exchange.
    /// </summary>
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

    /// <summary>
    /// Trimmed, but NEWLINES SURVIVE — deliberately unlike <c>StepOutcomeStore</c>, which flattens. That flatten
    /// exists because a claim's summary is rendered as its own line inside a later PROMPT, where an embedded
    /// newline could forge a surrounding fact line. A question is never fenced into a prompt: it becomes an
    /// ordinary assistant chat row, where a paragraph break is formatting rather than a forgery surface.
    /// </summary>
    [Fact]
    public void Record_TrimsButKeepsInternalNewlines()
    {
        var store = RootStore();

        store.Record(Args("  Which one?\nA or B?  "));

        Assert.Equal("Which one?\nA or B?", store.Question);
    }

    /// <summary>
    /// <b>18 D4, the observability half (spec §5).</b> A store that has been asked repeatedly reports HOW MANY —
    /// and still holds the first question, and still has no notion of a limit. This is the fact that makes a cap
    /// a measured follow-up instead of a guess; it is not a cap and must not become one.
    /// </summary>
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
