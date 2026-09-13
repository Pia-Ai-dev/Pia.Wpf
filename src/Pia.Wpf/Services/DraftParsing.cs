using System.Text.Json;
using Pia.Models;

namespace Pia.Services;

/// <summary>
/// Turns a model's reply into a draft record. Shared so the one-shot generators and the Advanced
/// Creation interview's closing turn agree on the shape they accept — an envelope that only the
/// interview understands would still have to end in one of these.
/// </summary>
internal static class DraftParsing
{
    /// <summary>Extracts the first {...} object, tolerating code fences and surrounding prose.</summary>
    public static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }

    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static PersonaDraft ParsePersonaDraft(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
            return RawPrompt(raw);

        try
        {
            var dto = JsonSerializer.Deserialize<PersonaDraftDto>(json, Options);
            if (dto is null)
                return RawPrompt(raw);

            return new PersonaDraft(
                Clean(dto.Name),
                Clean(dto.Tagline),
                Clean(dto.SystemPrompt),
                Clean(dto.Guardrails),
                Clean(dto.OutputFormat),
                Clean(dto.Archetype),
                Clean(dto.Emoji),
                Clean(dto.AccentColor),
                dto.Expertise?.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList());
        }
        catch (JsonException)
        {
            // Model didn't return valid JSON — fall back to using the raw text as the system prompt.
            return RawPrompt(raw);
        }

        static PersonaDraft RawPrompt(string text) =>
            new(null, null, text.Trim(), null, null, null, null, null, null);
    }

    public static RoutineDraft ParseRoutineDraft(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
            return RawGoal(raw);

        try
        {
            var dto = JsonSerializer.Deserialize<RoutineDraftDto>(json, Options);
            if (dto is null)
                return RawGoal(raw);

            return new RoutineDraft(
                Clean(dto.Name),
                Clean(dto.Goal),
                Enum.TryParse<RecurrenceType>(dto.Recurrence, ignoreCase: true, out var recurrence) ? recurrence : null,
                Enum.TryParse<DayOfWeek>(dto.DayOfWeek, ignoreCase: true, out var day) ? day : null,
                TimeOnly.TryParseExact(dto.TimeOfDay, "HH\\:mm", out var time) ? time : null,
                Enum.TryParse<ReasoningEffort>(dto.Effort, ignoreCase: true, out var effort) ? effort : null,
                dto.NeedsWebSearch,
                dto.Tools?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList());
        }
        catch (JsonException)
        {
            return RawGoal(raw);
        }

        static RoutineDraft RawGoal(string text) =>
            new(null, text.Trim(), null, null, null, null, false, null);
    }

    public static TemplateDraft ParseTemplateDraft(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
            return new TemplateDraft(null, null, raw.Trim());

        try
        {
            var dto = JsonSerializer.Deserialize<TemplateDraftDto>(json, Options);
            if (dto is null)
                return new TemplateDraft(null, null, raw.Trim());

            // A JSON object that carries no prompt is worse than no JSON: the caller would fill the
            // editor with nothing. Fall back to the raw text, as the non-JSON path does.
            return new TemplateDraft(Clean(dto.Name), Clean(dto.Description), Clean(dto.Prompt) ?? raw.Trim());
        }
        catch (JsonException)
        {
            return new TemplateDraft(null, null, raw.Trim());
        }
    }

    private sealed class RoutineDraftDto
    {
        public string? Name { get; set; }
        public string? Goal { get; set; }
        public string? Recurrence { get; set; }
        public string? DayOfWeek { get; set; }
        public string? TimeOfDay { get; set; }
        public string? Effort { get; set; }
        public bool NeedsWebSearch { get; set; }
        public List<string>? Tools { get; set; }
    }

    private sealed class PersonaDraftDto
    {
        public string? Name { get; set; }
        public string? Tagline { get; set; }
        public string? SystemPrompt { get; set; }
        public string? Guardrails { get; set; }
        public string? OutputFormat { get; set; }
        public string? Archetype { get; set; }
        public string? Emoji { get; set; }
        public string? AccentColor { get; set; }
        public List<string>? Expertise { get; set; }
    }

    private sealed class TemplateDraftDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Prompt { get; set; }
    }
}
