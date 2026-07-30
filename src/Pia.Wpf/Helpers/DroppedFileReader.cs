using System.IO;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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
        if (string.Equals(ext, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".xlsm", StringComparison.OrdinalIgnoreCase)) return FileKind.Xlsx;
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
                var body = doc.MainDocumentPart?.Document?.Body;
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
                var sheets = workbookPart?.Workbook?.Sheets?.Elements<SS.Sheet>().ToList();
                if (workbookPart is null || sheets is null || sheets.Count == 0)
                    return ReadResult.Success(string.Empty);

                var sst = workbookPart.SharedStringTablePart?.SharedStringTable;
                var sb = new StringBuilder();

                for (int s = 0; s < sheets.Count; s++)
                {
                    var sheet = sheets[s];
                    if (sheet.Id?.Value is not { } relId) continue;
                    if (workbookPart.GetPartById(relId) is not WorksheetPart wsPart) continue;
                    if (wsPart.Worksheet is not { } worksheet) continue;

                    if (s > 0) sb.AppendLine();
                    sb.Append("## Sheet: ").AppendLine(sheet.Name?.Value ?? $"Sheet{s + 1}");

                    foreach (var row in worksheet.Descendants<SS.Row>())
                    {
                        ct.ThrowIfCancellationRequested();

                        // Collect cells into column-indexed slots so gaps render as empty TSV cells.
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
                        if (!anyNonEmpty) continue;

                        // Trim trailing empty cells.
                        int lastNonEmpty = cells.Count - 1;
                        while (lastNonEmpty >= 0 && string.IsNullOrEmpty(cells[lastNonEmpty])) lastNonEmpty--;

                        sb.AppendLine(string.Join('\t', cells.Take(lastNonEmpty + 1)));

                        if (sb.Length > MaxTextBytes) return ReadResult.TooLarge;
                    }
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

    private static int ColumnIndex(string? cellRef)
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
