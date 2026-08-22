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

namespace Pia.Tests.Services;

public class RoutineBlueprintCatalogTests
{
    // Pinned literally: a key is a compatibility surface, so a rename has to fail here.
    private static readonly string[] ShippedKeys =
    [
        "topic-digest",
        "morning-brief",
        "evening-winddown",
        "habit-checkin",
        "weekly-review",
        "competitor-watch",
        "bills-renewals",
        "meeting-followup",
    ];

    [Fact]
    public void EveryShippedKeyIsPinnedAndFindable()
    {
        Assert.Equal("topic-digest", RoutineBlueprintCatalog.TopicDigest);
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
        Assert.Equal(8, RoutineBlueprintCatalog.All.Count);

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
            Assert.False(string.IsNullOrWhiteSpace(bp.QueryTemplate));
            // A key, not a sentence — literal prose renders as "[Some literal prose]".
            Assert.DoesNotContain(" ", bp.TitleKey);
            Assert.DoesNotContain(" ", bp.DescriptionKey);
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
        }
    }

    [Fact]
    public void NoQueryTemplateCarriesAnUnfilledPlaceholder()
    {
        // No fill step at this tier, so a brace would reach the model verbatim.
        foreach (var bp in RoutineBlueprintCatalog.All)
        {
            Assert.DoesNotContain("{", bp.QueryTemplate);
            Assert.DoesNotContain("}", bp.QueryTemplate);
        }
    }

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
                // Named explicitly so it replaces the launcher's default rather than adding to it.
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

        var query = bp.QueryTemplate.IndexOf("query_todos", StringComparison.Ordinal);
        var create = bp.QueryTemplate.IndexOf("create_todo", StringComparison.Ordinal);

        Assert.True(query >= 0, "the template must read the todo list");
        Assert.True(create > query,
            "the template must read the todo list before it creates one, or a re-run duplicates every follow-up");
        Assert.Contains("create_todo", bp.GrantedTools);
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
            .SelectMany(b => new[] { b.TitleKey, b.DescriptionKey })
            .Distinct()
            .ToList();

        // One title and one description per blueprint, so a copy-pasted key shrinks this loudly.
        Assert.NotEmpty(keys);
        Assert.Equal(RoutineBlueprintCatalog.All.Count * 2, keys.Count);

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
