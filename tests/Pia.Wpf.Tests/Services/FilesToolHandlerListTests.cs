using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Coverage for <see cref="FilesToolHandler.ListRelativeFiles"/> — the enumeration that
/// backs the <c>@Files</c> autocomplete picker. It shares <c>CollectRelativeFiles</c> with
/// <c>list_files</c> (same containment + sensitive-path filtering) so the picker and the
/// tools agree on which files exist, and normalizes to forward slashes.
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
    public void ListRelativeFiles_NoFolderConfigured_ReturnsEmpty()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = null });
        var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

        Assert.False(handler.IsAvailable);
        Assert.Empty(handler.ListRelativeFiles(filter: null, max: 50));
    }
}
