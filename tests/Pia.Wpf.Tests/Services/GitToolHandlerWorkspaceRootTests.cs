using System.IO;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 06 G3, git-tool parity: <c>GitToolHandler</c> resolves the repository against the ambient run
/// workspace, the way <c>FilesToolHandler</c> already does. Without this, isolation creates an incoherence
/// the tree did not have before — the agent writes into its workspace and commits the interactive folder's
/// stale tree.
/// <para>
/// The interactive TOCTOU re-guard is the thing most easily broken while doing that, so it is asserted HERE
/// as well, next to its isolated-run counterpart: an isolated run's approved mutation must NOT be refused
/// after the ambient is gone, while an interactive one MUST still be refused after the folder is re-pointed.
/// The two facts are each other's discrimination control. <c>GitToolHandlerContainmentTests</c> passes
/// unmodified — if any of its assertions needed editing, containment semantics changed and the change is wrong.
/// </para>
/// </summary>
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
        // CANONICALIZE the expectation, exactly as HeadlessRunLauncherTests and LiveTurnExecutorPlannedRunTests
        // do and for the same reason: GitToolHandler resolves the ambient root through
        // SafeFolderPath.NormalizeWorkspaceRoot, which canonicalizes (long form, junctions resolved, on-disk
        // casing). GetTempPath can carry an 8.3 or a link component — a corporate profile redirection or a
        // service account — so a raw Path.Combine expectation would compare two spellings of the same directory
        // and red as a git-parity regression that is really a fixture defect.
        _runRoot = SafeFolderPath.Canonicalize(_runRoot);
    }

    public void Dispose()
    {
        TaskAmbient.Current = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
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

    /// <summary>
    /// T-G3-11, <b>REGRESSION</b>. The dispatch resolves its base root from the ambient workspace root, so git
    /// runs in the run's workspace — and the discovery CEILING follows it too. R17: with a cwd under
    /// <c>%LOCALAPPDATA%\Pia\runs\&lt;id&gt;</c> a ceiling pointing at the assistant folder's parent does not
    /// apply at all, and upward <c>.git</c> discovery would walk runs → Pia → Local → AppData → %USERPROFILE%
    /// and could bind a repository the user keeps in their profile.
    /// </summary>
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

    /// <summary>
    /// T-G3-12, <b>REGRESSION</b>. The §6.2 trap, as a fact. A mutating tool captures the resolved scope at
    /// prepare time and re-guards against THAT, never against the ambient: ambient flow is not guaranteed
    /// inside the deferred execute closure, so a handler that re-read it would resolve <c>_currentFolder</c>,
    /// find the toplevel (the workspace) outside it, and refuse a commit the user had already approved.
    /// </summary>
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

    /// <summary>
    /// T-G3-13, <b>GUARD</b> and the discrimination control for the fact above: with no ambient workspace
    /// root, containment is still judged against the CURRENT configured folder, so a runtime re-point between
    /// prepare and execute still refuses. Freezing containment for every dispatch — rather than only for an
    /// isolated run — would silently retire this guard, and this is the row that would notice.
    /// </summary>
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

    /// <summary>
    /// <b>GUARD</b>. Copy mode's workspace has no <c>.git</c> (B6 prunes it), so the model gets the
    /// fresh-folder hint and may <c>git_init</c> inside its own workspace — contained and harmless, and a
    /// stated release-note item (§13.3) rather than a bug. What must NOT happen is a refusal that reads as
    /// "outside the assistant files folder", which is what a handler still comparing against
    /// <c>_currentFolder</c> would produce.
    /// </summary>
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
