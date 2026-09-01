using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Pia.Helpers;
using Pia.Models;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Pia.Infrastructure;

/// <summary>
/// Patches an <c>.xlsx</c>'s cells to match the model's submitted replacement text (the same
/// sheet-blocked TSV shape <see cref="DroppedFileReader.ReadXlsxAsync"/> returns), touching only
/// the cells whose text actually changed. Each sheet named in the submission is diffed
/// INDEPENDENTLY against that sheet's own current rows — never against the whole-file text — so a
/// change to one sheet can never be misread as touching another sheet's rows, and a sheet the
/// submission omits entirely is left untouched (never deleted). v1 is append-only for rows: any
/// attempt to remove or mid-sheet-insert a row is rejected; a formula cell whose rendered text
/// would change is rejected by name.
///
/// Same two-call shape as <see cref="DocxPatcher"/>: <c>apply: false</c> at prepare time
/// (validate only, against a read-only-opened file), <c>apply: true</c> at execute time (after the
/// caller's mtime staleness check), so no OpenXml object needs to cross the approval boundary.
/// </summary>
public static class XlsxPatcher
{
    public readonly record struct PatchLimits(int MaxTouchedCells);

    public readonly record struct PatchResult(bool Success, string? Error)
    {
        public static readonly PatchResult Ok = new(true, null);
        public static PatchResult Fail(string error) => new(false, error);
    }

    private sealed record CellEdit(int Column, string Value);
    private sealed record SetCellOp(SS.Row Row, IReadOnlyList<CellEdit> Edits);
    private sealed record AppendRowOp(string SheetName, IReadOnlyList<string> Fields);

    public static PatchResult Apply(SpreadsheetDocument doc, string newContent, bool apply, PatchLimits limits)
    {
        var workbookPart = doc.WorkbookPart;
        if (workbookPart is null) return PatchResult.Fail("Workbook has no workbook part.");

        var walk = DroppedFileReader.WalkXlsxWorkbook(workbookPart);

        var baselineBySheet = new Dictionary<string, (List<string> Texts, List<SS.Row> Rows)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in walk.Lines)
        {
            if (line.Kind != DroppedFileReader.XlsxLineKind.Row) continue;
            if (!baselineBySheet.TryGetValue(line.SheetName, out var entry))
            {
                entry = (new List<string>(), new List<SS.Row>());
                baselineBySheet[line.SheetName] = entry;
            }
            entry.Texts.Add(line.Text);
            entry.Rows.Add(line.RowNode!);
        }

