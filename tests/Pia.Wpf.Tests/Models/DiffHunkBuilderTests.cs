using System.Collections.Generic;
using System.Linq;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>
/// Covers <see cref="DiffHunkBuilder"/> hunk folding: long unchanged runs collapse into a
/// <see cref="CollapsedDiffRun"/> (kept ≥ <see cref="DiffHunkBuilder.ContextLines"/> lines of context,
/// only when ≥ <see cref="DiffHunkBuilder.MinHiddenLines"/> would be hidden), and expanding splices the
/// hidden lines back in place — correctly even after an earlier run was expanded.
/// </summary>
public class DiffHunkBuilderTests
{
    private static DiffLine Ctx(int i) => new(DiffLineKind.Context, $"c{i}", i + 1, i + 1);
    private static DiffLine Add(string t = "a") => new(DiffLineKind.Added, t, null, 1);
    private static DiffLine Rem(string t = "r") => new(DiffLineKind.Removed, t, 1, null);
    private static DiffLine Trunc() => new(DiffLineKind.TruncationNotice, "…");

    private static List<DiffLine> Context(int n) => Enumerable.Range(0, n).Select(Ctx).ToList();

    private static int CollapsedCount(IEnumerable<object> rows) => rows.OfType<CollapsedDiffRun>().Count();

