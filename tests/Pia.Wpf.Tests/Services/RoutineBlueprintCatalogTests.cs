using System.Collections;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;
using Pia.Services.MeetingAttendee;

namespace Pia.Tests.Services;

public class RoutineBlueprintCatalogTests
{
    // Pinned literally: a key is a compatibility surface, so a rename has to fail here.
    private static readonly string[] ShippedKeys =
    [
        "news-briefing",
        "word-of-the-day",
        "topic-digest",
        "security-advisories",
        "market-snapshot",
        "stock-watchlist",
        "sports-roundup",
        "client-watch",
        "competitor-watch",
        "industry-pulse",
        "regulation-watch",
        "release-watch",
        "meal-ideas",
        "learn-one-thing",
        "morning-brief",
        "meeting-followup",
        "evening-winddown",
        "habit-checkin",
        "bills-renewals",
        "weekly-review",
    ];

    /// <summary>Templates and slot defaults are resx values now, so every rule about them has to hold in
    /// each locale — a de translation that drops a {slot} would otherwise ship a literal brace to the model.</summary>
    private static readonly CultureInfo[] Locales =
        [CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr")];

    private static RoutineBlueprintText TextFor(RoutineBlueprint blueprint, CultureInfo culture) =>
        RoutineBlueprintText.Resolve(blueprint, key => ViewStrings.ResourceManager.GetString(key, culture)
            ?? throw new InvalidOperationException($"{culture.Name}: '{key}' resolves to nothing"));

    /// <summary>The body alone — the guard suffix is shared, so counting it would punish the fourteen cards
    /// that carry one.</summary>
    private static string BodyFor(RoutineBlueprint blueprint, CultureInfo culture) =>
        ViewStrings.ResourceManager.GetString(blueprint.QueryKey, culture)!;

    [Fact]
    public void EveryShippedKeyIsPinnedAndFindable()
    {
        Assert.Equal("news-briefing", RoutineBlueprintCatalog.NewsBriefing);
        Assert.Equal("word-of-the-day", RoutineBlueprintCatalog.WordOfTheDay);
        Assert.Equal("topic-digest", RoutineBlueprintCatalog.TopicDigest);
        Assert.Equal("security-advisories", RoutineBlueprintCatalog.SecurityAdvisories);
        Assert.Equal("market-snapshot", RoutineBlueprintCatalog.MarketSnapshot);
        Assert.Equal("stock-watchlist", RoutineBlueprintCatalog.StockWatchlist);
        Assert.Equal("sports-roundup", RoutineBlueprintCatalog.SportsRoundup);
        Assert.Equal("client-watch", RoutineBlueprintCatalog.ClientWatch);
        Assert.Equal("industry-pulse", RoutineBlueprintCatalog.IndustryPulse);
        Assert.Equal("regulation-watch", RoutineBlueprintCatalog.RegulationWatch);
        Assert.Equal("release-watch", RoutineBlueprintCatalog.ReleaseWatch);
        Assert.Equal("meal-ideas", RoutineBlueprintCatalog.MealIdeas);
        Assert.Equal("learn-one-thing", RoutineBlueprintCatalog.LearnOneThing);
        Assert.Equal("morning-brief", RoutineBlueprintCatalog.MorningBrief);
        Assert.Equal("evening-winddown", RoutineBlueprintCatalog.EveningWinddown);
        Assert.Equal("habit-checkin", RoutineBlueprintCatalog.HabitCheckin);
        Assert.Equal("weekly-review", RoutineBlueprintCatalog.WeeklyReview);
        Assert.Equal("competitor-watch", RoutineBlueprintCatalog.CompetitorWatch);
        Assert.Equal("bills-renewals", RoutineBlueprintCatalog.BillsRenewals);
        Assert.Equal("meeting-followup", RoutineBlueprintCatalog.MeetingFollowup);

        foreach (var key in ShippedKeys)
            Assert.NotNull(RoutineBlueprintCatalog.Find(key));
    }

    [Fact]
    public void TheCatalogHoldsEveryShippedBlueprintAndNothingElse()
    {
        Assert.Equal(20, RoutineBlueprintCatalog.All.Count);

        var keys = RoutineBlueprintCatalog.All.Select(b => b.Key).ToList();
        Assert.True(
            ShippedKeys.Order(StringComparer.Ordinal)
                .SequenceEqual(keys.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            $"the catalog must hold exactly the pinned keys, but it holds: {string.Join(", ", keys)}");
    }

    [Fact]
    public void FindIsExactAndOrdinal()
    {
        Assert.Null(RoutineBlueprintCatalog.Find("Topic-Digest"));
        Assert.Null(RoutineBlueprintCatalog.Find("no-such-blueprint"));
        Assert.Null(RoutineBlueprintCatalog.Find(null));
    }

    [Fact]
    public void KeysAreUniqueAndNonEmpty()
    {
        var keys = RoutineBlueprintCatalog.All.Select(b => b.Key).ToList();
        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        Assert.True(keys.Distinct(StringComparer.Ordinal).Count() == keys.Count,
            $"blueprint keys must be ordinally distinct: {string.Join(", ", keys)}");
    }

    [Fact]
    public void EveryBlueprintCarriesResxKeysAndAQuery()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(bp.TitleKey));
            Assert.False(string.IsNullOrWhiteSpace(bp.DescriptionKey));
            Assert.False(string.IsNullOrWhiteSpace(bp.Category));
            Assert.False(string.IsNullOrWhiteSpace(bp.QueryKey));
            // A key, not a sentence — literal prose renders as "[Some literal prose]".
            Assert.DoesNotContain(" ", bp.TitleKey);
            Assert.DoesNotContain(" ", bp.DescriptionKey);
            Assert.DoesNotContain(" ", bp.QueryKey);
        }
    }

    [Fact]
    public void EveryCategoryIsOneTheCatalogRendersAGroupFor()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
            Assert.True(RoutineBlueprintCategories.InDisplayOrder.Contains(bp.Category, StringComparer.Ordinal),
                $"{bp.Key} sits in category '{bp.Category}', which is not in the display order, so its card "
                + "would never be rendered");
    }

    [Fact]
    public void TheWebSearchFlagAndTheGuardClauseAgree()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            Assert.Equal(bp.RequiresWebSearch, bp.GuardKey == RoutineBlueprintCatalog.WebSearchGuardKey);

            if (bp.GuardKey is null) continue;

            // Appended, never inlined: a body that also spells the guard out ships it twice and drifts from
            // the shared one the next time either is edited.
            foreach (var culture in Locales)
            {
                var guard = ViewStrings.ResourceManager.GetString(bp.GuardKey, culture)!;
                Assert.DoesNotContain(guard, BodyFor(bp, culture), StringComparison.Ordinal);
                Assert.EndsWith(guard, TextFor(bp, culture).Template, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Parity tests only prove the key exists in all three files. A body pasted over untranslated is
    /// exactly what "make the prefill read natively" rules out, and it looks identical to a real translation
    /// from the key list alone.</summary>
    [Fact]
    public void EveryTemplateBodyAndSlotDefaultIsActuallyTranslated()
    {
        var untranslated = new List<string>();

        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            var keys = new[] { bp.QueryKey }.Concat(bp.Slots.Select(sl => sl.DefaultKey!));
            foreach (var key in keys)
            {
                var en = ViewStrings.ResourceManager.GetString(key, CultureInfo.InvariantCulture)!;
                foreach (var culture in Locales.Where(c => c != CultureInfo.InvariantCulture))
                    if (ViewStrings.ResourceManager.GetString(key, culture) == en)
                        untranslated.Add($"{key} [{culture.Name}]");
            }
        }

        // The tool-name lists inside a template are identical across locales, so this compares the WHOLE value
        // rather than looking for English words in it.
        Assert.True(untranslated.Count == 0,
            $"these values are still the English text: {string.Join(", ", untranslated)}");
    }

    /// <summary>The bar test users asked for: a body long enough to be a wall of text is the thing they could
    /// not edit. The guard is excluded — it is shared, so it reads as boilerplate rather than as instruction.</summary>
    [Fact]
    public void NoTemplateBodyOutgrowsTheLengthBar()
    {
        const int bar = 320;
        var over = new List<string>();

        foreach (var bp in RoutineBlueprintCatalog.All)
            foreach (var culture in Locales)
            {
                var body = BodyFor(bp, culture);
                if (body.Length > bar) over.Add($"{bp.Key} [{culture.Name}] {body.Length}");
            }

        Assert.True(over.Count == 0,
            $"every template body must stay within {bar} characters, but these do not: {string.Join(", ", over)}");
    }

    /// <summary>The pair above only proves flag and text agree; this is the direction the bug travels.</summary>
    [Fact]
    public void ATemplateThatSearchesTheWeb_AdvertisesThatItNeedsWebSearch()
    {
        // English only: the phrase is the needle, and the flag is per blueprint rather than per locale, so one
        // locale proves it for all three.
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            var body = BodyFor(bp, CultureInfo.InvariantCulture);
            if (!body.Contains("search the web", StringComparison.OrdinalIgnoreCase)) continue;

            Assert.True(bp.RequiresWebSearch,
                $"{bp.Key} tells the model to search the web but sets RequiresWebSearch false, so its card "
                + "carries no chip and its template carries no guard — on a provider that cannot search it "
                + "would answer from memory");
        }
    }

    /// <summary>The key, the resx stem and the card's AutomationId are three casings of one name.</summary>
    [Fact]
    public void EveryResxStemIsItsKeyInPascalCase()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            var stem = string.Concat(bp.Key.Split('-')
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

            Assert.Equal($"Routines_Blueprint_{stem}_Title", bp.TitleKey);
            Assert.Equal($"Routines_Blueprint_{stem}_Description", bp.DescriptionKey);
            Assert.Equal($"Routines_Blueprint_{stem}_Query", bp.QueryKey);

            foreach (var slot in bp.Slots)
            {
                var slotStem = char.ToUpperInvariant(slot.Name[0]) + slot.Name[1..];
                Assert.Equal($"Routines_Blueprint_{stem}_Slot_{slotStem}_Label", slot.LabelKey);
                Assert.Equal($"Routines_Blueprint_{stem}_Slot_{slotStem}_Help", slot.HelpKey);
                Assert.Equal($"Routines_Blueprint_{stem}_Slot_{slotStem}_Default", slot.DefaultKey);
            }
        }
    }

    [Fact]
    public void EveryBraceInATemplateNamesADeclaredSlotOfThatBlueprint()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
            foreach (var culture in Locales)
            {
                var template = TextFor(bp, culture).Template;

                Assert.True(RoutineBlueprintFill.BracesAreAllPlaceholders(template),
                    $"{bp.Key} [{culture.Name}] has a brace that is not part of a {{slot}} placeholder, so it "
                    + "would reach the model verbatim");

                foreach (var name in Placeholders(template))
                    Assert.True(bp.Slots.Any(s => s.Name == name),
                        $"{bp.Key} [{culture.Name}] references {{{name}}} but declares no such slot");
            }
    }

    /// <summary>A slot the template never mentions can never be filled, so it would show up in the tool's slot
    /// listing as a question with no effect on the prompt.</summary>
    [Fact]
    public void EveryDeclaredSlotIsReferencedByItsOwnTemplate()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            foreach (var culture in Locales)
            {
                var referenced = Placeholders(TextFor(bp, culture).Template);
                foreach (var slot in bp.Slots)
                    Assert.Contains(slot.Name, referenced);
            }

            Assert.Equal(bp.Slots.Count, bp.Slots.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count());
        }
    }

    /// <summary>What the card path shows the user: the shipped catalog must render with its own defaults, or a
    /// card would open the editor on a literal <c>{topic}</c>.</summary>
    [Fact]
    public void EveryBlueprintRendersCleanlyFromItsOwnDefaults()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
            foreach (var culture in Locales)
            {
                var fill = RoutineBlueprintFill.ToCreateArgs(bp, TextFor(bp, culture));

                Assert.True(fill.IsSuccess,
                    $"{bp.Key} [{culture.Name}] did not render: {fill.Error?.Kind} on '{fill.Error?.SlotName}'");
                Assert.DoesNotContain("{", fill.Query!);
                Assert.DoesNotContain("}", fill.Query!);
            }
    }

    private static List<string> Placeholders(string template) =>
        [.. System.Text.RegularExpressions.Regex.Matches(template, @"\{([^{}]*)\}").Select(m => m.Groups[1].Value)];

    [Fact]
    public void EveryPrefillIsLegalForTheEditor()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            Assert.True(Enum.IsDefined(bp.Kind));
            Assert.True(Enum.IsDefined(bp.Recurrence));

            // The editor round-trips the time through "HH:mm", so seconds would be dropped on save.
            Assert.Equal(0, bp.DefaultTime.Second);
            Assert.Equal(0, bp.DefaultTime.Millisecond);
            Assert.True(TimeOnly.TryParseExact(bp.DefaultTime.ToString("HH\\:mm"), "HH\\:mm", out _));

            Assert.Equal(bp.Recurrence == RecurrenceType.Weekly, bp.DefaultDayOfWeek is not null);

            if (bp.DefaultEffort is { } effort) Assert.True(Enum.IsDefined(effort));
        }
    }

    [Fact]
    public void NoBlueprintPinsAnEffortOfNone()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
            Assert.True(bp.DefaultEffort != ReasoningEffort.None,
                $"{bp.Key} would pin reasoning off, which is not what the editor's inherit row means");
    }

    /// <summary>A declared default is worth nothing unless the card carries it onto the field the user saves.</summary>
    [Fact]
    public void EveryBlueprintCarriesItsDefaultEffortOntoTheEditorField()
    {
        var vm = Editor();

        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            vm.StartFromBlueprintCommand.Execute(bp.Key);

            var chosen = vm.EditEffort;
            Assert.NotNull(chosen);
            Assert.Equal(bp.DefaultEffort, chosen!.Value);
            // A prefilled row the picker does not offer renders as an empty ComboBox.
            Assert.Contains(chosen, vm.EffortChoices);
        }
    }

    [Fact]
    public void OnlyTheMeetingFollowupGrantsAWriteTool()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            if (bp.Key == RoutineBlueprintCatalog.MeetingFollowup)
            {
                Assert.Equal("create_todo", Assert.Single(bp.GrantedTools));
                continue;
            }

            // Web search is a provider capability and every read tool runs ungranted, so a routine
            // that only reports needs nothing.
            Assert.True(bp.GrantedTools.Count == 0,
                $"{bp.Key} reports only, so it must grant no write tool, but it grants: {string.Join(", ", bp.GrantedTools)}");
        }
    }

    [Fact]
    public void TheGrantsABlueprintAdvertisesAreTheGrantsItsRunGets()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            // What the dispatcher does: the AgentTask leg maps an empty list to null, which the
            // launcher turns into write_file — so that card would run able to write files.
            var effective = bp.Kind == ScheduledJobKind.AgentTask && bp.GrantedTools.Count == 0
                ? HeadlessRunRequest.DefaultGrantedWrites
                : bp.GrantedTools;

            Assert.True(effective.SequenceEqual(bp.GrantedTools, StringComparer.Ordinal),
                $"{bp.Key} ({bp.Kind}) advertises [{string.Join(", ", bp.GrantedTools)}] "
                + $"but its run would get [{string.Join(", ", effective)}]");
        }
    }

    [Fact]
    public void TheMeetingFollowupQueriesTodosBeforeItCreatesOne()
    {
        var bp = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.MeetingFollowup)!;

        foreach (var culture in Locales)
        {
            var template = TextFor(bp, culture).Template;
            var query = template.IndexOf("query_todos", StringComparison.Ordinal);
            var create = template.IndexOf("create_todo", StringComparison.Ordinal);

            Assert.True(query >= 0, $"[{culture.Name}] the template must read the todo list");
            Assert.True(create > query,
                $"[{culture.Name}] the template must read the todo list before it creates one, or a re-run "
                + "duplicates every follow-up");
        }
    }

    [Fact]
    public void NoBlueprintGrantsADeleteLikeTool()
    {
        foreach (var bp in RoutineBlueprintCatalog.All)
            foreach (var tool in bp.GrantedTools)
                Assert.False(ToolPermissionService.IsDeleteLike(tool), tool);
    }

    /// <summary>The catalog holds these as record fields, so LocalizationTests' literal-key regexes cannot see them.</summary>
    [Fact]
    public void EveryBlueprintKeyResolvesInAllThreeLocales()
    {
        var keys = RoutineBlueprintCatalog.All
            .SelectMany(b => new[] { b.TitleKey, b.DescriptionKey, b.QueryKey }
                .Concat(b.GuardKey is null ? [] : new[] { b.GuardKey })
                .Concat(b.Slots.SelectMany(s => new[] { s.LabelKey, s.HelpKey, s.DefaultKey! })))
            .Distinct()
            .ToList();

        // A title, a description and a query per blueprint, a label, a help and a default per slot, plus the
        // three shared guards — so a copy-pasted key shrinks this loudly.
        Assert.NotEmpty(keys);
        Assert.Equal((RoutineBlueprintCatalog.All.Count * 3)
            + (RoutineBlueprintCatalog.All.Sum(b => b.Slots.Count) * 3)
            + RoutineBlueprintCatalog.All.Select(b => b.GuardKey).Where(k => k is not null).Distinct().Count(),
            keys.Count);

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every routine-blueprint key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>Built by interpolation from the category, so no literal-key scan can see them.</summary>
    [Fact]
    public void EveryCategoryHeaderKeyResolvesInAllThreeLocales()
    {
        var keys = RoutineBlueprintCategories.InDisplayOrder
            .SelectMany(c => new[]
            {
                $"Routines_Category_{RoutineBlueprintCategories.StemOf(c)}_Title",
                $"Routines_Category_{RoutineBlueprintCategories.StemOf(c)}_Subtitle",
            })
            .ToList();

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = GetResourceKeysForCulture(ViewStrings.ResourceManager, culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every group header must resolve in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    /// <summary>No substitute here is ever called: the effort rows are built in the constructor, and the prefill
    /// touches no service.</summary>
    private static RoutinesViewModel Editor()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization[Arg.Any<string>()].Returns(ci => (string)ci[0]);

        return new RoutinesViewModel(
            Substitute.For<IScheduledJobService>(),
            Substitute.For<IScheduledJobRunner>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IPersonaService>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IWindowManagerService>(),
            localization,
            Substitute.For<IPluginService>(),
            Substitute.For<IBrowserProvisioner>(),
            NullLogger<RoutinesViewModel>.Instance);
    }

    private static HashSet<string> GetResourceKeysForCulture(ResourceManager rm, CultureInfo culture)
    {
        var keys = new HashSet<string>();
        var resourceSet = rm.GetResourceSet(culture, true, false);
        if (resourceSet == null) return keys;

        foreach (DictionaryEntry entry in resourceSet)
            keys.Add((string)entry.Key);
        return keys;
    }
}
