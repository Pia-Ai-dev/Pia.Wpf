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

    private static string Cap(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
