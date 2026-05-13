using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Pia.Helpers;

public enum FileKind
{
    Unsupported,
    Text,
    Docx,
    Pdf,
    Image,
    Audio,
}

public static class DroppedFileReader
{
    public const int MaxTextBytes = 1 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".csv", ".log", ".ini",
        ".cs", ".js", ".ts", ".py", ".html", ".htm", ".css", ".sql", ".sh", ".ps1",
        ".bat", ".cmd", ".toml", ".env", ".gitignore", ".editorconfig"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".m4a", ".flac", ".ogg"
    };

    public static FileKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return FileKind.Unsupported;
        if (string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase)) return FileKind.Docx;
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        if (ImageExtensions.Contains(ext)) return FileKind.Image;
        if (AudioExtensions.Contains(ext)) return FileKind.Audio;
        if (TextExtensions.Contains(ext)) return FileKind.Text;
        return FileKind.Unsupported;
    }

    public enum ReadStatus { Ok, TooLarge, Failed }

    public readonly record struct ReadResult(ReadStatus Status, string? Text, string? Error)
    {
        public static ReadResult Success(string text) => new(ReadStatus.Ok, text, null);
        public static readonly ReadResult TooLarge = new(ReadStatus.TooLarge, null, null);
        public static ReadResult Fail(string error) => new(ReadStatus.Failed, null, error);
    }

    public static async Task<ReadResult> ReadTextAsync(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTextBytes) return ReadResult.TooLarge;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(ct);
            return ReadResult.Success(content);
        }
        catch (Exception ex)
        {
            return ReadResult.Fail(ex.Message);
        }
    }

    public static Task<ReadResult> ReadDocxAsync(string path, CancellationToken ct)
    {
        // OpenXml SDK is sync; offload to thread pool. ct used only at boundary.
        return Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxTextBytes * 8) return ReadResult.TooLarge;

                using var doc = WordprocessingDocument.Open(path, isEditable: false);
                var body = doc.MainDocumentPart?.Document.Body;
                if (body is null) return ReadResult.Success(string.Empty);

                var sb = new StringBuilder();
                foreach (var paragraph in body.Descendants<Paragraph>())
                {
                    ct.ThrowIfCancellationRequested();
                    var text = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
                    if (text.Length > 0)
                        sb.AppendLine(text);
                }

                if (sb.Length > MaxTextBytes)
                    return ReadResult.TooLarge;

                return ReadResult.Success(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return ReadResult.Fail(ex.Message);
            }
        }, ct);
    }
}
