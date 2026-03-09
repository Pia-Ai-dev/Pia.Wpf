using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pia.Models;

namespace Pia.Helpers;

public static partial class JsonHelper
{
    public static string FormatJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch
        {
            return json;
        }
    }

    public static List<ActionCardDetail> ParseToDetails(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
            {
                var details = new List<ActionCardDetail>();
                foreach (var kvp in obj)
                {
                    var label = KeyToTitleCase(kvp.Key);
                    var value = kvp.Value?.ToJsonString() is { } raw
                        ? raw.Trim('"')
                        : string.Empty;
                    details.Add(new ActionCardDetail(label, value));
                }
                return details;
            }
        }
        catch
        {
            // Fallback below
        }

        return [new ActionCardDetail("Value", json)];
    }

    public static List<ActionCardDetail> ParseKeyValueText(string text)
    {
        var details = new List<ActionCardDetail>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx > 0)
            {
                var label = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim();
                details.Add(new ActionCardDetail(label, value));
            }
            else
            {
                details.Add(new ActionCardDetail("Info", line.Trim()));
            }
        }
        return details;
    }

    private static string KeyToTitleCase(string key)
    {
        // Convert snake_case or camelCase to Title Case
        var spaced = SnakeCaseRegex().Replace(key, " ");
        spaced = CamelCaseRegex().Replace(spaced, " $1");
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    [GeneratedRegex("[_]")]
    private static partial Regex SnakeCaseRegex();

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex CamelCaseRegex();
}
