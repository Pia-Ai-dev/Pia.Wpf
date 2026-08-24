using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The <c>@Files</c> picker shares <c>CollectRelativeFiles</c> with <c>list_files</c>, so both must agree on
/// which files exist.
/// </summary>
public class FilesToolHandlerListTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;
    private readonly IFileStalenessStore _staleness = new FileStalenessStore();

    public FilesToolHandlerListTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-list-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, _staleness, NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void ListRelativeFiles_ReturnsTopLevelFiles()
    {
        WriteFile("a.txt");
        WriteFile("b.md");

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("a.txt", result);
        Assert.Contains("b.md", result);
    }

    [Fact]
    public void ListRelativeFiles_NormalizesNestedPathsToForwardSlashes()
    {
        WriteFile(Path.Combine("notes", "todo.md"));

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("notes/todo.md", result);
        Assert.DoesNotContain(result, r => r.Contains('\\'));
    }

    [Fact]
    public void ListRelativeFiles_FiltersBySubstring_CaseInsensitive()
    {
        WriteFile("readme.md");
        WriteFile("notes.txt");

        var result = _handler.ListRelativeFiles(filter: "READ", max: 50);

        Assert.Single(result);
        Assert.Equal("readme.md", result[0]);
    }

    [Fact]
    public void ListRelativeFiles_FilterMatchesPathSegment()
    {
        WriteFile(Path.Combine("src", "main.cs"));
        WriteFile(Path.Combine("docs", "guide.md"));

        // The substring filter matches against the normalized relative path, so a directory
        // segment narrows the results even though the user can't type the slash in the popup.
        var result = _handler.ListRelativeFiles(filter: "src/", max: 50);

        Assert.Single(result);
        Assert.Equal("src/main.cs", result[0]);
    }

    [Fact]
    public void ListRelativeFiles_RespectsMaxCap()
    {
        for (int i = 0; i < 10; i++)
            WriteFile($"f{i}.txt");

        var result = _handler.ListRelativeFiles(filter: null, max: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ListRelativeFiles_ResultsAreSortedOrdinalIgnoreCase()
    {
        WriteFile("Zebra.txt");
        WriteFile("apple.txt");
        WriteFile("Mango.txt");

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        var expected = result.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ListRelativeFiles_NonPositiveMax_ReturnsEmpty()
    {
        WriteFile("a.txt");

        Assert.Empty(_handler.ListRelativeFiles(filter: null, max: 0));
    }

    [Fact]
    public void ListRelativeFiles_ScopesToActiveUiWorkingSubpath()
    {
        WriteFile(Path.Combine("src", "main.cs"));
        WriteFile(Path.Combine("docs", "guide.md"));

        // The @Files autocomplete narrows to the active chat's working dir; results are
        // relative to that subfolder, and siblings outside it are not listed.
        _handler.ActiveUiWorkingSubpath = "src";
        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("main.cs", result);
        Assert.DoesNotContain("docs/guide.md", result);
        Assert.DoesNotContain("src/main.cs", result);
    }

    [Fact]
    public void ListRelativeFiles_MissingWorkingSubpath_FallsBackToRoot()
    {
        WriteFile("a.txt");

        // A subpath that doesn't exist on disk fails safe to the sandbox root.
        _handler.ActiveUiWorkingSubpath = "does/not/exist";
        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("a.txt", result);
    }

    [Fact]
    public void ListRelativeFiles_ExcludesDefaultIgnoredDirectories()
    {
        // The @Files picker must not surface VCS/build/dependency noise — especially .git, now that the
        // sandbox may be a git working tree.
        WriteFile("keep.txt");
        WriteFile(Path.Combine(".git", "config"));
        WriteFile(Path.Combine("bin", "app.dll"));
        WriteFile(Path.Combine("obj", "tmp.o"));
        WriteFile(Path.Combine("node_modules", "pkg", "index.js"));

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("keep.txt", result);
        Assert.DoesNotContain(result, r => r.Contains(".git", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, r => r.StartsWith("bin/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, r => r.StartsWith("obj/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, r => r.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ListRelativeFiles_DoesNotExcludeSubstringLookalikeDirectories()
    {
        // "cabinet" contains "bin" and "objects" contains "obj" as substrings — real folders that must
        // still be listed. Guards against a substring (rather than path-segment) ignore match.
        WriteFile(Path.Combine("cabinet", "note.txt"));
        WriteFile(Path.Combine("objects", "data.txt"));

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("cabinet/note.txt", result);
        Assert.Contains("objects/data.txt", result);
    }

    [Fact]
    public void ListRelativeFiles_HonorsPiaIgnoreFile()
    {
        WriteFile("keep.md");
        WriteFile("secret.txt");
        WriteFile(".piaignore", "secret.txt\n");

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("keep.md", result);
        Assert.DoesNotContain("secret.txt", result);
    }

    [Fact]
    public void ListRelativeFiles_PiaIgnoreNegation_ReincludesFile()
    {
        WriteFile("keep.log");
        WriteFile("skip.log");
        WriteFile(".piaignore", "*.log\n!keep.log\n");

        var result = _handler.ListRelativeFiles(filter: null, max: 50);

        Assert.Contains("keep.log", result);
        Assert.DoesNotContain("skip.log", result);
    }

    [Fact]
    public async Task ListFilesTool_RejectsPathBearingPattern()
    {
        WriteFile(Path.Combine("docs", "a.md"));

        var call = new FunctionCallContent("c1", "list_files",
            new Dictionary<string, object?> { ["pattern"] = "docs/*.md" });
        var (result, action) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Null(action);
        Assert.Contains("must be a file-name glob", (string)result!);
    }

    [Fact]
    public void ListRelativeFiles_NoFolderConfigured_ReturnsEmpty()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = null });
        var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        // IsAvailable no longer requires a configured folder: tools are enabled by default and an unattended run
        // supplies its own WorkspaceRoot, but the @Files autocomplete still needs an interactive folder.
        Assert.True(handler.IsAvailable);
        Assert.Empty(handler.ListRelativeFiles(filter: null, max: 50));
    }
}
