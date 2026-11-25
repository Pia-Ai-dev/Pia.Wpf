using System.Globalization;
using System.Text;

namespace Pia.Services.E2EE;

/// <summary>A device name as safe to render: no line breaks, bidi or invisible characters, bounded length.</summary>
public readonly record struct DeviceDisplayName(string Text, bool HadHiddenCharacters);

/// <summary>Device names come from the server unverified, so every surface that shows one goes through here.</summary>
public static class DeviceNameDisplay
{
    public const int MaxTextElements = 64;

    // Stacked combining marks draw outside the line box ("Zalgo"); two covers every real script.
    private const int MaxCombiningMarksPerBase = 2;

    public static DeviceDisplayName Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return new DeviceDisplayName("", false);

        var builder = new StringBuilder(raw.Length);
        var hidden = false;
        var combiningRun = 0;
        var pendingSpace = false;

        foreach (var rune in raw.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            switch (category)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    hidden = true;
                    pendingSpace = builder.Length > 0;
                    combiningRun = 0;
                    continue;
                case UnicodeCategory.SpaceSeparator:
                    pendingSpace = builder.Length > 0;
                    combiningRun = 0;
                    continue;
                case UnicodeCategory.Format:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.Surrogate:
                    hidden = true;
                    continue;
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                    if (builder.Length == 0 || pendingSpace || ++combiningRun > MaxCombiningMarksPerBase)
                    {
                        hidden = true;
                        continue;
                    }
                    break;
                default:
                    combiningRun = 0;
                    break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(rune.ToString());
        }

        return new DeviceDisplayName(Truncate(builder.ToString()), hidden);
    }

    private static string Truncate(string text)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= MaxTextElements
            ? text
            : info.SubstringByTextElements(0, MaxTextElements - 1).TrimEnd() + '…';
    }
}
