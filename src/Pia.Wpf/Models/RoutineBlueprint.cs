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
    string QueryKey,
    string? GuardKey,
    IReadOnlyList<string> GrantedTools,
    bool QuietOnSuccess = false,
    ReasoningEffort? DefaultEffort = null,
    bool RequiresWebSearch = false,
    IReadOnlyList<RoutineSlot>? Slots = null)
{
    public IReadOnlyList<RoutineSlot> Slots { get; init; } = Slots ?? [];
}

/// <summary>Only free text ships; time and day are typed editor fields and no blueprint wants a closed set.</summary>
public enum RoutineSlotKind
{
    Text
}

/// <summary>One fillable value in a blueprint's query template, written there as <c>{Name}</c>.</summary>
/// <param name="Name">The fill contract, not prose — it stays this English identifier in every locale.</param>
/// <param name="DefaultKey">Substituted when nothing is supplied. Null makes the slot required, so an unfilled
/// reference is an error rather than a hole in the prompt.</param>
public sealed record RoutineSlot(
    string Name,
    RoutineSlotKind Kind,
    string LabelKey,
    string HelpKey,
    string? DefaultKey = null);

internal static class RoutineBlueprintCategories
{
    public const string Ready = "ready";
    public const string YourData = "your-data";

    /// <summary>What works on an empty profile comes first.</summary>
    public static IReadOnlyList<string> InDisplayOrder { get; } = [Ready, YourData];

