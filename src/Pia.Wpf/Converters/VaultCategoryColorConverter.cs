using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Services.Wiki;

namespace Pia.Converters;

/// <summary>
/// Maps a Vault Overview group key to a swatch brush. A canonical vault type (e.g.
/// <c>personal_profile</c>) resolves to an existing theme brush resource — unlike
/// <see cref="MemoryTypeToBrushConverter"/>, which routes through <c>MemoryObjectTypes.ToKind</c> and so
/// collapses <c>contact_list</c>→Profile and <c>topic</c>→Note into duplicate colors, this keeps the
/// canonical types on distinct theme swatches by reusing the otherwise unused Skill/Context brushes.
/// An exploded topic <em>category</em> key (e.g. <c>person</c>) instead maps to a fixed color from
/// <see cref="TopicPalette"/>, indexed by the category's position in
/// <see cref="VaultIndexService.TopicCategories"/> and cycled (an 11th category reuses the first color).
/// </summary>
public class VaultCategoryColorConverter : IValueConverter
{
    public enum VaultCategoryBrushKind
    {
        Background,
        Foreground
    }

    public VaultCategoryBrushKind Kind { get; set; } = VaultCategoryBrushKind.Background;

    // 10-color categorical palette (Tableau 10) for the exploded topic-category swatches. Fixed hex —
    // theme-independent, unlike the canonical-type swatches — so each topic category keeps a stable,
    // distinguishable color. Assigned by TopicCategories index and cycled when categories exceed ten.
    private static readonly string[] TopicPalette =
    [
        "#4E79A7", // blue
        "#F28E2B", // orange
        "#59A14F", // green
        "#E15759", // red
        "#B07AA1", // purple
        "#76B7B2", // teal
        "#EDC948", // yellow
        "#FF9DA7", // pink
        "#9C755F", // brown
        "#BAB0AC", // gray
    ];

    // Topic category key → its index in the authoritative TopicCategories order, so the palette
    // assignment is stable per category regardless of which siblings are present.
    private static readonly Dictionary<string, int> TopicCategoryIndex =
        VaultIndexService.TopicCategories
            .Select((c, i) => (c.Category, i))
            .ToDictionary(x => x.Category, x => x.i, StringComparer.OrdinalIgnoreCase);

    // Frozen palette brushes, cached so every swatch of the same color shares one instance. Populated on
    // the UI thread (converters run there), so the plain dictionary needs no synchronization.
    private static readonly Dictionary<string, Brush> PaletteBrushCache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var isBackground = Kind == VaultCategoryBrushKind.Background;
        var lower = (value as string)?.ToLowerInvariant();

        // Canonical §8 types → theme brushes (ordinal-ignore-case, so a case-drifted frontmatter type
        // still resolves). Keys point at existing Type*Brush resources.
        var key = lower switch
        {
            "personal_profile" => isBackground ? "TypeProfileBgBrush" : "TypeProfileFgBrush",
            "contact_list" => isBackground ? "TypeSkillBgBrush" : "TypeSkillFgBrush",
            "preference" => isBackground ? "TypePreferenceBgBrush" : "TypePreferenceFgBrush",
            "note" => isBackground ? "TypeNoteBgBrush" : "TypeNoteFgBrush",
            "project" => isBackground ? "TypeProjectBgBrush" : "TypeProjectFgBrush",
            "topic" => isBackground ? "TypeContextBgBrush" : "TypeContextFgBrush",
            _ => null
        };

        if (key is not null && Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        // Exploded topic category → fixed palette color by category index (cycled).
        if (lower is not null && TopicCategoryIndex.TryGetValue(lower, out var idx))
            return GetPaletteBrush(TopicPalette[idx % TopicPalette.Length]);

        var fallback = isBackground ? "SurfaceMutedBrush" : "TextMutedBrush";
        return Application.Current?.TryFindResource(fallback) as Brush;
    }

    private static Brush GetPaletteBrush(string hex)
    {
        if (PaletteBrushCache.TryGetValue(hex, out var cached))
            return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        PaletteBrushCache[hex] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
