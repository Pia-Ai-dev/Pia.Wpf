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
/// The absolute containment invariant (git never operates on a repo whose toplevel is outside the
/// sandbox), the fresh-folder routing, path-arg containment, and the TOCTOU re-validation inside the
/// deferred execute closure. All git-free via the substituted runner (canned rev-parse toplevels).
/// </summary>
public sealed class GitToolHandlerContainmentTests : IDisposable
{
    private readonly string _sandbox;
    private readonly List<string> _tempDirs = [];

    public GitToolHandlerContainmentTests()
    {
        _sandbox = NewTempDir("pia-git-sandbox-");
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static ISettingsService SettingsFor(string folder)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = folder,
            AssistantGitToolsEnabled = true,
        });
        return settings;
    }

    private static GitToolHandler Handler(ISettingsService settings, FakeGitProcessRunner runner)
        => new(settings, runner, NullLogger<GitToolHandler>.Instance);

    private static async Task<string> Call(GitToolHandler handler, string tool, Dictionary<string, object?>? args = null)
    {
        var (result, _) = await handler.HandleToolCallAsync(new FunctionCallContent("id", tool, args ?? []));
        return result?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task Read_tool_refuses_when_repo_toplevel_is_outside_the_sandbox()
    {
        var outside = NewTempDir("pia-git-outside-"); // a real dir (Canonicalize needs an existing handle)
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(outside);

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_status");

        Assert.Contains("outside the assistant files folder", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_tool_refuses_sibling_directory_sharing_the_sandbox_prefix()
    {
        // A real sibling like "<sandbox>Evil" shares the prefix but is NOT inside the sandbox; the
        // trailing-separator containment check must reject it.
        var sibling = _sandbox + "Evil";
        Directory.CreateDirectory(sibling);
        _tempDirs.Add(sibling);
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(sibling);

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_status");

        Assert.Contains("outside the assistant files folder", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_tool_refuses_when_toplevel_cannot_be_canonicalized()
    {
        // A non-existent toplevel (Canonicalize needs an existing handle) must fail closed to refuse.
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(Path.Combine(_sandbox, "does", "not", "exist-" + Guid.NewGuid().ToString("N")));

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_status");

        Assert.Contains("outside the assistant files folder", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Show_rejects_leading_dash_revision_without_spawning_git()
    {
        // Runs before any command spawn, so this security assertion holds even on a box without git.
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_sandbox);

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_show",
            new Dictionary<string, object?> { ["rev"] = "--upload-pack=evil" });

        Assert.Contains("invalid revision", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.WasCalledWith("show"));
    }

    [Fact]
    public async Task Read_tool_routes_to_git_init_when_not_a_repo()
    {
        var runner = new FakeGitProcessRunner();
        runner.NotARepo();

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_status");

        Assert.Contains("git_init", result);
    }

    [Fact]
    public async Task Read_tool_succeeds_when_repo_toplevel_is_the_sandbox_itself()
    {
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_sandbox); // toplevel == sandbox root: inclusive containment must allow it

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_status");

        Assert.DoesNotContain("outside the assistant files folder", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git_init", result);
    }

    [Fact]
    public async Task Diff_path_traversal_is_refused()
    {
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_sandbox); // containment passes; the PATH arg must still be rejected

        var result = await Call(Handler(SettingsFor(_sandbox), runner), "git_diff",
            new Dictionary<string, object?> { ["path"] = "../../evil.txt" });

        Assert.Contains("outside the assistant files folder", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Init_refuses_when_sandbox_is_repointed_between_prepare_and_execute()
    {
        var sandboxB = NewTempDir("pia-git-sandbox-b-");
        var settings = SettingsFor(_sandbox);
        var runner = new FakeGitProcessRunner();
        var handler = Handler(settings, runner);

        // Prepare against sandbox A: a pending action, nothing executed yet.
        var (result, pending) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_init", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.NotNull(pending);

        // Re-point the sandbox to B (runtime setting change).
        settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(
            settings,
            new AppSettings { AssistantFilesFolder = sandboxB, AssistantGitToolsEnabled = true });

        // Executing the captured closure must now refuse (working dir A is outside the new sandbox B)
        // and must NOT spawn git init.
        var executed = await handler.ExecutePendingActionAsync(pending!);

        Assert.Contains("outside the assistant files folder", executed?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.WasCalledWith("init"));
    }

    [Theory]
    [InlineData("git_add", "add")]
    [InlineData("git_commit", "commit")]
    public async Task Mutating_tool_refuses_when_sandbox_is_repointed_between_prepare_and_execute(string tool, string subcommand)
    {
        var sandboxB = NewTempDir("pia-git-sandbox-b-");
        var settings = SettingsFor(_sandbox);
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_sandbox); // rev-parse always reports the (original) sandbox as the repo toplevel
        var handler = Handler(settings, runner);

        var args = new Dictionary<string, object?>();
        if (tool == "git_add") args["paths"] = new[] { "a.txt" };
        if (tool == "git_commit") args["message"] = "msg";

        // Prepare against sandbox A → a pending action; nothing mutated yet.
        var (result, pending) = await handler.HandleToolCallAsync(new FunctionCallContent("id", tool, args), TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.NotNull(pending);

        // Re-point the sandbox to B, then execute the captured closure.
        settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(
            settings,
            new AppSettings { AssistantFilesFolder = sandboxB, AssistantGitToolsEnabled = true });

        var executed = await handler.ExecutePendingActionAsync(pending!);

        // The repo toplevel (A) is no longer inside the current sandbox (B) → refuse, and never spawn the mutation.
        Assert.Contains("outside the assistant files folder", executed?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.WasCalledWith(subcommand));
    }

    [Fact]
    public async Task No_folder_configured_returns_actionable_error()
    {
        var runner = new FakeGitProcessRunner();
        var result = await Call(Handler(SettingsFor(folder: null!), runner), "git_status");

        Assert.Contains("Settings", result, StringComparison.OrdinalIgnoreCase);
    }
}
