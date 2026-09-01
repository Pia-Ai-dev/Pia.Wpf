using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Unless <c>GitToolHandler</c> resolves the repository against the ambient run workspace, the agent
/// writes into its workspace and commits the interactive folder's stale tree.</summary>
public sealed class GitToolHandlerWorkspaceRootTests : IDisposable
{
    private readonly string _dir;
    private readonly string _interactive;
    private readonly string _runRoot;

    public GitToolHandlerWorkspaceRootTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaGitWs_" + Guid.NewGuid().ToString("N"));
        _interactive = Path.Combine(_dir, "files");
        // Shaped like the production path (<runs base>\<runId>) although it need not BE it: the git tools
        // never consult SensitivePathGuard, so unlike the file tools they cannot be fooled by a temp fixture.
        _runRoot = Path.Combine(_dir, "runs", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_interactive);
        Directory.CreateDirectory(_runRoot);
        // The handler canonicalizes the ambient root and GetTempPath can carry an 8.3 or a link component, so a
        // raw Path.Combine expectation would compare two spellings of the same directory.
        _runRoot = SafeFolderPath.Canonicalize(_runRoot);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        TempPath.Remove(_dir);
    }

    private ISettingsService SettingsFor(string folder)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings
        {
            AssistantFilesFolder = folder,
            AssistantGitToolsEnabled = true,
        });
        return settings;
    }

    private GitToolHandler Handler(ISettingsService settings, FakeGitProcessRunner runner)
        => new(settings, runner, NullLogger<GitToolHandler>.Instance);

    /// <summary>The discovery ceiling has to follow the ambient root: left at the assistant folder's parent it
    /// does not apply, and upward <c>.git</c> discovery could bind a repository in the user's profile.</summary>
    [Fact]
    public async Task GitToolHandler_ResolvesTheRepoAgainstTheAmbientWorkspaceRoot()
    {
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_runRoot); // in worktree mode the toplevel IS the workspace root
        var handler = Handler(SettingsFor(_interactive), runner);
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, null, _runRoot);

        var (result, _) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_status", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("outside the assistant files folder", result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git_init", result?.ToString() ?? "");

        var status = Assert.Single(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "status");
        Assert.Equal(_runRoot, status.WorkingDirectory);
        Assert.Equal(Directory.GetParent(_runRoot)!.FullName, status.CeilingDirectory);
    }

    /// <summary>Ambient flow is not guaranteed inside the deferred execute closure, so a mutating tool re-guards
    /// against the scope captured at prepare time rather than re-reading the ambient.</summary>
    [Fact]
    public async Task GitToolHandler_MutatingTool_StillPassesContainment_AfterTheApprovalAwait()
    {
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_runRoot);
        var handler = Handler(SettingsFor(_interactive), runner);
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, null, _runRoot);

        var (result, pending) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_commit", new Dictionary<string, object?> { ["message"] = "msg" }),
            TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.NotNull(pending);

        // The user approves; by the time the closure runs, the ambient turn context is gone.
        TaskAmbient.Current = null;
        var executed = await handler.ExecutePendingActionAsync(pending!);

        Assert.DoesNotContain("outside the assistant files folder", executed?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "commit");
    }

    /// <summary>Discrimination control for the fact above: freezing containment for every dispatch, rather than
    /// only for an isolated run, would silently retire the refusal after a runtime re-point.</summary>
    [Fact]
    public async Task GitToolHandler_WithNoAmbient_StillRefusesAfterAFolderRepoint()
    {
        var folderB = Path.Combine(_dir, "files-b");
        Directory.CreateDirectory(folderB);
        var settings = SettingsFor(_interactive);
        var runner = new FakeGitProcessRunner();
        runner.RepoAt(_interactive);
        var handler = Handler(settings, runner);
        Assert.Null(TaskAmbient.Current);

        var (result, pending) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_commit", new Dictionary<string, object?> { ["message"] = "msg" }),
            TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.NotNull(pending);

        settings.SettingsChanged += Raise.Event<EventHandler<AppSettings>>(
            settings, new AppSettings { AssistantFilesFolder = folderB, AssistantGitToolsEnabled = true });

        var executed = await handler.ExecutePendingActionAsync(pending!);

        Assert.Contains("outside the assistant files folder", executed?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "commit");
    }

    /// <summary>Copy mode's workspace has no <c>.git</c>, so <c>git_init</c> inside it is contained and expected;
    /// what must not happen is the "outside the assistant files folder" refusal.</summary>
    [Fact]
    public async Task GitToolHandler_InAnIsolatedWorkspaceWithNoRepo_RoutesToInitInsideTheWorkspace()
    {
        var runner = new FakeGitProcessRunner();
        runner.NotARepo();
        var handler = Handler(SettingsFor(_interactive), runner);
        TaskAmbient.Current = new TaskContext(Guid.NewGuid(), null, null, _runRoot);

        var (status, _) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_status", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);
        Assert.Contains("git_init", status?.ToString() ?? "");

        var (result, pending) = await handler.HandleToolCallAsync(
            new FunctionCallContent("id", "git_init", new Dictionary<string, object?>()), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        var executed = await handler.ExecutePendingActionAsync(pending!);
        Assert.DoesNotContain("outside the assistant files folder", executed?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        var init = Assert.Single(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "init");
        Assert.Equal(_runRoot, init.WorkingDirectory);
    }
}
