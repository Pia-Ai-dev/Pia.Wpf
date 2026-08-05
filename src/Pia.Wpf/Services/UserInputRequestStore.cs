namespace Pia.Services;

/// <summary>
/// <b>18 D3/D4 (G5).</b> The per-step sink the <c>request_user_input</c> interception writes into when a STEP
/// declares it is blocked on something only the person who started the run can settle. One store is created per
/// step turn by the executor that offered the tool, and its lifetime is exactly that step's exchange — the same
/// in-process, non-persisted shape <see cref="StepOutcomeStore"/>, <see cref="ToolApprovalStore"/>,
/// <c>RunSteeringStore</c> and <c>ExecutingRunStore</c> have.
/// <para>
/// <b>This is NOT a third <c>emit_step_result</c> outcome (18 D6).</b> The outcome bool is untouched, and that is
/// what keeps this batch small: nothing ripples into <see cref="StepOutcomeStore"/>, <c>StepTurnResult.Succeeded</c>,
/// <c>AgentRunOrchestrator.ReplanAsync</c>, the panel's step chips or <c>AgentVerifier</c>'s <c>[declared]</c> tag
/// vocabulary. Spec §2 is the reason a third outcome would not have helped at all: <c>emit_step_result</c>'s own
/// description ALREADY tells the model, in plain words, that explaining a failure in prose is not a failure report
/// — and the model in the repro wrote prose anyway. <b>The failure mode is the ABSENCE of a call</b>, so the fix has
/// to be a differently-shaped channel, not another member on a tool nobody called.
/// </para>
/// <para>
/// <b>ARMED ON EVERY REAL STEP TURN — deliberately NOT "armed iff offered".</b> <see cref="StepOutcomeStore"/>'s
/// rule is armed-iff-offered, and its stated reason is that an ORDINARY CHAT turn is never offered the tool, so a
/// model that invents the name there must still get the honest "Unknown tool." answer. That reason does not reach
/// here: this store only ever exists on a step turn, where the tool IS part of the vocabulary — it is merely
/// REFUSED for a delegated run (see <see cref="CanAsk"/>). Answering such a call with "you may not ask on a
/// delegated step; declare the block through <c>emit_step_result</c> instead" is strictly better than routing it
/// into <c>RouteToolCallAsync</c> for an "Unknown tool." dead end, and it keeps the
/// <c>ToolGateDecision.UnknownTool</c> timeline row out of the audit table either way.
/// </para>
/// <para>
/// <b>SENSITIVE.</b> <see cref="Question"/> is model-generated text derived from the user's goal — payload under
/// CLAUDE.md. It may only be logged through <c>Pia.Logging.LoggingExtensions.SensitiveDebug</c> or a
/// <c>Sensitive*</c> sibling, never as an argument to <c>LogInformation</c>/<c>LogWarning</c>/<c>LogDebug</c>/
/// <c>LogError</c>/<c>LogTrace</c>. The counts below are app-owned scalars and are free to log; the text is not.
/// (<c>GoalClarificationLoggingRuleTests</c> is the source-level scan that holds this — spec §8.6 explains why it
/// cannot be a sink assertion.)
/// </para>
/// <para>
/// <b>NO CAP, by owner decision (18 D4, spec §5).</b> The owner was shown the stall risk — an unattended run may
/// park for a question any number of times — and chose "model declares, no cap" anyway. An implementer who finds
/// themselves adding a per-run limit here has re-opened a settled decision. The tool's DESCRIPTION carries the
/// entire weight of "critical only", and §2 is the reason to be pessimistic about that: a description is a
/// request. What this batch does instead is make repeat parks OBSERVABLE — the counters below and the run-scoped
/// park line the orchestrator writes — so a cap, if it is ever needed, can be a MEASURED follow-up. Counting is
/// not capping.
/// </para>
/// </summary>
public sealed class UserInputRequestStore
{
    private readonly object _lock = new();

    /// <summary>
    /// Cap on the question, matching <c>RunClarifications.MaxAnswerChars</c> — the same bound from the other side
    /// of the exchange, so a question and its answer are held to one number rather than two opinions. Head-kept,
    /// like every other text cap on this spine. The question becomes a durable chat row and is re-seeded into the
    /// model's transcript on the next segment, so an unbounded one would be paid for on every later turn of the run.
    /// </summary>
    internal const int MaxQuestionChars = 1000;

    /// <summary>
    /// What the model is told when the call is ACCEPTED. Deliberately NOT localized, for the same reason
    /// <see cref="AgentStepTools.UndetailedFailure"/> is not: this string is a model prompt, never a UI surface,
    /// and the headless executor has no <c>ILocalizationService</c> at all — localizing it would make the two
    /// executors say different things to the model for the same event.
    /// <para>
    /// It states the COST out loud (the step is abandoned and re-runs from the top) because that is the one fact
    /// the model cannot observe for itself, and because a model that knows the price is likelier to spend it only
    /// where 18 D3 says it should.
    /// </para>
    /// </summary>
    public const string Accepted =
        "Recorded: this run will stop after this step and ask the person your question. Nothing more happens in "
        + "this step — stop now and make no further tool calls. Everything you did in this step is discarded, and "
        + "the step runs again from the beginning once someone answers.";

    /// <summary>
    /// What the model is told on a SECOND and later call in the same step. See <see cref="Question"/> for why
    /// first-wins rather than last-wins.
    /// </summary>
    public const string AlreadyAsked =
        "Already recorded: this run is stopping to ask your FIRST question, and only that one is shown to the "
        + "person. Nothing more happens in this step — stop now and make no further tool calls.";

