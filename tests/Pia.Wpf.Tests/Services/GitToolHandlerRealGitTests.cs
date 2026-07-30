using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// End-to-end coverage against a real git process: the fresh-folder flow (non-repo → git_init → repo)
/// and the read tools on a seeded repository. Skipped when git is absent. Lives in the non-parallel
/// "GitLocatorStatic" collection because the real <see cref="GitProcessRunner"/> reads the process-global
/// <see cref="GitLocator"/> (a concurrent locator-probe test would otherwise race it).
/// </summary>
[Collection("GitLocatorStatic")]
public sealed class GitToolHandlerRealGitTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public GitToolHandlerRealGitTests() => GitLocator.ResetForTests();

    public void Dispose()
    {
        TaskAmbient.Current = null;
        GitLocator.ResetForTests();
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pia-git-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static GitToolHandler HandlerFor(string folder)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = folder,
            AssistantGitToolsEnabled = true,
        });
        return new GitToolHandler(settings, new GitProcessRunner(), NullLogger<GitToolHandler>.Instance);
    }

    private static async Task<string> Call(GitToolHandler h, string tool, Dictionary<string, object?>? args = null)
    {
        var (result, _) = await h.HandleToolCallAsync(new FunctionCallContent("id", tool, args ?? []));
        return result?.ToString() ?? string.Empty;
    }

    private static Task<(object? Result, GitToolCall? Pending)> Prepare(
        GitToolHandler h, string tool, Dictionary<string, object?>? args = null)
        => h.HandleToolCallAsync(new FunctionCallContent("id", tool, args ?? []));

    private static void RunRawGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(GitLocator.Executable!)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
    }

    private static void SeedRepo(string dir)
    {
        RunRawGit(dir, "init");
        SeedIdentity(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello\n");
        RunRawGit(dir, "add", "a.txt");
        RunRawGit(dir, "commit", "-m", "initial commit");
    }

    // Set a repo-local identity so the handler's own git_commit (which doesn't pass -c user.*) succeeds
    // regardless of whether a global identity is configured on the box.
    private static void SeedIdentity(string dir)
    {
        RunRawGit(dir, "config", "user.email", "t@e.st");
        RunRawGit(dir, "config", "user.name", "Test");
    }

    // Repo at the sandbox root with a committed file inside a "sub/" folder, for the working-subpath case.
    private static void SeedRepoWithSubfolderFile(string dir)
    {
        RunRawGit(dir, "init");
        SeedIdentity(dir);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "note.txt"), "hello\n");
        RunRawGit(dir, "add", "-A");
        RunRawGit(dir, "commit", "-m", "seed sub");
    }

    [Fact]
    public async Task FreshFolder_RoutesToInit_ThenInitCreatesRepo_ThenStatusSucceeds()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        var handler = HandlerFor(sandbox);

        // 1. A fresh folder is not a repo yet → the model is routed to git_init.
        var status1 = await Call(handler, "git_status");
        Assert.Contains("git_init", status1);

        // 2. git_init returns a pending action; executing it creates the repo.
        var (initResult, pending) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_init", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);
        Assert.Null(initResult);
        Assert.NotNull(pending);
        var initExecuted = await handler.ExecutePendingActionAsync(pending!);
        Assert.DoesNotContain("Error", initExecuted?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(sandbox, ".git")));

        // 3. A follow-up status now succeeds against the contained repo.
        var status2 = await Call(handler, "git_status");
        Assert.DoesNotContain("git_init", status2);
        Assert.DoesNotContain("outside the assistant files folder", status2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeededRepo_ReadTools_ReturnExpectedContent()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var handler = HandlerFor(sandbox);

        var status = await Call(handler, "git_status");
        Assert.Contains("clean", status, StringComparison.OrdinalIgnoreCase);

        var log = await Call(handler, "git_log");
        Assert.Contains("initial commit", log);

        var branch = await Call(handler, "git_branch");
        Assert.False(string.IsNullOrWhiteSpace(branch));

        var cleanDiff = await Call(handler, "git_diff");
        Assert.Contains("No differences", cleanDiff, StringComparison.OrdinalIgnoreCase);

        // Modify the tracked file; the diff now surfaces the change.
        File.AppendAllText(Path.Combine(sandbox, "a.txt"), "world\n");
        var dirtyDiff = await Call(handler, "git_diff");
        Assert.Contains("world", dirtyDiff);
    }

    [Fact]
    public async Task Show_ReturnsFileContentsAtRevision()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var handler = HandlerFor(sandbox);

        var show = await Call(handler, "git_show",
            new Dictionary<string, object?> { ["rev"] = "HEAD", ["path"] = "a.txt" });

        Assert.Contains("hello", show);
    }

    [Fact]
    public async Task Log_OnUnbornHead_ReportsNoCommits()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        RunRawGit(sandbox, "init"); // repo with no commits (unborn HEAD)
        var handler = HandlerFor(sandbox);

        var log = await Call(handler, "git_log");

        Assert.Contains("No commits yet", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Show_RejectsRevisionWithLeadingDash()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var handler = HandlerFor(sandbox);

        var show = await Call(handler, "git_show",
            new Dictionary<string, object?> { ["rev"] = "--upload-pack=evil" });

        Assert.Contains("invalid revision", show, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddThenCommit_ReturnPendingActions_AndExecutingThemPerformsTheWork()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        RunRawGit(sandbox, "init");
        SeedIdentity(sandbox);
        File.WriteAllText(Path.Combine(sandbox, "b.txt"), "new file\n");
        var handler = HandlerFor(sandbox);

        // git_add returns a pending action (not executed).
        var (addResult, addPending) = await Prepare(handler, "git_add",
            new Dictionary<string, object?> { ["paths"] = new[] { "b.txt" } });
        Assert.Null(addResult);
        Assert.NotNull(addPending);
        var addExec = await handler.ExecutePendingActionAsync(addPending!);
        Assert.DoesNotContain("Error", addExec?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);

        // git_commit returns a pending action; the commit does NOT exist until the closure runs.
        var (commitResult, commitPending) = await Prepare(handler, "git_commit",
            new Dictionary<string, object?> { ["message"] = "add b via handler" });
        Assert.Null(commitResult);
        Assert.NotNull(commitPending);
        Assert.Contains("No commits yet", await Call(handler, "git_log"), StringComparison.OrdinalIgnoreCase);

        var commitExec = await handler.ExecutePendingActionAsync(commitPending!);
        Assert.DoesNotContain("Error", commitExec?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("add b via handler", await Call(handler, "git_log"));
    }

    [Fact]
    public async Task Switch_CreateBranch_ReturnsPending_AndExecutingSwitches()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var handler = HandlerFor(sandbox);

        var (result, pending) = await Prepare(handler, "git_switch",
            new Dictionary<string, object?> { ["branch"] = "feature/x", ["create"] = true });
        Assert.Null(result);
        Assert.NotNull(pending);

        var exec = await handler.ExecutePendingActionAsync(pending!);
        Assert.DoesNotContain("Error", exec?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feature/x", await Call(handler, "git_branch"));
    }

    [Fact]
    public async Task Restore_ReturnsPending_AndExecutingDiscardsTheChange()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var file = Path.Combine(sandbox, "a.txt");
        File.AppendAllText(file, "uncommitted junk\n");
        var handler = HandlerFor(sandbox);

        var (result, pending) = await Prepare(handler, "git_restore",
            new Dictionary<string, object?> { ["paths"] = new[] { "a.txt" } });
        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Contains("junk", File.ReadAllText(file)); // not discarded until the closure runs

        await handler.ExecutePendingActionAsync(pending!);
        Assert.Equal("hello\n", File.ReadAllText(file).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Stash_List_RunsInline_WithNoPendingAction()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var handler = HandlerFor(sandbox);

        var (result, pending) = await Prepare(handler, "git_stash",
            new Dictionary<string, object?> { ["operation"] = "list" });

        Assert.Null(pending); // read-only: inline, never a confirmation card
        Assert.Contains("No stashes", result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stash_Push_ReturnsPendingAction()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        File.AppendAllText(Path.Combine(sandbox, "a.txt"), "change\n");
        var handler = HandlerFor(sandbox);

        var (result, pending) = await Prepare(handler, "git_stash",
            new Dictionary<string, object?> { ["operation"] = "push" });

        Assert.Null(result);
        Assert.NotNull(pending); // push mutates → confirmation card
    }

    [Fact]
    public async Task Stash_PushThenPop_RoundTrips()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepo(sandbox);
        var file = Path.Combine(sandbox, "a.txt");
        File.AppendAllText(file, "wip\n");
        var handler = HandlerFor(sandbox);

        var (_, pushPending) = await Prepare(handler, "git_stash", new Dictionary<string, object?> { ["operation"] = "push" });
        await handler.ExecutePendingActionAsync(pushPending!);
        Assert.Equal("hello\n", File.ReadAllText(file).Replace("\r\n", "\n")); // change stashed away

        var (_, popPending) = await Prepare(handler, "git_stash", new Dictionary<string, object?> { ["operation"] = "pop" });
        await handler.ExecutePendingActionAsync(popPending!);
        Assert.Contains("wip", File.ReadAllText(file)); // change restored
    }

    [Fact]
    public async Task WorkingSubpath_PathspecsResolveAgainstWorkingDir_NotToplevel()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        var sandbox = NewTempDir();
        SeedRepoWithSubfolderFile(sandbox); // repo root == sandbox; committed file at sub/note.txt
        var handler = HandlerFor(sandbox);
        var note = Path.Combine(sandbox, "sub", "note.txt");

        // Narrow the working directory to the "sub" subfolder — so root != repo toplevel.
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), "sub");

        // git_diff with a working-dir-relative path must find the change (a toplevel-relative pathspec
        // would look for sub/sub/note.txt and silently report "No differences").
        File.AppendAllText(note, "line2\n");
        var diff = await Call(handler, "git_diff", new Dictionary<string, object?> { ["path"] = "note.txt" });
        Assert.Contains("line2", diff);

        // git_restore must discard the change to the CORRECT file.
        var (_, restorePending) = await Prepare(handler, "git_restore",
            new Dictionary<string, object?> { ["paths"] = new[] { "note.txt" } });
        await handler.ExecutePendingActionAsync(restorePending!);
        Assert.Equal("hello\n", File.ReadAllText(note).Replace("\r\n", "\n"));

        // git_add must stage the working-dir-relative path (a toplevel-relative pathspec would fail
        // "pathspec did not match").
        File.AppendAllText(note, "line3\n");
        var (_, addPending) = await Prepare(handler, "git_add",
            new Dictionary<string, object?> { ["paths"] = new[] { "note.txt" } });
        var addExec = (await handler.ExecutePendingActionAsync(addPending!))?.ToString() ?? "";
        Assert.DoesNotContain("Error", addExec, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not match", addExec, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("note.txt", await Call(handler, "git_status"));
    }
}
