using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Pia.Converters;

/// <summary>
/// Maps a canonical vault type string (e.g. <c>personal_profile</c>) directly to an existing theme brush
/// resource for the Vault Overview swatches. Unlike <see cref="MemoryTypeToBrushConverter"/> — which
/// routes through <c>MemoryObjectTypes.ToKind</c> and so collapses <c>contact_list</c>→Profile and
/// <c>topic</c>→Note into duplicate colors — this keeps six distinct swatches by reusing the otherwise
/// unused Skill/Context brushes, so no new theme brushes are needed.
/// </summary>
public class VaultCategoryColorConverter : IValueConverter
{
    public enum VaultCategoryBrushKind
    {
        Background,
        Foreground
    }

    public VaultCategoryBrushKind Kind { get; set; } = VaultCategoryBrushKind.Background;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var isBackground = Kind == VaultCategoryBrushKind.Background;

        // Match the canonical type string ordinal-ignore-case (lowercased) so a case-drifted frontmatter
        // type still resolves to its swatch. Keys point at existing Type*Brush resources.
        var key = (value as string)?.ToLowerInvariant() switch
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

        var fallback = isBackground ? "SurfaceMutedBrush" : "TextMutedBrush";
        return Application.Current?.TryFindResource(fallback) as Brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
