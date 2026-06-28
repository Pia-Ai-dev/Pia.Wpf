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

    public static string? For(RegenerateStyle style) => style switch
    {
        RegenerateStyle.Shorten =>
            "Give a more concise version of your previous answer — keep the key points, cut elaboration and filler." + KeepLanguage,
        RegenerateStyle.Detailed =>
            "Give a more thorough version of your previous answer, expanding the reasoning and adding relevant detail and examples." + KeepLanguage,
        RegenerateStyle.Exportable =>
            "Rewrite your previous answer as a clean, self-contained document: clear Markdown headings, well-structured sections, and no conversational filler or meta-commentary. Use bold text very sparingly — only for the rare word or phrase that genuinely needs emphasis, never for whole sentences or as a default." + KeepLanguage,
        _ => null,
    };
}
