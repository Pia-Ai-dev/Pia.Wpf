namespace Pia.Models;

/// <summary>
/// AI-generated draft of an optimization template's fields from a short style description. Any field may
/// be null when the model didn't produce it; if the reply is not JSON at all, only <see cref="Prompt"/>
/// is set, from the raw text.
/// </summary>
public record TemplateDraft(
    string? Name,
    string? Description,
    string? Prompt);
