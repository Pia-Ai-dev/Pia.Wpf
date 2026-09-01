using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Paths;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
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
        TempPath.Remove(_root);
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
    public async Task Read_ForwardSlashRelativePath_ResolvesInsideSandbox()
    {
        // The @Files picker hands the model forward-slash paths (e.g. "notes/todo.md").
        // Path.GetFullPath accepts '/' on Windows, so the file tools must resolve them.
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(_root, "notes", "todo.md"), "buy milk\n");

        var result = (string)(await ReadAsync("notes/todo.md"))!;

        Assert.Contains("1|buy milk", result);
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

    // ---- @Files prompt-preview injection (ReadPromptPreviewAsync + CapForPrompt) ----

    [Fact]
    public async Task Preview_ReadsRawContent_NoLineNumberPrefixes()
    {
        WriteFile("greet.ps1", "# greet\nWrite-Host \"hi\"\n");

        var preview = await _handler.ReadPromptPreviewAsync("greet.ps1", workingSubpath: null, maxLines: 100, TestContext.Current.CancellationToken);

        Assert.True(preview.Found);
        Assert.Null(preview.Error);
        Assert.Equal(2, preview.TotalLines);
        Assert.Equal(2, preview.ShownLines);
        Assert.False(preview.Truncated);
        // Raw, human-readable content — not the read_file "N|content" form.
        Assert.Equal("# greet\nWrite-Host \"hi\"", preview.Text);
    }

    [Fact]
    public async Task Preview_TruncatesToMaxLines_AndFlagsTruncated()
    {
        WriteFile("many.txt", string.Join('\n', Enumerable.Range(1, 50).Select(i => "line" + i)));

        var preview = await _handler.ReadPromptPreviewAsync("many.txt", workingSubpath: null, maxLines: 10, TestContext.Current.CancellationToken);

        Assert.True(preview.Found);
        Assert.Equal(50, preview.TotalLines);
        Assert.Equal(10, preview.ShownLines);
        Assert.True(preview.Truncated);
        Assert.Contains("line10", preview.Text!);
        Assert.DoesNotContain("line11", preview.Text!);
    }

    [Fact]
    public async Task Preview_ForwardSlashPath_ScopedToWorkingSubpath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "proj", "src"));
        File.WriteAllText(Path.Combine(_root, "proj", "src", "a.cs"), "class A {}\n");

        // Path is relative to the chat's working dir ("proj"), mirroring read_file's narrowing.
        var preview = await _handler.ReadPromptPreviewAsync("src/a.cs", workingSubpath: "proj", maxLines: 100, TestContext.Current.CancellationToken);

        Assert.True(preview.Found);
        Assert.Equal("class A {}", preview.Text);
    }

    [Fact]
    public async Task Preview_NotFound_ReturnsFoundFalseWithError()
    {
        var preview = await _handler.ReadPromptPreviewAsync("ghost.txt", workingSubpath: null, maxLines: 100, TestContext.Current.CancellationToken);

        Assert.False(preview.Found);
        Assert.Null(preview.Text);
        Assert.Contains("not found", preview.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_BinaryFile_ReturnsFoundFalse_PlainReasonNoErrorPrefix()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), new byte[] { 0x41, 0x00, 0x42 });

        var preview = await _handler.ReadPromptPreviewAsync("blob.bin", workingSubpath: null, maxLines: 100, TestContext.Current.CancellationToken);

        Assert.False(preview.Found);
        Assert.Contains("binary", preview.Error!, StringComparison.OrdinalIgnoreCase);
        // The shared reader's "Error: " prefix is stripped so the preview reason reads plainly.
        Assert.False(preview.Error!.StartsWith("Error: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_DoesNotRecordStaleness()
    {
        var rel = WriteFile("notrack.txt", "a\nb\n");
        var full = Path.Combine(_root, rel);

        await _handler.ReadPromptPreviewAsync(rel, workingSubpath: null, maxLines: 100, TestContext.Current.CancellationToken);

        // Unlike read_file, a partial preview records no baseline — so an out-of-band change is NOT
        // flagged stale, and the model's own read_file before an edit still gets an honest signal.
        Assert.False(_staleness.CheckStaleness(Guid.Empty, full, File.GetLastWriteTimeUtc(full).AddSeconds(5)));
    }

    [Fact]
    public void CapForPrompt_CharBudgetBinds_BeforeLineCap()
    {
        // 100 lines of 100 chars each; a 1000-char budget admits far fewer than the 100-line cap.
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(_ => new string('x', 100)));

        var (capped, total, shown, truncated) = FilesToolHandler.CapForPrompt(text, maxLines: 100, maxChars: 1000);

        Assert.Equal(100, total);
        Assert.True(truncated);
        Assert.True(shown < 100);
        Assert.True(capped.Length <= 1000);
    }

    [Fact]
    public void CapForPrompt_AlwaysEmitsFirstLine_EvenWhenItAloneExceedsBudget()
    {
        var huge = new string('y', 5000);

        var (capped, total, shown, truncated) = FilesToolHandler.CapForPrompt(huge, maxLines: 100, maxChars: 1000);

        Assert.Equal(1, total);
        Assert.Equal(1, shown);
        Assert.False(truncated); // the whole single-line file is shown
        Assert.Equal(huge, capped);
    }

    [Theory]
    [InlineData("/c/docs/readme.md", "C:/docs/readme.md")]
    [InlineData("/C:/docs/readme.md", "C:/docs/readme.md")]
    [InlineData("/mnt/d/docs/readme.md", "D:/docs/readme.md")]
    [InlineData("/cygdrive/e/docs/readme.md", "E:/docs/readme.md")]
    [InlineData("/c", "C:/")]
    public void NormalizePathArg_RewritesPosixDriveSpellings(string input, string expected)
        => Assert.Equal(expected, FilesToolHandler.NormalizePathArg(input));

    [Theory]
    [InlineData("docs/readme.md")]
    [InlineData("C:/x")]
    [InlineData("/docs/x")]      // the lookahead stops "/d" matching here
    [InlineData("/config/x")]
    [InlineData("")]
    [InlineData(@"\\server\share\x.md")]
    public void NormalizePathArg_LeavesEverythingElseUntouched(string input)
        => Assert.Equal(input, FilesToolHandler.NormalizePathArg(input));

    [Fact]
    public async Task Read_PosixSpellingOfAbsoluteSandboxPath_Resolves()
    {
        WriteFile("posix.txt", "hello\n");
        var windows = Path.Combine(_root, "posix.txt");
        var posix = "/" + char.ToLowerInvariant(windows[0]) + windows.Replace('\\', '/')[2..];

        var result = (string)(await ReadAsync(posix))!;

        Assert.Contains("hello", result);
        Assert.DoesNotContain("not found", result);
    }

    [Fact]
    public async Task Read_NotFound_SuggestsASimilarSibling()
    {
        WriteFile("readme.md", "hi");

        var result = (string)(await ReadAsync("readme"))!;

        Assert.Contains("Error: File 'readme' not found.", result);
        Assert.Contains("Did you mean: readme.md?", result);
    }

    [Fact]
    public async Task Read_NotFound_SuggestionsAreCappedAtThree()
    {
        for (int i = 1; i <= 5; i++) WriteFile($"note{i}.txt", "x");

        var result = (string)(await ReadAsync("note"))!;

        var list = result[(result.IndexOf("Did you mean: ", StringComparison.Ordinal) + 14)..].TrimEnd('?');
        Assert.Equal(3, list.Split(", ").Length);
    }

    [Fact]
    public async Task Read_NotFound_NeverSuggestsAnIgnoredSibling()
    {
        WriteFile(".piaignore", "hidden-notes.txt\n");
        WriteFile("hidden-notes.txt", "x");
        WriteFile("shown-notes.txt", "x");

        var result = (string)(await ReadAsync("notes.txt"))!;

        Assert.Contains("shown-notes.txt", result);
        Assert.DoesNotContain("hidden-notes.txt", result);
    }

    [Fact]
    public async Task Read_NotFound_SuggestsInsideAnOrdinaryFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "open"));
        WriteFile(Path.Combine("open", "passwords.txt"), "x");

        var result = (string)(await ReadAsync("open/password"))!;

        Assert.Contains("Did you mean: open/passwords.txt?", result);
    }

    [Fact]
    public async Task Read_NotFound_DirectoryOnlyIgnoreRule_DoesNotLeakNamesInside()
    {
        WriteFile(".piaignore", "secret/\n");
        Directory.CreateDirectory(Path.Combine(_root, "secret"));
        WriteFile(Path.Combine("secret", "passwords.txt"), "x");

        var result = (string)(await ReadAsync("secret/password"))!;

        Assert.Contains("not found", result);
        Assert.DoesNotContain("Did you mean", result);
        Assert.DoesNotContain("passwords.txt", result);
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

/// <summary>
/// The one read-miss fact that needs a sandbox inside a root the live <c>SensitivePathGuard</c> blocks, which is
/// why it sits in its own class: it needs the redirected profile, and the redirect has to be serialized against
/// every other test that resolves a Pia path.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class FilesToolHandlerReadMissBlockedPathTests : IClassFixture<RedirectedProfileFixture>
{
    public FilesToolHandlerReadMissBlockedPathTests(RedirectedProfileFixture profile) => _ = profile;

    [Fact]
    public async Task Read_MissInsideABlockedRoot_NeverNamesASibling()
    {
        var blockedRoot = Path.Combine(PiaPaths.LocalDataDirectory, "pia-suggest-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blockedRoot);
        try
        {
            // Non-vacuity: the guard has to be the thing that hides the name, so assert it says so first.
            Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(blockedRoot, "credentials.txt"), out _));
            File.WriteAllText(Path.Combine(blockedRoot, "credentials.txt"), "x");

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = blockedRoot });
            var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

            var call = new FunctionCallContent("c1", "read_file",
                new Dictionary<string, object?> { ["path"] = "credentials" });
            var (result, _) = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

            Assert.DoesNotContain("credentials.txt", (string)result!);
        }
        finally
        {
            TempPath.Remove(blockedRoot);
        }
    }
}
