using System.Globalization;
using Pia.Services.LiveTranscription;
using Pia.Services.Wiki;

namespace Pia.Services.Consent;

/// <summary>
/// The consent block of a saved transcript's front matter. Top-level keys only, so a vault scan can read it
/// without a YAML parser and a revocation can be written back into a kept note line by line.
/// </summary>
public static class ConsentFrontMatter
{
    public const string Schema = "pia-consent-record/v1";

    private const string RecordKey = "consentRecord";
    private const string SessionsKey = "consentSessions";
    private const string NoticeVersionKey = "consentNoticeVersion";
    private const string NoticePurposesKey = "consentNoticePurposes";
    private const string NoticeLanguageKey = "consentNoticeLanguage";
    private const string ConsentsKey = "consents";
    private const string HostAcknowledgedKey = "hostAcknowledgedAt";

    private const string Delimiter = "---";

    private static readonly string[] OwnedKeys =
    [
        RecordKey, SessionsKey, NoticeVersionKey, NoticePurposesKey, NoticeLanguageKey, ConsentsKey, HostAcknowledgedKey,
    ];

    /// <summary>The block's lines without line endings; none when the record proves nothing.</summary>
    public static IReadOnlyList<string> Render(ConsentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.IsEmpty) return [];

        var lines = new List<string>
        {
            $"{RecordKey}: {Schema}",
            $"{SessionsKey}: {FlowList(record.SessionIds)}",
            $"{NoticeVersionKey}: {record.NoticeVersion.ToString(CultureInfo.InvariantCulture)}",
            $"{NoticePurposesKey}: {FlowList(record.NoticePurposes)}",
            $"{NoticeLanguageKey}: {YamlText.Scalar(record.NoticeLanguage)}",
        };

        if (record.HostAcknowledgedAt is { } acknowledgedAt)
            lines.Add($"{HostAcknowledgedKey}: {Timestamp(acknowledgedAt)}");

        if (record.Consents.Count > 0)
        {
            lines.Add($"{ConsentsKey}:");
            foreach (var consent in record.Consents)
            {
                var map = $"label: {YamlText.Scalar(consent.Label)}, grantedAt: {Timestamp(consent.GrantedAt)}";
                if (consent.RevokedAt is { } revokedAt)
                    map += $", revokedAt: {Timestamp(revokedAt)}";
                lines.Add($"  - {{{map}}}");
            }
        }

        return lines;
    }

    /// <summary>
    /// Swaps the note's consent block for <paramref name="record"/>'s, leaving every other line as it was. A
    /// block the user deleted is added back before the closing delimiter; a note without front matter gets one.
    /// </summary>
    public static string ReplaceConsents(string noteText, ConsentRecord record)
    {
        ArgumentNullException.ThrowIfNull(noteText);
        var rendered = Render(record);

        var bom = noteText.StartsWith('﻿') ? "﻿" : string.Empty;
        var text = noteText[bom.Length..];
        var carriageReturn = text.Contains("\r\n", StringComparison.Ordinal) ? "\r" : string.Empty;
        var lines = text.Split('\n');

        var close = FindClosingDelimiter(lines);
        if (close < 0)
        {
            if (rendered.Count == 0) return noteText;

            // An opening delimiter without a closing one is not ours to repair.
            if (IsDelimiter(lines[0])) return noteText;

            var newline = carriageReturn + "\n";
            var block = string.Join(newline, rendered.Prepend(Delimiter).Append(Delimiter));
            return bom + block + newline + text;
        }

        var kept = new List<string> { lines[0] };
        var insertAt = -1;
        for (var i = 1; i < close; i++)
        {
            if (!IsOwnedKeyLine(lines[i]))
            {
                kept.Add(lines[i]);
                continue;
            }

            if (insertAt < 0) insertAt = kept.Count;
            while (i + 1 < close && IsContinuation(lines[i + 1])) i++;
        }

        kept.InsertRange(insertAt < 0 ? kept.Count : insertAt, rendered.Select(line => line + carriageReturn));
        kept.AddRange(lines[close..]);
        return bom + string.Join('\n', kept);
    }

    /// <summary>The session ids a note's front matter names; empty when it names none or has no front matter.</summary>
    public static IReadOnlyList<string> ReadSessions(string? noteText)
    {
        if (string.IsNullOrEmpty(noteText)) return [];

        var lines = noteText.TrimStart('﻿').Split('\n');
        var close = FindClosingDelimiter(lines);
        for (var i = 1; i < close; i++)
        {
            if (!IsKeyLine(lines[i], SessionsKey)) continue;

            var value = lines[i].TrimEnd('\r')[(SessionsKey.Length + 1)..].Trim();
            if (value.Length == 0)
            {
                // An editor may have rewritten the flow list as a block list.
                var items = new List<string>();
                for (var j = i + 1; j < close && IsContinuation(lines[j]); j++)
                    items.Add(lines[j].TrimEnd('\r'));
                value = string.Join('\n', items);
            }

            return SourcesProvenance.ParseFlowList(value)
                .Select(Unquote)
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return [];
    }

    private static string FlowList(IEnumerable<string> values)
        => "[" + string.Join(", ", values.Select(YamlText.Scalar)) + "]";

    private static string Timestamp(DateTimeOffset value)
        => YamlText.Scalar(value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));

    private static int FindClosingDelimiter(string[] lines)
    {
        if (lines.Length == 0 || !IsDelimiter(lines[0])) return -1;

        for (var i = 1; i < lines.Length; i++)
        {
            if (IsDelimiter(lines[i])) return i;
        }
        return -1;
    }

    private static bool IsDelimiter(string line) => line.TrimEnd() == Delimiter;

    private static bool IsOwnedKeyLine(string line) => OwnedKeys.Any(key => IsKeyLine(line, key));

    private static bool IsKeyLine(string line, string key)
        => line.StartsWith(key, StringComparison.Ordinal)
           && line.Length > key.Length
           && line[key.Length] == ':';

    private static bool IsContinuation(string line)
    {
        var content = line.TrimEnd('\r');
        return content.Length > 0
               && (content[0] is ' ' or '\t' || content == "-" || content.StartsWith("- ", StringComparison.Ordinal));
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
