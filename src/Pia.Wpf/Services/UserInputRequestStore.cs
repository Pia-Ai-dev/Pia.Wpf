namespace Pia.Services;

/// <summary>Per-step, non-persisted sink for a <c>request_user_input</c> call; <see cref="Question"/> is sensitive and must only be logged via <c>SensitiveDebug</c>. No cap on how many times a run may park and ask — a settled product decision, not an oversight.</summary>
public sealed class UserInputRequestStore
{
    private readonly object _lock = new();

    /// <summary>Matches <see cref="RunClarifications.MaxAnswerChars"/> so a question and its answer share one cap.</summary>
    internal const int MaxQuestionChars = 1000;

    /// <summary>Shown when the ask is accepted. Not localized — it's a model prompt, not UI text, and the headless executor has no <c>ILocalizationService</c>.</summary>
    public const string Accepted =
        "Recorded: this run will stop after this step and ask the person your question. Nothing more happens in "
        + "this step — stop now and make no further tool calls. Everything you did in this step is discarded, and "
        + "the step runs again from the beginning once someone answers.";

    public const string AlreadyAsked =
        "Already recorded: this run is stopping to ask your FIRST question, and only that one is shown to the "
        + "person. Nothing more happens in this step — stop now and make no further tool calls.";

    /// <summary>Shown when a delegated step tries to ask — it has no one to ask, so it must declare the block via <c>emit_step_result</c> instead.</summary>
    public const string RefusedForDelegatedStep =
        "Not available here: this step is running as a delegated sub-run, which has no way to reach a person — a "
        + "sub-run that stopped to ask would wait behind a question nobody was shown. If you are blocked, call "
        + "emit_step_result with succeeded=false and say exactly what you are missing. The run that delegated this "
        + "step sees that and can either ask on your behalf or plan around it.";

    public const string NeedsAQuestion =
        "request_user_input needs a non-empty 'question' argument. Call it again with the question written out, or "
        + "carry on without asking.";

    /// <param name="canAsk">Whether this run may ask at all; derived from the resolved tool list so "offered" and "accepted" cannot drift apart.</param>
    public UserInputRequestStore(bool canAsk) => CanAsk = canAsk;

    /// <summary>Whether this run may stop and ask a person a mid-plan question; false for a delegated step.</summary>
    public bool CanAsk { get; }

    /// <summary>The question this run will park on, or null when nothing asked. First call wins. Sensitive — log only via <c>SensitiveDebug</c>.</summary>
    public string? Question { get; private set; }

    /// <summary>How many well-formed asks arrived in this step; only the first is kept in <see cref="Question"/>.</summary>
    public int AcceptedCalls { get; private set; }

    /// <summary>How many asks were refused (a delegated run, or a call with no usable question).</summary>
    public int RefusedCalls { get; private set; }

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

        // Newlines are kept (unlike StepOutcomeStore's flatten) — this becomes a chat row, not a fenced prompt line.
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
