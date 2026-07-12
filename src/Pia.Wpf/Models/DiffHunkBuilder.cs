using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pia.Models;

/// <summary>
/// Groups a flat <see cref="DiffLine"/> sequence into GitHub-style hunks: long runs of unchanged
/// context are folded into a clickable <see cref="CollapsedDiffRun"/> that expands in place. Folding
/// happens BEFORE the visible-row cap, so a change anywhere in a large file survives (its surrounding
/// context collapses instead of the change being dropped by a positional truncation). Only when the
/// folded output still exceeds <see cref="MaxRows"/> — i.e. there are more changes than fit — is a
/// head + tail slice kept with a truncation marker between them, so both the earliest and latest
/// changes (and both sides of a context-free full replacement) stay visible.
/// </summary>
public static class DiffHunkBuilder
{
    /// <summary>Unchanged lines kept adjacent to a change (on each side of an interior run).</summary>
    public const int ContextLines = 3;

    /// <summary>A run is only folded when it would hide at least this many lines (never fold 1–3).</summary>
    public const int MinHiddenLines = 4;

    /// <summary>Upper bound on rendered rows. The card never virtualizes, so the visible set is bounded.</summary>
    public const int MaxRows = 400;

    /// <summary>
    /// Builds the collapsed row list. The result mixes <see cref="DiffLine"/> rows (changes and kept
    /// context) with <see cref="CollapsedDiffRun"/> placeholders. A diff with no changes (all context)
    /// passes through unfolded; a diff with more folded rows than <see cref="MaxRows"/> is head/tail
    /// truncated with a <see cref="DiffLineKind.TruncationNotice"/> row between the slices.
    /// </summary>
    public static ObservableCollection<object> Build(IReadOnlyList<DiffLine> lines)
    {
        var folded = new List<object>();
        if (lines is not null && lines.Count > 0)
        {
            int n = lines.Count;
            int i = 0;
            while (i < n)
            {
                // A change / truncation-notice line is an anchor: always shown, never folded.
                if (lines[i].Kind != DiffLineKind.Context)
                {
                    folded.Add(lines[i]);
                    i++;
                    continue;
                }

                // Gather the maximal run of consecutive context lines [i, j).
                int j = i;
                while (j < n && lines[j].Kind == DiffLineKind.Context) j++;
                int runLength = j - i;

                bool atStart = i == 0;      // no change before the run (leading context)
                bool atEnd = j == n;        // no change after the run (trailing context)

                // Interior runs keep context on both sides; edge runs only keep the side facing the change;
                // a whole-diff context run (no changes at all) keeps everything and folds nothing.
                int keepHead, keepTail;
                if (atStart && atEnd) { keepHead = runLength; keepTail = 0; }
                else if (atStart)     { keepHead = 0; keepTail = ContextLines; }
                else if (atEnd)       { keepHead = ContextLines; keepTail = 0; }
                else                  { keepHead = ContextLines; keepTail = ContextLines; }

                int hidden = runLength - keepHead - keepTail;
                if (hidden < MinHiddenLines)
                {
                    // Too small to be worth folding — show the whole run.
                    for (int k = i; k < j; k++) folded.Add(lines[k]);
                }
                else
                {
                    for (int k = i; k < i + keepHead; k++) folded.Add(lines[k]);

                    var hiddenLines = new List<DiffLine>(hidden);
                    for (int k = i + keepHead; k < j - keepTail; k++) hiddenLines.Add(lines[k]);
                    folded.Add(new CollapsedDiffRun(hiddenLines));

                    for (int k = j - keepTail; k < j; k++) folded.Add(lines[k]);
                }

                i = j;
            }
        }

        // Cap the folded output. Truncating from the front would drop the entire added block of a
        // context-free replacement (all removals precede all additions), so keep a head AND a tail.
        List<object> visible;
        if (folded.Count > MaxRows)
        {
            int head = MaxRows / 2;          // rows kept from the start
            int tail = MaxRows - head - 1;   // rows kept from the end (one row reserved for the marker)
            visible = new List<object>(MaxRows);
            visible.AddRange(folded.GetRange(0, head));
            visible.Add(new DiffLine(DiffLineKind.TruncationNotice, $"… (diff truncated at {MaxRows} rows)"));
            visible.AddRange(folded.GetRange(folded.Count - tail, tail));
        }
        else
        {
            visible = folded;
        }

        var rows = new ObservableCollection<object>(visible);
        // Late-bind each surviving fold to the final collection so Expand splices into the right list.
        foreach (var row in rows)
            if (row is CollapsedDiffRun run) run.Owner = rows;
        return rows;
    }
}

/// <summary>
/// A folded run of unchanged diff lines rendered as a single "⋯ N unchanged lines" bar. Expanding
/// splices the hidden lines back into the owning row collection in place of the bar.
/// </summary>
public partial class CollapsedDiffRun : ObservableObject
{
    public CollapsedDiffRun(IReadOnlyList<DiffLine> hiddenLines) => HiddenLines = hiddenLines;

    /// <summary>
    /// The collection this bar lives in. Set by <see cref="DiffHunkBuilder.Build"/> after the (possibly
    /// truncated) row list is finalized, so <see cref="ExpandCommand"/> splices into the right list.
    /// </summary>
    internal ObservableCollection<object>? Owner { get; set; }

    /// <summary>The unchanged lines this bar stands in for.</summary>
    public IReadOnlyList<DiffLine> HiddenLines { get; }

    /// <summary>How many lines are folded (drives the label).</summary>
    public int Count => HiddenLines.Count;

    /// <summary>
    /// Replaces this bar with its hidden lines. The index is resolved at execute time via
    /// <see cref="System.Collections.ObjectModel.Collection{T}.IndexOf"/>, so it stays correct even
    /// after an earlier bar in the same diff was expanded (which shifts every later index).
    /// </summary>
    [RelayCommand]
    private void Expand()
    {
        var owner = Owner;
        if (owner is null) return;

        int index = owner.IndexOf(this);
        if (index < 0) return;

        owner.RemoveAt(index);
        for (int k = 0; k < HiddenLines.Count; k++)
            owner.Insert(index + k, HiddenLines[k]);
    }
}
