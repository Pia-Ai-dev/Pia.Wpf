using System.IO;

namespace Pia.Services.Wiki;

/// <summary>A source whose front matter says <c>ingest: manual</c> is compiled only when someone asks.</summary>
public static class SourceIngestMarker
{
    public const string YamlLine = "ingest: manual";

    // Front matter sits at the top, so a bounded read of its opening lines decides it.
    private const int MaxFrontMatterLines = 64;

    public static bool IsHeld(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine()?.Trim() != "---") return false;

            for (var i = 0; i < MaxFrontMatterLines && reader.ReadLine() is { } line; i++)
            {
                var trimmed = line.Trim();
                if (trimmed == "---") return false;
                if (trimmed == YamlLine) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }
}
