namespace Pia.Services.Interfaces;

/// <summary>One empty-state chip. <c>Id</c> doubles as the AutomationId suffix, so a script can tell
/// which group was drawn.</summary>
public sealed record StarterSuggestion(string Id, string Text);

public interface IStarterSuggestionService
{
    /// <summary>Draws up to <paramref name="count"/> chips from distinct capability groups, phrased for
    /// what the profile already holds.</summary>
    Task<IReadOnlyList<StarterSuggestion>> DrawAsync(int count, CancellationToken ct = default);
}
