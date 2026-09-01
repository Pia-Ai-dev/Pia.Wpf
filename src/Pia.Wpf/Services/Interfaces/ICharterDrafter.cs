namespace Pia.Services.Interfaces;

/// <summary>
/// Proposes the text of <c>memory/charter.md</c> from the documents already under <c>sources/</c>.
/// Never writes: the user edits and approves the draft first.
/// </summary>
public interface ICharterDrafter
{
    /// <summary>The proposed charter, or <c>""</c> when no provider is configured or no readable
    /// text source exists to draft from.</summary>
    Task<string> DraftAsync(CancellationToken ct = default);
}
