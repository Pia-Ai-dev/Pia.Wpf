using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public sealed class StarterSuggestionService : IStarterSuggestionService
{
    /// <summary>Empty <c>Start</c> means the group is skipped until its data exists — its prompts have
    /// nothing to match against on a fresh profile. Empty <c>Grow</c> means the group has no data axis
    /// and always draws from <c>Start</c>.</summary>
    private sealed record Group(string Id, string[] Start, string[] Grow, Func<DataSnapshot, bool> HasData);

    private sealed record DataSnapshot(bool Memories, bool Todos, bool Reminders, bool Routines, bool Chats)
    {
        public static readonly DataSnapshot Empty = new(false, false, false, false, false);
    }

    private static readonly Group[] Groups =
    [
        new("Memory",
            ["Assistant_Suggestion_Memory_Start1", "Assistant_Suggestion_Memory_Start2"],
            ["Assistant_Suggestion_Memory_Grow1", "Assistant_Suggestion_Memory_Grow2"],
            s => s.Memories),
        new("Recall",
            ["Assistant_Suggestion_Recall_Start1", "Assistant_Suggestion_Recall_Start2"],
            ["Assistant_Suggestion_Recall_Grow1", "Assistant_Suggestion_Recall_Grow2"],
            s => s.Memories),
        new("Todo",
            ["Assistant_Suggestion_Todo_Start1", "Assistant_Suggestion_Todo_Start2"],
            ["Assistant_Suggestion_Todo_Grow1", "Assistant_Suggestion_Todo_Grow2"],
            s => s.Todos),
        new("Reminder",
            ["Assistant_Suggestion_Reminder_Start1", "Assistant_Suggestion_Reminder_Start2"],
            ["Assistant_Suggestion_Reminder_Grow1", "Assistant_Suggestion_Reminder_Grow2"],
            s => s.Reminders),
        new("Routine",
            ["Assistant_Suggestion_Routine_Start1", "Assistant_Suggestion_Routine_Start2"],
            ["Assistant_Suggestion_Routine_Grow1", "Assistant_Suggestion_Routine_Grow2"],
            s => s.Routines),
        new("Plan",
            ["Assistant_Suggestion_Plan1", "Assistant_Suggestion_Plan2"],
            [],
            _ => false),
        new("Chats",
            [],
            ["Assistant_Suggestion_Chats_Grow1", "Assistant_Suggestion_Chats_Grow2"],
            s => s.Chats),
    ];

    /// <summary>Flattened for the localization parity test — the keys sit in a table its regexes cannot see.</summary>
    public static IReadOnlyList<string> AllKeys { get; } =
        [.. Groups.SelectMany(g => g.Start.Concat(g.Grow)).Distinct(StringComparer.Ordinal)];

    public static IReadOnlyList<string> GroupIds { get; } = [.. Groups.Select(g => g.Id)];

    private readonly IMemoryService _memory;
    private readonly ITodoService _todos;
    private readonly IReminderService _reminders;
    private readonly IScheduledJobService _jobs;
    private readonly IAssistantChatService _chats;
    private readonly ILocalizationService _localization;
    private readonly ILogger<StarterSuggestionService> _logger;

    public StarterSuggestionService(
        IMemoryService memory,
        ITodoService todos,
        IReminderService reminders,
        IScheduledJobService jobs,
        IAssistantChatService chats,
        ILocalizationService localization,
        ILogger<StarterSuggestionService> logger)
    {
        _memory = memory;
        _todos = todos;
        _reminders = reminders;
        _jobs = jobs;
        _chats = chats;
        _localization = localization;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StarterSuggestion>> DrawAsync(int count, CancellationToken ct = default)
    {
        if (count <= 0) return [];

        var data = await SnapshotAsync(ct);

        return
        [
            .. Shuffle(Groups)
                .Where(g => g.Start.Length > 0 || g.HasData(data))
                .Take(count)
                .Select(g =>
                {
                    var keys = g.Grow.Length > 0 && g.HasData(data) ? g.Grow : g.Start;
                    return new StarterSuggestion(g.Id, _localization[keys[RandomNumberGenerator.GetInt32(keys.Length)]]);
                })
        ];
    }

    private async Task<DataSnapshot> SnapshotAsync(CancellationToken ct)
    {
        try
        {
            return new DataSnapshot(
                await _memory.GetObjectCountAsync() > 0,
                await _todos.GetPendingCountAsync() > 0,
                (await _reminders.GetActiveAsync()).Count > 0,
                (await _jobs.GetAllAsync()).Count > 0,
                // No excludeChatId on CountAsync, but a brand-new chat has no row yet, so the one the user
                // is sitting in is never counted.
                await _chats.CountAsync(ct: ct) > 0);
        }
        catch (Exception ex)
        {
            // A blank chip row is worse than a data-blind one, and every Start phrasing suits any profile.
            _logger.LogWarning(ex, "Starter-suggestion data probe failed; drawing without it");
            return DataSnapshot.Empty;
        }
    }

    private static Group[] Shuffle(Group[] source)
    {
        var copy = (Group[])source.Clone();
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }
}
