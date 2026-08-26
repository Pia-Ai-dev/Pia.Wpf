using Pia.Services.Operators;

namespace Pia.Services.Interfaces;

/// <summary>How a proposed assignment reaches a human. The tool handler never sees a dialog or a view model.</summary>
public interface IAssignmentConsentPrompt
{
    /// <summary><c>null</c> is "seen and closed without sending"; an implementation that could not ask at all
    /// reports <c>ConsentMissing</c>, so the two are never conflated.</summary>
    Task<AssignmentStartStatus?> PromptAsync(string? skillName, string prompt, CancellationToken ct = default);
}
