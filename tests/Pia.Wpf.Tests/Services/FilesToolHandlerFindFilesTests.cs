using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Paths;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

public class FilesToolHandlerFindFilesTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerFindFilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-find-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });

        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        TempPath.Remove(_root);
    }

    private async Task<string> FindAsync(string pattern, string? path = null, int? limit = null)
    {
        var args = new Dictionary<string, object?> { ["pattern"] = pattern };
        if (path is not null) args["path"] = path;
        if (limit is not null) args["limit"] = limit.Value;
        var call = new FunctionCallContent("c1", "find_files", args);
        var (result, action) = await _handler.HandleToolCallAsync(call);
        Assert.Null(action); // read-only: never an action card
        return (string)result!;
    }

    private void Write(string relPath, string content = "x")
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Find_BareGlob_MatchesAtAnyDepth_SortedRootRelativeForwardSlash()
    {
        Write("a.md");
        Write("docs/b.md");
        Write("docs/deep/c.md");
        Write("note.txt");

        var result = await FindAsync("*.md");

        Assert.Equal("a.md\ndocs/b.md\ndocs/deep/c.md", result);
    }

    [Fact]
    public async Task Find_SlashBearingGlob_IsAnchoredToTheSearchedFolder()
    {
        Write("docs/b.md");
        Write("other/b.md");

        var result = await FindAsync("docs/**/*.md");

        Assert.Equal("docs/b.md", result);
    }

    [Fact]
    public async Task Find_QuestionMark_MatchesExactlyOneCharacter()
    {
        Write("a1.txt");
        Write("a12.txt");

        var result = await FindAsync("a?.txt");

        Assert.Equal("a1.txt", result);
    }

    [Fact]
    public async Task Find_CharacterClass_Narrows()
    {
        Write("a1.txt");
        Write("ab.txt");

        var result = await FindAsync("a[0-9].txt");

        Assert.Equal("a1.txt", result);
    }

    [Fact]
    public async Task Find_Path_NarrowsTheSearch_ResultsStayRootRelative()
    {
        Write("docs/a.md");
        Write("other/a.md");

        var result = await FindAsync("*.md", path: "docs");

        Assert.Equal("docs/a.md", result);
    }

    [Fact]
    public async Task Find_SlashBearingGlob_AnchorsToTheSearchedFolder_NotTheSandboxRoot()
    {
        Write("docs/sub/a.md");
        Write("docs/other.md");

        // The glob is matched against the SEARCHED folder while the hit is emitted root-relative, so
        // the two bases differ here and a bare "*.md" could not tell them apart.
        Assert.Equal("docs/sub/a.md", await FindAsync("sub/*.md", path: "docs"));
        Assert.Equal("No files found.", await FindAsync("docs/sub/*.md", path: "docs"));
    }

    [Fact]
    public async Task Find_FolderShapedGlob_MatchesEverythingBeneathIt()
    {
        Write("docs/a.md");
        Write("docs/deep/b.md");
        Write("other/c.md");

        Assert.Equal("docs/a.md\ndocs/deep/b.md", await FindAsync("docs/"));
    }

    [Fact]
    public async Task Find_BackslashSpelledGlob_MatchesTheSameFilesAsForwardSlashes()
    {
        Write("docs/a.md");
        Write("other/b.md");

        Assert.Equal("docs/a.md", await FindAsync(@"docs\*.md"));
    }

    [Fact]
    public async Task Find_ScopedHit_IsRoundTrippableThroughReadFile()
    {
        Write("docs/a.md", "FOUND");

        var hit = await FindAsync("*.md", path: "docs");
        Assert.Equal("docs/a.md", hit);

        // The contract: a hit is consumable by the other file tools without re-derivation.
        var read = new FunctionCallContent("c2", "read_file", new Dictionary<string, object?> { ["path"] = hit });
        var (readResult, _) = await _handler.HandleToolCallAsync(read, TestContext.Current.CancellationToken);
        Assert.Contains("FOUND", (string)readResult!);
    }

    [Fact]
    public async Task Find_PathNotFound_SuggestsSimilarDirectory()
    {
        Write("reports/q1.md");

        var result = await FindAsync("*.md", path: "report"); // typo: missing 's'

        Assert.Contains("was not found", result);
        Assert.Contains("reports", result);
    }

    [Fact]
    public async Task Find_PathClimbingOutOfTheSandbox_IsRejected()
    {
        Write("inside.md");

        var result = await FindAsync("*.md", path: "../..");

        Assert.Contains("was not found", result);
        Assert.DoesNotContain("inside.md", result);
    }

    [Fact]
    public async Task Find_MoreHitsThanLimit_TruncatesWithTheNote()
    {
        for (int i = 1; i <= 5; i++) Write($"f{i}.md");

        var result = await FindAsync("*.md", limit: 2);

        Assert.Equal(
            "f1.md\nf2.md\n(Results are truncated: showing first 2 of 5 results. " +
            "Consider using a more specific path or pattern.)",
            result);
    }

    [Fact]
    public async Task Find_HitsEqualToLimit_HasNoTruncationNote()
    {
        Write("a.md");
        Write("b.md");

        var result = await FindAsync("*.md", limit: 2);

        Assert.Equal("a.md\nb.md", result);
    }

    [Fact]
    public async Task Find_PathTypo_Transposition_SuggestsTheDirectory()
    {
        Write("reports/q1.md");

        var result = await FindAsync("*.md", path: "reprots");

        Assert.Contains("Did you mean: reports?", result);
    }

    [Fact]
    public async Task Find_PathTypo_DroppedLetter_SuggestsTheDirectory()
    {
        Write("reports/q1.md");

        var result = await FindAsync("*.md", path: "reprts");

        Assert.Contains("Did you mean: reports?", result);
    }

    [Fact]
    public async Task Find_PathWhollyUnrelated_SuggestsNothing()
    {
        Write("reports/q1.md");

        var result = await FindAsync("*.md", path: "zzz-qqq");

        Assert.Contains("was not found", result);
        Assert.DoesNotContain("Did you mean", result);
    }

    [Fact]
    public async Task Find_PathNotFound_SuggestionUsesForwardSlashes()
    {
        Write("docs/reports/q1.md");

        var result = await FindAsync("*.md", path: "docs/report");

        Assert.Contains("Did you mean: docs/reports?", result);
    }

    [Fact]
    public async Task Find_NoHits_ReturnsNoFilesFound()
    {
        Write("a.txt");

        var result = await FindAsync("*.md");

        Assert.Equal("No files found.", result);
    }

    [Fact]
    public async Task Find_ExcludesIgnoredTreesAndFiles()
    {
        Write(".piaignore", "secret.md\n");
        Write("keep.md");
        Write("secret.md");
        Write(".git/g.md");
        Write("bin/b.md");
        Write("obj/o.md");
        Write("node_modules/n.md");

        var result = await FindAsync("*.md");

        Assert.Equal("keep.md", result);
    }

    [Fact]
    public async Task Find_ExcludesTheRunScratchFolder()
    {
        Write("keep.md");
        Write(".scratch/notes.md");

        var result = await FindAsync("*.md");

        Assert.Equal("keep.md", result);
    }

    [Fact]
    public async Task Find_PointedAtScratch_StillSeesIt()
    {
        Write(".scratch/notes.md");

        var result = await FindAsync("*.md", path: ".scratch");

        Assert.Equal(".scratch/notes.md", result);
    }

    [Fact]
    public async Task Find_SeparatorBearingPattern_IsAccepted_UnlikeListFiles()
    {
        Write("docs/a.md");

        Assert.Equal("docs/a.md", await FindAsync("docs/*.md"));

        var list = new FunctionCallContent("c2", "list_files",
            new Dictionary<string, object?> { ["pattern"] = "docs/*.md" });
        var (listResult, _) = await _handler.HandleToolCallAsync(list, TestContext.Current.CancellationToken);
        Assert.Contains("must be a file-name glob", (string)listResult!);
    }

    [Fact]
    public async Task Find_MissingPattern_ReturnsError()
    {
        var call = new FunctionCallContent("c3", "find_files", new Dictionary<string, object?>());
        var (result, _) = await _handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

        Assert.Contains("'pattern'", (string)result!);
    }

    [Fact]
    public async Task Find_InvalidPattern_IsADiagnosticNotACrash()
    {
        Write("a.md");

        var result = await FindAsync("[z-a].md"); // reversed character range

        Assert.Contains("Error: Invalid glob pattern", result);
    }
}

