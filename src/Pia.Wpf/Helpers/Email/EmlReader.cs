using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Pia.Helpers.Email;

internal static partial class EmlReader
{
    private const int MaxMultipartDepth = 10;

    private sealed record MimePart(IReadOnlyList<string> Headers, string Content);

    private readonly record struct BodyCandidate(string Text, bool FromHtml);

    internal static EmailMessage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    internal static EmailMessage Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        // Latin1 is one char per byte, so each part's original bytes survive to its own charset decode.
        return ParseRaw(Encoding.Latin1.GetString(buffer.ToArray()));
    }

    internal static EmailMessage Parse(string messageText) =>
        ParseRaw(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(messageText ?? string.Empty)));

    private static EmailMessage ParseRaw(string raw)
    {
        var message = ParsePart(raw);
        var attachments = new List<string>();
        var body = SelectBody(message, attachments, 0);

        return new EmailMessage(
            MimeDecoding.NormalizeSubject(MimeDecoding.DecodeEncodedWords(Header(message, "Subject"))),
            ParseAddresses(Header(message, "From")).FirstOrDefault(),
            ParseAddresses(Header(message, "To")),
            ParseAddresses(Header(message, "Cc")),
            MimeDecoding.TryParseDate(Header(message, "Date"), out var date) ? date : null,
            body?.Text ?? string.Empty,
            attachments,
            body?.FromHtml ?? false);
    }

    private static MimePart ParsePart(string raw)
    {
        var lineStart = 0;
        while (lineStart < raw.Length)
        {
            var lineEnd = LineEnd(raw, lineStart);
            if (lineEnd == lineStart)
            {
                return new MimePart(MimeDecoding.UnfoldHeaderLines(raw[..lineStart]), raw[NextLineStart(raw, lineEnd)..]);
            }

            if (lineEnd >= raw.Length) break;
            lineStart = NextLineStart(raw, lineEnd);
        }

        return new MimePart(MimeDecoding.UnfoldHeaderLines(raw), string.Empty);
    }

    private static BodyCandidate? SelectBody(MimePart part, List<string> attachments, int depth)
    {
        var contentType = Header(part, "Content-Type");
        var mediaType = MediaType(contentType);

        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = depth < MaxMultipartDepth ? Parameter(contentType, "boundary") : null;
            var children = string.IsNullOrEmpty(boundary) ? [] : SplitMultipart(part.Content, boundary);
            return children.Count > 0
                ? SelectFromChildren(children, mediaType, attachments, depth + 1)
                : PlainCandidate(part, contentType);
        }

        if (depth > 0 && TryTakeAttachmentName(part, attachments)) return null;

        return mediaType switch
        {
            "text/html" => HtmlCandidate(part, contentType),
            "" or "text/plain" => PlainCandidate(part, contentType),
            _ => null,
        };
    }

    private static BodyCandidate? SelectFromChildren(
        List<MimePart> children,
        string mediaType,
        List<string> attachments,
        int depth)
    {
        var candidates = new List<BodyCandidate>();
        foreach (var child in children)
        {
            if (SelectBody(child, attachments, depth) is { } candidate) candidates.Add(candidate);
        }

        if (candidates.Count == 0) return null;
        if (!string.Equals(mediaType, "multipart/alternative", StringComparison.Ordinal)) return candidates[0];

        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            if (!candidates[i].FromHtml) return candidates[i];
        }

        return candidates[^1];
    }

    private static List<MimePart> SplitMultipart(string content, string boundary)
    {
        var parts = new List<MimePart>();
        var delimiter = "--" + boundary;
        var partStart = -1;
        var lineStart = 0;

        while (true)
        {
            var lineEnd = LineEnd(content, lineStart);
            if (IsDelimiterLine(content, lineStart, lineEnd, delimiter, out var closing))
            {
                if (partStart >= 0) parts.Add(ParsePart(TrimLineBreak(content, partStart, lineStart)));
                if (closing) return parts;

                partStart = NextLineStart(content, lineEnd);
            }

            if (lineEnd >= content.Length) break;
            lineStart = NextLineStart(content, lineEnd);
        }

        // An unterminated final part ends at EOF.
        if (partStart >= 0) parts.Add(ParsePart(TrimLineBreak(content, partStart, content.Length)));

        return parts;
    }

    private static bool IsDelimiterLine(string content, int lineStart, int lineEnd, string delimiter, out bool closing)
    {
        closing = false;
        var length = lineEnd - lineStart;
        if (length < delimiter.Length) return false;
        if (!content.AsSpan(lineStart, delimiter.Length).Equals(delimiter, StringComparison.Ordinal)) return false;

        var rest = content.AsSpan(lineStart + delimiter.Length, length - delimiter.Length);
        if (rest.StartsWith("--", StringComparison.Ordinal))
        {
            closing = true;
            rest = rest[2..];
        }

        return rest.IsWhiteSpace();
    }

    private static bool TryTakeAttachmentName(MimePart part, List<string> attachments)
    {
        var disposition = Header(part, "Content-Disposition");
        var raw = Parameter(disposition, "filename") ?? Parameter(Header(part, "Content-Type"), "name");
        // Recovered here rather than on the whole field: these headers also carry the boundary, which
        // has to stay in Latin1 space to keep matching the part content.
        if (raw is not null) raw = MimeDecoding.RecoverRawUtf8(raw);
        var name = MimeDecoding.NormalizeSubject(MimeDecoding.DecodeEncodedWords(raw));

        if (name is null && !string.Equals(MediaType(disposition), "attachment", StringComparison.Ordinal))
        {
            return false;
        }

        if (name is not null) attachments.Add(name);
        return true;
    }

    private static BodyCandidate? PlainCandidate(MimePart part, string contentType)
    {
        var text = MimeDecoding.NormalizeBody(DecodeContentText(part, contentType));
        return text.Length == 0 ? null : new BodyCandidate(text, false);
    }

    private static BodyCandidate? HtmlCandidate(MimePart part, string contentType)
    {
        var text = MimeDecoding.HtmlToText(DecodeContentText(part, contentType));
        return text.Length == 0 ? null : new BodyCandidate(text, true);
    }

    private static string DecodeContentText(MimePart part, string contentType)
    {
        var transferEncoding = MediaType(Header(part, "Content-Transfer-Encoding"));
        var bytes = transferEncoding switch
        {
            "quoted-printable" => MimeDecoding.DecodeQuotedPrintable(part.Content),
            "base64" => MimeDecoding.DecodeBase64(part.Content),
            _ => MimeDecoding.GetRawBytes(part.Content),
        };

        return MimeDecoding.DecodeText(bytes, MimeDecoding.GetEncoding(Parameter(contentType, "charset")));
    }

    private static List<string> ParseAddresses(string? value)
    {
        var addresses = new List<string>();
        foreach (var entry in SplitAddressList(value))
        {
            if (FormatAddress(entry) is { Length: > 0 } address) addresses.Add(address);
        }

        return addresses;
    }

    private static string? FormatAddress(string entry)
    {
        var decoded = MimeDecoding.DecodeEncodedWords(entry).Trim();
        var open = decoded.LastIndexOf('<');
        var close = decoded.LastIndexOf('>');

        return open >= 0 && close > open
            ? MimeDecoding.FormatAddress(decoded[..open], decoded[(open + 1)..close])
            : MimeDecoding.FormatAddress(null, decoded);
    }

    private static IEnumerable<string> SplitAddressList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        var start = 0;
        var quoted = false;
        var angleDepth = 0;

        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '<' when !quoted:
                    angleDepth++;
                    break;
                case '>' when !quoted:
                    angleDepth = Math.Max(0, angleDepth - 1);
                    break;
                case ',' when !quoted && angleDepth == 0:
                    if (!string.IsNullOrWhiteSpace(value[start..i])) yield return value[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(value[start..])) yield return value[start..];
    }

    private static string Header(MimePart part, string name) =>
        MimeDecoding.TryFindHeader(part.Headers, name, out var value) ? value : string.Empty;

    private static string MediaType(string? headerValue)
    {
        if (string.IsNullOrEmpty(headerValue)) return string.Empty;

        var semicolon = headerValue.IndexOf(';');
        return (semicolon < 0 ? headerValue : headerValue[..semicolon]).Trim().ToLowerInvariant();
    }

    private static string? Parameter(string? headerValue, string name)
    {
        if (string.IsNullOrEmpty(headerValue)) return null;

        foreach (Match match in ParameterRegex().Matches(headerValue))
        {
            // An RFC 2231 continuation is named "filename*0*", so it never matches and is skipped.
            if (!string.Equals(match.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)) continue;

            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value.Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }

    private static int LineEnd(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n') return i;
        }

        return text.Length;
    }

    private static int NextLineStart(string text, int lineEnd)
    {
        if (lineEnd >= text.Length) return text.Length;
        return text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? lineEnd + 2 : lineEnd + 1;
    }

    private static string TrimLineBreak(string content, int start, int endExclusive)
    {
        var end = endExclusive;
        if (end > start && content[end - 1] == '\n') end--;
        if (end > start && content[end - 1] == '\r') end--;
        return end > start ? content[start..end] : string.Empty;
    }

    [GeneratedRegex(@";\s*([^\s=;]+)\s*=\s*(?:""([^""]*)""|([^;]*))")]
    private static partial Regex ParameterRegex();
}
