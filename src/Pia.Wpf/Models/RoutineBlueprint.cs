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

/// <summary>One fillable value in a <see cref="RoutineBlueprint.QueryTemplate"/>, written there as <c>{Name}</c>.</summary>
/// <param name="Default">Substituted when nothing is supplied. Null makes the slot required, so an unfilled
/// reference is an error rather than a hole in the prompt.</param>
public sealed record RoutineSlot(
    string Name,
    RoutineSlotKind Kind,
    string LabelKey,
    string HelpKey,
    string? Default = null);

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

    // Nothing tells the model it cannot search, so the template has to forbid answering from memory.
    internal const string WebSearchGuard =
        " Do not answer from memory: every figure and every claim here has to come from a search result you "
        + "actually got back, and each one carries its source link and its date. If you cannot search the "
        + "web, say exactly that in one line and report nothing else.";

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
            DefaultTime: new TimeOnly(6, 30),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Search the web for the main news of the past day, focused on {focus}. At most six headlines, one "
                + "sentence each, every item with its source link and its date. Lead with what changed rather "
                + "than with what was said about it, and skip opinion, speculation and re-reporting of "
                + "yesterday's news. Change nothing. If nothing material happened, say exactly that in one line "
                + "instead of padding the list."
                + WebSearchGuard,
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
                    Default: "world news and business"),
            ]),

        new RoutineBlueprint(
            Key: WordOfTheDay,
            TitleKey: "Routines_Blueprint_WordOfTheDay_Title",
            DescriptionKey: "Routines_Blueprint_WordOfTheDay_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(7, 30),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Teach one word or short phrase in {language}. Give the word, its pronunciation written in plain "
                + "letters, its literal meaning, and three example sentences at everyday difficulty with their "
                + "translations. Pick something a learner would actually say in conversation rather than a rarity, "
                + "and let today's date decide which area of everyday life it comes from — food, travel, work, "
                + "family, feelings — rather than trying to recall what earlier runs picked, which you cannot "
                + "see. Change nothing, and do not quiz, score or grade the reader.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Minimal,
            Slots:
            [
                new RoutineSlot(
                    Name: "language",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_WordOfTheDay_Slot_Language_Label",
                    HelpKey: "Routines_Blueprint_WordOfTheDay_Slot_Language_Help",
                    Default: "Spanish"),
            ]),

        new RoutineBlueprint(
            Key: TopicDigest,
            TitleKey: "Routines_Blueprint_TopicDigest_Title",
            DescriptionKey: "Routines_Blueprint_TopicDigest_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Search the web for what is new on the topic of {topic} in the past day. "
                + "Report only material developments — releases, announcements, results, reversals — and skip "
                + "speculation, opinion and re-reporting of what was already covered. At most five items, one "
                + "sentence each, every item with its source link and its date. If nothing material happened, "
                + "say exactly that in one line instead of padding the list."
                + WebSearchGuard,
            // Web search is a provider capability and every read tool runs ungranted, so a digest that
            // writes nothing needs nothing.
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
                    Default: "artificial intelligence"),
            ]),

        new RoutineBlueprint(
            Key: SecurityAdvisories,
            TitleKey: "Routines_Blueprint_SecurityAdvisories_Title",
            DescriptionKey: "Routines_Blueprint_SecurityAdvisories_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Daily,
            DefaultTime: new TimeOnly(8, 30),
            DefaultDayOfWeek: null,
            QueryTemplate:
                "Search the web for security advisories and patches published in the past day for {products}. One "
                + "line per advisory: the product and version affected, what the flaw allows, whether a fix or a "
                + "workaround is out, and the CVE identifier where one is given, each with its source link and "
                + "its date. Put anything already being exploited first. At most six items, taken from the "
                + "vendor's own advisory or a recognised tracker rather than from a news write-up. Change "
                + "nothing. If nothing was published in the past day, say exactly that in one line and then "
                + "list at most three advisories for these products that are still unpatched or are being "
                + "actively exploited, each with its date."
                + WebSearchGuard,
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
                    Default: "Windows, Microsoft 365 and Google Chrome"),
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
            QueryTemplate:
                "Search the web for where these markets currently stand: {markets}. Give one line per market: its "
                + "level, its move on the day in percent, and the date and time the figure is quoted for. If a "
                + "market is closed at this hour, say so and quote its last close rather than a live figure. Then "
                + "at most three lines on what moved them — what happened, why it matters, and what to watch next "
                + "— each with its source link. Report what happened and nothing else: no forecast, no "
                + "recommendation, no view on whether anything is cheap, expensive, worth buying or worth selling, "
                + "and no commentary on the reader's own money. Change nothing. If none of the named markets could "
                + "be found, say exactly that in one line rather than substituting others."
                + WebSearchGuard,
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
                    Default: "the S&P 500, the Nasdaq, the DAX, EUR/USD and gold"),
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
            QueryTemplate:
                "Search the web for where these holdings currently stand: {holdings}. One line each: the last "
                + "price, the move on the day in percent, and the date and time the figure is quoted for; if the "
                + "market is shut, say so and quote the last close rather than a live figure. Then at most three "
                + "lines on what moved them, each line saying what happened, why it matters and what to watch "
                + "next, with its source link. Report what happened and nothing else: no forecast, no price "
                + "target, no recommendation, no view on whether anything is cheap, expensive, worth buying or "
                + "worth selling, and no commentary on the reader's own money. Change nothing. If a holding could "
                + "not be found, say exactly that in one line rather than substituting another."
                + WebSearchGuard,
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
                    Default: "Apple, Microsoft and Nvidia"),
            ]),

        new RoutineBlueprint(
            Key: SportsRoundup,
            TitleKey: "Routines_Blueprint_SportsRoundup_Title",
            DescriptionKey: "Routines_Blueprint_SportsRoundup_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(7, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Follow these teams: {teams}. If that list still names Bayern Munich and Real Madrid, open with "
                + "one line saying it is a placeholder to be replaced with the teams actually followed. Then "
                + "search the web for their results in the past week and their fixtures in the week ahead. One "
                + "line per result with the score and the competition, one line per fixture with its date and "
                + "kick-off time, every line with its source link. Add at most two lines on anything else that "
                + "changed at a club, such as an injury, a transfer or a change of manager. Change nothing, and "
                + "give no prediction and no tip. If a team neither played nor has a fixture, say exactly that in "
                + "one line for that team."
                + WebSearchGuard,
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
                    Default: "Bayern Munich and Real Madrid"),
            ]),

        new RoutineBlueprint(
            Key: ClientWatch,
            TitleKey: "Routines_Blueprint_ClientWatch_Title",
            DescriptionKey: "Routines_Blueprint_ClientWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(7, 30),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Watch these clients and partners: {accounts}. If that list still names Microsoft, SAP and "
                + "Salesforce, open with one line saying it is a placeholder to be replaced with the accounts "
                + "actually worked with. Then search the web for what changed at each of them in the past week "
                + "and could change the relationship: funding, results, leadership changes, acquisitions, "
                + "restructuring, a large win or a large loss. At most two items per account, one sentence each, "
                + "every item with its source link and its date. Close with at most three lines on what to raise "
                + "in the next conversation with them, drawn only from the items above. Skip speculation, opinion "
                + "and re-reporting of what was already covered, and give an account with nothing material its "
                + "own one-line entry saying so. Change nothing."
                + WebSearchGuard,
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
                    Default: "Microsoft, SAP and Salesforce"),
            ]),

        new RoutineBlueprint(
            Key: CompetitorWatch,
            TitleKey: "Routines_Blueprint_CompetitorWatch_Title",
            DescriptionKey: "Routines_Blueprint_CompetitorWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Watch these companies: {companies}. If no company is named there, start with recall and "
                + "browse_index to find which companies the vault already names as competitors or as companies "
                + "being tracked, and watch those. Only if the vault names none either, fall back to Microsoft, "
                + "Google, OpenAI and Anthropic, and open the report with one line saying that this is a "
                + "placeholder list to be replaced with the companies actually being tracked. Then "
                + "search the web for material developments in the past week — launches, pricing changes, "
                + "funding, leadership changes, outages, withdrawals. At most two items per company, one "
                + "sentence each, every item with its source link and its date. Skip speculation, opinion and "
                + "re-reporting of what was already covered, and give a company with nothing material its own "
                + "one-line entry saying so. Change nothing. If recall or browse_index is unavailable, say so "
                + "in one line before falling back, rather than presenting the fallback as the tracked list."
                + WebSearchGuard,
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            RequiresWebSearch: true,
            Slots:
            [
                // The default is a phrase rather than an empty string: the sentence after it branches on
                // "no company named there", and an empty substitution would leave a dangling colon.
                new RoutineSlot(
                    Name: "companies",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_CompetitorWatch_Slot_Companies_Label",
                    HelpKey: "Routines_Blueprint_CompetitorWatch_Slot_Companies_Help",
                    Default: "(none given)"),
            ]),

        new RoutineBlueprint(
            Key: IndustryPulse,
            TitleKey: "Routines_Blueprint_IndustryPulse_Title",
            DescriptionKey: "Routines_Blueprint_IndustryPulse_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(8, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Search the web for what moved in {industry} over the past week. At most six items; for each, one "
                + "line on what happened, one on why it matters, and one on what to watch next, with its source "
                + "link and its date. Cover the sector as a whole rather than a single company, and prefer "
                + "results, deals, launches, closures and rule changes over commentary and predictions. Keep the "
                + "whole report short enough to read in five minutes. Change nothing. If nothing material "
                + "happened, say exactly that in one line instead of padding the list."
                + WebSearchGuard,
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
                    Default: "information technology"),
            ]),

        new RoutineBlueprint(
            Key: RegulationWatch,
            TitleKey: "Routines_Blueprint_RegulationWatch_Title",
            DescriptionKey: "Routines_Blueprint_RegulationWatch_Description",
            Category: RoutineBlueprintCategories.Ready,
            Kind: ScheduledJobKind.Research,
            Recurrence: RecurrenceType.Weekly,
            DefaultTime: new TimeOnly(9, 0),
            DefaultDayOfWeek: DayOfWeek.Monday,
            QueryTemplate:
                "Search the web for regulatory changes published in the past week covering {scope}. For each one "
                + "name the instrument itself, the body that issued it, the date it was published and the date it "
                + "takes effect, and give its source link, preferably the official text rather than a summary of "
                + "it. Then one line on what happened, one on why it matters, and one on what to watch next. At "
                + "most five items. This is a reading of published documents and not legal advice: give no "
                + "opinion on whether anything applies to the reader, no compliance recommendation, and no "
                + "assessment of risk or exposure. Change nothing. If an instrument's name, date or source cannot "
                + "be established, leave it out and say so in one line rather than describing it vaguely. If "
                + "nothing was published, say exactly that in one line."
                + WebSearchGuard,
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
                    Default: "data protection and IT security in the European Union"),
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
            QueryTemplate:
                "Search the web for new releases of {projects} in the past week. One line each: the project, the "
                + "version, the release date, and the one change most likely to matter to somebody already using "
                + "it, with a link to the release notes. Mark a release as a security fix or as a breaking change "
                + "where the notes say so. At most six items, taken from the project's own release notes rather "
                + "than from a news write-up. Change nothing, and give no advice on whether to upgrade. If "
                + "nothing was released, say exactly that in one line instead of listing older versions."
                + WebSearchGuard,
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
                    Default: ".NET, Python and Node.js"),
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
            QueryTemplate:
                "Plan the coming week's dinners around this: {preferences}. Give five ideas, one line each, "
                + "naming the dish, roughly how long it takes, and its main ingredients. Vary the protein and the "
                + "cuisine across the five rather than repeating one theme. Close with a single combined shopping "
                + "list grouped by aisle, and nothing after it. Change nothing, and give no nutritional claim, no "
                + "calorie count and no advice on what the reader should be eating. If the preferences rule out "
                + "so much that five ideas are not possible, say exactly that in one line and give the ones that "
                + "do fit.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Low,
            Slots:
            [
                new RoutineSlot(
                    Name: "preferences",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_MealIdeas_Slot_Preferences_Label",
                    HelpKey: "Routines_Blueprint_MealIdeas_Slot_Preferences_Help",
                    Default: "quick weeknight dinners for two, nothing that needs special equipment"),
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
            QueryTemplate:
                "Explain one idea from {subject} in plain language. Give the idea in a sentence, then how it "
                + "works in at most five short paragraphs, then one worked example the reader can follow, then "
                + "one common misunderstanding of it. Pick something a curious beginner would meet early rather "
                + "than an obscure corner, and let this week's date decide which branch of {subject} it comes "
                + "from, rather than trying to recall what earlier runs picked, which you cannot see. Say "
                + "plainly where the idea is contested or where you are unsure rather than smoothing it over. "
                + "Change nothing, and do not quiz, score or grade the reader. Say in one line which branch you "
                + "drew from before you explain the idea.",
            GrantedTools: [],
            DefaultEffort: ReasoningEffort.Medium,
            Slots:
            [
                new RoutineSlot(
                    Name: "subject",
                    Kind: RoutineSlotKind.Text,
                    LabelKey: "Routines_Blueprint_LearnOneThing_Slot_Subject_Label",
                    HelpKey: "Routines_Blueprint_LearnOneThing_Slot_Subject_Help",
                    Default: "economics"),
            ]),

        new RoutineBlueprint(
            Key: MorningBrief,
            TitleKey: "Routines_Blueprint_MorningBrief_Title",
            DescriptionKey: "Routines_Blueprint_MorningBrief_Description",
            Category: RoutineBlueprintCategories.YourData,
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
            Key: MeetingFollowup,
            TitleKey: "Routines_Blueprint_MeetingFollowup_Title",
            DescriptionKey: "Routines_Blueprint_MeetingFollowup_Description",
            Category: RoutineBlueprintCategories.YourData,
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

        new RoutineBlueprint(
            Key: EveningWinddown,
            TitleKey: "Routines_Blueprint_EveningWinddown_Title",
            DescriptionKey: "Routines_Blueprint_EveningWinddown_Description",
            Category: RoutineBlueprintCategories.YourData,
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
            Category: RoutineBlueprintCategories.YourData,
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
            Key: BillsRenewals,
            TitleKey: "Routines_Blueprint_BillsRenewals_Title",
            DescriptionKey: "Routines_Blueprint_BillsRenewals_Description",
            Category: RoutineBlueprintCategories.YourData,
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
            Key: WeeklyReview,
            TitleKey: "Routines_Blueprint_WeeklyReview_Title",
            DescriptionKey: "Routines_Blueprint_WeeklyReview_Description",
            Category: RoutineBlueprintCategories.YourData,
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
    ];

    public static RoutineBlueprint? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));
}
