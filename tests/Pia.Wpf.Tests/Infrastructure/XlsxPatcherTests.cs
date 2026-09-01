using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Pia.Helpers;
using Pia.Infrastructure;
using Xunit;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Pia.Tests.Infrastructure;

public class XlsxPatcherTests : IDisposable
{
    private readonly string _dir;
    private static readonly XlsxPatcher.PatchLimits DefaultLimits = new(MaxTouchedCells: 2000);

    public XlsxPatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-xlsxpatcher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string NewPath(string name) => Path.Combine(_dir, name);

    private static SS.Cell MakeCell(string reference, string value, string? styleIndex = null)
    {
        var cell = new SS.Cell
        {
            CellReference = reference,
            DataType = SS.CellValues.String,
            CellValue = new SS.CellValue(value),
        };
        if (styleIndex is not null) cell.StyleIndex = uint.Parse(styleIndex);
        return cell;
    }

    private static void CreateXlsx(string path, string sheetName, params (string Ref, string Value)[] cells)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new SS.Workbook();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();

        var row = new SS.Row { RowIndex = 1 };
        foreach (var (r, v) in cells) row.Append(MakeCell(r, v));
        wsPart.Worksheet = new SS.Worksheet(new SS.SheetData(row));

        var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
        sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = sheetName });
        wbPart.Workbook.Save();
    }

    private static void CreateXlsxWithFormula(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new SS.Workbook();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();

        var row = new SS.Row { RowIndex = 1 };
        row.Append(MakeCell("A1", "2"));
        row.Append(MakeCell("B1", "3"));
        var formulaCell = new SS.Cell { CellReference = "C1", CellFormula = new SS.CellFormula("A1+B1"), CellValue = new SS.CellValue("5"), DataType = SS.CellValues.Number };
        row.Append(formulaCell);
        wsPart.Worksheet = new SS.Worksheet(new SS.SheetData(row));

        var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
        sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });
        wbPart.Workbook.Save();
    }

    private static string ReadText(string path)
        => DroppedFileReader.ReadXlsxAsync(path, CancellationToken.None).GetAwaiter().GetResult().Text!;

    private static XlsxPatcher.PatchResult Patch(string path, string newContent, XlsxPatcher.PatchLimits? limits = null)
    {
        using (var validateDoc = SpreadsheetDocument.Open(path, isEditable: false))
        {
            var dryRun = XlsxPatcher.Apply(validateDoc, newContent, apply: false, limits ?? DefaultLimits);
            if (!dryRun.Success) return dryRun;
        }

        using var doc = SpreadsheetDocument.Open(path, isEditable: true);
        var result = XlsxPatcher.Apply(doc, newContent, apply: true, limits ?? DefaultLimits);
        if (result.Success) doc.WorkbookPart!.Workbook!.Save();
        return result;
    }

    [Fact]
    public void EditOneCell_LeavesOthersUntouched()
    {
        var path = NewPath("a.xlsx");
        CreateXlsx(path, "Data", ("A1", "one"), ("B1", "two"), ("C1", "three"));

        var result = Patch(path, "## Sheet: Data\none\tTWO EDITED\tthree");

        Assert.True(result.Success, result.Error);
        Assert.Contains("one\tTWO EDITED\tthree", ReadText(path));
    }

    [Fact]
    public void EditOneCell_PreservesStyleIndexOfEditedCell()
    {
        var path = NewPath("a.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new SS.Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var row = new SS.Row { RowIndex = 1 };
            row.Append(MakeCell("A1", "one", styleIndex: "3"));
            wsPart.Worksheet = new SS.Worksheet(new SS.SheetData(row));
            var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });
            wbPart.Workbook.Save();
        }

        var result = Patch(path, "## Sheet: Data\nEDITED");

        Assert.True(result.Success, result.Error);
        using var check = SpreadsheetDocument.Open(path, isEditable: false);
        var cell = check.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<SS.Cell>().First();
        Assert.Equal(3u, cell.StyleIndex?.Value);
    }

    [Fact]
    public void FormulaCell_ChangedByEdit_IsRejected()
    {
        var path = NewPath("a.xlsx");
        CreateXlsxWithFormula(path);

        var result = Patch(path, "## Sheet: Data\n2\t3\t99");

        Assert.False(result.Success);
        Assert.Contains("formula", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormulaCell_UnchangedByEdit_Survives()
    {
        var path = NewPath("a.xlsx");
        CreateXlsxWithFormula(path);

        var result = Patch(path, "## Sheet: Data\n20\t3\t5");

        Assert.True(result.Success, result.Error);
        using var check = SpreadsheetDocument.Open(path, isEditable: false);
        var formulaCell = check.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<SS.Cell>()
            .First(c => c.CellReference == "C1");
        Assert.NotNull(formulaCell.CellFormula);
    }

    [Fact]
    public void AppendRow_AtEnd_Succeeds()
    {
        var path = NewPath("a.xlsx");
        CreateXlsx(path, "Data", ("A1", "one"));

        var result = Patch(path, "## Sheet: Data\none\ntwo");

        Assert.True(result.Success, result.Error);
        Assert.Contains("two", ReadText(path));
    }

    [Fact]
    public void MidSheetRowInsert_IsRejected()
    {
        var path = NewPath("a.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new SS.Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SS.SheetData();
            var row1 = new SS.Row { RowIndex = 1 };
            row1.Append(MakeCell("A1", "one"));
            var row2 = new SS.Row { RowIndex = 2 };
            row2.Append(MakeCell("A2", "two"));
            sheetData.Append(row1, row2);
            wsPart.Worksheet = new SS.Worksheet(sheetData);
            var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });
            wbPart.Workbook.Save();
        }

        var result = Patch(path, "## Sheet: Data\none\nnew middle row\ntwo");

        Assert.False(result.Success);
        Assert.Contains("middle", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RowRemoval_IsRejected()
    {
        var path = NewPath("a.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new SS.Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SS.SheetData();
            var row1 = new SS.Row { RowIndex = 1 };
            row1.Append(MakeCell("A1", "one"));
            var row2 = new SS.Row { RowIndex = 2 };
            row2.Append(MakeCell("A2", "two"));
            sheetData.Append(row1, row2);
            wsPart.Worksheet = new SS.Worksheet(sheetData);
            var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });
            wbPart.Workbook.Save();
        }

        var result = Patch(path, "## Sheet: Data\none");

        Assert.False(result.Success);
        Assert.Contains("remove", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SheetOmittedFromNewContent_IsPreservedNotDeleted()
    {
        var path = NewPath("a.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new SS.Workbook();
            var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());

            var ws1 = wbPart.AddNewPart<WorksheetPart>();
            var row1 = new SS.Row { RowIndex = 1 };
            row1.Append(MakeCell("A1", "sheet1-data"));
            ws1.Worksheet = new SS.Worksheet(new SS.SheetData(row1));
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(ws1), SheetId = 1, Name = "First" });

            var ws2 = wbPart.AddNewPart<WorksheetPart>();
            var row2 = new SS.Row { RowIndex = 1 };
            row2.Append(MakeCell("A1", "sheet2-data"));
            ws2.Worksheet = new SS.Worksheet(new SS.SheetData(row2));
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(ws2), SheetId = 2, Name = "Second" });

            wbPart.Workbook.Save();
        }

        // New content only mentions "First" — "Second" must survive untouched.
        var result = Patch(path, "## Sheet: First\nsheet1-data EDITED");

        Assert.True(result.Success, result.Error);
        var text = ReadText(path);
        Assert.Contains("sheet1-data EDITED", text);
        Assert.Contains("sheet2-data", text);
    }

    [Fact]
    public void NewSheet_IsCreated()
    {
        var path = NewPath("a.xlsx");
        CreateXlsx(path, "Data", ("A1", "one"));

        var result = Patch(path, "## Sheet: Data\none\n\n## Sheet: NewSheet\nhello\tworld");

        Assert.True(result.Success, result.Error);
        var text = ReadText(path);
        Assert.Contains("## Sheet: NewSheet", text);
        Assert.Contains("hello\tworld", text);
    }

    [Fact]
    public void ReorderedSheets_AreMatchedByNameNotPosition()
    {
        var path = NewPath("c.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new SS.Workbook();
            var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
            var ws1 = wbPart.AddNewPart<WorksheetPart>();
            var row1 = new SS.Row { RowIndex = 1 };
            row1.Append(MakeCell("A1", "data-value"));
            ws1.Worksheet = new SS.Worksheet(new SS.SheetData(row1));
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(ws1), SheetId = 1, Name = "Data" });
            var ws2 = wbPart.AddNewPart<WorksheetPart>();
            var row2 = new SS.Row { RowIndex = 1 };
            row2.Append(MakeCell("A1", "other-value"));
            ws2.Worksheet = new SS.Worksheet(new SS.SheetData(row2));
            sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(ws2), SheetId = 2, Name = "Other" });
            wbPart.Workbook.Save();
        }

        // Submitted content lists "Other" before "Data" (the reverse of the workbook's own order)
        // and edits Data's cell — sheets are matched by name, so this is well-defined, not rejected.
        var result = Patch(path, "## Sheet: Other\nother-value\n\n## Sheet: Data\ndata-value EDITED");

        Assert.True(result.Success, result.Error);
        var text = ReadText(path);
        Assert.Contains("data-value EDITED", text);
        Assert.Contains("other-value", text);
    }

    [Fact]
    public void DuplicateSheetNameInSubmittedContent_IsRejected()
    {
        var path = NewPath("a.xlsx");
        CreateXlsx(path, "Data", ("A1", "one"));

        var result = Patch(path, "## Sheet: Data\none\n\n## Sheet: Data\ntwo");

        Assert.False(result.Success);
        Assert.Contains("more than once", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateFresh_ProducesReadableXlsx()
    {
        var path = NewPath("new.xlsx");

        XlsxPatcher.CreateFresh(path, "## Sheet: Sheet1\na1\tb1\na2\tb2");

        var text = ReadText(path);
        Assert.Contains("a1\tb1", text);
        Assert.Contains("a2\tb2", text);
    }
}
