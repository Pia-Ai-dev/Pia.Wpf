using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Pia.Helpers;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class DocxPatcherTests : IDisposable
{
    private readonly string _dir;
    private static readonly DocxPatcher.PatchLimits DefaultLimits = new(MaxTouchedNodes: 2000, MaxRemovedAbsolute: 50, MaxRemovedFraction: 0.4);

    public DocxPatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-docxpatcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string NewPath(string name) => Path.Combine(_dir, name);

    private static void CreateDocx(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        foreach (var p in paragraphs)
            body.Append(new Paragraph(new Run(new Text(p))));
        main.Document.Save();
    }

    private static void CreateDocxWithBoldFirstRun(string path, string paragraph1, string paragraph2)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        var run = new Run(new RunProperties(new Bold()), new Text(paragraph1));
        body.Append(new Paragraph(run));
        body.Append(new Paragraph(new Run(new Text(paragraph2))));
        main.Document.Save();
    }

    private static void CreateDocxWithTableCellParagraph(string path, string cellText)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);

        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell(new Paragraph(new Run(new Text(cellText))));
        row.Append(cell);
        table.Append(row);
        body.Append(table);
        main.Document.Save();
    }

    private static string ReadText(string path)
        => DroppedFileReader.ReadDocxAsync(path, CancellationToken.None).GetAwaiter().GetResult().Text!;

    private static List<string> BodyParagraphTexts(string path)
    {
        using var doc = WordprocessingDocument.Open(path, isEditable: false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        return body.Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text)))
            .ToList();
    }

    private static DocxPatcher.PatchResult Patch(string path, string newContent, DocxPatcher.PatchLimits? limits = null)
    {
        var oldText = ReadText(path);
        var diff = Pia.Infrastructure.LineDiff.Compute(oldText, newContent);

        using (var validateDoc = WordprocessingDocument.Open(path, isEditable: false))
        {
            var dryRun = DocxPatcher.Apply(validateDoc, diff, apply: false, limits ?? DefaultLimits);
            if (!dryRun.Success) return dryRun;
        }

        using var doc = WordprocessingDocument.Open(path, isEditable: true);
        var result = DocxPatcher.Apply(doc, diff, apply: true, limits ?? DefaultLimits);
        if (result.Success) doc.MainDocumentPart!.Document!.Save();
        return result;
    }

    [Fact]
    public void Replace_EditsOnlyTheChangedParagraph()
    {
        var path = NewPath("a.docx");
        CreateDocxWithBoldFirstRun(path, "Para 1", "Para 2");

        var result = Patch(path, "Para 1\nPara 2 EDITED");

        Assert.True(result.Success, result.Error);
        var texts = BodyParagraphTexts(path);
        Assert.Equal(["Para 1", "Para 2 EDITED"], texts);

        // The untouched paragraph's bold run formatting survives the replace of the OTHER paragraph.
        using var doc = WordprocessingDocument.Open(path, isEditable: false);
        var firstRun = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().First().Elements<Run>().First();
        Assert.NotNull(firstRun.RunProperties?.Bold);
    }

    [Fact]
    public void Replace_PreservesFirstRunFormatting_OnTheEditedParagraphItself()
    {
        var path = NewPath("a.docx");
        CreateDocxWithBoldFirstRun(path, "Para 1", "Para 2");

        var result = Patch(path, "Para 1 EDITED\nPara 2");

        Assert.True(result.Success, result.Error);
        using var doc = WordprocessingDocument.Open(path, isEditable: false);
        var p1 = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().First();
        Assert.Equal("Para 1 EDITED", string.Concat(p1.Descendants<Text>().Select(t => t.Text)));
        Assert.NotNull(p1.Elements<Run>().First().RunProperties?.Bold);
    }

    [Fact]
    public void BlankParagraph_SurvivesAnEditNearby()
    {
        var path = NewPath("a.docx");
        CreateDocx(path, "Para 1", "", "Para 2");

        // The blank paragraph is invisible to the reader — baseline text is "Para 1\nPara 2".
        Assert.Equal("Para 1\r\nPara 2", ReadText(path));

        var result = Patch(path, "Para 1\nPara 2 EDITED");

        Assert.True(result.Success, result.Error);
        var texts = BodyParagraphTexts(path);
        Assert.Equal(["Para 1", "", "Para 2 EDITED"], texts);
    }

    [Fact]
    public void Insert_AddsNewParagraphAtCorrectPosition()
    {
        var path = NewPath("a.docx");
        CreateDocx(path, "Para 1", "Para 2");

        var result = Patch(path, "Para 1\nNew Para\nPara 2");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Para 1", "New Para", "Para 2"], BodyParagraphTexts(path));
    }

    [Fact]
    public void Insert_AtDocumentEnd_Appends()
    {
        var path = NewPath("a.docx");
        CreateDocx(path, "Para 1", "Para 2");

        var result = Patch(path, "Para 1\nPara 2\nPara 3");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Para 1", "Para 2", "Para 3"], BodyParagraphTexts(path));
    }

    [Fact]
    public void Append_WithTrailingSectionProperties_InsertsBeforeIt_NotAfter()
    {
        // Nearly every real Word document ends its body with a w:sectPr, which per the schema
        // MUST be the body's last child — appending after it produces a schema-invalid package.
        var path = NewPath("a.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            body.Append(new Paragraph(new Run(new Text("Para 1"))));
            body.Append(new SectionProperties(new PageSize { Width = 12240, Height = 15840 }));
            main.Document.Save();
        }

        var result = Patch(path, "Para 1\nPara 2");

        Assert.True(result.Success, result.Error);
        using var check = WordprocessingDocument.Open(path, isEditable: false);
        var body2 = check.MainDocumentPart!.Document!.Body!;
        Assert.IsType<SectionProperties>(body2.LastChild);
        Assert.Equal(["Para 1", "Para 2"], BodyParagraphTexts(path));
    }

    [Fact]
    public void Delete_RemovesParagraph()
    {
        var path = NewPath("a.docx");
        CreateDocx(path, "Para 1", "Para 2", "Para 3");

        var result = Patch(path, "Para 1\nPara 3");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Para 1", "Para 3"], BodyParagraphTexts(path));
    }

    [Fact]
    public void Delete_LastParagraphInTableCell_IsRejected()
    {
        var path = NewPath("a.docx");
        CreateDocxWithTableCellParagraph(path, "Cell text");

        var result = Patch(path, "");

        Assert.False(result.Success);
        Assert.Contains("table cell", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Edit_TableCellParagraphText_Succeeds()
    {
        var path = NewPath("a.docx");
        CreateDocxWithTableCellParagraph(path, "Cell text");

        var result = Patch(path, "Cell text EDITED");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Cell text EDITED", BodyParagraphTexts(path));
    }

    [Fact]
    public void DeletionGuard_RejectsMassRemoval()
    {
        var path = NewPath("a.docx");
        CreateDocx(path, "P1", "P2", "P3", "P4", "P5");
        var strict = new DocxPatcher.PatchLimits(MaxTouchedNodes: 2000, MaxRemovedAbsolute: 1, MaxRemovedFraction: 0.2);

        var result = Patch(path, "P1", strict);

        Assert.False(result.Success);
        Assert.Contains("partial read", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TouchedNodeCap_RejectsTooManyChanges()
    {
        var path = NewPath("a.docx");
        CreateDocx(path, "P1", "P2");
        var strict = new DocxPatcher.PatchLimits(MaxTouchedNodes: 1, MaxRemovedAbsolute: 50, MaxRemovedFraction: 0.9);

        var result = Patch(path, "P1 EDITED\nP2 EDITED", strict);

        Assert.False(result.Success);
        Assert.Contains("Split the edit", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFresh_ProducesReadableDocx()
    {
        var path = NewPath("new.docx");

        DocxPatcher.CreateFresh(path, "Line 1\nLine 2\nLine 3");

        Assert.Equal("Line 1\r\nLine 2\r\nLine 3", ReadText(path));
    }
}
