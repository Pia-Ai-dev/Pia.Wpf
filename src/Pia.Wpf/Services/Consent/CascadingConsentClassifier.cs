using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Two-stage consent classifier: rule-based first, LLM fallback when rule confidence is below
/// <see cref="LlmFallbackThreshold"/>. When both run, the LLM either confirms (boost confidence)
/// or disagrees with the rule output (demote to ambiguous).
/// </summary>
public sealed class CascadingConsentClassifier : IConsentClassifier
{
    public float LlmFallbackThreshold { get; init; } = 0.9f;

    private readonly RuleBasedConsentClassifier _rule;
    private readonly LlmConsentClassifier _llm;
    private readonly ILogger<CascadingConsentClassifier> _logger;

    public CascadingConsentClassifier(
        RuleBasedConsentClassifier rule,
        LlmConsentClassifier llm,
        ILogger<CascadingConsentClassifier> logger)
    {
        _rule = rule;
        _llm = llm;
        _logger = logger;
    }

    public async Task<ConsentClassification> ClassifyAsync(string transcriptText, string promptText, CancellationToken cancellationToken = default)
    {
        var ruleResult = _rule.Classify(transcriptText);
        if (ruleResult.Confidence >= LlmFallbackThreshold)
            return ruleResult;

        _logger.LogDebug("Rule confidence {Conf} below threshold; consulting LLM", ruleResult.Confidence);
        var llmResult = await _llm.ClassifyAsync(transcriptText, promptText, cancellationToken).ConfigureAwait(false);

        // Disagreement: demote to ambiguous so the prompt is repeated.
        if (ruleResult.Decision != ConsentDecision.Ambiguous &&
            llmResult.Decision != ConsentDecision.Ambiguous &&
            ruleResult.Decision != llmResult.Decision)
        {
            _logger.LogInformation(
                "Cascade disagreement: rule={Rule} llm={Llm} — demoting to ambiguous",
                ruleResult.Decision, llmResult.Decision);
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.5f);
        }

        // Agreement (or one side ambiguous): keep the more decisive non-ambiguous result, boost.
        var preferred = ruleResult.Decision != ConsentDecision.Ambiguous ? ruleResult : llmResult;
        var combinedConfidence = Math.Min(1.0f, Math.Max(ruleResult.Confidence, llmResult.Confidence) + 0.1f);
        return new ConsentClassification(preferred.Decision, combinedConfidence);
    }
}
