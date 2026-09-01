using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Pia.Helpers.Email;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Pia.Helpers;

public enum FileKind
{
    Unsupported,
    Text,
    Docx,
    Xlsx,
    Pdf,
    Image,
    Audio,
    Email,
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

    private static readonly FrozenDictionary<string, FileKind> KindByExtension =
        new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase)
        {
            [".docx"] = FileKind.Docx,
            [".xlsx"] = FileKind.Xlsx,
            [".xlsm"] = FileKind.Xlsx,
            [".pdf"] = FileKind.Pdf,
            [".msg"] = FileKind.Email,
            [".eml"] = FileKind.Email,
        }
        .Concat(ImageExtensions.Select(e => new KeyValuePair<string, FileKind>(e, FileKind.Image)))
        .Concat(AudioExtensions.Select(e => new KeyValuePair<string, FileKind>(e, FileKind.Audio)))
        .Concat(TextExtensions.Select(e => new KeyValuePair<string, FileKind>(e, FileKind.Text)))
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FileKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        // Guard stays: GetValueOrDefault(null) throws on an OrdinalIgnoreCase dictionary.
        if (string.IsNullOrEmpty(ext)) return FileKind.Unsupported;
        return KindByExtension.GetValueOrDefault(ext, FileKind.Unsupported);
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
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is null) return ReadResult.Success(string.Empty);

                var walk = WalkDocxParagraphs(body, ct);

                var sb = new StringBuilder();
                foreach (var line in walk.Lines) sb.AppendLine(line);

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

    /// <summary>One entry per paragraph whose concatenated text is non-empty — exactly the paragraphs
    /// that make it into <see cref="ReadDocxAsync"/>'s emitted text, in the same order. Shared by the
    /// read path and by the write-side patch engine (<c>DocxPatcher</c>) so "line N of the extracted
    /// text" always resolves to the same real paragraph both places — a paragraph the reader skips
    /// (blank) never appears here either, and is therefore never a diff/patch target.</summary>
    internal readonly record struct DocxParagraphWalk(
        IReadOnlyList<Paragraph> AllParagraphs,
        IReadOnlyList<string> Lines,
        IReadOnlyList<int> Ordinals);

    internal static DocxParagraphWalk WalkDocxParagraphs(Body body, CancellationToken ct = default)
    {
        var all = body.Descendants<Paragraph>().ToList();
        var lines = new List<string>();
        var ordinals = new List<int>();
        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Concat(all[i].Descendants<Text>().Select(t => t.Text));
            if (text.Length > 0)
            {
                lines.Add(text);
                ordinals.Add(i);
            }
        }
        return new DocxParagraphWalk(all, lines, ordinals);
    }

    public static Task<ReadResult> ReadXlsxAsync(string path, CancellationToken ct)
    {
        // OpenXml SDK is sync; offload to thread pool. ct checked at row boundaries.
        return Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxTextBytes * 8) return ReadResult.TooLarge;

                using var doc = SpreadsheetDocument.Open(path, isEditable: false);
                var workbookPart = doc.WorkbookPart;
                if (workbookPart is null) return ReadResult.Success(string.Empty);

                var walk = WalkXlsxWorkbook(workbookPart, ct);
                if (walk.Truncated) return ReadResult.TooLarge;
                if (walk.Lines.Count == 0) return ReadResult.Success(string.Empty);

                var sb = new StringBuilder();
                foreach (var line in walk.Lines)
                {
                    if (line.Kind == XlsxLineKind.Separator) sb.AppendLine();
                    else sb.AppendLine(line.Text);
                }

                if (sb.Length > MaxTextBytes) return ReadResult.TooLarge;
                return ReadResult.Success(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return ReadResult.Fail(ex.Message);
            }
        }, ct);
    }

    internal enum XlsxLineKind { Separator, Header, Row }

    /// <summary>One entry per line <see cref="ReadXlsxAsync"/> emits (including the blank separator
    /// between sheets), so that a diff computed against the emitted text always indexes into this
    /// array 1:1. <see cref="RowNode"/> is the real backing row for a <see cref="XlsxLineKind.Row"/>
    /// line; null for a separator/header line, which are never a patch target.</summary>
    internal sealed record XlsxWalkLine(XlsxLineKind Kind, string Text, string SheetName, SS.Row? RowNode);

    internal readonly record struct XlsxWorkbookWalk(
        IReadOnlyList<XlsxWalkLine> Lines,
        IReadOnlyDictionary<string, WorksheetPart> SheetsByName,
        bool Truncated);

    /// <summary>Walks every sheet's rows into the same shape <see cref="ReadXlsxAsync"/> emits.
    /// Aborts early (setting <see cref="XlsxWorkbookWalk.Truncated"/>) once the accumulated line
    /// text would exceed <see cref="MaxTextBytes"/>, so a huge workbook doesn't fully materialize
    /// in memory before the caller's size check rejects it — callers besides <see cref="ReadXlsxAsync"/>
    /// (i.e. the write path's patch engines) only ever run on a workbook whose baseline text already
    /// passed that check, so they should treat <c>Truncated</c> as "reject, don't patch."</summary>
    internal static XlsxWorkbookWalk WalkXlsxWorkbook(WorkbookPart workbookPart, CancellationToken ct = default)
    {
        var sheets = workbookPart.Workbook?.Sheets?.Elements<SS.Sheet>().ToList();
        var sheetsByName = new Dictionary<string, WorksheetPart>(StringComparer.OrdinalIgnoreCase);
        if (sheets is null || sheets.Count == 0)
            return new XlsxWorkbookWalk([], sheetsByName, Truncated: false);

        var sst = workbookPart.SharedStringTablePart?.SharedStringTable;
        var lines = new List<XlsxWalkLine>();
        long runningChars = 0;
        bool truncated = false;

        for (int s = 0; s < sheets.Count && !truncated; s++)
        {
            var sheet = sheets[s];
            if (sheet.Id?.Value is not { } relId) continue;
            if (workbookPart.GetPartById(relId) is not WorksheetPart wsPart) continue;
            if (wsPart.Worksheet is not { } worksheet) continue;

            var sheetName = sheet.Name?.Value ?? $"Sheet{s + 1}";
            sheetsByName[sheetName] = wsPart;

            if (s > 0) lines.Add(new XlsxWalkLine(XlsxLineKind.Separator, "", "", null));
            lines.Add(new XlsxWalkLine(XlsxLineKind.Header, "## Sheet: " + sheetName, sheetName, null));

            foreach (var row in worksheet.Descendants<SS.Row>())
            {
                ct.ThrowIfCancellationRequested();

                var cells = GetCellsByColumn(row, sst);
                if (cells.Count == 0) continue;

                var lineText = string.Join('\t', cells);
                lines.Add(new XlsxWalkLine(XlsxLineKind.Row, lineText, sheetName, row));

                runningChars += lineText.Length + 1;
                if (runningChars > MaxTextBytes) { truncated = true; break; }
            }
        }

        return new XlsxWorkbookWalk(lines, sheetsByName, truncated);
    }

    /// <summary>Column-indexed, trailing-empty-trimmed cell text for one row — the exact fields a
    /// TSV line renders. Shared by the read path and by <c>XlsxPatcher</c>, which re-derives this
    /// fresh at both prepare (validate) and execute (apply) time rather than caching it, so a
    /// same-row field-by-field diff always compares against the row's current real values.</summary>
    internal static List<string> GetCellsByColumn(SS.Row row, SS.SharedStringTable? sst)
    {
        var cells = new List<string>();
        var anyNonEmpty = false;
        foreach (var cell in row.Elements<SS.Cell>())
        {
            var colIdx = ColumnIndex(cell.CellReference?.Value);
            while (cells.Count < colIdx) cells.Add(string.Empty);

            var value = CellToString(cell, sst);
            cells.Add(value);
            if (!string.IsNullOrWhiteSpace(value)) anyNonEmpty = true;
        }
        if (!anyNonEmpty) return [];

        int lastNonEmpty = cells.Count - 1;
        while (lastNonEmpty >= 0 && string.IsNullOrEmpty(cells[lastNonEmpty])) lastNonEmpty--;

        return cells.Take(lastNonEmpty + 1).ToList();
    }

    public static Task<ReadResult> ReadEmailAsync(string path, CancellationToken ct)
    {
        // Both mail readers are synchronous and hold the whole file in memory, hence the container
        // ceiling; the rendered text is gated too, because attachment bytes inflate the file past it.
        return Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxTextBytes * 8) return ReadResult.TooLarge;

                var mail = Path.GetExtension(path).Equals(".eml", StringComparison.OrdinalIgnoreCase)
                    ? EmlReader.Read(path)
                    : MsgReader.Read(path);

                var text = RenderEmail(mail);
                return text.Length > MaxTextBytes ? ReadResult.TooLarge : ReadResult.Success(text);
            }
            catch (Exception ex)
            {
                // Not ex.Message: an IO or parse message routinely embeds the full user path.
                return ReadResult.Fail(ex.GetType().Name);
            }
        }, ct);
    }

    // Slashes, not hyphens: the PII detector reads a hyphenated date as a phone number, so the model
    // would receive "Date: [Phone_1]:46 +00:00".
    private const string MailDateFormat = "yyyy/MM/dd HH:mm zzz";

    // The rule ends the header block with a character the PII detector's phone pattern cannot cross.
    private const string BodyRule = "===\n\n";

    private static string RenderEmail(EmailMessage mail)
    {
        var sb = new StringBuilder();
        AppendField(sb, "Subject", mail.Subject);
        AppendField(sb, "From", mail.From);
        AppendField(sb, "To", string.Join(", ", mail.To));
        AppendField(sb, "Cc", string.Join(", ", mail.Cc));
        AppendField(sb, "Date", mail.Date?.ToString(MailDateFormat, CultureInfo.InvariantCulture));
        AppendField(sb, "Attachments", string.Join(", ", mail.AttachmentNames));

        if (mail.Body.Length == 0) return sb.ToString();
        if (sb.Length > 0) sb.Append(BodyRule);
        return sb.Append(mail.Body).ToString();
    }

    // An empty "From: " invites the model to invent a sender, so a valueless field gets no line.
    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        sb.Append(label).Append(": ").Append(value).Append('\n');
    }

    // Date cells surface as OLE Automation date numbers (e.g. 45678). Resolving them to
    // readable dates requires the workbook's numFmt table — deferred; values match what
    // Excel writes when copy-pasting as "Values" to a TSV target.
    private static string CellToString(SS.Cell cell, SS.SharedStringTable? sst)
    {
        string raw;
        if (cell.DataType?.Value == SS.CellValues.SharedString)
        {
            if (sst is null) return string.Empty;
            if (!int.TryParse(cell.CellValue?.InnerText, out var idx)) return string.Empty;
            var item = sst.ElementAtOrDefault(idx);
            raw = item?.InnerText ?? string.Empty;
        }
        else if (cell.DataType?.Value == SS.CellValues.InlineString)
        {
            raw = cell.InlineString?.InnerText ?? string.Empty;
        }
        else if (cell.DataType?.Value == SS.CellValues.Boolean)
        {
            raw = cell.CellValue?.InnerText == "1" ? "TRUE" : "FALSE";
        }
        else
        {
            raw = cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
        }

        // Keep TSV rows well-formed: collapse tabs/newlines inside a cell to single spaces.
        if (raw.IndexOfAny(['\t', '\r', '\n']) >= 0)
            raw = raw.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return raw;
    }

    // internal: also used by XlsxPatcher to locate a cell by column at patch time.
    internal static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return 0;
        int idx = 0;
        foreach (var c in cellRef)
        {
            if (c < 'A' || c > 'Z')
            {
                if (c >= 'a' && c <= 'z') { idx = idx * 26 + (c - 'a' + 1); continue; }
                break;
            }
            idx = idx * 26 + (c - 'A' + 1);
        }
        return idx > 0 ? idx - 1 : 0;
    }
}
