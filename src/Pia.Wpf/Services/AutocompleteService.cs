using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Wpf.Ui.Controls;

namespace Pia.Services;

public class AutocompleteService : IAutocompleteService
{
    // Always-available domains. Files and Assignment are appended dynamically (see
    // GetTier1Suggestions), because tagging either restricts the turn's toolset to that
    // domain's tools — which the plugin host doesn't register with no sandbox folder set,
    // or with no server-offered assignment surface, leaving an empty toolset.
    private static readonly AutocompleteSuggestion[] BaseTier1Suggestions =
    [
        new() { DisplayText = "Memory", Icon = SymbolRegular.BrainCircuit24, Domain = AtCommandDomain.Memory, IsTier1 = true },
        new() { DisplayText = "Todo", Icon = SymbolRegular.TaskListSquareLtr24, Domain = AtCommandDomain.Todo, IsTier1 = true },
        new() { DisplayText = "Reminder", Icon = SymbolRegular.Clock24, Domain = AtCommandDomain.Reminder, IsTier1 = true },
        new() { DisplayText = "Routine", Icon = SymbolRegular.Search24, Domain = AtCommandDomain.Routine, IsTier1 = true }
    ];

    private static readonly AutocompleteSuggestion FilesTier1Suggestion =
        new() { DisplayText = "Files", Icon = SymbolRegular.Folder24, Domain = AtCommandDomain.Files, IsTier1 = true };

    private static readonly AutocompleteSuggestion AssignmentTier1Suggestion =
        new() { DisplayText = "Assignment", Icon = SymbolRegular.Rocket24, Domain = AtCommandDomain.Assignment, IsTier1 = true };

    private const int MaxResults = 8;

    // @Files is deliberately NOT truncated to the tier-2 preview count (MaxResults): the picker
    // surfaces every match so the user can arrow-key through the whole list. The result is still
    // bounded by the handler's own hard listing cap (FilesToolHandler.MaxListEntries = 500);
    // int.MaxValue just means "no additional cap on the service side." The popup list is
    // UI-virtualized, so rendering a full 500-item result stays cheap.
    private const int AllFileResults = int.MaxValue;

    private readonly IMemoryService _memoryService;
    private readonly ITodoService _todoService;
    private readonly IReminderService _reminderService;
    private readonly IScheduledJobService _scheduledJobService;
    private readonly IFilesToolHandler _filesToolHandler;
    private readonly IAssignmentSurfaceCache _assignmentSurface;

    public AutocompleteService(
        IMemoryService memoryService,
        ITodoService todoService,
        IReminderService reminderService,
        IScheduledJobService scheduledJobService,
        IFilesToolHandler filesToolHandler,
        IAssignmentSurfaceCache assignmentSurface)
    {
        _memoryService = memoryService;
        _todoService = todoService;
        _reminderService = reminderService;
        _scheduledJobService = scheduledJobService;
        _filesToolHandler = filesToolHandler;
        _assignmentSurface = assignmentSurface;
    }

    public async Task<IReadOnlyList<AutocompleteSuggestion>> GetSuggestionsAsync(
        AtCommandDomain? domain, string? filter)
    {
        if (domain is null)
            return GetTier1Suggestions(filter);

        return await GetTier2SuggestionsAsync(domain.Value, filter);
    }

    private IReadOnlyList<AutocompleteSuggestion> GetTier1Suggestions(string? filter)
    {
        IEnumerable<AutocompleteSuggestion> tier1 = BaseTier1Suggestions;
        if (_filesToolHandler.IsAvailable)
            tier1 = tier1.Append(FilesTier1Suggestion);
        if (_assignmentSurface.Surface.Available)
            tier1 = tier1.Append(AssignmentTier1Suggestion);

        if (!string.IsNullOrEmpty(filter))
            tier1 = tier1.Where(s => s.DisplayText.StartsWith(filter, StringComparison.OrdinalIgnoreCase));

        return tier1.ToArray();
    }

    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetTier2SuggestionsAsync(
        AtCommandDomain domain, string? filter)
    {
        return domain switch
        {
            AtCommandDomain.Memory => await GetMemorySuggestionsAsync(filter),
            AtCommandDomain.Todo => await GetTodoSuggestionsAsync(filter),
            AtCommandDomain.Reminder => await GetReminderSuggestionsAsync(filter),
            AtCommandDomain.Routine => await GetRoutineSuggestionsAsync(filter),
            AtCommandDomain.Files => GetFileSuggestions(filter),
            AtCommandDomain.Assignment => await GetAssignmentSuggestionsAsync(filter),
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

    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetRoutineSuggestionsAsync(string? filter)
    {
        var jobs = await _scheduledJobService.GetActiveAsync();
        return jobs
            .Where(j => string.IsNullOrEmpty(filter) ||
                        j.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .Select(j => new AutocompleteSuggestion
            {
                DisplayText = j.Name,
                Icon = SymbolRegular.Search24,
                Domain = AtCommandDomain.Routine,
                ItemId = j.Id,
                IsTier1 = false
            })
            .ToArray();
    }

    // Files differ from the other domains: a file is keyed by its sandbox-relative path
    // (carried in DisplayText, not a Guid ItemId), and the handler owns the folder
    // resolution + containment/blocklist filtering. The path enumeration is synchronous.
    private IReadOnlyList<AutocompleteSuggestion> GetFileSuggestions(string? filter)
    {
        return _filesToolHandler.ListRelativeFiles(filter, AllFileResults)
            .Select(path => new AutocompleteSuggestion
            {
                DisplayText = path,
                Icon = SymbolRegular.DocumentText24,
                Domain = AtCommandDomain.Files,
                IsTier1 = false
            })
            .ToArray();
    }

    // Everything rides in DisplayText because that is the token inserted into the message and there is no
    // separate label field: the status is a snapshot, so a run picked as Running may already have finished, and
    // the id prefix is what tells two runs of one skill apart — ItemId never reaches the model.
    private async Task<IReadOnlyList<AutocompleteSuggestion>> GetAssignmentSuggestionsAsync(string? filter)
    {
        var runs = await _assignmentSurface.GetRunsAsync();

        // Null is "the server could not answer", and a popup has no room to say so — offer nothing rather
        // than an empty list the user reads as "you have no runs".
        if (runs is null)
            return [];

        return runs
            .Select(r => (Run: r, Label: $"{r.SkillName} — {r.Status} ({r.Id.ToString("N")[..8]})"))
            .Where(x => string.IsNullOrEmpty(filter) ||
                        x.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .Select(x => new AutocompleteSuggestion
            {
                DisplayText = x.Label,
                Icon = SymbolRegular.Rocket24,
                Domain = AtCommandDomain.Assignment,
                ItemId = x.Run.Id,
                IsTier1 = false
            })
            .ToArray();
    }
}
