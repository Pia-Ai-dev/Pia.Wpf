using Pia.Models;

namespace Pia.Services.Interfaces;

public interface IAutocompleteService
{
    Task<IReadOnlyList<AutocompleteSuggestion>> GetSuggestionsAsync(
        AtCommandDomain? domain, string? filter);
}
