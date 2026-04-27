using System.Text.RegularExpressions;

namespace Pia.Services.Consent;

public sealed class RuleBasedConsentClassifier : IConsentClassifier
{
    private static readonly string[] GrantPatterns =
    {
        // German
        @"\bja\b", @"\bjawohl\b", @"\bjep\b", @"\bjap\b",
        @"\beinverstanden\b", @"\bokay\b", @"\bok\b",
        @"\bkein problem\b", @"\bin ordnung\b", @"\bvon mir aus\b",
        @"\bpasst\b", @"\bgeht klar\b", @"\bgerne\b", @"\bklar\b",
        // English
        @"\byes\b", @"\byeah\b", @"\byep\b", @"\bsure\b",
        @"\bgo ahead\b", @"\bof course\b", @"\bagreed\b", @"\bno problem\b",
    };

    private static readonly string[] DenyPatterns =
    {
        // German
        @"\bnein\b", @"\bnicht einverstanden\b", @"\blieber nicht\b",
        @"\bstopp\b", @"\bkein einverständnis\b", @"\bauf keinen fall\b",
        // English
        @"\bno\b", @"\bnope\b", @"\bnot ok\b", @"\babsolutely not\b",
        @"\bdo not\b", @"\bdon't\b",
    };

    private static readonly string[] AmbiguousPatterns =
    {
        // German
        @"\bvielleicht\b", @"\bich weiß nicht\b", @"\bich weiss nicht\b",
        @"\bwarum\b", @"\bwas genau\b", @"\bwas meinen sie\b", @"\bmoment\b",
        // English
        @"\bmaybe\b", @"\bi don't know\b", @"\bwhy\b", @"\bwhat do you mean\b",
    };

    public Task<ConsentClassification> ClassifyAsync(string transcriptText, string promptText, CancellationToken cancellationToken = default)
        => Task.FromResult(Classify(transcriptText));

    public ConsentClassification Classify(string transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);

        var normalized = Normalize(transcriptText);

        // Count deny first, then strip deny matches before scanning for grants —
        // otherwise "nicht einverstanden" double-counts (matches both deny and grant).
        var deny = CountMatches(normalized, DenyPatterns);
        var afterDenyStripped = StripMatches(normalized, DenyPatterns);
        var grant = CountMatches(afterDenyStripped, GrantPatterns);
        var ambiguous = CountMatches(normalized, AmbiguousPatterns);

        if (grant > 0 && deny > 0)
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.5f);

        if (ambiguous > 0 && grant == 0 && deny == 0)
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.7f);

        if (grant > 0)
            return new ConsentClassification(ConsentDecision.Grant, 0.95f);

        if (deny > 0)
            return new ConsentClassification(ConsentDecision.Deny, 0.95f);

        return new ConsentClassification(ConsentDecision.Ambiguous, 0.3f);
    }

    private static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '\'')
                sb.Append(ch);
            else
                sb.Append(' ');
        }
        return sb.ToString();
    }

    private static int CountMatches(string text, string[] patterns)
    {
        var count = 0;
        foreach (var p in patterns)
            count += Regex.Matches(text, p, RegexOptions.IgnoreCase).Count;
        return count;
    }

    private static string StripMatches(string text, string[] patterns)
    {
        foreach (var p in patterns)
            text = Regex.Replace(text, p, " ", RegexOptions.IgnoreCase);
        return text;
    }
}
