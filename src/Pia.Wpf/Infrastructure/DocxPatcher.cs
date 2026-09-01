using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Pia.Helpers;
using Pia.Models;

namespace Pia.Infrastructure;

/// <summary>
/// Patches a <c>.docx</c>'s paragraphs to match a diff computed against
/// <see cref="DroppedFileReader.ReadDocxAsync"/>'s extracted text, touching only the paragraphs
/// whose text actually changed — everything else in the package (styles, headers/footers, images,
/// other parts) is never opened, so it stays byte-identical by construction.
///
/// <see cref="Apply"/> is called twice against independently-opened instances of the identical
/// file: once at prepare time (<c>apply: false</c>, validate only) and once at execute time
/// (<c>apply: true</c>, after the caller's mtime staleness check proves the file hasn't changed).
/// Because planning re-derives real paragraph references fresh from whatever document it's given,
/// no OpenXml object needs to cross the approval boundary — only the plain <see cref="DiffLine"/>
/// list does, exactly like <c>write_file</c>'s existing text path already carries its content.
/// </summary>
public static class DocxPatcher
{
    public readonly record struct PatchLimits(int MaxTouchedNodes, int MaxRemovedAbsolute, double MaxRemovedFraction);

    public readonly record struct PatchResult(bool Success, string? Error)
    {
        public static readonly PatchResult Ok = new(true, null);
        public static PatchResult Fail(string error) => new(false, error);
    }

    private enum DocxOpKind { Replace, InsertBefore, Append, Delete }

    private sealed record DocxOp(DocxOpKind Kind, int? Ordinal, string Text)
    {
        public static DocxOp Replace(int ordinal, string text) => new(DocxOpKind.Replace, ordinal, text);
        public static DocxOp InsertBefore(int beforeOrdinal, string text) => new(DocxOpKind.InsertBefore, beforeOrdinal, text);
        public static DocxOp Append(string text) => new(DocxOpKind.Append, null, text);
        public static DocxOp Delete(int ordinal) => new(DocxOpKind.Delete, ordinal, "");
    }

    public static PatchResult Apply(WordprocessingDocument doc, IReadOnlyList<DiffLine> diff, bool apply, PatchLimits limits)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return PatchResult.Fail("Document has no body.");

        var walk = DroppedFileReader.WalkDocxParagraphs(body);

        // Defensive: the diff must have been computed against exactly this baseline text (the
        // caller's mtime staleness check should already guarantee this).
        int maxOld = 0;
        foreach (var d in diff)
            if (d.OldLineNumber is { } o && o > maxOld) maxOld = o;
        if (maxOld > walk.Lines.Count)
            return PatchResult.Fail("Internal error: the document changed since this edit was prepared. Re-read and re-submit.");

        var ops = new List<DocxOp>();
        int removedCount = 0, touchedCount = 0;

