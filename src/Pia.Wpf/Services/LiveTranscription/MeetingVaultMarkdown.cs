using System.Globalization;
using System.Text;
using Pia.Infrastructure.Vault;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// The details a user attaches to a meeting when saving it into the vault, plus the session facts the
/// overlay already knows. Optional members are omitted from the rendered frontmatter when empty.
/// </summary>
public sealed record MeetingVaultMetadata(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Source,
    IReadOnlyCollection<string> Attendees,
    IReadOnlyCollection<string> Tags,
    string? Project,
    string? Notes);

/// <summary>
/// Renders a meeting as a vault <c>sources/</c> document: a YAML metadata block followed by the
/// transcript body. Pure and synchronous, and — like <see cref="DirectTranscriptMarkdown"/> — it never
/// logs, because its return value IS transcript text.
///
/// <para>The block deliberately carries none of the vault format's <c>pia: managed</c> ownership keys:
/// <c>sources/</c> is the RAW layer, and this metadata exists for the ingest compiler (which reads the
/// whole file as text) and for the user's own editor.</para>
/// </summary>
public static class MeetingVaultMarkdown
{
    public const string Schema = "pia-meeting/v1";

    public static string Render(MeetingVaultMetadata meta, string body)
    {
        ArgumentNullException.ThrowIfNull(meta);

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("schema: ").Append(Schema).Append('\n');
        sb.Append(AiContentMarking.YamlLines());
        sb.Append("title: ").Append(YamlText.Scalar(meta.Title?.Trim())).Append('\n');
        sb.Append("date: ").Append(meta.Start.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("start: ").Append(meta.Start.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("end: ").Append(meta.End.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("source: ").Append(YamlText.Scalar(meta.Source)).Append('\n');
        AppendFlowList(sb, "attendees", meta.Attendees);
        AppendFlowList(sb, "tags", meta.Tags);

        var project = meta.Project?.Trim();
        if (!string.IsNullOrEmpty(project))
        {
            sb.Append("project: ").Append(YamlText.Scalar(project)).Append('\n');
        }

        AppendNotes(sb, meta.Notes);
        sb.Append("---\n");
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>Folder meetings are saved under, so transcripts do not sit loose among every other raw source.</summary>
    public const string TranscriptsFolder = "sources/transcripts";

    /// <summary>
    /// The vault-relative ref a meeting is saved under. The slug is the normative §6 algorithm, so a
    /// title with punctuation or diacritics still yields a portable filename.
    /// </summary>
    public static string BuildReference(DateTimeOffset start, string? title)
    {
        var stamp = start.LocalDateTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"{TranscriptsFolder}/meeting-{stamp}-{VaultSlug.Slugify(title ?? string.Empty)}.md";
    }

    /// <summary>Splits a comma-separated form field into trimmed, non-empty entries.</summary>
    public static IReadOnlyList<string> SplitList(string? value)
        => (value ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void AppendFlowList(StringBuilder sb, string key, IReadOnlyCollection<string>? values)
    {
        var entries = (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => YamlText.Scalar(v.Trim()))
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        sb.Append(key).Append(": [").AppendJoin(", ", entries).Append("]\n");
    }

    private static void AppendNotes(StringBuilder sb, string? notes)
    {
        var text = (notes ?? string.Empty).ReplaceLineEndings("\n").Trim();
        if (text.Length == 0)
        {
            return;
        }

        // Literal block scalar so a multi-paragraph note survives without escaping.
        sb.Append("notes: |-\n");
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            sb.Append(trimmed.Length == 0 ? string.Empty : "  " + trimmed).Append('\n');
        }
    }
}
