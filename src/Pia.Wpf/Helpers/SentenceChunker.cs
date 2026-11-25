using System.Text;

namespace Pia.Helpers;

public class SentenceChunker
{
    public const int MaxChunkLength = 300;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "mr", "mrs", "ms", "dr", "prof", "vs", "nr", "bzw", "ca", "usw", "vgl",
        "ggf", "evtl", "inkl", "zzgl", "sog", "z.b", "d.h", "u.a", "mme", "mlle", "st", "jr", "sr",
        "fig", "str", "tel", "ex",
    };

    private const string OpeningPunctuation = "([\"'„“«";
    private const string ClosingMarks = "\"'”“’»)]";
    private const string ClauseMarks = ";:,—–";

    private readonly StringBuilder _buffer = new();

    public static IReadOnlyList<string> Split(string text)
    {
        var chunker = new SentenceChunker();
        var chunks = chunker.AddToken(text).ToList();
        if (chunker.Flush() is { } rest)
            chunks.Add(rest);
        return chunks;
    }

    public IEnumerable<string> AddToken(string token)
    {
        _buffer.Append(token);
        var text = _buffer.ToString();
        var chunks = new List<string>();

        while (true)
        {
            var (end, next) = FindBoundary(text);
            if (end < 0 || end > MaxChunkLength)
            {
                if (text.Length < MaxChunkLength)
                    break;
                (end, next) = FindLengthCut(text);
            }

            var chunk = text[..end].Trim();
            if (chunk.Length > 0)
                chunks.Add(chunk);
            text = text[next..];
        }

        _buffer.Clear();
        _buffer.Append(text);
        return chunks;
    }

    public string? Flush()
    {
        var remaining = _buffer.ToString().Trim();
        _buffer.Clear();
        return string.IsNullOrWhiteSpace(remaining) ? null : remaining;
    }

    private static (int End, int Next) FindBoundary(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
                return (i, i + 1);

            if (c is not ('.' or '!' or '?' or '…'))
                continue;

            var end = i + 1;
            while (end < text.Length && ClosingMarks.Contains(text[end]))
                end++;

            // At the end of the buffer the next token may continue the sentence ("3." + "14").
            if (end < text.Length
                && char.IsWhiteSpace(text[end])
                && !(c == '.' && EndsAbbreviation(text, i)))
            {
                return (end, SkipWhitespace(text, end));
            }
        }

        return (-1, -1);
    }

    private static bool EndsAbbreviation(string text, int dotIndex)
    {
        var start = dotIndex;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;
        while (start < dotIndex && OpeningPunctuation.Contains(text[start]))
            start++;

        var word = text[start..dotIndex];
        return word.Length switch
        {
            0 => false,
            1 when char.IsLetter(word[0]) => true,
            <= 2 when word.All(char.IsAsciiDigit) => true,
            _ => Abbreviations.Contains(word),
        };
    }

    private static (int End, int Next) FindLengthCut(string text)
    {
        for (var i = MaxChunkLength - 1; i >= 0; i--)
        {
            if (ClauseMarks.Contains(text[i]) && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return (i + 1, SkipWhitespace(text, i + 1));
        }

        for (var i = MaxChunkLength - 1; i > 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return (i, SkipWhitespace(text, i));
        }

        return (MaxChunkLength, MaxChunkLength);
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }
}
