namespace Pia.Services.Interfaces;

/// <summary>
/// Generates a short, human-friendly chat title from the first exchange using
/// the Assistant-mode default provider. Returns <c>null</c> when no title could
/// be produced (no provider, empty model output). Extracted from
/// <c>AssistantViewModel</c> so title generation is an isolated responsibility.
/// </summary>
public interface IChatTitleService
{
    Task<string?> GenerateAsync(string userContent, string assistantContent, CancellationToken cancellationToken = default);
}