    /// <summary>"your-data" is keyed "YourData" in resx — the same rule the blueprint stems follow.</summary>
    public static string StemOf(string category) =>
        string.Concat(category.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

internal static class RoutineBlueprintCatalog
{
    public const string NewsBriefing = "news-briefing";
    public const string WordOfTheDay = "word-of-the-day";
    public const string TopicDigest = "topic-digest";
    public const string SecurityAdvisories = "security-advisories";
    public const string MarketSnapshot = "market-snapshot";
    public const string StockWatchlist = "stock-watchlist";
    public const string SportsRoundup = "sports-roundup";
    public const string ClientWatch = "client-watch";
    public const string CompetitorWatch = "competitor-watch";
    public const string IndustryPulse = "industry-pulse";
    public const string RegulationWatch = "regulation-watch";
    public const string ReleaseWatch = "release-watch";
    public const string MealIdeas = "meal-ideas";
    public const string LearnOneThing = "learn-one-thing";
    public const string MorningBrief = "morning-brief";
    public const string MeetingFollowup = "meeting-followup";
    public const string EveningWinddown = "evening-winddown";
    public const string HabitCheckin = "habit-checkin";
    public const string BillsRenewals = "bills-renewals";
    public const string WeeklyReview = "weekly-review";

    /// <summary>The clauses a whole family of templates repeats, appended once instead of retyped twenty times —
    /// which is what let every body fit the length bar. Not under <c>Routines_Blueprint_</c>: that namespace is
    /// asserted to name a blueprint.</summary>
    internal const string WebSearchGuardKey = "Routines_Catalog_WebSearchGuard";

    /// <summary>Nothing tells the model it cannot search, so the guard has to forbid answering from memory.</summary>
    internal const string ReadOnlyGuardKey = "Routines_Catalog_ReadOnlyGuard";

    internal const string WriteGuardKey = "Routines_Catalog_WriteGuard";

    // Kind is Research even for the routines that only read: the AgentTask dispatch leg maps an empty
    // grant list to null, which the launcher turns into its write_file default.
    // Literal order is display order: Ready before YourData, daily before weekly, then day and time.
    public static IReadOnlyList<RoutineBlueprint> All { get; } =
    [
        new RoutineBlueprint(
            Key: NewsBriefing,
            TitleKey: "Routines_Blueprint_NewsBriefing_Title",
            DescriptionKey: "Routines_Blueprint_NewsBriefing_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_NewsBriefing_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "focus",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_NewsBriefing_Slot_Focus_Label",
                    HelpKey: "Routines_Blueprint_NewsBriefing_Slot_Focus_Help",
                    DefaultKey: "Routines_Blueprint_NewsBriefing_Slot_Focus_Default"),
            ]),

        new RoutineBlueprint(
            Key: WordOfTheDay,
            TitleKey: "Routines_Blueprint_WordOfTheDay_Title",
            DescriptionKey: "Routines_Blueprint_WordOfTheDay_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 30),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_WordOfTheDay_Query",
            GuardKey: null,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Minimal,
            Slots:
            [
                new RoutineSlot(
                    Name: "language",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_WordOfTheDay_Slot_Language_Label",
                    HelpKey: "Routines_Blueprint_WordOfTheDay_Slot_Language_Help",
                    DefaultKey: "Routines_Blueprint_WordOfTheDay_Slot_Language_Default"),
            ]),

        new RoutineBlueprint(
            Key: TopicDigest,
            TitleKey: "Routines_Blueprint_TopicDigest_Title",
            DescriptionKey: "Routines_Blueprint_TopicDigest_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_TopicDigest_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "topic",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_TopicDigest_Slot_Topic_Label",
                    HelpKey: "Routines_Blueprint_TopicDigest_Slot_Topic_Help",
                    DefaultKey: "Routines_Blueprint_TopicDigest_Slot_Topic_Default"),
            ]),

        new RoutineBlueprint(
            Key: SecurityAdvisories,
            TitleKey: "Routines_Blueprint_SecurityAdvisories_Title",
            DescriptionKey: "Routines_Blueprint_SecurityAdvisories_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(9, 30),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_SecurityAdvisories_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "products",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_SecurityAdvisories_Slot_Products_Label",
                    HelpKey: "Routines_Blueprint_SecurityAdvisories_Slot_Products_Help",
                    DefaultKey: "Routines_Blueprint_SecurityAdvisories_Slot_Products_Default"),
            ]),

        new RoutineBlueprint(
            Key: MarketSnapshot,
            TitleKey: "Routines_Blueprint_MarketSnapshot_Title",
            DescriptionKey: "Routines_Blueprint_MarketSnapshot_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(12, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_MarketSnapshot_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "markets",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_MarketSnapshot_Slot_Markets_Label",
                    HelpKey: "Routines_Blueprint_MarketSnapshot_Slot_Markets_Help",
                    DefaultKey: "Routines_Blueprint_MarketSnapshot_Slot_Markets_Default"),
            ]),

        new RoutineBlueprint(
            Key: StockWatchlist,
            TitleKey: "Routines_Blueprint_StockWatchlist_Title",
            DescriptionKey: "Routines_Blueprint_StockWatchlist_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(18, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_StockWatchlist_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "holdings",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_StockWatchlist_Slot_Holdings_Label",
                    HelpKey: "Routines_Blueprint_StockWatchlist_Slot_Holdings_Help",
                    DefaultKey: "Routines_Blueprint_StockWatchlist_Slot_Holdings_Default"),
            ]),

        new RoutineBlueprint(
            Key: SportsRoundup,
            TitleKey: "Routines_Blueprint_SportsRoundup_Title",
            DescriptionKey: "Routines_Blueprint_SportsRoundup_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_SportsRoundup_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "teams",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_SportsRoundup_Slot_Teams_Label",
                    HelpKey: "Routines_Blueprint_SportsRoundup_Slot_Teams_Help",
                    DefaultKey: "Routines_Blueprint_SportsRoundup_Slot_Teams_Default"),
            ]),

        new RoutineBlueprint(
            Key: ClientWatch,
            TitleKey: "Routines_Blueprint_ClientWatch_Title",
            DescriptionKey: "Routines_Blueprint_ClientWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(8, 30),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_ClientWatch_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "accounts",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_ClientWatch_Slot_Accounts_Label",
                    HelpKey: "Routines_Blueprint_ClientWatch_Slot_Accounts_Help",
                    DefaultKey: "Routines_Blueprint_ClientWatch_Slot_Accounts_Default"),
            ]),

        new RoutineBlueprint(
            Key: CompetitorWatch,
            TitleKey: "Routines_Blueprint_CompetitorWatch_Title",
            DescriptionKey: "Routines_Blueprint_CompetitorWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_CompetitorWatch_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "companies",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_CompetitorWatch_Slot_Companies_Label",
                    HelpKey: "Routines_Blueprint_CompetitorWatch_Slot_Companies_Help",
                    DefaultKey: "Routines_Blueprint_CompetitorWatch_Slot_Companies_Default"),
            ]),

        new RoutineBlueprint(
            Key: IndustryPulse,
            TitleKey: "Routines_Blueprint_IndustryPulse_Title",
            DescriptionKey: "Routines_Blueprint_IndustryPulse_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_IndustryPulse_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "industry",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_IndustryPulse_Slot_Industry_Label",
                    HelpKey: "Routines_Blueprint_IndustryPulse_Slot_Industry_Help",
                    DefaultKey: "Routines_Blueprint_IndustryPulse_Slot_Industry_Default"),
            ]),

        new RoutineBlueprint(
            Key: RegulationWatch,
            TitleKey: "Routines_Blueprint_RegulationWatch_Title",
            DescriptionKey: "Routines_Blueprint_RegulationWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 30),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_RegulationWatch_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "scope",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_RegulationWatch_Slot_Scope_Label",
                    HelpKey: "Routines_Blueprint_RegulationWatch_Slot_Scope_Help",
                    DefaultKey: "Routines_Blueprint_RegulationWatch_Slot_Scope_Default"),
            ]),

        new RoutineBlueprint(
            Key: ReleaseWatch,
            TitleKey: "Routines_Blueprint_ReleaseWatch_Title",
            DescriptionKey: "Routines_Blueprint_ReleaseWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(10, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_ReleaseWatch_Query",
            GuardKey: WebSearchGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            RequiresWebSearch: true,
            Slots:
            [
                new RoutineSlot(
                    Name: "projects",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_ReleaseWatch_Slot_Projects_Label",
                    HelpKey: "Routines_Blueprint_ReleaseWatch_Slot_Projects_Help",
                    DefaultKey: "Routines_Blueprint_ReleaseWatch_Slot_Projects_Default"),
            ]),

        new RoutineBlueprint(
            Key: MealIdeas,
            TitleKey: "Routines_Blueprint_MealIdeas_Title",
            DescriptionKey: "Routines_Blueprint_MealIdeas_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(10, 0),
            DefaultDayOfWeek: DayOfWeek.Saturday,
            QueryKey: "Routines_Blueprint_MealIdeas_Query",
            GuardKey: null,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            Slots:
            [
                new RoutineSlot(
                    Name: "preferences",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_MealIdeas_Slot_Preferences_Label",
                    HelpKey: "Routines_Blueprint_MealIdeas_Slot_Preferences_Help",
                    DefaultKey: "Routines_Blueprint_MealIdeas_Slot_Preferences_Default"),
            ]),

        new RoutineBlueprint(
            Key: LearnOneThing,
            TitleKey: "Routines_Blueprint_LearnOneThing_Title",
            DescriptionKey: "Routines_Blueprint_LearnOneThing_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: DayOfWeek.Sunday,
            QueryKey: "Routines_Blueprint_LearnOneThing_Query",
            GuardKey: null,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            Slots:
            [
                new RoutineSlot(
                    Name: "subject",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_LearnOneThing_Slot_Subject_Label",
                    HelpKey: "Routines_Blueprint_LearnOneThing_Slot_Subject_Help",
                    DefaultKey: "Routines_Blueprint_LearnOneThing_Slot_Subject_Default"),
            ]),

        new RoutineBlueprint(
            Key: MorningBrief,
            TitleKey: "Routines_Blueprint_MorningBrief_Title",
            DescriptionKey: "Routines_Blueprint_MorningBrief_Description",
            Category: RoutineBlueprintCategories.YourData,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_MorningBrief_Query",
            GuardKey: ReadOnlyGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Minimal),

        new RoutineBlueprint(
            Key: MeetingFollowup,
            TitleKey: "Routines_Blueprint_MeetingFollowup_Title",
            DescriptionKey: "Routines_Blueprint_MeetingFollowup_Description",
            Category: RoutineBlueprintCategories.YourData,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(18, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_MeetingFollowup_Query",
            GuardKey: WriteGuardKey,
            GrantedTools: ["create_todo"],
            DefaultEffort: ReasoningEffort.High),

        new RoutineBlueprint(
            Key: EveningWinddown,
            TitleKey: "Routines_Blueprint_EveningWinddown_Title",
            DescriptionKey: "Routines_Blueprint_EveningWinddown_Description",
            Category: RoutineBlueprintCategories.YourData,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(20, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_EveningWinddown_Query",
            GuardKey: ReadOnlyGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Minimal),

        new RoutineBlueprint(
            Key: HabitCheckin,
            TitleKey: "Routines_Blueprint_HabitCheckin_Title",
            DescriptionKey: "Routines_Blueprint_HabitCheckin_Description",
            Category: RoutineBlueprintCategories.YourData,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(21, 0),
            DefaultDayOfWeek: null,
            QueryKey: "Routines_Blueprint_HabitCheckin_Query",
            GuardKey: ReadOnlyGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low),

        new RoutineBlueprint(
            Key: BillsRenewals,
            TitleKey: "Routines_Blueprint_BillsRenewals_Title",
            DescriptionKey: "Routines_Blueprint_BillsRenewals_Description",
            Category: RoutineBlueprintCategories.YourData,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryKey: "Routines_Blueprint_BillsRenewals_Query",
            GuardKey: ReadOnlyGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low),

        new RoutineBlueprint(
            Key: WeeklyReview,
            TitleKey: "Routines_Blueprint_WeeklyReview_Title",
            DescriptionKey: "Routines_Blueprint_WeeklyReview_Description",
            Category: RoutineBlueprintCategories.YourData,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(17, 0),
            DefaultDayOfWeek: DayOfWeek.Friday,
            QueryKey: "Routines_Blueprint_WeeklyReview_Query",
            GuardKey: ReadOnlyGuardKey,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.High),
    ];

    public static RoutineBlueprint? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
}