    /// <summary>
    /// What the model is told when the run may not ask at all (a DELEGATED step — see <see cref="CanAsk"/>). It
    /// names the channel that does work there rather than merely refusing, because a delegated step that is
    /// genuinely blocked and told only "no" is the §1.3 failure restated: it writes prose, the prose reads as a
    /// declared success under <c>HeadlessTurnExecutor</c>'s text fallback, and the block is silently swallowed.
    /// </summary>
    public const string RefusedForDelegatedStep =
        "Not available here: this step is running as a delegated sub-run, which has no way to reach a person — a "
        + "sub-run that stopped to ask would wait behind a question nobody was shown. If you are blocked, call "
        + "emit_step_result with succeeded=false and say exactly what you are missing. The run that delegated this "
        + "step sees that and can either ask on your behalf or plan around it.";

    /// <summary>What the model is told when it called the tool with no usable question. See <see cref="Record"/>.</summary>
    public const string NeedsAQuestion =
        "request_user_input needs a non-empty 'question' argument. Call it again with the question written out, or "
        + "carry on without asking.";

    /// <param name="canAsk">
    /// May THIS run stop and ask? Passed rather than assumed so a caller that builds a store still has to state
    /// the answer — the shape <see cref="ToolApprovalStore"/>'s <c>canPark</c> set. Both executors derive it from
    /// <see cref="AgentStepTools.CanRequestUserInput"/> via the resolved tool list, so "offered" and "accepted"
    /// cannot drift apart.
    /// </param>
    public UserInputRequestStore(bool canAsk) => CanAsk = canAsk;

    /// <summary>
    /// May this run stop and ask a person a mid-plan question? False for a DELEGATED step (owner Q1) — see
    /// <see cref="AgentStepTools.CanRequestUserInput"/> for the reasoning, which is NOT the same reasoning
    /// <c>HeadlessRunLauncher.CanParkForApproval</c> gives for the identical predicate.
    /// </summary>
    public bool CanAsk { get; }

    /// <summary>
    /// The question the run will park on, or null when nothing asked. <b>FIRST call wins</b> — the same rule
    /// <see cref="ToolApprovalStore.PendingToolName"/> uses and the opposite of
    /// <see cref="StepOutcomeStore.Claim"/>'s last-wins, for the reason that is not symmetry: the park carries ONE
    /// question, that question is what the person is shown in the run's chat, and what they are shown must be the
    /// question that actually stopped the run. A later call in the same exchange is one the model made AFTER it
    /// was told the run was parking, so it cannot be the thing being asked.
    /// <para>
    /// SENSITIVE — see the class remarks. <c>SensitiveDebug</c> only.
    /// </para>
    /// </summary>
    public string? Question { get; private set; }

    /// <summary>How many well-formed asks arrived in this step. &gt;1 means the model kept going after being told
    /// to stop; only the first is in <see cref="Question"/>. A count, never the content — safe to log.</summary>
    public int AcceptedCalls { get; private set; }

    /// <summary>How many asks were REFUSED (a delegated run, or a call with no usable question). A count, never
    /// the content — safe to log, and the number that makes "a child kept trying to ask" visible at all.</summary>
    public int RefusedCalls { get; private set; }

    /// <summary>
    /// Records one <c>request_user_input</c> call and returns the string handed back to the model as the tool
    /// result. Four answers, in order:
    /// <list type="number">
    /// <item><see cref="RefusedForDelegatedStep"/> when <see cref="CanAsk"/> is false — checked FIRST, so a
    /// delegated run never records a question even if one was well-formed.</item>
    /// <item><see cref="NeedsAQuestion"/> when the argument is missing or blank. Recording an empty question would
    /// park the run with nothing to answer: the chat post no-ops on blank text, so the person would find a run
    /// stopped and waiting with no question anywhere — the one outcome worse than not asking. This is the shape
    /// <see cref="StepOutcomeStore.Record"/> uses for a missing <c>succeeded</c>, and for the same reason: a
    /// provider's argument-encoding quirk must never be read as a decision.</item>
    /// <item><see cref="AlreadyAsked"/> for the second and later well-formed call.</item>
    /// <item><see cref="Accepted"/> for the first.</item>
    /// </list>
    /// </summary>
    public string Record(IDictionary<string, object?>? arguments)
    {
        if (!CanAsk)
        {
            lock (_lock)
                RefusedCalls++;
            return RefusedForDelegatedStep;
        }

        var question = StepOutcomeStore.ReadString(arguments, "question");
        if (string.IsNullOrWhiteSpace(question))
        {
            lock (_lock)
                RefusedCalls++;
            return NeedsAQuestion;
        }

        // Trim + head-cap only; newlines are KEPT, unlike StepOutcomeStore's flatten. That flatten exists because
        // a claim's summary is rendered as its own line inside a later prompt, where an embedded newline could
        // forge a surrounding fact line. A question is never fenced into a prompt: it becomes an ordinary
        // assistant CHAT ROW (the same place 18 G3 puts the plan turn's question), where a paragraph break is
        // legible formatting rather than a forgery surface.
        var trimmed = question.Trim();
        if (trimmed.Length > MaxQuestionChars)
            trimmed = trimmed[..MaxQuestionChars] + "…";

        lock (_lock)
        {
            AcceptedCalls++;
            if (Question is not null)
                return AlreadyAsked;
            Question = trimmed;
            return Accepted;
        }
    }
}
