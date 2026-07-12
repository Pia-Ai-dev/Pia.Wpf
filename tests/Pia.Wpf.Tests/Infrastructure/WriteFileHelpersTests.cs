using System.IO;
using System.Text;
using Pia.Infrastructure;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Infrastructure;

public class WriteFileHelpersTests
{
    // ---- SensitivePathGuard ----

    [Fact]
    public void SensitivePathGuard_BlocksPiaLocalAppData()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Assert.False(string.IsNullOrEmpty(localAppData));

        var target = Path.Combine(localAppData!, "Pia", "settings.json");
        Assert.True(SensitivePathGuard.IsBlocked(target, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void SensitivePathGuard_BlocksWindowsDir()
    {
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        Assert.False(string.IsNullOrEmpty(windir));
        Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(windir!, "System32", "evil.dll"), out _));
    }

    [Fact]
    public void SensitivePathGuard_AllowsOrdinaryTempPath()
    {
        var ordinary = Path.Combine(Path.GetTempPath(), "pia-ok-" + Guid.NewGuid().ToString("N"), "file.txt");
        Assert.False(SensitivePathGuard.IsBlocked(ordinary, out _));
    }

    // ---- WriteLintHelper (delta-filtered) ----

    [Fact]
    public void Lint_NewFile_InvalidJson_Surfaced()
    {
        var lint = WriteLintHelper.Lint("a.json", oldContent: null, newContent: "{ nope ");
        Assert.NotNull(lint);
    }

    [Fact]
    public void Lint_PreExistingError_Suppressed()
    {
        // Same broken content in old and new → no NEW error.
        var lint = WriteLintHelper.Lint("a.json", oldContent: "{ broken", newContent: "{ broken");
        Assert.Null(lint);
    }

    [Fact]
    public void Lint_CleanToBroken_Surfaced()
    {
        var lint = WriteLintHelper.Lint("a.json", oldContent: "{\"a\":1}", newContent: "{\"a\":}");
        Assert.NotNull(lint);
    }

    [Fact]
    public void Lint_ValidJson_NoOpinion()
    {
        Assert.Null(WriteLintHelper.Lint("a.json", oldContent: null, newContent: "{\"a\":1}"));
    }

    [Fact]
    public void Lint_NonJsonExtension_NoOpinion()
    {
        Assert.Null(WriteLintHelper.Lint("a.yaml", oldContent: null, newContent: ": : : not valid"));
        Assert.Null(WriteLintHelper.Lint("a.txt", oldContent: null, newContent: "{ broken"));
    }

    // ---- LineDiff ----

    [Fact]
    public void LineDiff_NewFile_AllAdded()
    {
        var diff = LineDiff.Compute(null, "one\ntwo\nthree");
        Assert.Equal(3, diff.Count);
        Assert.All(diff, d => Assert.Equal(DiffLineKind.Added, d.Kind));
    }

