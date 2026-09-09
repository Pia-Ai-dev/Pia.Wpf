using System.Security.Cryptography;
using System.Text;

namespace Pia.Services.Screen;

internal readonly record struct UiaTextNode(
    int Depth, string ControlType, string? Name, string? Value, bool IsOffscreen);

/// <summary>Turns a raw automation walk into the lines a model would read.</summary>
internal static class ScreenTextCompactor
{
    private static readonly HashSet<string> Contributing = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Hyperlink", "Button", "MenuItem", "TabItem", "ListItem", "TreeItem", "HeaderItem",
        "Header", "CheckBox", "RadioButton", "ToolTip", "StatusBar", "Edit", "ComboBox", "Document", "Window",
    };

    private static readonly HashSet<string> PrefersValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "ComboBox", "Document",
    };

    public static (string Text, int TextNodes, bool Truncated) Compact(
        IReadOnlyList<UiaTextNode> nodes, int maxChars)
    {
        var builder = new StringBuilder();
        var kept = 0;
        string? previous = null;

        foreach (var node in nodes)
        {
            if (node.IsOffscreen || !Contributing.Contains(node.ControlType)) continue;

            var raw = PrefersValue.Contains(node.ControlType)
                ? node.Value ?? node.Name
                : node.Name;
            var line = Collapse(raw);
            if (line.Length == 0) continue;

            // Chromium names both the item and its inner text node, so the same line arrives twice in a row.
            if (line == previous) continue;
            previous = line;

            if (builder.Length + line.Length + 1 > maxChars)
            {
                builder.Append("\n[truncated]");
                return (builder.ToString().TrimStart('\n'), kept, true);
            }

            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
            kept++;
        }

        return (builder.ToString(), kept, false);
    }

    public static string Hash(string text)
    {
        if (text.Length == 0) return string.Empty;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }

    private static string Collapse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