        // Walk the diff in maximal non-Context blocks. LineDiff does not interleave a block's
        // Removed/Added lines one-for-one — a multi-paragraph edit emits ALL its Removed lines
        // before ALL its Added lines — so pairing "a Removed immediately followed by an Added"
        // only ever catches a single-paragraph edit. Instead, collect every Removed and every Added
        // ordinal/text within one contiguous non-Context run and pair them up positionally: the
        // first min(R,A) pairs are in-place replaces (preserving each paragraph's pPr/rPr), any
        // leftover Removed are deletes, any leftover Added are inserts anchored right after the block.
        int i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == DiffLineKind.Context) { i++; continue; }

            var removedOrdinals = new List<int>();
            var addedTexts = new List<string>();
            while (i < diff.Count && diff[i].Kind != DiffLineKind.Context)
            {
                if (diff[i].Kind == DiffLineKind.Removed)
                    removedOrdinals.Add(walk.Ordinals[diff[i].OldLineNumber!.Value - 1]);
                else
                    addedTexts.Add(diff[i].Text);
                i++;
            }

            int? beforeOrdinal = i < diff.Count ? walk.Ordinals[diff[i].OldLineNumber!.Value - 1] : null;

            int pairCount = Math.Min(removedOrdinals.Count, addedTexts.Count);
            for (int k = 0; k < pairCount; k++)
            {
                ops.Add(DocxOp.Replace(removedOrdinals[k], addedTexts[k]));
                touchedCount++;
            }

            for (int k = pairCount; k < removedOrdinals.Count; k++)
            {
                var ordinal = removedOrdinals[k];
                var paragraph = walk.AllParagraphs[ordinal];
                if (paragraph.Parent is not Body && paragraph.Parent!.Elements<Paragraph>().Count() <= 1)
                    return PatchResult.Fail(
                        "Cannot remove the last paragraph inside a table cell or text box — that would produce an invalid document.");
                ops.Add(DocxOp.Delete(ordinal));
                removedCount++;
                touchedCount++;
            }

            if (addedTexts.Count > pairCount)
            {
                if (beforeOrdinal is { } b)
                {
                    if (walk.AllParagraphs[b].Parent is not Body)
                        return PatchResult.Fail(
                            "Cannot insert a new paragraph next to text inside a table cell or text box — " +
                            "insert outside the table, or edit the cell's existing paragraph text instead.");
                    for (int k = pairCount; k < addedTexts.Count; k++)
                    {
                        ops.Add(DocxOp.InsertBefore(b, addedTexts[k]));
                        touchedCount++;
                    }
                }
                else
                {
                    if (walk.AllParagraphs.Count > 0 && walk.AllParagraphs[^1].Parent is not Body)
                        return PatchResult.Fail(
                            "Cannot append a new paragraph after content inside a table cell or text box.");
                    for (int k = pairCount; k < addedTexts.Count; k++)
                    {
                        ops.Add(DocxOp.Append(addedTexts[k]));
                        touchedCount++;
                    }
                }
            }
        }

        if (removedCount > limits.MaxRemovedAbsolute && removedCount > limits.MaxRemovedFraction * Math.Max(1, walk.Lines.Count))
            return PatchResult.Fail(
                $"This write removes {removedCount} of {walk.Lines.Count} paragraph(s) — that looks like an edit " +
                "based on a partial read rather than the whole document. Re-read the file with a large enough " +
                "'limit' and submit the full updated text.");

        if (touchedCount > limits.MaxTouchedNodes)
            return PatchResult.Fail(
                $"This write changes {touchedCount} paragraphs in one call (max {limits.MaxTouchedNodes}). Split the edit into smaller write_file calls.");

        if (!apply) return PatchResult.Ok;

        // Apply order: replace (in place, no structural change) -> insert (anchors still valid,
        // nothing removed yet) -> delete (independent node removals by direct reference).
        foreach (var op in ops)
            if (op.Kind == DocxOpKind.Replace)
                ReplaceParagraphText(walk.AllParagraphs[op.Ordinal!.Value], op.Text);

        foreach (var op in ops)
            if (op.Kind == DocxOpKind.InsertBefore)
                walk.AllParagraphs[op.Ordinal!.Value].InsertBeforeSelf(NewParagraph(op.Text));

        foreach (var op in ops)
            if (op.Kind == DocxOpKind.Append)
                body.AppendChild(NewParagraph(op.Text));

        foreach (var op in ops)
            if (op.Kind == DocxOpKind.Delete)
                walk.AllParagraphs[op.Ordinal!.Value].Remove();

        return PatchResult.Ok;
    }

    /// <summary>Builds a brand-new minimal docx directly from plain text (one paragraph per line) —
    /// used when the target path has no baseline to preserve. Default styling only.</summary>
    public static void CreateFresh(string tempPath, string content)
    {
        using var doc = WordprocessingDocument.Create(tempPath, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        var body = new Body();
        mainPart.Document = new Document(body);

        foreach (var line in SplitContentLines(content))
            body.AppendChild(NewParagraph(line));

        mainPart.Document.Save();
    }

    /// <summary>
    /// Preserves the paragraph's <c>w:pPr</c> and the first run's <c>w:rPr</c>; every existing run
    /// (and any hyperlink) is removed and replaced with a single new run carrying the new text. This
    /// is lossy for a paragraph with multiple differently-formatted runs (bold/italic ranges collapse
    /// to one run) — acceptable only because it's scoped to paragraphs the model actually touched.
    /// </summary>
    private static void ReplaceParagraphText(Paragraph paragraph, string newText)
    {
        RunProperties? preservedRunProps = null;
        if (paragraph.Elements<Run>().FirstOrDefault()?.RunProperties is { } rPr)
            preservedRunProps = (RunProperties)rPr.CloneNode(true);

        foreach (var run in paragraph.Elements<Run>().ToList()) run.Remove();
        foreach (var hyperlink in paragraph.Elements<Hyperlink>().ToList()) hyperlink.Remove();

        var newRun = new Run(NewRunText(newText));
        if (preservedRunProps is not null) newRun.RunProperties = preservedRunProps;
        paragraph.AppendChild(newRun);
    }

    private static Paragraph NewParagraph(string text) => new(new Run(NewRunText(text)));

    private static Text NewRunText(string text)
    {
        var t = new Text(text);
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
            t.Space = SpaceProcessingModeValues.Preserve;
        return t;
    }

    private static IEnumerable<string> SplitContentLines(string content)
    {
        if (content.Length == 0) return [];
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        return lines;
    }
}
