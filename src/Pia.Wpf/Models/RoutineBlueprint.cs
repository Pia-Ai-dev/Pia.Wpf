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
    bool QuietOnSuccess = false,
    ReasoningEffort? DefaultEffort = null);

internal static class RoutineBlueprintCatalog
{
    public const string TopicDigest = "topic-digest";
    public const string MorningBrief = "morning-brief";
    public const string EveningWinddown = "evening-winddown";
    public const string HabitCheckin = "habit-checkin";
    public const string WeeklyReview = "weekly-review";
    public const string CompetitorWatch = "competitor-watch";
    public const string BillsRenewals = "bills-renewals";
    public const string MeetingFollowup = "meeting-followup";

    // Kind is Research even for the routines that only read: the AgentTask dispatch leg maps an empty
    // grant list to null, which the launcher turns into its write_file default.
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
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low),

        new RoutineBlueprint(
            Key: MorningBrief,
            TitleKey: "Routines_Blueprint_MorningBrief_Title",
            DescriptionKey: "Routines_Blueprint_MorningBrief_Description",
            Category: "daily",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(7, 0),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Call query_todos with filter pending and query_reminders with filter active, then report today "
                + "and nothing else. Include every todo whose Due date falls today, everything the read marks "
                + "OVERDUE, and every reminder whose next fire is today — one line each, each with its time. "
                + "Order the whole list by time of day, not by priority. Omit anything due later in the week, "
                + "anything already completed, and any advice, encouragement or commentary. Change nothing: "
                + "create, edit and complete nothing. If there is nothing for today, say exactly that in one "
                + "line. If either read comes back empty or unavailable, name that read in one line rather "
                + "than filling the gap from memory.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Minimal),

        new RoutineBlueprint(
            Key: EveningWinddown,
            TitleKey: "Routines_Blueprint_EveningWinddown_Title",
            DescriptionKey: "Routines_Blueprint_EveningWinddown_Description",
            Category: "daily",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(20, 0),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Call query_todos with filter pending and query_reminders with filter active. Give two short "
                + "lists and nothing else: first what went overdue today and is still not completed, then what "
                + "is due or fires tomorrow — one line each, each with its time. No encouragement, no advice, "
                + "no verdict on the day, and no writes of any kind. If one of the two lists is empty, say so "
                + "in one line instead of padding it. If either read comes back empty or unavailable, name that "
                + "read in one line rather than inventing entries for it.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Minimal),

        new RoutineBlueprint(
            Key: HabitCheckin,
            TitleKey: "Routines_Blueprint_HabitCheckin_Title",
            DescriptionKey: "Routines_Blueprint_HabitCheckin_Description",
            Category: "daily",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(21, 0),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Call query_reminders and treat every reminder whose printed recurrence is not Once as a "
                + "standing commitment — the recurrence comes straight off the read, so nothing has to be "
                + "guessed. Then call query_todos with filter completed. Say which of those commitments were "
                + "due today and whether anything matching them was actually ticked off, in at most three "
                + "lines. Close with one short reflection question about whichever of them is going worst. Do "
                + "not congratulate, do not moralise, do not score the day, and change nothing. If nothing "
                + "recurring was due today, say exactly that in one line. If either read comes back empty or "
                + "unavailable, name that read in one line rather than treating the day as a blank.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low),

        new RoutineBlueprint(
            Key: WeeklyReview,
            TitleKey: "Routines_Blueprint_WeeklyReview_Title",
            DescriptionKey: "Routines_Blueprint_WeeklyReview_Description",
            Category: "weekly",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(17, 0),
            DefaultDayOfWeek: DayOfWeek.Friday,
            QueryTemplate:
                "Call query_todos with filter completed for what finished this week, then query_todos with "
                + "filter all and list_columns for what is still open, then browse_index and read_topic for the "
                + "notes worth carrying forward. Report four short sections: what was completed, what is still "
                + "open and past its Due date, how the open items sit across the kanban columns, and the two or "
                + "three notes to carry into next week. Report where an open item is sitting and say nothing "
                + "about how long it has been there — the reads record no movement date, so calling a card "
                + "stalled, stuck or neglected would be a guess. Choose a note on what it says and say nothing "
                + "about when it was written — browse_index and read_topic carry no date, so placing a note in "
                + "this week rather than last would be a guess. No scores, no ratings, no praise, and no plan "
                + "for next week beyond what the notes already say. Change nothing. If a section has nothing in "
                + "it, say so in one line; if a read comes back empty or unavailable, name that read in one "
                + "line rather than filling the section in.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.High),

        new RoutineBlueprint(
            Key: CompetitorWatch,
            TitleKey: "Routines_Blueprint_CompetitorWatch_Title",
            DescriptionKey: "Routines_Blueprint_CompetitorWatch_Description",
            Category: "weekly",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Start with recall and browse_index to find which companies the vault already names as "
                + "competitors or as companies being tracked, and watch those. Only if it names none, fall back "
                + "to Microsoft, Google, OpenAI and Anthropic, and open the report with one line saying that "
                + "this is a placeholder list to be replaced with the companies actually being tracked. Then "
                + "search the web for material developments in the past week — launches, pricing changes, "
                + "funding, leadership changes, outages, withdrawals. At most two items per company, one "
                + "sentence each, every item with its source link and its date. Skip speculation, opinion and "
                + "re-reporting of what was already covered, and give a company with nothing material its own "
                + "one-line entry saying so. Change nothing. If recall or browse_index is unavailable, say so "
                + "in one line before falling back, rather than presenting the fallback as the tracked list.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium),

        new RoutineBlueprint(
            Key: BillsRenewals,
            TitleKey: "Routines_Blueprint_BillsRenewals_Title",
            DescriptionKey: "Routines_Blueprint_BillsRenewals_Description",
            Category: "weekly",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Call query_todos with filter all and query_reminders with filter all, then scan the todo "
                + "titles, the todo notes and the reminder descriptions for renewal, subscription, invoice, "
                + "licence, insurance and membership wording. Report only what falls due in the next fourteen "
                + "days, one line each, with its date and where it was found — the todo or the reminder the "
                + "line came from. Invent no amount, no vendor and no date the reads did not give you. If "
                + "nothing matches, say exactly that in one line rather than widening the wording until "
                + "something does. Change nothing. If either read comes back empty or unavailable, name that "
                + "read in one line instead of reporting an all-clear.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low),

        new RoutineBlueprint(
            Key: MeetingFollowup,
            TitleKey: "Routines_Blueprint_MeetingFollowup_Title",
            DescriptionKey: "Routines_Blueprint_MeetingFollowup_Description",
            Category: "meetings",
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(18, 0),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Turn today's meetings into follow-ups, evidence before extraction. First, call recall for this "
                + "week's meetings; recall does not reach raw sources, so a hit is a topic page — call "
                + "read_topic on it and follow the source refs it cites to read_source for the transcript "
                + "itself, and use browse_index only when recall misses. Do not use search_files: it searches "
                + "the assistant files folder rather than the vault, so it would quietly find nothing. Second, "
                + "for each meeting source dated today, state before any action item: the title and date, who "
                + "the front matter lists as attendees, whether the transcript reads as complete or breaks off "
                + "mid-sentence, and whether the speaker labels are real names, generic placeholders such as "
                + "Speaker 1, or absent because labelling was switched off. Name every passage you are not "
                + "confident about. Third, only then extract action items, attributing an owner only where the "
                + "transcript actually supports it and writing owner unclear rather than inferring one. Fourth, "
                + "call query_todos with filter all before creating anything and skip every follow-up that is "
                + "already on the list; then call create_todo once per genuinely new item, with the meeting "
                + "title and date in the notes so a later run recognises it. If no meeting source is dated "
                + "today, say exactly that in one line and create nothing. If a read comes back empty or "
                + "unavailable, name it in one line and create nothing from the part you could not read.",
            // create_todo only, and it replaces the launcher's write_file default rather than adding to it.
            GrantedTools: ["create_todo"],
            DefaultEffort: ReasoningEffort.High),
    ];

    public static RoutineBlueprint? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
}