        var setCellOps = new List<SetCellOp>();
        var appendOps = new List<AppendRowOp>();
        int touchedCount = 0;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sheetName, rowTexts) in ParseSheetBlocksText(newContent))
        {
            if (!seenNames.Add(sheetName))
                return PatchResult.Fail($"Sheet '{sheetName}' appears more than once in the submitted content.");

            if (walk.SheetsByName.ContainsKey(sheetName))
            {
                var baseline = baselineBySheet.TryGetValue(sheetName, out var b) ? b : (Texts: [], Rows: []);
                var rowDiff = LineDiff.Compute(string.Join('\n', baseline.Texts), string.Join('\n', rowTexts));

                var error = ApplySheetRowDiff(sheetName, baseline.Rows, rowDiff, setCellOps, appendOps, ref touchedCount);
                if (error is not null) return PatchResult.Fail(error);
            }
            else
            {
                // Brand-new sheet: every submitted row is a fresh append, in submitted order.
                foreach (var text in rowTexts)
                {
                    var fields = text.Length == 0 ? [] : text.Split('\t');
                    appendOps.Add(new AppendRowOp(sheetName, fields));
                    touchedCount += fields.Length;
                }
            }
        }

        if (touchedCount > limits.MaxTouchedCells)
            return PatchResult.Fail(
                $"This write changes {touchedCount} cells in one call (max {limits.MaxTouchedCells}). Split the edit into smaller write_file calls.");

        if (!apply) return PatchResult.Ok;

        foreach (var op in setCellOps)
        {
            var rowNumber = op.Row.RowIndex?.Value ?? 0u;
            foreach (var edit in op.Edits)
                SetCellValue(GetOrCreateCell(op.Row, edit.Column, rowNumber), edit.Value);
        }

        foreach (var group in appendOps.GroupBy(o => o.SheetName, StringComparer.OrdinalIgnoreCase))
        {
            var wsPart = walk.SheetsByName.TryGetValue(group.Key, out var existing)
                ? existing
                : CreateNewWorksheet(workbookPart, group.Key);
            var sheetData = wsPart.Worksheet!.GetFirstChild<SS.SheetData>() ?? wsPart.Worksheet.AppendChild(new SS.SheetData());
            var nextRowIndex = MaxRowIndex(wsPart.Worksheet) + 1;

            foreach (var op in group)
            {
                var row = new SS.Row { RowIndex = nextRowIndex };
                for (int col = 0; col < op.Fields.Count; col++)
                {
                    var cell = new SS.Cell { CellReference = ColumnLetter(col) + nextRowIndex };
                    SetCellValue(cell, op.Fields[col]);
                    row.AppendChild(cell);
                }
                sheetData.AppendChild(row);
                nextRowIndex++;
            }
        }

        return PatchResult.Ok;
    }

    /// <summary>Applies one sheet's row-only diff (baseline rows vs. submitted rows for that SAME
    /// sheet — never mixed with another sheet's lines) using the same block-pairing approach as
    /// <see cref="DocxPatcher"/>: a multi-row edit emits all its Removed rows before all its Added
    /// rows, so they're paired positionally within one contiguous non-Context run rather than only
    /// ever catching an adjacent Removed-then-Added single-row edit. Returns an error message, or
    /// null on success.</summary>
    private static string? ApplySheetRowDiff(
        string sheetName, List<SS.Row> baselineRows, IReadOnlyList<DiffLine> rowDiff,
        List<SetCellOp> setCellOps, List<AppendRowOp> appendOps, ref int touchedCount)
    {
        int pos = 0;
        while (pos < rowDiff.Count)
        {
            if (rowDiff[pos].Kind == DiffLineKind.Context) { pos++; continue; }

            var removedRows = new List<SS.Row>();
            var removedTexts = new List<string>();
            var addedTexts = new List<string>();
            while (pos < rowDiff.Count && rowDiff[pos].Kind != DiffLineKind.Context)
            {
                if (rowDiff[pos].Kind == DiffLineKind.Removed)
                {
                    removedRows.Add(baselineRows[rowDiff[pos].OldLineNumber!.Value - 1]);
                    removedTexts.Add(rowDiff[pos].Text);
                }
                else
                {
                    addedTexts.Add(rowDiff[pos].Text);
                }
                pos++;
            }

            int pairCount = Math.Min(removedRows.Count, addedTexts.Count);
            for (int k = 0; k < pairCount; k++)
            {
                var edits = BuildCellEdits(sheetName, removedRows[k], removedTexts[k], addedTexts[k], out var error);
                if (error is not null) return error;
                if (edits.Count > 0)
                {
                    setCellOps.Add(new SetCellOp(removedRows[k], edits));
                    touchedCount += edits.Count;
                }
            }

            if (removedRows.Count > pairCount)
            {
                var extra = removedRows[pairCount];
                return $"Cannot remove row {extra.RowIndex?.Value} from sheet '{sheetName}' — deleting existing " +
                       "rows isn't supported. Clear the cell values instead, or leave the row unchanged.";
            }

            if (addedTexts.Count > pairCount)
            {
                // Trailing-append validity: any surviving (Context/paired) row for this sheet still
                // ahead in ITS OWN row-only diff means this isn't the true end.
                bool laterExistingRow = false;
                for (int j = pos; j < rowDiff.Count; j++)
                    if (rowDiff[j].Kind is DiffLineKind.Context or DiffLineKind.Removed) { laterExistingRow = true; break; }

                if (laterExistingRow)
                    return $"Cannot insert a new row in the middle of sheet '{sheetName}' — new rows can only be " +
                           "appended at the end. Move this content after the sheet's last existing row.";

                for (int k = pairCount; k < addedTexts.Count; k++)
                {
                    var fields = addedTexts[k].Length == 0 ? [] : addedTexts[k].Split('\t');
                    appendOps.Add(new AppendRowOp(sheetName, fields));
                    touchedCount += fields.Length;
                }
            }
        }
        return null;
    }

    private static List<CellEdit> BuildCellEdits(string sheetName, SS.Row row, string oldText, string newText, out string? error)
    {
        error = null;
        var oldFields = oldText.Length == 0 ? [] : oldText.Split('\t');
        var newFields = newText.Length == 0 ? [] : newText.Split('\t');
        var edits = new List<CellEdit>();

        for (int col = 0; col < newFields.Length; col++)
        {
            var newVal = newFields[col];
            var oldVal = col < oldFields.Length ? oldFields[col] : "";
            if (newVal == oldVal) continue;

            var existingCell = FindCell(row, col);
            if (existingCell?.CellFormula is not null)
            {
                var rowNumber = row.RowIndex?.Value ?? 0u;
                error = $"Cell {ColumnLetter(col)}{rowNumber} on sheet '{sheetName}' has a formula and would be " +
                        "overwritten by this edit — formulas can't be changed by write_file. Leave that cell out of the edit.";
                return [];
            }
            edits.Add(new CellEdit(col, newVal));
        }
        // Fields present in the old row but beyond the new row's length are left alone — trailing
        // TSV cells are ambiguous (the reader trims trailing-empty cells, so "shorter" can't be told
        // apart from "unchanged"), never inferred as a clear-to-empty.

        return edits;
    }

    /// <summary>Builds a brand-new minimal xlsx directly from the model's sheet-blocked TSV text —
    /// used when the target path has no baseline to preserve. No header at all defaults to "Sheet1".</summary>
    public static void CreateFresh(string tempPath, string content)
    {
        using var doc = SpreadsheetDocument.Create(tempPath, SpreadsheetDocumentType.Workbook);
        var workbookPart = doc.AddWorkbookPart();
        workbookPart.Workbook = new SS.Workbook();
        workbookPart.Workbook.AppendChild(new SS.Sheets());

        foreach (var (name, rowTexts) in ParseSheetBlocksText(content))
        {
            var wsPart = CreateNewWorksheet(workbookPart, name);
            var sheetData = wsPart.Worksheet!.GetFirstChild<SS.SheetData>()!;
            uint rowNum = 1;
            foreach (var text in rowTexts)
            {
                var fields = text.Length == 0 ? [] : text.Split('\t');
                var row = new SS.Row { RowIndex = rowNum };
                for (int col = 0; col < fields.Length; col++)
                {
                    var cell = new SS.Cell { CellReference = ColumnLetter(col) + rowNum };
                    SetCellValue(cell, fields[col]);
                    row.AppendChild(cell);
                }
                sheetData.AppendChild(row);
                rowNum++;
            }
        }

        workbookPart.Workbook.Save();
    }

    /// <summary>Splits sheet-blocked TSV text (the shape <see cref="DroppedFileReader.ReadXlsxAsync"/>
    /// emits) into (sheet name, ordered row texts) blocks. No header at all defaults to "Sheet1".
    /// Shared by <see cref="Apply"/> (diffed against the current workbook) and <see cref="CreateFresh"/>
    /// (authored directly, no baseline).</summary>
    private static List<(string Name, List<string> RowTexts)> ParseSheetBlocksText(string content)
    {
        var result = new List<(string, List<string>)>();
        if (content.Length == 0) return result;

        string currentName = "Sheet1";
        List<string> currentRows = [];
        bool started = false;

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("## Sheet: ", StringComparison.Ordinal))
            {
                if (started) result.Add((currentName, currentRows));
                currentName = raw["## Sheet: ".Length..].Trim();
                currentRows = [];
                started = true;
                continue;
            }
            if (raw.Trim().Length == 0) continue;
            started = true;
            currentRows.Add(raw);
        }
        result.Add((currentName, currentRows));
        return result;
    }

    private static WorksheetPart CreateNewWorksheet(WorkbookPart workbookPart, string sheetName)
    {
        var wsPart = workbookPart.AddNewPart<WorksheetPart>();
        wsPart.Worksheet = new SS.Worksheet(new SS.SheetData());

        var sheets = workbookPart.Workbook!.GetFirstChild<SS.Sheets>() ?? workbookPart.Workbook.AppendChild(new SS.Sheets());
        var newSheetId = sheets.Elements<SS.Sheet>().Select(s => s.SheetId?.Value ?? 0u).DefaultIfEmpty(0u).Max() + 1;
        sheets.AppendChild(new SS.Sheet
        {
            Id = workbookPart.GetIdOfPart(wsPart),
            SheetId = newSheetId,
            Name = sheetName,
        });
        return wsPart;
    }

    private static uint MaxRowIndex(SS.Worksheet worksheet)
        => worksheet.Descendants<SS.Row>().Select(r => r.RowIndex?.Value ?? 0u).DefaultIfEmpty(0u).Max();

    private static SS.Cell? FindCell(SS.Row row, int col0)
    {
        foreach (var c in row.Elements<SS.Cell>())
            if (DroppedFileReader.ColumnIndex(c.CellReference?.Value) == col0) return c;
        return null;
    }

    private static SS.Cell GetOrCreateCell(SS.Row row, int col0, uint rowNumber)
    {
        SS.Cell? insertBefore = null;
        foreach (var c in row.Elements<SS.Cell>())
        {
            var idx = DroppedFileReader.ColumnIndex(c.CellReference?.Value);
            if (idx == col0) return c;
            if (idx > col0) { insertBefore = c; break; }
        }
        var cell = new SS.Cell { CellReference = ColumnLetter(col0) + rowNumber };
        if (insertBefore is not null) row.InsertBefore(cell, insertBefore);
        else row.AppendChild(cell);
        return cell;
    }

    private static void SetCellValue(SS.Cell cell, string value)
    {
        cell.RemoveAllChildren<SS.CellValue>();
        cell.RemoveAllChildren<SS.InlineString>();

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            cell.DataType = SS.CellValues.Number;
            cell.CellValue = new SS.CellValue(num.ToString("R", CultureInfo.InvariantCulture));
        }
        else if (value is "TRUE" or "FALSE")
        {
            cell.DataType = SS.CellValues.Boolean;
            cell.CellValue = new SS.CellValue(value == "TRUE" ? "1" : "0");
        }
        else
        {
            cell.DataType = SS.CellValues.InlineString;
            var text = new SS.Text(value);
            if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
                text.Space = SpaceProcessingModeValues.Preserve;
            cell.InlineString = new SS.InlineString(text);
        }
    }

    private static string ColumnLetter(int index0)
    {
        int n = index0 + 1;
        var chars = new Stack<char>();
        while (n > 0)
        {
            int rem = (n - 1) % 26;
            chars.Push((char)('A' + rem));
            n = (n - 1) / 26;
        }
        return new string(chars.ToArray());
    }
}
