using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Pia.Tests.Services;

public class FilesToolHandlerReadTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerReadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-read-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<object?> ReadAsync(string path, int? offset = null, int? limit = null)
    {
        var args = new Dictionary<string, object?> { ["path"] = path };
        if (offset is not null) args["offset"] = offset.Value;
        if (limit is not null) args["limit"] = limit.Value;
        var call = new FunctionCallContent("c1", "read_file", args);
        var (result, _) = await _handler.HandleToolCallAsync(call);
        return result;
    }

    private string WriteFile(string name, string content)
    {
        var full = Path.Combine(_root, name);
        File.WriteAllText(full, content);
        return name;
    }

    [Fact]
    public async Task Read_EmitsLineNumberedOutput_WithTotalLines()
    {
        WriteFile("a.txt", "alpha\nbeta\ngamma\n");

        var result = (string)(await ReadAsync("a.txt"))!;

        Assert.Contains("total_lines=3", result);
        Assert.Contains("1|alpha", result);
        Assert.Contains("2|beta", result);
        Assert.Contains("3|gamma", result);
        // No zero-padding on line numbers.
        Assert.DoesNotContain("001|", result);
    }

    [Fact]
    public async Task Read_StripsCarriageReturns_FromContent()
    {
        WriteFile("crlf.txt", "one\r\ntwo\r\nthree\r\n");

        var result = (string)(await ReadAsync("crlf.txt"))!;

        Assert.Contains("1|one\n", result + "\n");
        Assert.DoesNotContain("\r", result);
        Assert.Contains("total_lines=3", result);
    }

    [Fact]
    public async Task Read_Windowing_SlicesOffsetAndLimit()
    {
        WriteFile("nums.txt", string.Join('\n', Enumerable.Range(1, 100).Select(i => "line" + i)));

        var result = (string)(await ReadAsync("nums.txt", offset: 10, limit: 5))!;

        Assert.Contains("total_lines=100", result);
        Assert.Contains("10|line10", result);
        Assert.Contains("14|line14", result);
        Assert.DoesNotContain("9|line9", result);
        Assert.DoesNotContain("15|line15", result);
        // More lines remain -> pagination hint.
        Assert.Contains("use offset=15", result);
    }

    [Fact]
    public async Task Read_OffsetPastEnd_EmptyContent_CorrectTotalLines()
    {
        WriteFile("small.txt", "x\ny\nz\n");

        var result = (string)(await ReadAsync("small.txt", offset: 99))!;

        Assert.Contains("total_lines=3", result);
        Assert.Contains("past end of file", result);
        Assert.DoesNotContain("1|x", result);
    }

    [Fact]
    public async Task Read_OffsetBelowOne_ClampedToOne()
    {
        WriteFile("clamp.txt", "a\nb\nc");

        var result = (string)(await ReadAsync("clamp.txt", offset: -5, limit: 2))!;

        Assert.Contains("1|a", result);
        Assert.Contains("2|b", result);
        Assert.DoesNotContain("3|c", result);
    }

    [Fact]
    public async Task Read_LimitClampedToMax()
    {
        WriteFile("big.txt", string.Join('\n', Enumerable.Range(1, 50).Select(i => "L" + i)));

        // limit above the 2000 cap must clamp, not error.
        var result = (string)(await ReadAsync("big.txt", limit: 99999))!;

        Assert.Contains("total_lines=50", result);
        Assert.Contains("50|L50", result);
    }

    [Fact]
    public async Task Read_FormattedWindowOverflow_ReturnsGuidance_NotTruncated()
    {
        // ~200 chars/line x 800 lines ~= 160K formatted: over the ~100K cap but well under the
        // 1 MB raw-byte ceiling and the 2000-line cap, so it exercises the formatted-window guard.
        var line = new string('x', 200);
        WriteFile("wide.txt", string.Join('\n', Enumerable.Repeat(line, 800)));

        var result = (string)(await ReadAsync("wide.txt", offset: 1, limit: 800))!;

        Assert.Contains("too large to return", result);
        Assert.Contains("Narrow the read", result);
    }

    [Fact]
    public async Task Read_BinaryFile_NulSniff_Rejects()
    {
        var full = Path.Combine(_root, "blob.dat");
        File.WriteAllBytes(full, new byte[] { 0x41, 0x42, 0x00, 0x43, 0x44 });

        var result = (string)(await ReadAsync("blob.dat"))!;

        Assert.Contains("binary file", result);
        Assert.Contains("NUL", result);
    }

    [Fact]
    public async Task Read_ExtensionlessTextFile_IsReadNotRejected()
    {
        // No extension gate: a Go/Rust-style file with an unknown extension still reads.
        WriteFile("Makefile", "all:\n\tbuild\n");
        var result = (string)(await ReadAsync("Makefile"))!;
        Assert.Contains("1|all:", result);
    }

    [Fact]
    public async Task Read_UnknownCodeExtension_IsReadNotRejected()
    {
        WriteFile("main.rs", "fn main() {}\n");
        var result = (string)(await ReadAsync("main.rs"))!;
        Assert.Contains("1|fn main", result);
    }

    [Fact]
    public async Task Read_ImageExtension_ReturnsAttachGuidance()
    {
        var full = Path.Combine(_root, "pic.png");
        File.WriteAllBytes(full, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var result = (string)(await ReadAsync("pic.png"))!;

        Assert.Contains("attach the image instead", result);
    }

    [Fact]
    public async Task Read_NotFound_ReturnsError()
    {
        var result = (string)(await ReadAsync("nope.txt"))!;
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Read_RecordsMtimeInStalenessStore()
    {
        var rel = WriteFile("track.txt", "hello\nworld\n");
        var full = Path.Combine(_root, rel);

        await ReadAsync(rel);

        // Unchanged mtime -> not stale; out-of-band change -> stale, proving RecordRead ran.
        var mtime = File.GetLastWriteTimeUtc(full);
        Assert.False(_staleness.CheckStaleness(Guid.Empty, full, mtime));
        Assert.True(_staleness.CheckStaleness(Guid.Empty, full, mtime.AddSeconds(5)));
    }

    [Fact]
    public async Task Read_Docx_NumbersExtractedText()
    {
        var rel = "doc.docx";
        var full = Path.Combine(_root, rel);
        CreateDocx(full, ["First paragraph", "Second paragraph"]);

        var result = (string)(await ReadAsync(rel))!;

        Assert.Contains("1|First paragraph", result);
        Assert.Contains("2|Second paragraph", result);
        Assert.Contains("total_lines=2", result);
    }

    [Fact]
    public async Task Read_Xlsx_NumbersExtractedText()
    {
        var rel = "book.xlsx";
        var full = Path.Combine(_root, rel);
        CreateXlsx(full);

        var result = (string)(await ReadAsync(rel))!;

        // First line is the sheet header emitted by ReadXlsxAsync.
        Assert.Contains("1|## Sheet:", result);
        Assert.Contains("a1\tb1", result);
    }

    private static void CreateDocx(string path, string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();
        main.Document.Append(body);
        foreach (var p in paragraphs)
            body.Append(new Paragraph(new DW.Run(new DW.Text(p))));
        main.Document.Save();
    }

    private static void CreateXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();

        var row = new SS.Row();
        row.Append(MakeCell("A1", "a1"));
        row.Append(MakeCell("B1", "b1"));
        var sheetData = new SheetData(row);
        wsPart.Worksheet = new Worksheet(sheetData);

        var sheets = wbPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });
        wbPart.Workbook.Save();
    }

    private static SS.Cell MakeCell(string reference, string value)
        => new()
        {
            CellReference = reference,
            DataType = CellValues.String,
            CellValue = new SS.CellValue(value),
        };
}