    [Fact]
    public void Build_NoChanges_PassesThrough()
    {
        var rows = DiffHunkBuilder.Build(Context(10));
        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.IsType<DiffLine>(r));
        Assert.Equal(0, CollapsedCount(rows));
    }

    [Fact]
    public void Build_AllAdded_PassesThrough()
    {
        var rows = DiffHunkBuilder.Build([Add(), Add(), Add()]);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(DiffLineKind.Added, Assert.IsType<DiffLine>(r).Kind));
    }

    [Fact]
    public void Build_InteriorRun_KeepsThreeEachSide_CollapsesMiddle()
    {
        // Add, 10 context, Add → interior run hides 10 - 6 = 4 lines.
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(10));
        lines.Add(Add());

        var rows = DiffHunkBuilder.Build(lines);

        Assert.Equal(1, CollapsedCount(rows));
        var bar = rows.OfType<CollapsedDiffRun>().Single();
        Assert.Equal(4, bar.Count);
        // 1 change + 3 head context + 1 bar + 3 tail context + 1 change.
        Assert.Equal(9, rows.Count);
        // The hidden lines are the middle four (c3..c6).
        Assert.Equal(new[] { "c3", "c4", "c5", "c6" }, bar.HiddenLines.Select(h => h.Text));
    }

    [Fact]
    public void Build_InteriorRun_HiddenThree_NotCollapsed()
    {
        // Add, 9 context, Add → would hide only 3 (9 - 6) → below MinHiddenLines, shown in full.
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(9));
        lines.Add(Add());

        var rows = DiffHunkBuilder.Build(lines);

        Assert.Equal(0, CollapsedCount(rows));
        Assert.Equal(11, rows.Count);
    }

    [Fact]
    public void Build_InteriorRun_HiddenFour_Collapsed()
    {
        // Add, 10 context, Add → hides exactly 4 → collapses (boundary of MinHiddenLines).
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(10));
        lines.Add(Add());

        Assert.Equal(1, CollapsedCount(DiffHunkBuilder.Build(lines)));
    }

    [Fact]
    public void Build_AdjacentChangesWithinSixContext_StayOneHunk()
    {
        // Add, 6 context, Add → interior run of 6 hides 0 → single hunk, no fold.
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(6));
        lines.Add(Add());

        var rows = DiffHunkBuilder.Build(lines);
        Assert.Equal(0, CollapsedCount(rows));
        Assert.Equal(8, rows.Count);
    }

    [Fact]
    public void Build_LeadingRun_KeepsOnlyLastThree()
    {
        // 10 leading context then a change → leading run keeps the last 3, hides the first 7.
        var lines = new List<DiffLine>();
        lines.AddRange(Context(10));
        lines.Add(Add());

        var rows = DiffHunkBuilder.Build(lines);

        Assert.IsType<CollapsedDiffRun>(rows[0]);
        Assert.Equal(7, ((CollapsedDiffRun)rows[0]).Count);
        // bar + 3 context + 1 change.
        Assert.Equal(5, rows.Count);
        Assert.Equal(new[] { "c7", "c8", "c9" }, rows.OfType<DiffLine>().Where(d => d.Kind == DiffLineKind.Context).Select(d => d.Text));
    }

    [Fact]
    public void Build_TrailingRun_KeepsOnlyFirstThree()
    {
        // A change then 10 trailing context → trailing run keeps the first 3, hides the last 7.
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(10));

        var rows = DiffHunkBuilder.Build(lines);

        var bar = rows.OfType<CollapsedDiffRun>().Single();
        Assert.Equal(7, bar.Count);
        Assert.Equal(5, rows.Count);
        Assert.Same(rows[^1], bar); // the fold is the last row (nothing after a trailing run)
    }

    [Fact]
    public void Build_TruncationNotice_IsNeverAbsorbed_AndAnchorsPrecedingRun()
    {
        // Add, 10 context, Trunc → the notice is a non-context anchor, so the run is interior (folds),
        // and the notice itself passes through as its own row.
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(10));
        lines.Add(Trunc());

        var rows = DiffHunkBuilder.Build(lines);

        Assert.Equal(1, CollapsedCount(rows));
        var last = Assert.IsType<DiffLine>(rows[^1]);
        Assert.Equal(DiffLineKind.TruncationNotice, last.Kind);
    }

    [Fact]
    public void Expand_SplicesHiddenLinesInPlace()
    {
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(10));
        lines.Add(Add());

        var rows = DiffHunkBuilder.Build(lines);
        var bar = rows.OfType<CollapsedDiffRun>().Single();
        int barIndex = rows.IndexOf(bar);

        bar.ExpandCommand.Execute(null);

        Assert.Equal(0, CollapsedCount(rows));
        Assert.Equal(12, rows.Count); // all 10 context + 2 changes now visible
        // The four hidden lines landed exactly where the bar was.
        Assert.Equal(new[] { "c3", "c4", "c5", "c6" },
            rows.Skip(barIndex).Take(4).Cast<DiffLine>().Select(d => d.Text));
    }

    [Fact]
    public void Expand_SecondBar_IndexShiftsAfterFirstExpansion()
    {
        // Two interior runs → two bars. Expanding the first shifts the second's index; because Expand
        // resolves its index at execute time, the second still splices correctly.
        var lines = new List<DiffLine> { Add() };
        lines.AddRange(Context(10));
        lines.Add(Add());
        lines.AddRange(Context(10));
        lines.Add(Add());

        var rows = DiffHunkBuilder.Build(lines);
        var bars = rows.OfType<CollapsedDiffRun>().ToList();
        Assert.Equal(2, bars.Count);

        bars[0].ExpandCommand.Execute(null);
        bars[1].ExpandCommand.Execute(null);

        Assert.Equal(0, CollapsedCount(rows));
        // 3 changes + 20 context lines, all restored.
        Assert.Equal(23, rows.Count);
        Assert.All(rows, r => Assert.IsType<DiffLine>(r));
    }

    [Fact]
    public void Build_ChangeDeepInLargeContext_RemainsVisible()
    {
        // A change buried in 1500 lines of context on each side (a mid-file edit) must survive folding:
        // the context collapses, but the change is never dropped. This is the fold-before-cap guarantee.
        var lines = new List<DiffLine>();
        lines.AddRange(Enumerable.Range(0, 1500).Select(i => new DiffLine(DiffLineKind.Context, $"c{i}", i + 1, i + 1)));
        lines.Add(new DiffLine(DiffLineKind.Removed, "old", 1501, null));
        lines.Add(new DiffLine(DiffLineKind.Added, "new", null, 1501));
        lines.AddRange(Enumerable.Range(0, 1500).Select(i => new DiffLine(DiffLineKind.Context, $"d{i}", 1502 + i, 1502 + i)));

        var rows = DiffHunkBuilder.Build(lines);

        Assert.Contains(rows, r => r is DiffLine { Kind: DiffLineKind.Removed });
        Assert.Contains(rows, r => r is DiffLine { Kind: DiffLineKind.Added });
        Assert.DoesNotContain(rows, r => r is DiffLine { Kind: DiffLineKind.TruncationNotice });
        Assert.True(rows.Count < 20, $"context should fold to a handful of rows, got {rows.Count}");
    }

    [Fact]
    public void Build_MoreChangesThanCap_KeepsHeadAndTailWithNotice()
    {
        // A context-free replacement larger than the cap: all removals precede all additions. Head+tail
        // truncation must keep removed rows at the top and added rows at the bottom (never a pure deletion).
        var lines = new List<DiffLine>();
        lines.AddRange(Enumerable.Range(0, 600).Select(i => new DiffLine(DiffLineKind.Removed, $"r{i}", i + 1, null)));
        lines.AddRange(Enumerable.Range(0, 600).Select(j => new DiffLine(DiffLineKind.Added, $"a{j}", null, j + 1)));

        var rows = DiffHunkBuilder.Build(lines);

        Assert.Equal(DiffHunkBuilder.MaxRows, rows.Count);
        var notice = Assert.Single(rows.OfType<DiffLine>(), d => d.Kind == DiffLineKind.TruncationNotice);
        Assert.Equal(DiffLineKind.Removed, Assert.IsType<DiffLine>(rows[0]).Kind);
        Assert.Equal(DiffLineKind.Added, Assert.IsType<DiffLine>(rows[^1]).Kind);
        // The notice sits between the head and tail slices, not at either end.
        Assert.NotSame(rows[0], notice);
        Assert.NotSame(rows[^1], notice);
    }

    [Fact]
    public void Build_UnderCap_NoTruncationNotice()
    {
        var lines = Enumerable.Range(0, 100).Select(i => new DiffLine(DiffLineKind.Added, $"a{i}", null, i + 1)).ToList();

        var rows = DiffHunkBuilder.Build(lines);

        Assert.Equal(100, rows.Count);
        Assert.DoesNotContain(rows, r => r is DiffLine { Kind: DiffLineKind.TruncationNotice });
    }
}
