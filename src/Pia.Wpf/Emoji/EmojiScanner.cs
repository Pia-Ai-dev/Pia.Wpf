using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Pia.Emoji;

/// <summary>
/// Splits a string into runs of plain text and individual emoji, so each emoji can be rendered
/// through the OS color pipeline (<see cref="EmojiImageRenderer"/>) while text stays as normal
/// glyph runs.
/// </summary>
/// <remarks>
/// There is no <c>IsEmoji</c> in the BCL, so classification is heuristic. It is driven by the
/// Unicode blocks that <c>Segoe UI Emoji</c> renders in color, plus the structural markers that
/// force emoji presentation: variation selector-16 (U+FE0F), ZWJ joins (U+200D), the keycap
/// combiner (U+20E3), regional-indicator flag pairs (U+1F1E6–1F1FF) and skin-tone modifiers
/// (U+1F3FB–1F3FF). Grapheme-cluster boundaries follow UAX #29 via
/// <see cref="StringInfo.GetTextElementEnumerator(string)"/> (grapheme-cluster aware since
/// .NET 5), which keeps ZWJ families, flag pairs, skin-tone and keycap sequences together as
/// single clusters.
/// </remarks>
public static class EmojiScanner
{
    private const int ZeroWidthJoiner = 0x200D;
    private const int VariationSelector16 = 0xFE0F; // forces emoji (color) presentation
    private const int KeycapCombiner = 0x20E3;       // base (+ optional VS16) + 20E3 → keycap

    /// <summary>
    /// Walks <paramref name="text"/> grapheme cluster by grapheme cluster, coalescing consecutive
    /// non-emoji clusters into a single text run and emitting each emoji cluster on its own.
    /// </summary>
    public static IEnumerable<(string Text, bool IsEmoji)> Segment(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var pending = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var cluster = (string)enumerator.Current;
            if (IsEmojiCluster(cluster))
            {
                if (pending.Length > 0)
                {
                    yield return (pending.ToString(), false);
                    pending.Clear();
                }

                yield return (cluster, true);
            }
            else
            {
                pending.Append(cluster);
            }
        }

        if (pending.Length > 0)
            yield return (pending.ToString(), false);
    }

    /// <summary>True when the grapheme cluster should be rendered as a color emoji.</summary>
    internal static bool IsEmojiCluster(string cluster)
    {
        var hasDefaultEmojiScalar = false;

        for (var i = 0; i < cluster.Length;)
        {
            int cp;
            if (char.IsHighSurrogate(cluster[i]) && i + 1 < cluster.Length && char.IsLowSurrogate(cluster[i + 1]))
            {
                cp = char.ConvertToUtf32(cluster[i], cluster[i + 1]);
                i += 2;
            }
            else
            {
                cp = cluster[i];
                i += 1;
            }

            // Structural markers make the whole cluster unambiguously an emoji.
            if (cp is ZeroWidthJoiner or VariationSelector16 or KeycapCombiner)
                return true;
            if (cp is >= 0x1F1E6 and <= 0x1F1FF) // regional indicator (flags)
                return true;
            if (cp is >= 0x1F3FB and <= 0x1F3FF) // skin-tone modifier
                return true;

            if (IsDefaultEmojiScalar(cp))
                hasDefaultEmojiScalar = true;
        }

        return hasDefaultEmojiScalar;
    }

    /// <summary>
    /// Scalars that render as emoji on their own (Emoji_Presentation = Yes), without a VS16.
    /// Text-default-but-emoji-capable scalars (©, ®, ™, ▶, arrows, bare digits, …) are
    /// intentionally excluded — they only count as emoji when the cluster carries a VS16, which
    /// <see cref="IsEmojiCluster"/> already catches structurally. Excluding them keeps ordinary
    /// punctuation/symbols out of the (potentially tofu-prone) emoji path.
    /// </summary>
    private static bool IsDefaultEmojiScalar(int cp) =>
        cp switch
        {
            // Supplementary pictograph blocks — essentially all emoji.
            >= 0x1F300 and <= 0x1F5FF => true, // Misc Symbols & Pictographs
            >= 0x1F600 and <= 0x1F64F => true, // Emoticons
            >= 0x1F680 and <= 0x1F6FF => true, // Transport & Map
            >= 0x1F900 and <= 0x1F9FF => true, // Supplemental Symbols & Pictographs
            >= 0x1FA00 and <= 0x1FAFF => true, // Symbols & Pictographs Extended-A

            // BMP symbol/dingbat blocks that Segoe UI Emoji renders in color.
            >= 0x2600 and <= 0x26FF => true,   // Miscellaneous Symbols
            >= 0x2700 and <= 0x27BF => true,   // Dingbats

            // Common default-presentation singletons outside those blocks.
            0x1F004 => true, // 🀄 mahjong red dragon
            0x1F0CF => true, // 🃏 joker
            0x231A or 0x231B => true,                       // ⌚ ⌛
            0x23E9 or 0x23EA or 0x23EB or 0x23EC => true,   // ⏩ ⏪ ⏫ ⏬
            0x23F0 or 0x23F3 => true,                       // ⏰ ⏳
            0x25FD or 0x25FE => true,                       // ◽ ◾
            0x2B1B or 0x2B1C => true,                       // ⬛ ⬜
            0x2B50 or 0x2B55 => true,                       // ⭐ ⭕
            _ => false,
        };
}
