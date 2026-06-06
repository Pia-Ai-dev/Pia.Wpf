namespace Pia.Models;

/// <summary>
/// AI-generated draft of a persona's fields produced from a short description (the "draft from a
/// description" assist in the persona edit dialog). Any field may be null when the model didn't
/// produce it; if the model returns unparseable output, only <see cref="SystemPrompt"/> is set from
/// the raw text.
/// </summary>
public record PersonaDraft(
    string? Name,
    string? Tagline,
    string? SystemPrompt,
    string? Guardrails,
    string? Archetype,
    string? Emoji,
    string? AccentColor,
    List<string>? Expertise);
