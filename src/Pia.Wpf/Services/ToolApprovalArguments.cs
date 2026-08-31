using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pia.Services;

/// <summary>
/// What a parked tool call asked to act on, rendered for the person who has to approve it. String arguments
/// only — a path is what makes "allow delete_file" a decision rather than a guess. This is user content: it is
/// shown on the Continue card and in the run panel, and it is never logged.
/// </summary>
internal static class ToolApprovalArguments
{
    private const int MaxValueChars = 120;
    private const int MaxTotalChars = 400;

    /// <summary>One call's string arguments as <c>key=value</c> pairs, or null when it carried none.</summary>
    internal static string? Describe(FunctionCallContent call)
    {
        if (call.Arguments is null || call.Arguments.Count == 0)
            return null;

        var parts = new List<string>(call.Arguments.Count);
        foreach (var (key, value) in call.Arguments)
        {
            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text))
                parts.Add($"{key}={Cap(text, MaxValueChars)}");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Every parked call's description as one envelope-sized line, or null when there is nothing to show.</summary>
    internal static string? Join(IReadOnlyList<string> descriptions) =>
        descriptions.Count == 0 ? null : Cap(string.Join(", ", descriptions), MaxTotalChars);

    internal const int MaxDetailValueChars = 4000;
    internal const int MaxDetailTotalChars = 8000;

    /// <summary>The rendered call, and whether the display caps cut anything the store still holds.</summary>
    internal readonly record struct Detail(string Text, bool Shortened);

    /// <summary>
    /// The persisted arguments object rendered one <c>key=value</c> per line for the disclosure surface, or null
    /// when there is nothing to show. Malformed or non-object input is swallowed, as the envelope readers do.
    /// </summary>
    internal static Detail? DescribeDetail(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var lines = new List<string>();
            var budget = MaxDetailTotalChars;
            var shortened = false;

            foreach (var member in doc.RootElement.EnumerateObject())
            {
                // Non-string values are rendered rather than dropped, or a call with a numeric or array
                // argument would read as partial here exactly as it does on the collapsed line.
                var raw = member.Value.ValueKind == JsonValueKind.String
                    ? member.Value.GetString() ?? string.Empty
                    : member.Value.GetRawText();
                // The RAW length: Cap returns max+1 chars, so a raw of exactly max+1 would read as un-shortened.
                shortened |= raw.Length > MaxDetailValueChars;

                var line = $"{member.Name}={Cap(raw, MaxDetailValueChars)}";
                if (line.Length > budget)
                {
                    // Naming the argument costs nothing against the budget, so no argument is ever silently
                    // dropped and a short decisive one after a huge one still renders in full.
                    lines.Add($"{member.Name}=…");
                    shortened = true;
                    continue;
                }

                lines.Add(line);
                budget -= line.Length;
            }

            return lines.Count == 0 ? null : new Detail(string.Join('\n', lines), shortened);
        }
    }

    private static string Cap(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
