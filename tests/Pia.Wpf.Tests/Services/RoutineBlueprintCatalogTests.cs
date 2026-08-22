using System.Collections;
using System.Globalization;
using System.Resources;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Services;

public class RoutineBlueprintCatalogTests
{
    [Fact]
    public void CatalogShipsTheTopicDigest()
    {
        Assert.NotEmpty(RoutineBlueprintCatalog.All);
        // Pinned literally: the key is a compatibility surface, so a rename has to fail here.
        Assert.Equal("topic-digest", RoutineBlueprintCatalog.TopicDigest);
        Assert.NotNull(RoutineBlueprintCatalog.Find("topic-digest"));
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
        }
    }

    [Fact]
    public void TopicDigestGrantsNoWriteTools()
    {
        var bp = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;
        Assert.Equal(ScheduledJobKind.Research, bp.Kind);
        // Web search is a provider capability and reads run ungranted; the empty set is the point.
        Assert.Empty(bp.GrantedTools);
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
