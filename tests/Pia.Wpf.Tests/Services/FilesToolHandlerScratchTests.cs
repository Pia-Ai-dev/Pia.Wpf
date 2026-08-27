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
/// The <c>.scratch/</c> convention as the file tools see it. BG3's config-todo.txt reported three TODOs where
/// the fixture had two, and the third was a match inside the run's OWN notes file — a run's intermediate output
/// being searchable by its later steps is a self-contamination loop.
/// </summary>
public sealed class FilesToolHandlerScratchTests : IDisposable
{
    private readonly string _root;
    private readonly FilesToolHandler _handler;

    public FilesToolHandlerScratchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-scratch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _root });
        _handler = new FilesToolHandler(settings, new FileStalenessStore(), NullLogger<FilesToolHandler>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var (result, _) = await _handler.HandleToolCallAsync(new FunctionCallContent("c1", tool, args));
        return (string)result!;
    }

    private Task<string> ListAsync() => CallAsync("list_files", []);

    private Task<string> SearchAsync(string pattern, string? path = null)
    {
        var args = new Dictionary<string, object?> { ["pattern"] = pattern };
        if (path is not null) args["path"] = path;
        return CallAsync("search_files", args);
    }

    [Fact]
    public async Task ListFiles_DoesNotShowTheScratchFolder()
    {
        Write("staging.env", "# TODO: align with baseline");
        Write(".scratch/env-pairs.md", "notes");
        Write(".scratch/deep/more.md", "more notes");

        var listing = await ListAsync();

        Assert.Contains("staging.env", listing);
        Assert.DoesNotContain("env-pairs.md", listing);
        Assert.DoesNotContain("more.md", listing);
    }

    /// <summary>Root-level only — a <c>.scratch</c> the user keeps inside their own tree is theirs.</summary>
    [Fact]
    public async Task ListFiles_StillShowsANestedScratchFolder()
    {
        Write("docs/.scratch/kept.md", "the user's own");

        Assert.Contains("kept.md", await ListAsync());
    }

    [Fact]
    public async Task SearchFiles_DoesNotMatchInsideTheScratchFolder()
    {
        Write("staging.env", "# TODO: align with baseline before the next release");
        Write(".scratch/env-pairs.md", "- REGION — not set; noted as intentional (# TODO: REGION is unset)");

        var hits = await SearchAsync("TODO");

        Assert.Contains("staging.env", hits);
        Assert.DoesNotContain("env-pairs.md", hits);
    }

    /// <summary>The carve-out: a search pointed AT the notes still reads them, so the model can use its own.</summary>
    [Fact]
    public async Task SearchFiles_PointedAtTheScratchFolder_StillReadsIt()
    {
        Write(".scratch/env-pairs.md", "# TODO: REGION is unset");
        Write(".scratch/deep/later.md", "# TODO: and one further down");

        var hits = await SearchAsync("TODO", path: ".scratch");

        Assert.Contains("env-pairs.md", hits);
        Assert.Contains("later.md", hits);
    }

    /// <summary>Hidden from the walks, never from an explicit path: the notes would be useless otherwise.</summary>
    [Fact]
    public async Task ReadAndWrite_ByExplicitPath_StillReachTheScratchFolder()
    {
        // A write is a DEFERRED action (the gate decides whether it runs), so the handler hands back a
        // pending call rather than the effect.
        var write = new FunctionCallContent("c1", "write_file", new Dictionary<string, object?>
        {
            ["path"] = ".scratch/notes.md",
            ["content"] = "carried across the step",
        });
        var (_, pending) = await _handler.HandleToolCallAsync(write, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        await pending!.Execute();
        Assert.True(File.Exists(Path.Combine(_root, ".scratch", "notes.md")));

        var read = await CallAsync("read_file", new Dictionary<string, object?> { ["path"] = ".scratch/notes.md" });
        Assert.Contains("carried across the step", read);
    }
}
