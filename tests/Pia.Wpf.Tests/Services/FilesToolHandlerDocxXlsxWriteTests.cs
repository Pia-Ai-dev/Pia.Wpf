using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Pia.Tests.Services;

/// <summary>End-to-end write_file coverage for .docx/.xlsx, through the full
/// HandleToolCallAsync -> approval card -> Execute pipeline (patch-engine internals are covered
/// directly in Infrastructure/DocxPatcherTests.cs and XlsxPatcherTests.cs).</summary>
public class FilesToolHandlerDocxXlsxWriteTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerDocxXlsxWriteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-docxlsx-write-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        TempPath.Remove(_root);
    }

    private static void CreateDocx(string path, params string[] paragraphs)
        => OfficeDocuments.CreateDocx(path, paragraphs);

    private static void CreateXlsx(string path, string sheetName, params (string Ref, string Value)[] cells)
        => OfficeDocuments.CreateXlsx(path, sheetName, cells);

    private async Task<(object? Result, FilesToolCall? Pending)> Prepare(string path, string content)
    {
        var args = new Dictionary<string, object?> { ["path"] = path, ["content"] = content };
        var call = new FunctionCallContent("c1", "write_file", args);
        return await _handler.HandleToolCallAsync(call);
    }

    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    private static List<string> BodyParagraphTexts(string path)
    {
        using var doc = WordprocessingDocument.Open(path, isEditable: false);
        return doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<DW.Text>().Select(t => t.Text)))
            .ToList();
    }

    [Fact]
    public async Task Docx_RoundTrip_EditsOnlyTheChangedParagraph()
    {
        var full = Path.Combine(_root, "doc.docx");
        CreateDocx(full, "Para 1", "Para 2", "Para 3");

        var (_, pending) = await Prepare("doc.docx", "Para 1\nPara 2 EDITED\nPara 3");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.Equal(["Para 1", "Para 2 EDITED", "Para 3"], BodyParagraphTexts(full));
    }

    [Fact]
    public async Task Xlsx_RoundTrip_EditsOnlyTheChangedCell()
    {
        var full = Path.Combine(_root, "book.xlsx");
        CreateXlsx(full, "Data", ("A1", "one"), ("B1", "two"));

        var (_, pending) = await Prepare("book.xlsx", "## Sheet: Data\none\tTWO EDITED");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        using var doc = SpreadsheetDocument.Open(full, isEditable: false);
        var cells = doc.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<SS.Cell>().ToList();
        Assert.Contains(cells, c => c.CellReference == "A1");
    }

    [Fact]
    public async Task Docx_CreateNew_NoBaseline()
    {
        var (_, pending) = await Prepare("new.docx", "Hello\nWorld");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.Equal(["Hello", "World"], BodyParagraphTexts(Path.Combine(_root, "new.docx")));
    }

    [Fact]
    public async Task Xlsx_CreateNew_NoBaseline()
    {
        var (_, pending) = await Prepare("new.xlsx", "## Sheet: Sheet1\na1\tb1");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.True(File.Exists(Path.Combine(_root, "new.xlsx")));
    }

    [Fact]
    public async Task Docm_IsRejected_AtPrepareTime_NoActionCard()
    {
        var (result, pending) = await Prepare("macro.docm", "content");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("macro", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("thread.eml")]
    [InlineData("thread.msg")]
    public async Task Mail_IsRejected_AtPrepareTime_NoActionCard(string name)
    {
        var (result, pending) = await Prepare(name, "Subject: x\n===\n\nbody");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("read-only", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Xlsm_IsRejected_AtPrepareTime_NoActionCard()
    {
        var (result, pending) = await Prepare("macro.xlsm", "## Sheet: Data\nvalue");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("macro", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Xlsx_InvalidSheetName_IsRejected_AtPrepareTime_NoActionCard()
    {
        var (result, pending) = await Prepare("new.xlsx", "## Sheet: Bad:Name\nvalue");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("character", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "new.xlsx")));
    }

    [Fact]
    public async Task Docx_LockedFile_PrepareReturnsCleanError_DoesNotThrow()
    {
        // A locked file can be rejected either by the baseline read (DroppedFileReader's own
        // try/catch) or by the separate validate-open — whichever fires, HandleToolCallAsync must
        // return a clean WriteFailure, never throw.
        var full = Path.Combine(_root, "locked.docx");
        CreateDocx(full, "Original");

        using var lockStream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None);

        var (result, pending) = await Prepare("locked.docx", "Edited");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.False(string.IsNullOrEmpty(Prop<string?>(result!, "error")));
    }

    [Fact]
    public async Task Docx_CorruptBaseline_IsHardRejected_NotTreatedAsNewFile()
    {
        var full = Path.Combine(_root, "corrupt.docx");
        File.WriteAllText(full, "this is not a real docx file");

        var (result, pending) = await Prepare("corrupt.docx", "Replacement text");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("could not read", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        // The corrupt file was never touched (no regenerate-as-new-file fallback).
        Assert.Equal("this is not a real docx file", File.ReadAllText(full));
    }

    [Fact]
    public async Task Docx_FileChangedSincePreview_IsBlocked_NoClobber()
    {
        var full = Path.Combine(_root, "concurrent.docx");
        CreateDocx(full, "Original");

        var (_, pending) = await Prepare("concurrent.docx", "Edited");
        Assert.NotNull(pending);

        // Out-of-band change after preview, before execute.
        CreateDocx(full, "Someone else's edit");
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(5));

        var result = await pending!.Execute();

        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("changed on disk", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Someone else's edit"], BodyParagraphTexts(full));
    }

    [Fact]
    public async Task Xlsx_MidSheetInsert_RejectedAtPrepareTime_OriginalUntouched_NoTempFile()
    {
        var full = Path.Combine(_root, "book.xlsx");
        using (var doc = SpreadsheetDocument.Create(full, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new SS.Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SS.SheetData();
            var row1 = new SS.Row { RowIndex = 1 };
            row1.Append(new SS.Cell { CellReference = "A1", DataType = SS.CellValues.String, CellValue = new SS.CellValue("one") });
            var row2 = new SS.Row { RowIndex = 2 };
            row2.Append(new SS.Cell { CellReference = "A2", DataType = SS.CellValues.String, CellValue = new SS.CellValue("two") });
            sheetData.Append(row1, row2);
            wsPart.Worksheet = new SS.Worksheet(sheetData);
            var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });
            wbPart.Workbook.Save();
        }
        var originalBytes = File.ReadAllBytes(full);

        var (result, pending) = await Prepare("book.xlsx", "## Sheet: Data\none\nnew middle row\ntwo");

        Assert.Null(pending); // rejected at prepare time, no approval card
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("middle", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalBytes, File.ReadAllBytes(full));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Docx_AtomicReplace_LeavesNoTempFiles()
    {
        var full = Path.Combine(_root, "atomic.docx");
        CreateDocx(full, "Before");

        var (_, pending) = await Prepare("atomic.docx", "After");
        await pending!.Execute();

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.Equal(["After"], BodyParagraphTexts(full));
    }

    // A one-word edit means re-emitting the whole document with every "N|" prefix stripped by hand.
    // The observed failure was three of ~560 prefixes coming back as paragraphs of their own.
    [Fact]
    public async Task Docx_LeakedLineNumbers_AreRejected_AtPrepareTime_NoActionCard()
    {
        var full = Path.Combine(_root, "landscape.docx");
        CreateDocx(full,
            "Version: 1.0",
            "Overview",
            "Main Features: a very long feature list that runs on",
            "Reference: example.com",
            "Scope: everything",
            "Reference: other.com");

        var (result, pending) = await Prepare("landscape.docx",
            "Version: DRAFT\nOverview\nMain Features: a very long feature list that runs on\n4\n" +
            "Reference: example.com\nScope: everything\n6\nReference: other.com");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        var error = Prop<string?>(result!, "error")!;
        Assert.Contains("line number", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 line(s)", error, StringComparison.Ordinal);

        // The intended edit is rejected along with the leak — the model resubmits the whole content.
        Assert.Equal("Version: 1.0", BodyParagraphTexts(full)[0]);
        Assert.Equal(6, BodyParagraphTexts(full).Count);
    }

    // A leak can OVERWRITE the line it prefixed instead of preceding it — observed live, on a document
    // already carrying artifacts from an earlier leak: each stray line's text was replaced with its own
    // current line number.
    [Fact]
    public async Task Docx_LeakThatOverwritesTheLine_IsRejected()
    {
        var full = Path.Combine(_root, "again.docx");
        CreateDocx(full,
            "Version: DRAFT",
            "Overview",
            "Main Features: a long feature sentence",
            "329",                        // artifact from an earlier leak, at read-line 4
            "Reference: example.com");

        // The model re-emits and "corrects" the artifact to its current line number.
        var (result, pending) = await Prepare("again.docx",
            "Version: 0.9\nOverview\nMain Features: a long feature sentence\n4\nReference: example.com");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("line number", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("329", BodyParagraphTexts(full)[3]);
    }

    // The earlier guard exempted a hit whose anchor was itself a bare integer, which turned it off on
    // exactly the files a leak had already damaged. This is that inversion, pinned.
    [Fact]
    public async Task Docx_AlreadyDamagedFile_StillCatchesANewLeak()
    {
        var full = Path.Combine(_root, "damaged.docx");
        CreateDocx(full, "Title", "185", "Body text", "Reference: example.com", "Tail");

        var (result, pending) = await Prepare("damaged.docx",
            "Title\n185\nBody text\n4\nReference: example.com\nTail");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("line number", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_ContentIdenticalToTheFile_IsRejected_SoNoOneCanClaimAChange()
    {
        var full = Path.Combine(_root, "same.docx");
        CreateDocx(full, "Alpha", "Beta", "Gamma");

        var (result, pending) = await Prepare("same.docx", "Alpha\nBeta\nGamma");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("nothing was written", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    // The guard resolves "old line N" in the EXTRACTED text, which skips blank paragraphs — a paragraph
    // index would put the range and the anchors somewhere else entirely.
    [Fact]
    public async Task Docx_LeakedLineNumber_IsFoundByReadLineNumber_NotParagraphIndex()
    {
        var full = Path.Combine(_root, "blanks.docx");
        CreateDocx(full, "alpha", "", "beta", "Reference: x", "gamma");

        // "Reference: x" is paragraph 4 but read-line 3; a paragraph-index guard would miss this.
        var (result, pending) = await Prepare("blanks.docx", "alpha\nbeta\n3\nReference: x\ngamma");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("line number", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Xlsx_LeakedLineNumber_IsRejected_AtPrepareTime_NoActionCard()
    {
        var full = Path.Combine(_root, "book.xlsx");
        CreateXlsx(full, "Data", ("A1", "one"), ("B1", "two"));

        var (result, pending) = await Prepare("book.xlsx", "## Sheet: Data\n2\none\ttwo");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("line number", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }
}