/// <summary>
/// The one find_files fact that needs a sandbox inside a root the live <c>SensitivePathGuard</c> blocks, which is
/// why it sits in its own class: it needs the redirected profile, and the redirect has to be serialized against
/// every other test that resolves a Pia path.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class FilesToolHandlerFindFilesSensitivePathTests : IClassFixture<RedirectedProfileFixture>
{
    public FilesToolHandlerFindFilesSensitivePathTests(RedirectedProfileFixture profile) => _ = profile;

    [Fact]
    public async Task Find_NegationCannotResurfaceAGuardBlockedPath()
    {
        var blockedRoot = Path.Combine(PiaPaths.LocalDataDirectory, "pia-find-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blockedRoot);
        try
        {
            // Non-vacuity: the guard has to be the thing that hides the file, not an unreadable root.
            Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(blockedRoot, "secret.md"), out _));

            File.WriteAllText(Path.Combine(blockedRoot, "secret.md"), "x");
            File.WriteAllText(Path.Combine(blockedRoot, ".piaignore"), "!**\n"); // try to re-include everything

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = blockedRoot });
            var handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);

            var call = new FunctionCallContent("c1", "find_files",
                new Dictionary<string, object?> { ["pattern"] = "*.md" });
            var (result, _) = await handler.HandleToolCallAsync(call, TestContext.Current.CancellationToken);

            Assert.Equal("No files found.", (string)result!);
        }
        finally
        {
            TempPath.Remove(blockedRoot);
        }
    }
}
