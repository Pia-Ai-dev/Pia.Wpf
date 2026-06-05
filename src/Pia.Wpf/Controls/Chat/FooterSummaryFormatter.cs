using System.Globalization;
using Pia.Models;

namespace Pia.Controls.Chat;

/// <summary>
/// Builds the assistant-message footer text: token count, persona name, model — joined by " · ",
/// omitting any part that is absent. Pure so it can be unit-tested without WPF.
/// </summary>
internal static class FooterSummaryFormatter
{
    public static string Compose(AnswerStats? stats, string? personaName)
    {
        var parts = new List<string>(3);
        if (stats is not null)
            parts.Add(stats.Tokens.ToString("N0", CultureInfo.InvariantCulture) + " Tokens");
        if (!string.IsNullOrWhiteSpace(personaName))
            parts.Add(personaName);
        if (stats is not null && !string.IsNullOrWhiteSpace(stats.Model))
            parts.Add(stats.Model);
        return string.Join(" · ", parts);
    }
}
