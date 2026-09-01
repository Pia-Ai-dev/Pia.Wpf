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

namespace Pia.Tests.Services;

/// <summary>
/// edit_file exists so a one-word change stops requiring the whole file back — the re-emission is what
/// let read_file's line-number prefixes reach the document. It rejoins write_file's pipeline once the
/// new content is resolved, so these cover the resolution and the guards that are its own.
/// </summary>
public class FilesToolHandlerEditTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerEditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-edit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        TempPath.Remove(_root);
    }

    private async Task<(object? Result, FilesToolCall? Pending)> Edit(
        string path, string oldString, string newString, bool? replaceAll = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["old_string"] = oldString,
            ["new_string"] = newString,
        };
        if (replaceAll is not null) args["replace_all"] = replaceAll.Value;
        return await _handler.HandleToolCallAsync(new FunctionCallContent("c1", "edit_file", args));
    }

    private static T Prop<T>(object obj, string name)
    {
        var p = obj.GetType().GetProperty(name);
        Assert.NotNull(p);
        return (T)p!.GetValue(obj)!;
    }

    private static void CreateDocx(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();
        main.Document.Append(body);
        foreach (var p in paragraphs)
            body.Append(new Paragraph(new DW.Run(new DW.Text(p))));
        main.Document.Save();
    }

    private static List<string> BodyParagraphTexts(string path)
    {
        using var doc = WordprocessingDocument.Open(path, isEditable: false);
        return doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<DW.Text>().Select(t => t.Text)))
            .ToList();
    }

    [Fact]
    public async Task Edit_ReplacesTheMatch_AndLeavesEverythingElse()
    {
        var full = Path.Combine(_root, "notes.txt");
        File.WriteAllText(full, "alpha\nVersion: 1.0\ngamma\n");

        var (_, pending) = await Edit("notes.txt", "Version: 1.0", "Version: DRAFT");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.Equal(["alpha", "Version: DRAFT", "gamma"], File.ReadAllLines(full));
    }

    // The whole point: the model sends two short strings, not 500 lines, and the untouched paragraphs
    // are never re-typed and so cannot be mistranscribed.
    [Fact]
    public async Task Edit_OnDocx_TouchesOnlyTheMatchedParagraph()
    {
        var full = Path.Combine(_root, "doc.docx");
        CreateDocx(full, "Title", "Version: 1.0", "Main Features: a long line that must survive verbatim", "End");

        var (_, pending) = await Edit("doc.docx", "Version: 1.0", "Version: 0.9");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.Equal(
            ["Title", "Version: 0.9", "Main Features: a long line that must survive verbatim", "End"],
            BodyParagraphTexts(full));
    }

    [Fact]
    public async Task Edit_NoMatch_IsRejected_WithNoActionCard()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "alpha\nbeta\n");

        var (result, pending) = await Edit("notes.txt", "not present", "x");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("not found", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_AmbiguousMatch_IsRejected_AndNamesTheCount()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "todo\nalpha\ntodo\n");

        var (result, pending) = await Edit("notes.txt", "todo", "done");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("2 places", Prop<string?>(result!, "error")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_ReplaceAll_ChangesEveryOccurrence()
    {
        var full = Path.Combine(_root, "notes.txt");
        File.WriteAllText(full, "todo\nalpha\ntodo\n");

        var (_, pending) = await Edit("notes.txt", "todo", "done", replaceAll: true);
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.Equal(["done", "alpha", "done"], File.ReadAllLines(full));
    }

    [Fact]
    public async Task Edit_EmptyNewString_DeletesTheMatchedText()
    {
        var full = Path.Combine(_root, "notes.txt");
        File.WriteAllText(full, "alpha\nDRAFT ONLY\nbeta\n");

        var (_, pending) = await Edit("notes.txt", "DRAFT ONLY\n", "");
        Assert.NotNull(pending);
        var result = await pending!.Execute();

        Assert.True(Prop<bool>(result!, "success"), Prop<string?>(result!, "error"));
        Assert.Equal(["alpha", "beta"], File.ReadAllLines(full));
    }

    [Fact]
    public async Task Edit_MissingFile_PointsAtWriteFile()
    {
        var (result, pending) = await Edit("nope.txt", "a", "b");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("write_file", Prop<string?>(result!, "error")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edit_IdenticalStrings_IsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "alpha\n");

        var (result, pending) = await Edit("notes.txt", "alpha", "alpha");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("identical", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Edit_OutsideTheSandbox_IsRejected()
    {
        var (result, pending) = await Edit("../escape.txt", "a", "b");

        Assert.Null(pending);
        Assert.False(Prop<bool>(result!, "success"));
        Assert.Contains("outside", Prop<string?>(result!, "error")!, StringComparison.OrdinalIgnoreCase);
    }

    // The approval card is the safety net and edit_file must not slip past it.
    [Fact]
    public async Task Edit_RaisesAnApprovalCard_NamedForTheTool()
    {
        var full = Path.Combine(_root, "notes.txt");
        File.WriteAllText(full, "alpha\nbeta\n");

        var (result, pending) = await Edit("notes.txt", "beta", "gamma");

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("edit_file", pending!.ToolName);
        Assert.Equal("alpha\nbeta\n", File.ReadAllText(full)); // nothing written before approval
    }
}