    [Fact]
    public void LineDiff_Update_ContextAddedRemoved()
    {
        var diff = LineDiff.Compute("a\nb\nc\n", "a\nB\nc\n");
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Context && d.Text == "a");
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Removed && d.Text == "b");
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Added && d.Text == "B");
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Context && d.Text == "c");
    }

    [Fact]
    public void LineDiff_IgnoresEolStyle()
    {
        // Same logical content, different EOL → no add/remove, all context.
        var diff = LineDiff.Compute("a\r\nb\r\n", "a\nb\n");
        Assert.All(diff, d => Assert.Equal(DiffLineKind.Context, d.Kind));
        Assert.Equal(2, diff.Count);
    }

    // ---- LineDiff line numbers (dual gutter) ----

    [Fact]
    public void LineDiff_Context_NumbersLockstep()
    {
        var diff = LineDiff.Compute("a\nb\nc", "a\nb\nc");
        Assert.Equal(3, diff.Count);
        Assert.All(diff, d => Assert.Equal(DiffLineKind.Context, d.Kind));
        Assert.Equal((1, 1), (diff[0].OldLineNumber, diff[0].NewLineNumber));
        Assert.Equal((2, 2), (diff[1].OldLineNumber, diff[1].NewLineNumber));
        Assert.Equal((3, 3), (diff[2].OldLineNumber, diff[2].NewLineNumber));
    }

    [Fact]
    public void LineDiff_Removed_HasOldNumberOnly()
    {
        // 'b' is dropped: old advances past it while new does not.
        var diff = LineDiff.Compute("a\nb\nc", "a\nc");
        var removed = Assert.Single(diff, d => d.Kind == DiffLineKind.Removed);
        Assert.Equal("b", removed.Text);
        Assert.Equal(2, removed.OldLineNumber);
        Assert.Null(removed.NewLineNumber);

        // Trailing context keeps advancing both cursors: 'c' is old line 3, new line 2.
        var c = Assert.Single(diff, d => d.Kind == DiffLineKind.Context && d.Text == "c");
        Assert.Equal((3, 2), (c.OldLineNumber, c.NewLineNumber));
    }

    [Fact]
    public void LineDiff_Added_HasNewNumberOnly()
    {
        var diff = LineDiff.Compute("a\nc", "a\nb\nc");
        var added = Assert.Single(diff, d => d.Kind == DiffLineKind.Added);
        Assert.Equal("b", added.Text);
        Assert.Null(added.OldLineNumber);
        Assert.Equal(2, added.NewLineNumber);
    }

    [Fact]
    public void LineDiff_NewFile_NumbersAllAddedFromOne()
    {
        var diff = LineDiff.Compute(null, "one\ntwo\nthree");
        Assert.Equal(3, diff.Count);
        for (int i = 0; i < diff.Count; i++)
        {
            Assert.Equal(DiffLineKind.Added, diff[i].Kind);
            Assert.Null(diff[i].OldLineNumber);
            Assert.Equal(i + 1, diff[i].NewLineNumber);
        }
    }

    [Fact]
    public void LineDiff_SmallEditInLargeFile_DiffsAsSingleChange_NotFullReplacement()
    {
        // 3000 identical lines except line 1500 differs. Without prefix/suffix stripping, 3000*3000 =
        // 9,000,000 > 4M would take the plain-replace fallback and render the whole file as changed.
        // Stripping keeps the differing middle tiny, so the real change surfaces with its true number.
        var oldLines = Enumerable.Range(0, 3000).Select(i => $"line{i}").ToArray();
        var newLines = (string[])oldLines.Clone();
        newLines[1500] = "CHANGED";

        var diff = LineDiff.Compute(string.Join("\n", oldLines), string.Join("\n", newLines));

        var removed = Assert.Single(diff, d => d.Kind == DiffLineKind.Removed);
        Assert.Equal("line1500", removed.Text);
        Assert.Equal(1501, removed.OldLineNumber);

        var added = Assert.Single(diff, d => d.Kind == DiffLineKind.Added);
        Assert.Equal("CHANGED", added.Text);
        Assert.Equal(1501, added.NewLineNumber);

        // LineDiff does not truncate — folding/limiting is the display layer's job (DiffHunkBuilder).
        Assert.DoesNotContain(diff, d => d.Kind == DiffLineKind.TruncationNotice);
    }

    [Fact]
    public void LineDiff_HugeDifferentFile_FallbackNumbersBothSides_NoTruncation()
    {
        // 2001 * 2001 > 4,000,000 with no common prefix/suffix → the O(n*m) LCS is skipped for the
        // plain replace fallback. LineDiff emits the full diff (both sides, numbered) and never truncates.
        var old = string.Join("\n", Enumerable.Range(0, 2001).Select(i => $"o{i}"));
        var @new = string.Join("\n", Enumerable.Range(0, 2001).Select(i => $"x{i}"));

        var diff = LineDiff.Compute(old, @new);

        Assert.DoesNotContain(diff, d => d.Kind == DiffLineKind.TruncationNotice);
        Assert.Equal(4002, diff.Count);

        Assert.Equal(DiffLineKind.Removed, diff[0].Kind);
        Assert.Equal(1, diff[0].OldLineNumber);
        Assert.Null(diff[0].NewLineNumber);

        Assert.Equal(DiffLineKind.Added, diff[^1].Kind);
        Assert.Equal(2001, diff[^1].NewLineNumber);
        Assert.Null(diff[^1].OldLineNumber);
    }

    [Fact]
    public void LineDiff_FullReplacement_EmitsAllRemovedThenAllAdded_NoTruncation()
    {
        // Two 500-line files with no common lines: product 250k < 4M, so the real LCS runs and emits
        // all removals before all additions. Both sides are present; LineDiff itself never truncates.
        var old = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"o{i}"));
        var @new = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"n{i}"));

        var diff = LineDiff.Compute(old, @new);

        Assert.DoesNotContain(diff, d => d.Kind == DiffLineKind.TruncationNotice);
        Assert.Equal(1000, diff.Count);
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Removed);
        Assert.Contains(diff, d => d.Kind == DiffLineKind.Added);
    }

    // ---- AtomicTextWriter ----

    [Fact]
    public void AtomicWriter_NewFile_CrlfNoBom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pia-aw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "n.txt");
            var r = AtomicTextWriter.Write(path, "a\nb");
            Assert.True(r.UsedCrlf);
            Assert.False(r.HadBom);
            Assert.Equal("a\r\nb", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AtomicWriter_PreservesBomAndCrlf()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pia-aw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "e.txt");
            var original = new byte[] { 0xEF, 0xBB, 0xBF }
                .Concat(Encoding.UTF8.GetBytes("x\r\ny\r\n")).ToArray();
            File.WriteAllBytes(path, original);

            var r = AtomicTextWriter.Write(path, "p\nq");
            Assert.True(r.HadBom);
            Assert.True(r.UsedCrlf);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal("p\r\nq", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally { Directory.Delete(dir, true); }
    }
}
