using Pia.Models;

namespace Pia.ViewModels.Models;

/// <summary>
/// A regeneration variant chosen from the Regenerate button's ▾ menu. Each non-default style maps
/// to an extra instruction appended to the regenerated turn (see <see cref="RegenerateInstructions"/>) —
/// no new model parameters, just prompt steering.
/// </summary>
public enum RegenerateStyle
{
    Default,
    Shorten,
    Detailed,
    Exportable,
}

/// <summary>Carries the message to regenerate plus the chosen style from the menu to the command.</summary>
public sealed record RegenerateRequest(AssistantMessage Message, RegenerateStyle Style);

/// <summary>
/// Maps a <see cref="RegenerateStyle"/> to the model-facing instruction appended to the regenerated
/// turn. The instruction is English (model-facing, not user-facing — the menu labels are localized)
/// and asks the model to keep the answer's original language.
/// </summary>
public static class RegenerateInstructions
{
    private const string KeepLanguage = " Respond in the same language as the original answer.";

    // The quoted block can be a failed turn's placeholder ("Pia didn't respond", a timeout notice), which
    // the toolbar offers Regenerate on just like a real answer.
    private const string NotAnAnswerEscape =
        " If <previous_answer> is an error or a notice that no answer was produced, ignore it and answer the request above from scratch instead.";

    /// <summary>
    /// Regenerating drops the old answer from the transcript, so every style that transforms it has to
    /// carry it here — without <paramref name="previousAnswer"/> the model re-runs the original task
    /// instead of rewriting.
    /// </summary>
    public static string? For(RegenerateStyle style, string? previousAnswer = null)
    {
        var quoted = !string.IsNullOrWhiteSpace(previousAnswer);
        if (Directive(style, quoted) is not { } directive)
            return null;

        return quoted
            ? $"<previous_answer>\n{previousAnswer}\n</previous_answer>\n\n{directive}{NotAnAnswerEscape}{KeepLanguage}"
            : directive + KeepLanguage;
    }

    private static string? Directive(RegenerateStyle style, bool quoted)
    {
        var target = quoted ? "the answer in <previous_answer>" : "your previous answer";
        return style switch
        {
            RegenerateStyle.Shorten =>
                $"Give a more concise version of {target} — keep the key points, cut elaboration and filler.",
            RegenerateStyle.Detailed =>
                $"Give a more thorough version of {target}, expanding the reasoning and adding relevant detail and examples.",
            RegenerateStyle.Exportable =>
                $"Rewrite {target} as a clean, self-contained document: clear Markdown headings, well-structured sections, and no conversational filler or meta-commentary. Use bold text very sparingly — only for the rare word or phrase that genuinely needs emphasis, never for whole sentences or as a default.",
            _ => null,
        };
    }
}
