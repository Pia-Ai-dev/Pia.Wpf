using System.IO;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>
/// Opens an <see cref="OpenFileDialog"/> constrained to the same accepted-extension
/// list used by <c>FileDropBehavior</c>, then re-validates the selection so a user
/// who types a path or picks via the "All files" pattern can't bypass the filter.
/// </summary>
public static class FilePicker
{
    public static IReadOnlyList<string> PickFiles(string acceptedExtensions, string? title = null)
    {
        var extensions = ParseExtensions(acceptedExtensions);
        if (extensions.Count == 0) return [];

        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = BuildFilter(extensions),
            Title = title,
        };

        if (dialog.ShowDialog() != true) return [];

        var validated = new List<string>(dialog.FileNames.Length);
        foreach (var path in dialog.FileNames)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext) && extensions.Contains(ext))
                validated.Add(path);
        }
        return validated;
    }

    private static string BuildFilter(HashSet<string> extensions)
    {
        var patterns = string.Join(";", extensions.Select(e => $"*{e}"));
        return $"Supported files ({patterns})|{patterns}";
    }

    private static HashSet<string> ParseExtensions(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part.StartsWith('.') ? part : "." + part);
        }
        return set;
    }
}
