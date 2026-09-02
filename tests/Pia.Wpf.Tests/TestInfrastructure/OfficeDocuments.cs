using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Pia.Tests.TestInfrastructure;

/// <summary>Minimal on-disk .docx/.xlsx fixtures for tests that need a real OpenXml package.</summary>
public static class OfficeDocuments
{
    public static void CreateDocx(string path, params string[] paragraphs)
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

    public static void CreateXlsx(string path, string sheetName, params (string Ref, string Value)[] cells)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new SS.Workbook();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();

        var row = new SS.Row { RowIndex = 1 };
        foreach (var (r, v) in cells)
            row.Append(new SS.Cell { CellReference = r, DataType = SS.CellValues.String, CellValue = new SS.CellValue(v) });
        wsPart.Worksheet = new SS.Worksheet(new SS.SheetData(row));

        var sheets = wbPart.Workbook.AppendChild(new SS.Sheets());
        sheets.Append(new SS.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = sheetName });
        wbPart.Workbook.Save();
    }
}
