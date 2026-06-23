using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class FilesToolHandlerSearchTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-search-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> SearchAsync(
        string pattern, string? path = null, string? mode = null, int? offset = null, int? limit = null)
    {
        var args = new Dictionary<string, object?> { ["pattern"] = pattern };
        if (path is not null) args["path"] = path;
        if (mode is not null) args["mode"] = mode;
        if (offset is not null) args["offset"] = offset.Value;
        if (limit is not null) args["limit"] = limit.Value;
        var call = new FunctionCallContent("c1", "search_files", args);
        var (result, action) = await _handler.HandleToolCallAsync(call);
        Assert.Null(action); // read-only: never an action card
        return (string)result!;
    }

    private void Write(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Search_FindsRegexMatch_WithFileAndLine()
    {
        Write("a.txt", "alpha\nbeta needle here\ngamma");

        var result = await SearchAsync("n[ee]+dle");

        Assert.Contains("a.txt:2:", result);
        Assert.Contains("beta needle here", result);
        Assert.Contains("matches=1", result);
    }

    [Fact]
    public async Task Search_NoMatch_ReportsNoMatches()
    {
        Write("a.txt", "nothing relevant");

        var result = await SearchAsync("zzz_not_present");

        Assert.Contains("No matches found", result);
    }

    [Fact]
    public async Task Search_ExcludesIgnoredDirectories()
    {
        Write("src/keep.txt", "TARGET in source");
        Write(".git/config.txt", "TARGET in git");
        Write("bin/out.txt", "TARGET in bin");
        Write("obj/tmp.txt", "TARGET in obj");
        Write("node_modules/pkg.txt", "TARGET in node_modules");

        var result = await SearchAsync("TARGET", mode: "files");

        Assert.Contains("keep.txt", result);
        Assert.DoesNotContain(".git", result);
        Assert.DoesNotContain("bin", result);
        Assert.DoesNotContain("obj", result);
        Assert.DoesNotContain("node_modules", result);
        Assert.Contains("matches=1", result);
    }

    [Fact]
    public async Task Search_IgnoreSet_MatchesBySegmentNotSubstring()
    {
        // "cabinet" contains "bin" and "object.txt" contains "obj" as substrings, but they
        // are real files/dirs and must NOT be excluded.
        Write("cabinet/object.txt", "FOUND substring trap");

        var result = await SearchAsync("FOUND", mode: "files");

        Assert.Contains("cabinet", result);
        Assert.Contains("object.txt", result);
        Assert.Contains("matches=1", result);
    }

    [Fact]
    public async Task Search_DefaultPath_SearchesWholeRoot()
    {
        // No 'path' arg -> the resolver would reject the root itself; the handler must
        // special-case the missing path to the whole sandbox.
        Write("top.txt", "ROOTHIT at top");

        var result = await SearchAsync("ROOTHIT");

        Assert.Contains("top.txt", result);
        Assert.Contains("matches=1", result);
    }

    [Fact]
    public async Task Search_Pagination_SlicesAndHints()
    {
        for (int i = 1; i <= 10; i++)
            Write($"f{i:00}.txt", "HIT line");

        var result = await SearchAsync("HIT", mode: "files", offset: 1, limit: 4);

        Assert.Contains("matches=10", result);
        Assert.Contains("showing 4 of 10; pass offset=5", result);

        // Lines: count only the result rows in the window (exclude header + hint).
        var rows = result.Split('\n').Count(l => l.EndsWith(".txt"));
        Assert.Equal(4, rows);
    }

    [Fact]
    public async Task Search_CountMode_ReportsPerFileCounts()
    {
        Write("multi.txt", "X here\nnope\nX again\nX third");

        var result = await SearchAsync("X", mode: "count");

        Assert.Contains("multi.txt: 3", result);
    }

    [Fact]
    public async Task Search_MultilinePattern_WarnsInDiagnostics()
    {
        Write("a.txt", "alpha\nbeta");

        var result = await SearchAsync("alpha\\nbeta");

        Assert.Contains("multiline", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_InvalidRegex_ReturnsDiagnosticNotCrash()
    {
        Write("a.txt", "content");

        var result = await SearchAsync("(unclosed");

        Assert.Contains("Invalid regular expression", result);
    }

    [Fact]
    public async Task Search_NonexistentPath_SuggestsAndDoesNotCrash()
    {
        Write("reports/q1.txt", "data");

        var result = await SearchAsync("data", path: "report"); // typo: missing 's'

        Assert.Contains("was not found", result);
        Assert.Contains("reports", result); // similar-name suggestion
    }

    [Fact]
    public async Task Search_NonexistentPath_NoSuggestionsStillSafe()
    {
        Write("a.txt", "data");

        var result = await SearchAsync("data", path: "totally_unrelated_xyz");

        Assert.Contains("was not found", result);
    }

    [Fact]
    public async Task Search_ResultsNeverEscapeRoot_OutOfBasePathRejected()
    {
        Write("inside.txt", "SECRET inside");

        // A path that climbs out of the sandbox must be rejected, never searched.
        var result = await SearchAsync("SECRET", path: "../..");

        Assert.Contains("was not found", result);
        Assert.DoesNotContain("SECRET", result);
    }

    [Fact]
    public async Task Search_ScopedToSubdirectory()
    {
        Write("sub/only.txt", "SCOPED hit");
        Write("other.txt", "SCOPED hit elsewhere");

        var result = await SearchAsync("SCOPED", path: "sub", mode: "files");

        Assert.Contains("only.txt", result);
        Assert.DoesNotContain("other.txt", result);
        Assert.Contains("matches=1", result);
    }
}
