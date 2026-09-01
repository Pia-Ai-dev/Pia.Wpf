using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pia.Helpers.Email;

internal static partial class MimeDecoding
{
    private const string BreakMarker = "\u0001";

    private static readonly Encoding Utf8Lenient = new UTF8Encoding(false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private static readonly Encoding Windows1252 = new Windows1252Encoding();

    private static readonly FrozenSet<string> RecoverableHeaders =
        new[] { "Subject", "From", "To", "Cc" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> NamedEntities =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'",
            ["nbsp"] = "\u00A0", ["shy"] = "\u00AD", ["ensp"] = " ", ["emsp"] = " ", ["thinsp"] = " ",
            ["zwnj"] = "\u200C", ["zwj"] = "\u200D",
            ["ndash"] = "\u2013", ["mdash"] = "\u2014", ["hellip"] = "\u2026", ["bull"] = "\u2022",
            ["middot"] = "\u00B7", ["dagger"] = "\u2020", ["Dagger"] = "\u2021", ["permil"] = "\u2030",
            ["laquo"] = "\u00AB", ["raquo"] = "\u00BB", ["lsaquo"] = "\u2039", ["rsaquo"] = "\u203A",
            ["ldquo"] = "\u201C", ["rdquo"] = "\u201D", ["lsquo"] = "\u2018", ["rsquo"] = "\u2019",
            ["sbquo"] = "\u201A", ["bdquo"] = "\u201E",
            ["copy"] = "\u00A9", ["reg"] = "\u00AE", ["trade"] = "\u2122",
            ["euro"] = "\u20AC", ["pound"] = "\u00A3", ["yen"] = "\u00A5", ["cent"] = "\u00A2",
            ["sect"] = "\u00A7", ["para"] = "\u00B6", ["deg"] = "\u00B0",
            ["plusmn"] = "\u00B1", ["times"] = "\u00D7", ["divide"] = "\u00F7",
            ["frac12"] = "\u00BD", ["frac14"] = "\u00BC", ["frac34"] = "\u00BE",
            ["sup2"] = "\u00B2", ["sup3"] = "\u00B3", ["micro"] = "\u00B5",
            ["szlig"] = "\u00DF",
            ["auml"] = "\u00E4", ["ouml"] = "\u00F6", ["uuml"] = "\u00FC",
            ["Auml"] = "\u00C4", ["Ouml"] = "\u00D6", ["Uuml"] = "\u00DC",
            ["agrave"] = "\u00E0", ["aacute"] = "\u00E1", ["acirc"] = "\u00E2", ["aring"] = "\u00E5",
            ["ccedil"] = "\u00E7", ["egrave"] = "\u00E8", ["eacute"] = "\u00E9", ["ecirc"] = "\u00EA",
            ["iacute"] = "\u00ED", ["ntilde"] = "\u00F1", ["oacute"] = "\u00F3", ["ocirc"] = "\u00F4",
            ["oslash"] = "\u00F8", ["ugrave"] = "\u00F9", ["uacute"] = "\u00FA",
            ["Agrave"] = "\u00C0", ["Aacute"] = "\u00C1", ["Ccedil"] = "\u00C7",
            ["Egrave"] = "\u00C8", ["Eacute"] = "\u00C9", ["Oslash"] = "\u00D8",
        }
        .ToFrozenDictionary(StringComparer.Ordinal);

    internal static IReadOnlyList<string> UnfoldHeaderLines(string? headerBlock)
    {
        var fields = new List<string>();
        if (string.IsNullOrEmpty(headerBlock)) return fields;

        foreach (var line in SplitLines(headerBlock))
        {
            if (line.Length == 0) continue;

            // The retained leading whitespace is what makes the encoded-word separator rule detectable.
            if (fields.Count > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                fields[^1] += line;
            }
            else
            {
                fields.Add(line);
            }
        }

        for (var i = 0; i < fields.Count; i++)
        {
            if (IsRecoverable(fields[i])) fields[i] = RecoverRawUtf8(fields[i]);
        }

        return fields;
    }

    internal static bool TryFindHeader(IReadOnlyList<string> unfoldedLines, string name, out string value)
    {
        foreach (var line in unfoldedLines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            value = line[(colon + 1)..].Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal static string StripHeaderComments(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : HeaderCommentRegex().Replace(value, string.Empty).Trim();

    // TryParse returns false while a trailing RFC 5322 comment such as "(UTC)" is still attached.
    internal static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        date = default;
        var cleaned = StripHeaderComments(value);
        return cleaned.Length > 0
            && DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static string DecodeEncodedWords(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        var result = new StringBuilder();
        var pending = new List<byte>();
        string? pendingCharset = null;
        var cursor = 0;
        var previousWasEncoded = false;

        void Flush()
        {
            if (pending.Count > 0)
            {
                result.Append(DecodeText(pending.ToArray(), GetEncoding(pendingCharset)));
                pending.Clear();
            }

            pendingCharset = null;
        }

        foreach (Match match in EncodedWordRegex().Matches(value))
        {
            var gap = value[cursor..match.Index];
            // RFC 2047: whitespace separating two adjacent encoded-words is not content and must vanish.
            var isSeparator = previousWasEncoded && gap.Length > 0 && gap.All(char.IsWhiteSpace);
            if (gap.Length > 0 && !isSeparator)
            {
                Flush();
                result.Append(gap);
            }

            cursor = match.Index + match.Length;

            var charset = match.Groups[1].Value.Split('*')[0];
            var payload = match.Groups[3].Value;
            var bytes = match.Groups[2].Value is "B" or "b"
                ? TryDecodeBase64(payload, tolerant: false)
                : DecodeQEncodedWord(payload);

            if (bytes is null)
            {
                Flush();
                result.Append(match.Value);
                previousWasEncoded = false;
                continue;
            }

            if (pendingCharset is not null && !string.Equals(pendingCharset, charset, StringComparison.OrdinalIgnoreCase))
            {
                Flush();
            }

            // Adjacent words of one charset concatenate as bytes, so a multi-byte sequence split
            // across the fold still decodes.
            pendingCharset = charset;
            pending.AddRange(bytes);
            previousWasEncoded = true;
        }

        Flush();
        result.Append(value[cursor..]);
        return result.ToString();
    }

    internal static byte[] DecodeQuotedPrintable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '=')
            {
                AppendRawChar(bytes, c);
                continue;
            }

            if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
            {
                i += 2;
            }
            else if (i + 1 < text.Length && (text[i + 1] == '\n' || text[i + 1] == '\r'))
            {
                i += 1;
            }
            else if (i + 2 < text.Length && TryParseHexByte(text[i + 1], text[i + 2], out var decoded))
            {
                bytes.Add(decoded);
                i += 2;
            }
            else
            {
                bytes.Add((byte)'=');
            }
        }

        return bytes.ToArray();
    }

    internal static byte[] DecodeBase64(string? text) =>
        string.IsNullOrEmpty(text) ? [] : TryDecodeBase64(text, tolerant: true) ?? [];

    internal static byte[] GetRawBytes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0xFF) return Encoding.UTF8.GetBytes(text);
            bytes[i] = (byte)text[i];
        }

        return bytes;
    }

    // Encoding.GetEncoding throws on most of these names: System.Text.Encoding.CodePages is not
    // referenced, so the table is the whole story and an unknown charset must degrade, not throw.
    internal static Encoding GetEncoding(string? charset) =>
        charset?.Trim().Trim('"').ToLowerInvariant() switch
        {
            "utf-8" or "utf8" => Encoding.UTF8,
            "us-ascii" or "ascii" => Encoding.ASCII,
            "iso-8859-1" or "latin1" or "latin-1" => Encoding.Latin1,
            "windows-1252" or "cp1252" or "1252" => Windows1252,
            "utf-16" or "utf-16le" or "unicode" => Encoding.Unicode,
            _ => Utf8Lenient,
        };

    internal static Encoding GetEncoding(int codePage) => codePage switch
    {
        65001 => Encoding.UTF8,
        1252 => Windows1252,
        20127 => Encoding.ASCII,
        _ => Utf8Lenient,
    };

    internal static string DecodeText(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = HiddenElementRegex().Replace(html, string.Empty);
        text = HtmlCommentRegex().Replace(text, string.Empty);
        text = CellBoundaryRegex().Replace(text, " ");
        // A marker keeps the breaks we inject apart from the source's own newlines, which are
        // insignificant whitespace and get collapsed away below.
        text = LineBreakTagRegex().Replace(text, BreakMarker);
        text = TagRegex().Replace(text, string.Empty);
        text = DecodeEntities(text);
        text = InvisibleCharRegex().Replace(text, string.Empty);
        text = WhitespaceRunRegex().Replace(text, " ");
        text = text.Replace(" " + BreakMarker, BreakMarker, StringComparison.Ordinal)
            .Replace(BreakMarker + " ", BreakMarker, StringComparison.Ordinal)
            .Replace(BreakMarker, "\n", StringComparison.Ordinal);

        return NormalizeBody(text);
    }

    internal static string NormalizeBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;

        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return BlankLineRunRegex().Replace(text, "\n\n\n").Trim();
    }

    internal static string? NormalizeSubject(string? subject)
    {
        if (string.IsNullOrEmpty(subject)) return null;

        var collapsed = WhitespaceRunRegex().Replace(subject, " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    internal static string? FormatAddress(string? displayName, string? address)
    {
        var name = Unquote(displayName?.Trim());
        var mail = address?.Trim().Trim('<', '>').Trim();

        if (string.IsNullOrEmpty(mail)) return string.IsNullOrEmpty(name) ? null : name;
        if (string.IsNullOrEmpty(name) || string.Equals(name, mail, StringComparison.OrdinalIgnoreCase)) return mail;
        return $"{name} <{mail}>";
    }

    private static string? Unquote(string? value) =>
        value is { Length: >= 2 } && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal)
            : value;

    // The multipart boundary is byte-matched against content that stays in Latin1 space, so recovery
    // is confined to the fields a human reads.
    private static bool IsRecoverable(string field)
    {
        var colon = field.IndexOf(':');
        return colon > 0 && RecoverableHeaders.Contains(field[..colon].Trim());
    }

    // A header sent as raw 8-bit UTF-8 carries no encoded-word, so nothing else re-decodes it; the
    // file arrives one byte per char, and a strict decode either recovers it or proves it was Latin1.
    internal static string RecoverRawUtf8(string field)
    {
        var bytes = new byte[field.Length];
        var eightBit = false;
        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];
            if (c > 0xFF) return field;

            eightBit |= c > 0x7F;
            bytes[i] = (byte)c;
        }

        if (!eightBit) return field;

        try
        {
            return Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return field;
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static byte[] DecodeQEncodedWord(string payload)
    {
        var bytes = new List<byte>(payload.Length);
        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            if (c == '_')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '=' && i + 2 < payload.Length && TryParseHexByte(payload[i + 1], payload[i + 2], out var decoded))
            {
                bytes.Add(decoded);
                i += 2;
            }
            else
            {
                AppendRawChar(bytes, c);
            }
        }

        return bytes.ToArray();
    }

    private static byte[]? TryDecodeBase64(string text, bool tolerant)
    {
        if (!tolerant && StrictBase64NoiseRegex().IsMatch(text)) return null;

        var cleaned = NonBase64Regex().Replace(text, string.Empty);
        var remainder = cleaned.Length % 4;
        if (remainder == 1)
        {
            if (!tolerant) return null;
            cleaned = cleaned[..^1];
            remainder = 0;
        }

        if (remainder != 0) cleaned += new string('=', 4 - remainder);

        try
        {
            return Convert.FromBase64String(cleaned);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void AppendRawChar(List<byte> bytes, char c)
    {
        if (c <= 0xFF) bytes.Add((byte)c);
        else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
    }

    private static bool TryParseHexByte(char high, char low, out byte value)
    {
        value = 0;
        var h = HexValue(high);
        var l = HexValue(low);
        if (h < 0 || l < 0) return false;

        value = (byte)((h << 4) | l);
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static string DecodeEntities(string text) =>
        text.Contains('&', StringComparison.Ordinal)
            ? EntityRegex().Replace(text, m => ResolveEntity(m.Groups[1].Value) ?? m.Value)
            : text;

    private static string? ResolveEntity(string token)
    {
        if (token[0] != '#')
        {
            return NamedEntities.TryGetValue(token, out var named) ? named : null;
        }

        var isHex = token[1] is 'x' or 'X';
        var digits = isHex ? token[2..] : token[1..];
        var style = isHex ? NumberStyles.HexNumber : NumberStyles.None;
        if (!int.TryParse(digits, style, CultureInfo.InvariantCulture, out var code)) return null;
        if (code <= 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF) return null;

        return char.ConvertFromUtf32(code);
    }

    // Not Encoding.Latin1: windows-1252 spends 0x80-0x9F on the euro sign, curly quotes, dashes and
    // the ellipsis, where ISO-8859-1 leaves invisible C1 controls. Hand-mapped to stay off a NuGet package.
    private sealed class Windows1252Encoding : Encoding
    {
        private const string HighRange =
            "\u20AC\u0081\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u008D\u017D\u008F" +
            "\u0090\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u009D\u017E\u0178";

        public override int CodePage => 1252;

        public override string WebName => "windows-1252";

        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (var i = 0; i < charCount; i++)
            {
                var c = chars[charIndex + i];
                var high = HighRange.AsSpan().IndexOf(c);
                bytes[byteIndex + i] = high >= 0 ? (byte)(0x80 + high) : c <= 0xFF ? (byte)c : (byte)'?';
            }

            return charCount;
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (var i = 0; i < byteCount; i++)
            {
                var b = bytes[byteIndex + i];
                chars[charIndex + i] = b is >= 0x80 and <= 0x9F ? HighRange[b - 0x80] : (char)b;
            }

            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;
    }

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex HeaderCommentRegex();

    [GeneratedRegex(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=")]
    private static partial Regex EncodedWordRegex();

    [GeneratedRegex("[^A-Za-z0-9+/]")]
    private static partial Regex NonBase64Regex();

    [GeneratedRegex("[^A-Za-z0-9+/=]")]
    private static partial Regex StrictBase64NoiseRegex();

    [GeneratedRegex(@"<(?:script|style|title)\b[^>]*>.*?(?:</(?:script|style|title)\s*>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenElementRegex();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"<\s*br\b[^>]*>|<\s*/\s*(?:p|div|tr|li|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<\s*/\s*t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellBoundaryRegex();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[\u034F\u200B-\u200D\uFEFF\u00AD]")]
    private static partial Regex InvisibleCharRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"\n{4,}")]
    private static partial Regex BlankLineRunRegex();

    [GeneratedRegex("&(#[0-9]+|#[xX][0-9a-fA-F]+|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex EntityRegex();
}
