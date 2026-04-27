using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pia.Converters;
using Pia.Models;

namespace Pia.Services.LiveTranscription;

public sealed record MeetingFrontMatter(string? Date, IReadOnlyList<string> Speakers, string? OriginalFilename);

/// <summary>
/// Renders a list of <see cref="TranscriptBubble"/>s into Markdown with a YAML front-matter
/// block (schema, start/end, speakers, originalFilename) and parses that front-matter back
/// out of an existing transcript. The format is durable: <c>schema: pia-meeting-transcript/v1</c>
/// gates future changes.
/// </summary>
public static class MeetingTranscriptWriter
{
    public const string Schema = "pia-meeting-transcript/v1";

    public static string Render(
        IReadOnlyList<TranscriptBubble> bubbles,
        DateTimeOffset sessionStart,
        string originalFilename,
        string title)
    {
        var sb = new StringBuilder();

        var speakers = ResolveSpeakers(bubbles);
        var end = bubbles.Count > 0
            ? bubbles.Max(b => b.EndTimestamp)
            : sessionStart;

        sb.Append("---\n");
        sb.Append("schema: ").Append(Schema).Append('\n');
        sb.Append("start: ").Append(sessionStart.ToString("yyyy-MM-ddTHH:mm:sszzz")).Append('\n');
        sb.Append("end:   ").Append(end.ToString("yyyy-MM-ddTHH:mm:sszzz")).Append('\n');
        sb.Append("speakers:\n");
        foreach (var s in speakers) sb.Append("  - ").Append(s).Append('\n');
        sb.Append("originalFilename: ").Append(originalFilename).Append('\n');
        sb.Append("---\n");

        sb.Append("# ").Append(title).Append(" — ")
          .Append(sessionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm")).Append('\n');
        sb.Append('\n');

        foreach (var bubble in bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(bubble.Speaker, bubble.SpeakerLabel);
            sb.Append("**").Append(label).Append("** _")
              .Append(bubble.StartTimestamp.LocalDateTime.ToString("HH:mm:ss"));
            if (bubble.EndTimestamp != bubble.StartTimestamp)
                sb.Append('–').Append(bubble.EndTimestamp.LocalDateTime.ToString("HH:mm:ss"));
            sb.Append('_').Append('\n').Append('\n');
            sb.Append(bubble.Text).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static List<string> ResolveSpeakers(IReadOnlyList<TranscriptBubble> bubbles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var b in bubbles)
        {
            var label = SpeakerToDisplayNameConverter.Resolve(b.Speaker, b.SpeakerLabel);
            if (seen.Add(label)) ordered.Add(label);
        }
        return ordered;
    }

    private static readonly Regex FrontMatterRegex =
        new("^---\\s*\\r?\\n(?<body>[\\s\\S]*?)\\r?\\n---\\s*\\r?\\n",
            RegexOptions.Compiled);

    public static string StripFrontMatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        var m = FrontMatterRegex.Match(markdown);
        return m.Success ? markdown.Substring(m.Length) : markdown;
    }

    public static bool TryParseFrontMatter(string markdown, out MeetingFrontMatter? frontMatter)
    {
        frontMatter = null;
        if (string.IsNullOrEmpty(markdown)) return false;

        var m = FrontMatterRegex.Match(markdown);
        if (!m.Success) return false;

        var body = m.Groups["body"].Value;
        string? date = null;
        string? originalFilename = null;
        var speakers = new List<string>();

        var lines = body.Split('\n');
        var inSpeakers = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (inSpeakers)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("- "))
                {
                    speakers.Add(trimmed.Substring(2).Trim());
                    continue;
                }
                inSpeakers = false;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = line.Substring(0, colonIdx).Trim();
            var value = line.Substring(colonIdx + 1).Trim();

            switch (key)
            {
                case "start":
                    if (DateTimeOffset.TryParse(value, out var dto))
                        date = dto.ToString("yyyy-MM-dd");
                    break;
                case "originalFilename":
                    originalFilename = value;
                    break;
                case "speakers":
                    inSpeakers = true;
                    break;
            }
        }

        frontMatter = new MeetingFrontMatter(date, speakers, originalFilename);
        return true;
    }
}
