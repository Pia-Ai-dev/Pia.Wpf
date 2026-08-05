using System.Text.Json;

namespace Pia.Services;

/// <summary>Reader+writer for a run's accumulated clarification answers, stored oldest-first as a JSON string array in <c>AgentRuns.ClarificationsJson</c>. Sensitive user content — log the count freely, the text only via <c>SensitiveDebug</c>. A malformed or missing document reads as empty rather than throwing.</summary>
internal static class RunClarifications
{
    /// <summary>Cap on one answer; matches <c>RunContext.MaxNudgeChars</c>, since the same text also arrives as the resume's nudge.</summary>
    internal const int MaxAnswerChars = 1000;

    /// <summary>Cap on how many answers are kept for the prompt — not a cap on how many times a run may ask. Oldest answers are dropped once exceeded.</summary>
    internal const int MaxAnswers = 8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The recorded answers oldest-first, or EMPTY when the run carries no readable document.</summary>
    internal static IReadOnlyList<string> Read(string? clarificationsJson)
    {
        if (string.IsNullOrWhiteSpace(clarificationsJson))
            return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(clarificationsJson, Json);
            if (parsed is null)
                return [];

            // Drop blank elements — an empty bullet would read as a clarification the user never gave.
            return parsed.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Returns the appended document, or null when there is nothing to append — a blank answer is the normal case for a resume, not an error.</summary>
    internal static string? Append(string? clarificationsJson, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        // Flatten CR/LF/TAB before storing — this text is later fenced into a prompt, and a newline could forge extra lines.
        var flat = answer.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (flat.Length > MaxAnswerChars)
            flat = flat[..MaxAnswerChars] + "…";

        var kept = new List<string>(Read(clarificationsJson)) { flat };
        if (kept.Count > MaxAnswers)
            kept.RemoveRange(0, kept.Count - MaxAnswers); // drop the OLDEST — the newest answer is the one just given

        return JsonSerializer.Serialize(kept, Json);
    }
}
