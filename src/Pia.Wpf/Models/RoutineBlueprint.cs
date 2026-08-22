namespace Pia.Models;

/// <summary>Prefill values for the routines editor. <see cref="Key"/> is persisted-adjacent: add one, never rename one.</summary>
public sealed record RoutineBlueprint(
    string Key,
    string TitleKey,
    string DescriptionKey,
    string Category,
    ScheduledJobKind Kind,
    RecurrenceType Recurrence,
    TimeOnly DefaultTime,
    DayOfWeek? DefaultDayOfWeek,
    string QueryTemplate,
    IReadOnlyList<string> GrantedTools,
    bool QuietOnSuccess = false);

internal static class RoutineBlueprintCatalog
{
    public const string TopicDigest = "topic-digest";

    public static IReadOnlyList<RoutineBlueprint> All { get; } =
    [
        new RoutineBlueprint(
            Key: TopicDigest,
            TitleKey: "Routines_Blueprint_TopicDigest_Title",
            DescriptionKey: "Routines_Blueprint_TopicDigest_Description",
            Category: "daily",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Search the web for what is new on the topic of artificial intelligence in the past day. "
                + "Report only material developments — releases, announcements, results, reversals — and skip "
                + "speculation, opinion and re-reporting of what was already covered. At most five items, one "
                + "sentence each, every item with its source link and its date. If nothing material happened, "
                + "say exactly that in one line instead of padding the list.",
            // Web search is a provider capability and every read tool runs ungranted, so a digest that
            // writes nothing needs nothing.
            GrantedTools: []),
    ];

    public static RoutineBlueprint? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
}
