using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITextOptimizationService
{
    Task<OptimizationSession> OptimizeTextAsync(
        string inputText,
        Guid templateId,
        Guid? providerId = null,
        string targetLanguage = "EN",
        string? mode = null,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateInputAsync(string inputText, Guid templateId);

    Task<string> GeneratePromptAsync(string styleDescription, Guid? providerId = null);

    /// <summary>
    /// Drafts a persona's fields (name, tagline, system prompt, emoji, accent colour, expertise)
    /// from a short free-text description. <paramref name="providerId"/> null ⇒ Assistant-mode default.
    /// </summary>
    Task<PersonaDraft> GeneratePersonaDraftAsync(string description, Guid? providerId = null);

    /// <summary>
    /// Drafts a routine's fields (name, goal, schedule, effort, write tools) from a short free-text
    /// description, in the language the description is written in.
    /// <paramref name="providerId"/> null ⇒ Assistant-mode default.
    /// </summary>
    /// <param name="availableTools">What this device offers, so the model picks a real name. Empty ⇒ the
    /// draft is asked for no tools at all rather than invited to guess.</param>
    Task<RoutineDraft> GenerateRoutineDraftAsync(
        string description,
        IReadOnlyList<RoutineDraftTool> availableTools,
        Guid? providerId = null);
}
