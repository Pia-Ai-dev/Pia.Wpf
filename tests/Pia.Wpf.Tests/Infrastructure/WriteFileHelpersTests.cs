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
