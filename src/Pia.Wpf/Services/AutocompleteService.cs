using Pia.Models;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Services;

public class AutocompleteService : IAutocompleteService
{
    private static readonly AutocompleteSuggestion[] Tier1Suggestions =
    [
        new() { DisplayText = "Memory", Icon = SymbolRegular.BrainCircuit24, Domain = AtCommandDomain.Memory, IsTier1 = true },
        new() { DisplayText = "Todo", Icon = SymbolRegular.TaskListSquareLtr24, Domain = AtCommandDomain.Todo, IsTier1 = true },
        new() { DisplayText = "Reminder", Icon = SymbolRegular.Clock24, Domain = AtCommandDomain.Reminder, IsTier1 = true }
    ];

    private const int MaxResults = 8;

    private readonly IMemoryService _memoryService;
    private readonly ITodoService _todoService;
    private readonly IReminderService _reminderService;

    public AutocompleteService(
        IMemoryService memoryService,
        ITodoService todoService,
        IReminderService reminderService)
    {
        _memoryService = memoryService;
        _todoService = todoService;
        _reminderService = reminderService;
    }

    public async Task<IReadOnlyList<AutocompleteSuggestion>> GetSuggestionsAsync(
        AtCommandDomain? domain, string? filter)
    {
        if (domain is null)
            return GetTier1Suggestions(filter);

        return await GetTier2SuggestionsAsync(domain.Value, filter);
    }

    private static IReadOnlyList<AutocompleteSuggestion> GetTier1Suggestions(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return Tier1Suggestions;

        return Tier1Suggestions
            .Where(s => s.DisplayText.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetTier2SuggestionsAsync(
        AtCommandDomain domain, string? filter)
    {
        return domain switch
        {
            AtCommandDomain.Memory => await GetMemorySuggestionsAsync(filter),
            AtCommandDomain.Todo => await GetTodoSuggestionsAsync(filter),
            AtCommandDomain.Reminder => await GetReminderSuggestionsAsync(filter),
            _ => []
        };
    }

    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetMemorySuggestionsAsync(string? filter)
    {
        var summaries = await _memoryService.GetMemorySummariesAsync();
        return summaries
            .Where(s => string.IsNullOrEmpty(filter) ||
                        s.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .Select(s => new AutocompleteSuggestion
            {
                DisplayText = s.Label,
                Icon = SymbolRegular.BrainCircuit24,
                Domain = AtCommandDomain.Memory,
                ItemId = s.Id,
                IsTier1 = false
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetTodoSuggestionsAsync(string? filter)
    {
        var todos = await _todoService.GetPendingAsync();
        return todos
            .Where(t => string.IsNullOrEmpty(filter) ||
                        t.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .Select(t => new AutocompleteSuggestion
            {
                DisplayText = t.Title,
                Icon = SymbolRegular.TaskListSquareLtr24,
                Domain = AtCommandDomain.Todo,
                ItemId = t.Id,
                IsTier1 = false
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetReminderSuggestionsAsync(string? filter)
    {
        var reminders = await _reminderService.GetActiveAsync();
        return reminders
            .Where(r => string.IsNullOrEmpty(filter) ||
                        r.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .Select(r => new AutocompleteSuggestion
            {
                DisplayText = r.Description,
                Icon = SymbolRegular.Clock24,
                Domain = AtCommandDomain.Reminder,
                ItemId = r.Id,
                IsTier1 = false
            })
            .ToArray();
    }
}
