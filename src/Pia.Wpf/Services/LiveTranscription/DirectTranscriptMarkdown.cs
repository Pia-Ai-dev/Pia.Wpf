using System.Globalization;
using System.Text;
using Pia.Converters;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

/// <summary>
/// Renders a saved direct-transcription session as Markdown: a YAML front-matter block
/// (schema, start/end, speakers, voice statistics) followed by the transcript body, plus a
/// front-matter-free variant of the same body for the "summarize with assistant" prompt.
/// Pure and synchronous — no I/O. Deliberately takes no <c>ILogger</c>: this method's return
/// value IS transcript text, so it must never itself log anything; callers own that discipline.
/// </summary>
public static class DirectTranscriptMarkdown
{
    public const string Schema = "pia-direct-transcript/v1";

    /// <summary>
    /// Renders the full saved-file Markdown: YAML front matter (schema/start/end/speakers/
    /// voiceStats) followed by <see cref="RenderBody"/>. <paramref name="sessionEnd"/> is
    /// emitted as given even when it is earlier than <paramref name="sessionStart"/> — this
    /// renderer never "fixes" caller data, it only formats it.
    /// </summary>
    public static string Render(
        string title,
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd,
        IReadOnlyList<TranscriptBubble> bubbles,
        IReadOnlyList<SpeakerVoiceStats> voiceStats,
        string? counterpartName)
    {
        ArgumentNullException.ThrowIfNull(bubbles);
        ArgumentNullException.ThrowIfNull(voiceStats);

        var sb = new StringBuilder();
        AppendFrontMatter(sb, sessionStart, sessionEnd, bubbles, voiceStats, counterpartName);
        sb.Append(RenderBody(title, bubbles, counterpartName));
        return sb.ToString();
    }

    /// <summary>
    /// Renders the transcript body only (heading + per-bubble blocks), with no YAML front
    /// matter — used for the "summarize with assistant" prompt, which has no session object to
    /// draw a start/end timestamp from. The heading date is therefore derived from the first
    /// bubble's <see cref="TranscriptBubble.StartTimestamp"/> (the practical session start),
    /// and is omitted entirely when there are no bubbles.
    /// </summary>
    public static string RenderBody(
        string title,
        IReadOnlyList<TranscriptBubble> bubbles,
        string? counterpartName)
    {
        ArgumentNullException.ThrowIfNull(bubbles);

        var sb = new StringBuilder();

        sb.Append("# ").Append(title);
        if (bubbles.Count > 0)
        {
            var headingTimestamp = bubbles[0].StartTimestamp;
            sb.Append(" — ").Append(headingTimestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }
        sb.Append('\n').Append('\n');

        foreach (var bubble in bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel, counterpartName);
            sb.Append("**").Append(label).Append("** _")
              .Append(bubble.StartTimestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (bubble.EndTimestamp != bubble.StartTimestamp)
            {
                sb.Append('–').Append(bubble.EndTimestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }
            sb.Append('_').Append('\n').Append('\n');
            sb.Append(bubble.Text).Append('\n').Append('\n');
        }

        return sb.ToString();
    }

    private static void AppendFrontMatter(
        StringBuilder sb,
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd,
        IReadOnlyList<TranscriptBubble> bubbles,
        IReadOnlyList<SpeakerVoiceStats> voiceStats,
        string? counterpartName)
    {
        sb.Append("---\n");
        sb.Append("schema: ").Append(Schema).Append('\n');
        sb.Append("start: ").Append(sessionStart.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("end: ").Append(sessionEnd.ToString("O", CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("speakers:\n");
        foreach (var label in ResolveDeduplicatedSpeakers(bubbles, counterpartName))
        {
            sb.Append("  - ").Append(YamlScalar(label)).Append('\n');
        }

        sb.Append("voiceStats:\n");
        foreach (var stat in voiceStats)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(stat.Speaker, stat.SpeakerLabel, counterpartName);
            sb.Append("  - speaker: ").Append(YamlScalar(label)).Append('\n');
            sb.Append("    utterances: ").Append(stat.UtteranceCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("    totalSeconds: ").Append(stat.TotalSpeechSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("    meanSeconds: ").Append(stat.MeanUtteranceSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("    sharePercent: ").Append((stat.ShareOfMeasuredSpeech * 100.0).ToString("F1", CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("---\n");
    }

    private static List<string> ResolveDeduplicatedSpeakers(IReadOnlyList<TranscriptBubble> bubbles, string? counterpartName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var bubble in bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel, counterpartName);
            if (seen.Add(label)) ordered.Add(label);
        }
        return ordered;
    }

    /// <summary>
    /// Quotes a YAML scalar when it contains a character that would otherwise change the
    /// document's structure (a colon, a hash, a leading dash, or a quote character), doubling
    /// any inner double quotes. A display label can be a user-supplied name, so this must hold
    /// even for adversarial input (e.g. a name containing ": " or one that starts with "- ").
    /// </summary>
    private static string YamlScalar(string value)
    {
        var needsQuoting = value.Length == 0
            || value.StartsWith('-')
            || value.Contains(':')
            || value.Contains('#')
            || value.Contains('"')
            || value.Contains('\'');

        if (!needsQuoting) return value;

        var escaped = value.Replace("\"", "\"\"");
        return "\"" + escaped + "\"";
    }
}
