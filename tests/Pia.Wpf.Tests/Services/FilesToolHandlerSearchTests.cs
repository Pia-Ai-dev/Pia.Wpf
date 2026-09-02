using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
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
        TempPath.Remove(_root);
    }

    private async Task<string> SearchAsync(
        string pattern, string? path = null, string? mode = null, int? offset = null, int? limit = null,
        string? include = null, bool? caseSensitive = null)
    {
        var args = new Dictionary<string, object?> { ["pattern"] = pattern };
        if (path is not null) args["path"] = path;
        if (include is not null) args["include"] = include;
        if (caseSensitive is not null) args["case_sensitive"] = caseSensitive.Value;
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
    public async Task Search_HonorsFileLevelIgnorePatterns()
    {
        // A file hidden via .piaignore must be hidden from search too (parity with list_files/@Files),
        // otherwise its contents leak through search results despite being "ignored".
        Write("keep.txt", "TARGET keep");
        Write("secret.txt", "TARGET secret");
        Write(".piaignore", "secret.txt\n");

        var result = await SearchAsync("TARGET", mode: "files");

        Assert.Contains("keep.txt", result);
        Assert.DoesNotContain("secret.txt", result);
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
    public async Task Search_EmitsRootRelativeForwardSlashPaths()
    {
        Write("docs/sub/a.txt", "HIT here");

        var result = await SearchAsync("HIT");

        Assert.Contains("docs/sub/a.txt:1:HIT here", result);
        Assert.DoesNotContain('\\', result);
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
    public async Task Search_SkipsFileOverSizeCap()
    {
        // A small file that matches, plus an oversized (>1 MB raw-byte ceiling) file whose content
        // also matches. The big file must be skipped by the per-file size guard, never loaded whole.
        Write("small.txt", "BIGMATCH here");
        var bigPath = Path.Combine(_root, "huge.txt");
        // 1.5 MB of a repeated matching token — over MaxReadFileBytes (1 MB).
        File.WriteAllText(bigPath, string.Concat(Enumerable.Repeat("BIGMATCH\n", 200_000)));
        Assert.True(new FileInfo(bigPath).Length > 1024 * 1024);

        var result = await SearchAsync("BIGMATCH", mode: "files");

        // Not searched — but named in the diagnostic, because a silent skip reads as "not there".
        Assert.Contains("matches=1", result);
        Assert.Contains("small.txt", result);
        Assert.Contains("could not be searched", result);
    }

    [Fact]
    public async Task Search_ScopedToSubdirectory()
    {
        Write("sub/only.txt", "SCOPED hit");
        Write("other.txt", "SCOPED hit elsewhere");

        var result = await SearchAsync("SCOPED", path: "sub", mode: "files");

        // The emitted path must be SANDBOX-ROOT-relative ("sub/only.txt"), not subdir-relative
        // ("only.txt") — only the root-relative form round-trips back through read_file.
        var expected = "sub/only.txt";
        Assert.Contains(expected, result);
        Assert.DoesNotContain("other.txt", result);
        Assert.Contains("matches=1", result);
    }

    [Fact]
    public async Task Search_ScopedHit_IsRoundTrippableThroughReadFile()
    {
        Write("sub/only.txt", "SCOPED hit");

        var search = await SearchAsync("SCOPED", path: "sub", mode: "files");

        // Extract the emitted relative path and feed it straight into read_file. The contract is
        // that a scoped search hit is consumable by the other file tools without re-derivation.
        var rel = "sub/only.txt";
        Assert.Contains(rel, search);

        var read = new FunctionCallContent("c2", "read_file", new Dictionary<string, object?> { ["path"] = rel });
        var (readResult, _) = await _handler.HandleToolCallAsync(read, TestContext.Current.CancellationToken);
        var readText = (string)readResult!;
        Assert.Contains("SCOPED hit", readText);
        Assert.DoesNotContain("not found", readText);
    }

    [Fact]
    public async Task Search_Include_NarrowsToMatchingFiles()
    {
        Write("a.cs", "TARGET in code");
        Write("b.md", "TARGET in prose");

        var result = await SearchAsync("TARGET", mode: "files", include: "*.cs");

        Assert.Contains("a.cs", result);
        Assert.DoesNotContain("b.md", result);
        Assert.Contains("matches=1", result);
    }

    [Fact]
    public async Task Search_IncludeBareName_MatchesNestedFiles()
    {
        Write("deep/nested/x.cs", "TARGET nested");
        Write("y.md", "TARGET shallow");

        var result = await SearchAsync("TARGET", mode: "files", include: "*.cs");

        Assert.Contains("x.cs", result);
        Assert.DoesNotContain("y.md", result);
    }

    [Fact]
    public async Task Search_IncludeWithSlash_IsAnchored()
    {
        Write("docs/a.md", "TARGET in docs");
        Write("other/a.md", "TARGET elsewhere");

        var result = await SearchAsync("TARGET", mode: "files", include: "docs/**/*.md");

        Assert.Contains("docs/a.md", result);
        Assert.DoesNotContain("other", result);
    }

    [Fact]
    public async Task Search_IncludeIsRelativeToThePathBeingSearched()
    {
        Write("docs/sub/a.md", "TARGET deep");
        Write("docs/other.md", "TARGET shallow");

        // Anchored against the SEARCHED folder: relative to the sandbox root this file is
        // "docs/sub/a.md", so a root-anchored "sub/*.md" would find nothing.
        var result = await SearchAsync("TARGET", path: "docs", mode: "files", include: "sub/*.md");

        Assert.Contains("sub/a.md", result);
        Assert.DoesNotContain("other.md", result);
    }

    [Fact]
    public async Task Search_IncludeCannotResurfaceAnIgnoredFile()
    {
        Write(".piaignore", "secret.cs\n");
        Write("secret.cs", "TARGET hidden");
        Write("keep.cs", "TARGET visible");

        var result = await SearchAsync("TARGET", mode: "files", include: "*.cs");

        Assert.Contains("keep.cs", result);
        Assert.DoesNotContain("secret.cs", result);
    }

    private async Task<string> ReadWindowAsync(string path, int offset, int limit)
    {
        var args = new Dictionary<string, object?> { ["path"] = path, ["offset"] = offset, ["limit"] = limit };
        var call = new FunctionCallContent("c2", "read_file", args);
        var (result, _) = await _handler.HandleToolCallAsync(call);
        return (string)result!;
    }

    [Fact]
    public async Task Search_ReadsInsideADocx()
    {
        OfficeDocuments.CreateDocx(Path.Combine(_root, "rezepte.docx"), "Vorspeise", "Kekse mit Zimt", "Nachtisch");

        var result = await SearchAsync("Kekse");

        Assert.Contains("rezepte.docx:2:Kekse mit Zimt", result);
    }

    [Fact]
    public async Task Search_ReadsInsideAnXlsx()
    {
        OfficeDocuments.CreateXlsx(Path.Combine(_root, "zutaten.xlsx"), "Liste", ("A1", "Zucker"), ("B1", "Kekse"));

        var result = await SearchAsync("Kekse", mode: "files");

        Assert.Contains("zutaten.xlsx", result);
    }

    [Fact]
    public async Task Search_ReadsInsideAnEml()
    {
        File.WriteAllText(Path.Combine(_root, "mail.eml"), "Subject: Rezept\r\n\r\nKekse mit Zimt\r\n");

        var result = await SearchAsync("Kekse", mode: "files");

        Assert.Contains("mail.eml", result);
    }

    [Theory]
    [InlineData("rezepte.docx")]
    [InlineData("mail.eml")]
    public async Task Search_HitLineNumberIsTheReadFileLineNumber(string name)
    {
        OfficeDocuments.CreateDocx(Path.Combine(_root, "rezepte.docx"), "Vorspeise", "Kekse mit Zimt", "Nachtisch");
        File.WriteAllText(Path.Combine(_root, "mail.eml"), "Subject: Rezept\r\n\r\nKekse mit Zimt\r\n");

        var hit = (await SearchAsync("Kekse")).Split('\n')
            .Single(l => l.StartsWith(name + ":", StringComparison.Ordinal));
        var line = int.Parse(hit.Split(':')[1]);

        Assert.Contains($"{line}|Kekse mit Zimt", await ReadWindowAsync(name, line, 1));
    }

    [Fact]
    public async Task Search_NamesTheFilesItCouldNotRead()
    {
        Write("notes.txt", "Kekse hier");
        File.WriteAllBytes(Path.Combine(_root, "scan.pdf"), [0x25, 0x50, 0x44, 0x46, 0x00, 0x01]);

        var result = await SearchAsync("Kekse");

        Assert.Contains("1 file(s) could not be searched", result);
        Assert.Contains("scan.pdf", result);
        Assert.Contains("notes.txt:1:", result);
    }

    [Fact]
    public async Task Search_AllLowercasePattern_IgnoresCase()
    {
        Write("a.txt", "Kekse mit Zimt");

        Assert.Contains("a.txt:1:Kekse mit Zimt", await SearchAsync("kekse"));
    }

    [Fact]
    public async Task Search_CapitalisedPattern_StillIgnoresCase()
    {
        // A model capitalises a German noun or an English topic word as orthography, not as a
        // demand for an exact match — reading intent into that capital costs a silent miss.
        Write("a.txt", "zwei cookies gegessen");

        Assert.Contains("a.txt:1:zwei cookies gegessen", await SearchAsync("Cookie"));
    }

    [Fact]
    public async Task Search_CaseSensitiveTrue_MatchesCapitalisationExactly()
    {
        Write("a.txt", "todo: buy milk");

        Assert.Contains("No matches found", await SearchAsync("TODO", caseSensitive: true));
        Assert.Contains("a.txt:1:todo: buy milk", await SearchAsync("TODO"));
    }
}
