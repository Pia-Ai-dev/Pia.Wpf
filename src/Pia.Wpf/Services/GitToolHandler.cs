using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Exposes a discrete, allowlisted set of local git tools over the git repository in the active chat's
/// working directory. There is deliberately no generic <c>git_run(cmd)</c> and no network tool
/// (push/pull/fetch/clone) — the no-network scope is enforced by absence, and by the hardened
/// environment in <see cref="GitProcessRunner"/>.
///
/// <para>
/// Absolute invariant: git never operates on a repository whose toplevel lives outside the configured
/// <c>AssistantFilesFolder</c>. Every call resolves <c>git rev-parse --show-toplevel</c>, canonicalizes
/// it (resolving reparse points), and requires containment; mutating tools re-run the guard inside the
/// deferred <see cref="GitToolCall.Execute"/> closure because the sandbox root is mutable at runtime.
/// </para>
/// </summary>
public class GitToolHandler : IGitToolHandler
{
    private const int MaxFormattedChars = 100 * 1024; // ~100K-char cap on returned text
    private const int DefaultLogCount = 20;
    private const int MaxLogCount = 100;

    // Conservative allowlist for revisions/branch names: letters, digits, and . _ / - only, and never a
    // leading '-' (ProcessStartInfo.ArgumentList stops quoting injection, but git still reads a leading-dash
    // positional as a flag). Anchored, so any other character rejects the whole value.
    private static readonly Regex RevPattern = new("^[A-Za-z0-9._/-]+$", RegexOptions.Compiled);

    private const string FreshFolderHint =
        "This folder isn't a git repository yet. Call git_init to create one here, then retry.";
    private const string OutsideSandboxRefusal =
        "Refused: the working directory is inside a git repository whose root is outside the assistant files folder. " +
        "Git tools only operate on repositories within the configured folder.";

    private readonly ISettingsService _settingsService;
    private readonly IGitProcessRunner _runner;
    private readonly ILogger<GitToolHandler> _logger;
    private volatile string? _currentFolder;
    private volatile bool _toolsEnabled = true;

    public GitToolHandler(ISettingsService settingsService, IGitProcessRunner runner, ILogger<GitToolHandler> logger)
    {
        _settingsService = settingsService;
        _runner = runner;
        _logger = logger;

        // Settings are already loaded and cached by the time any handler is constructed, so this sync
        // wait returns immediately from the in-memory cache (mirrors FilesToolHandler).
        try
        {
            var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();
            _toolsEnabled = settings.AssistantGitToolsEnabled;
            UpdateFolder(settings.AssistantFilesFolder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load initial git tool settings");
        }

        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Git installed (from the injected runner) AND enabled AND a sandbox folder configured.</summary>
    public bool IsAvailable => _runner.IsGitInstalled && _toolsEnabled && _currentFolder is not null;

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _toolsEnabled = settings.AssistantGitToolsEnabled;
        UpdateFolder(settings.AssistantFilesFolder);
    }

    private void UpdateFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            _currentFolder = null;
            return;
        }
        try
        {
            var full = Path.GetFullPath(folder);
            // Canonicalize (resolve reparse points) when the folder exists so a junction/symlink in the
            // configured root itself is not a containment hole; a not-yet-created folder stays as-is
            // (the per-call Directory.Exists guard handles the missing case).
            _currentFolder = Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : full;
        }
        catch { _currentFolder = null; }
    }

    public IList<AITool> GetTools()
    {
        if (!IsAvailable) return [];

        return
        [
            AIFunctionFactory.Create(StatusSchema, "git_status",
                "Show the working-tree status (current branch and changed files) of the git repository in the working directory."),
            AIFunctionFactory.Create(LogSchema, "git_log",
                "Show recent commit history (short hash, date, author, subject)."),
            AIFunctionFactory.Create(DiffSchema, "git_diff",
                "Show changes as a unified diff: working tree vs index by default, staged changes with staged=true, optionally limited to a path."),
            AIFunctionFactory.Create(BranchSchema, "git_branch",
                "List local branches and mark the current one (or report a detached HEAD)."),
            AIFunctionFactory.Create(ShowSchema, "git_show",
                "Show a revision's summary and diffstat, or the contents of a file at a given revision."),
            AIFunctionFactory.Create(InitSchema, "git_init",
                "Initialize a new git repository in the working directory (turns a fresh, non-repo folder into a git repository). Requires user approval."),
            AIFunctionFactory.Create(AddSchema, "git_add",
                "Stage file changes for the next commit. Requires user approval."),
            AIFunctionFactory.Create(CommitSchema, "git_commit",
                "Record the staged changes as a new commit (skips repo commit hooks). Requires user approval."),
            AIFunctionFactory.Create(SwitchSchema, "git_switch",
                "Switch to another branch (optionally creating it). Requires user approval — switching can shed or overlay uncommitted changes."),
            AIFunctionFactory.Create(RestoreSchema, "git_restore",
                "Restore working-tree or staged files, discarding changes. Requires user approval — this cannot be undone."),
            AIFunctionFactory.Create(StashSchema, "git_stash",
                "Manage the stash: list (read-only), push (save changes), or pop (restore them). push/pop require user approval."),
        ];
    }

    public async Task<(object? Result, GitToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GitToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        // Batch 06: an isolated run supplies its own workspace root, so git resolves the repository THERE —
        // or files and git disagree and the agent commits the interactive folder's stale tree. Same one-line
        // shape as FilesToolHandler's dispatch point (:170-171), canonicalization included.
        var ambientRoot = TaskAmbient.Current?.WorkspaceRoot;
        var baseRoot = ambientRoot is not null ? NormalizeWorkspaceRoot(ambientRoot) : _currentFolder;
        if (baseRoot is null || !Directory.Exists(baseRoot))
        {
            return (
                "Error: No assistant files folder is configured. Ask the user to set one under Settings → Assistant.",
                null);
        }

        // Narrow the sandbox to the active chat's working directory (if any). The deferred write closure
        // captures this resolved scope at prepare time, so it never reads the ambient after the approval await.
        var scope = new GitScope(
            ResolveEffectiveRoot(baseRoot, TaskAmbient.Current?.WorkingSubpath),
            ambientRoot is null ? null : baseRoot);

        return toolCall.Name switch
        {
            "git_status" => (await HandleStatusAsync(scope, cancellationToken), null),
            "git_log" => (await HandleLogAsync(scope, args, cancellationToken), null),
            "git_diff" => (await HandleDiffAsync(scope, args, cancellationToken), null),
            "git_branch" => (await HandleBranchAsync(scope, cancellationToken), null),
            "git_show" => (await HandleShowAsync(scope, args, cancellationToken), null),
            "git_init" => PrepareInit(scope),
            "git_add" => await PrepareAddAsync(scope, args, cancellationToken),
            "git_commit" => await PrepareCommitAsync(scope, args, cancellationToken),
            "git_switch" => await PrepareSwitchAsync(scope, args, cancellationToken),
            "git_restore" => await PrepareRestoreAsync(scope, args, cancellationToken),
            "git_stash" => await HandleStashAsync(scope, args, cancellationToken),
            _ => ((object?)$"Unknown tool: {toolCall.Name}", (GitToolCall?)null)
        };
    }

    /// <summary>
    /// The sandbox one dispatch is contained to, resolved ONCE in <see cref="HandleToolCallAsync"/> and
    /// captured into every deferred <c>GitToolCall.Execute</c> closure.
    /// <para>
    /// <paramref name="WorkspaceRoot"/> is the DISCRIMINATOR and it is non-null <b>only</b> when the ambient
    /// supplied an isolated run workspace. Non-null ⇒ containment is frozen for the turn: the run cannot
    /// escape its workspace even if the user re-points the assistant folder mid-run, and — the reason this
    /// exists at all — a mutating tool that passed prepare cannot be refused after the approval await, where
    /// ambient flow is not guaranteed (<c>FilesToolHandler:179-182</c> states that rule) and a null ambient
    /// would fall back to <c>_currentFolder</c> and refuse a toplevel inside the workspace. Null ⇒ the
    /// interactive case, where <c>_currentFolder</c> is RE-READ at execute time, which is the runtime-re-point
    /// TOCTOU re-guard this handler has always had. Never set this to <c>_currentFolder</c>: that would
    /// silently retire that guard with no failing test.
    /// </para>
    /// </summary>
    /// <param name="WorkingDir">The effective working directory git runs in: the sandbox root narrowed by the
    /// active chat's working subpath.</param>
    private readonly record struct GitScope(string WorkingDir, string? WorkspaceRoot);

    /// <summary>Canonicalizes an ambient-supplied workspace root the same way <c>UpdateFolder</c> canonicalizes
    /// the configured folder, so the containment comparisons cannot false-refuse on a link/8.3 asymmetry.</summary>
    private static string NormalizeWorkspaceRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : full;
    }

    public async Task<object?> ExecutePendingActionAsync(GitToolCall pendingAction)
    {
        _logger.LogDebug("Executing git action: {ToolName}", pendingAction.ToolName);
        try
        {
            var result = await pendingAction.Execute();
            _logger.LogInformation("Git action completed: {ToolName}", pendingAction.ToolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute git tool action: {ToolName}", pendingAction.ToolName);
            return $"Error executing {pendingAction.ToolName}: {ex.Message}";
        }
    }

    // ---- read-only tools (inline) ----

    private async Task<object> HandleStatusAsync(GitScope scope, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } shortCircuit) return shortCircuit;

        var result = await RunGitAsync(scope, ["status", "--short", "--branch"], GitCommandKind.ReadOnly, ct);
        LogExit("git_status", result);
        if (!result.Succeeded) return MapGitError(result, "status");

        var output = result.StandardOutput.TrimEnd();
        if (string.IsNullOrWhiteSpace(output))
            return "Working tree clean (no changes).";

        // --short --branch always prints the "## <branch>" header even on a clean tree; make the
        // no-changes case explicit for the model rather than leaving just the bare branch line.
        var hasChanges = output.Split('\n').Any(line => line.Length > 0 && !line.StartsWith("##", StringComparison.Ordinal));
        return hasChanges ? Cap(output) : Cap(output) + "\nWorking tree clean (no changes).";
    }

    private async Task<object> HandleLogAsync(GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } shortCircuit) return shortCircuit;

        var count = Math.Clamp(GetOptionalIntArg(args, "count", DefaultLogCount), 1, MaxLogCount);
        var result = await RunGitAsync(scope,
            ["log", $"--max-count={count}", "--date=short", "--pretty=format:%h %ad %an %s"], GitCommandKind.ReadOnly, ct);
        LogExit("git_log", result);
        if (!result.Succeeded)
        {
            if (result.StandardError.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase))
                return "No commits yet.";
            return MapGitError(result, "log");
        }

        var output = result.StandardOutput.TrimEnd();
        return string.IsNullOrWhiteSpace(output) ? "No commits yet." : Cap(output);
    }

    private async Task<object> HandleDiffAsync(GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } shortCircuit) return shortCircuit;

        var staged = GetOptionalBoolArg(args, "staged");
        var pathArg = NormalizePathArg(GetOptionalStringArg(args, "path"));

        // --no-ext-diff disables the diff.external driver (a code-exec vector git diff honors by default);
        // --no-textconv disables textconv filters. Both close repo-config exec surfaces on this read tool.
        var cmd = new List<string> { "diff", "--no-textconv", "--no-ext-diff" };
        if (staged) cmd.Add("--staged");
        if (!string.IsNullOrWhiteSpace(pathArg))
        {
            // Pathspec after `--` is cwd-relative (cwd == the working dir), so resolve against the working dir, not the toplevel.
            if (!TryResolveRepoRelative(scope.WorkingDir, scope.WorkingDir, pathArg, out var rel, out var error))
                return error;
            cmd.Add("--");
            cmd.Add(rel);
        }

        var result = await RunGitAsync(scope, cmd, GitCommandKind.ReadOnly, ct);
        LogExit("git_diff", result);
        if (!result.Succeeded) return MapGitError(result, "diff");

        var output = result.StandardOutput.TrimEnd();
        return string.IsNullOrWhiteSpace(output) ? "No differences." : Cap(output);
    }

    private async Task<object> HandleBranchAsync(GitScope scope, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } shortCircuit) return shortCircuit;

        var result = await RunGitAsync(scope, ["branch", "--list"], GitCommandKind.ReadOnly, ct);
        LogExit("git_branch", result);
        if (!result.Succeeded) return MapGitError(result, "branch");

        var output = result.StandardOutput.TrimEnd();
        return string.IsNullOrWhiteSpace(output) ? "No branches yet (the repository has no commits)." : Cap(output);
    }

    private async Task<object> HandleShowAsync(GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } shortCircuit) return shortCircuit;

        var rev = GetOptionalStringArg(args, "rev") ?? "HEAD";
        if (!IsValidRev(rev))
            return "Error: invalid revision. Use only letters, digits, '.', '_', '/', '-' (and no leading '-').";

        var pathArg = NormalizePathArg(GetOptionalStringArg(args, "path"));
        List<string> cmd;
        if (string.IsNullOrWhiteSpace(pathArg))
        {
            cmd = ["show", "--no-textconv", "--no-ext-diff", "--stat", rev];
        }
        else
        {
            // <rev>:<path> is an object spec, not a filesystem path: it must be a repo-toplevel-relative
            // forward-slash path (an absolute/backslash path yields 'fatal: invalid object name').
            if (!TryResolveRepoRelative(scope.WorkingDir, repo.Toplevel!, pathArg, out var rel, out var error))
                return error;
            cmd = ["show", "--no-textconv", "--no-ext-diff", $"{rev}:{rel}"];
        }

        var result = await RunGitAsync(scope, cmd, GitCommandKind.ReadOnly, ct);
        LogExit("git_show", result);
        if (!result.Succeeded) return MapGitError(result, "show");

        var output = result.StandardOutput.TrimEnd();
        return string.IsNullOrWhiteSpace(output) ? "(empty)" : Cap(output);
    }

    // ---- mutating tools (confirmation card, then execute) ----

    private (object? Result, GitToolCall? Pending) PrepareInit(GitScope scope)
    {
        // git_init has no is-repo requirement (it CREATES the repo). It only needs the working dir to be
        // inside the sandbox — true by construction here (it derives from the sandbox), re-checked defensively.
        if (!IsInsideSandbox(scope.WorkingDir, scope))
            return ("Error: the working directory is outside the assistant files folder.", null);

        var rel = SafeRelative(SandboxRootFor(scope) ?? string.Empty, scope.WorkingDir);
        var label = string.IsNullOrEmpty(rel) ? "the assistant files folder" : $"'{rel}'";

        return (null, new GitToolCall(
            ToolName: "git_init",
            Description: $"Initialize a git repository in {label}",
            Details: "Creates a new .git repository in the working directory.",
            Execute: () => ExecuteInitAsync(scope),
            TargetPath: rel));
    }

    private async Task<object?> ExecuteInitAsync(GitScope scope)
    {
        // TOCTOU: the sandbox root can be re-pointed between prepare and confirmation, so re-validate the
        // captured working dir against the CURRENT sandbox immediately before spawning git. In an isolated
        // run the captured workspace root IS the sandbox and cannot be re-pointed (see GitScope) — the run is
        // confined to it either way, and a settings change must not refuse an already-approved action.
        if (!IsInsideSandbox(scope.WorkingDir, scope))
            return "Error: the working directory is outside the assistant files folder.";

        var result = await RunGitAsync(scope, ["init"], GitCommandKind.Mutating, CancellationToken.None);
        LogExit("git_init", result);
        if (!result.Succeeded) return MapGitError(result, "init");

        var output = result.StandardOutput.Trim();
        return string.IsNullOrEmpty(output) ? "Initialized a new git repository." : Cap(output);
    }

    private async Task<(object? Result, GitToolCall? Pending)> PrepareAddAsync(
        GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } sc) return (sc, null);

        var rawPaths = GetStringArrayArg(args, "paths");
        List<string> gitArgs;
        string desc;
        if (rawPaths.Count == 0 || rawPaths.Any(p => p == "."))
        {
            // `add -- .` stages everything under the working directory (cwd-bounded, symmetric with
            // git_restore) rather than `--all` which would reach across the whole repo.
            gitArgs = ["add", "--", "."];
            desc = "Stage all changes in the working directory";
        }
        else
        {
            var rels = new List<string>();
            foreach (var p in rawPaths)
            {
                // Pathspec after `--` is cwd-relative (cwd == the working dir).
                if (!TryResolveRepoRelative(scope.WorkingDir, scope.WorkingDir, p, out var rel, out var error)) return (error, null);
                rels.Add(rel);
            }
            gitArgs = ["add", "--", .. rels];
            desc = $"Stage {rels.Count} path(s): {string.Join(", ", rels)}";
        }

        return (null, new GitToolCall("git_add", desc, DetailsFor(gitArgs),
            () => ExecuteMutatingAsync(scope, gitArgs, "git_add", "Staged changes."), TargetPath: null));
    }

    private async Task<(object? Result, GitToolCall? Pending)> PrepareCommitAsync(
        GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } sc) return (sc, null);

        var message = GetOptionalStringArg(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return ("Error: a commit message is required (pass 'message').", null);

        // --no-verify: repo commit hooks are out-of-band code execution (locked decision). The message
        // is passed as a single -m argument value, never concatenated into a command string.
        var gitArgs = new List<string> { "commit", "--no-verify", "-m", message };
        return (null, new GitToolCall("git_commit", $"Commit staged changes: \"{message}\"", DetailsFor(gitArgs),
            () => ExecuteCommitAsync(scope, gitArgs), TargetPath: null));
    }

    private async Task<(object? Result, GitToolCall? Pending)> PrepareSwitchAsync(
        GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } sc) return (sc, null);

        var branch = GetOptionalStringArg(args, "branch");
        if (string.IsNullOrWhiteSpace(branch) || !IsValidRev(branch))
            return ("Error: a valid branch name is required (letters, digits, '.', '_', '/', '-' and no leading '-').", null);
        var create = GetOptionalBoolArg(args, "create");

        // Use `switch`, never the legacy `checkout <branch>` (an overloaded pathspec could silently
        // overwrite a file from HEAD). `switch` never interprets a pathspec.
        var gitArgs = create ? new List<string> { "switch", "-c", branch } : ["switch", branch];
        var desc = create ? $"Create and switch to branch '{branch}'" : $"Switch to branch '{branch}'";
        return (null, new GitToolCall("git_switch", desc, DetailsFor(gitArgs),
            () => ExecuteMutatingAsync(scope, gitArgs, "git_switch", "Switched branch."), TargetPath: null));
    }

    private async Task<(object? Result, GitToolCall? Pending)> PrepareRestoreAsync(
        GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var repo = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repo) is { } sc) return (sc, null);

        var rawPaths = GetStringArrayArg(args, "paths");
        if (rawPaths.Count == 0)
            return ("Error: git_restore requires one or more paths (pass 'paths'; use \".\" to restore everything in the working directory).", null);
        var staged = GetOptionalBoolArg(args, "staged");

        var gitArgs = new List<string> { "restore" };
        if (staged) gitArgs.Add("--staged");
        gitArgs.Add("--");

        string desc;
        if (rawPaths.Any(p => p == "."))
        {
            gitArgs.Add(".");
            desc = staged ? "Unstage all changes in the working directory" : "Discard all working-tree changes in the working directory";
        }
        else
        {
            var rels = new List<string>();
            foreach (var p in rawPaths)
            {
                // Pathspec after `--` is cwd-relative (cwd == the working dir).
                if (!TryResolveRepoRelative(scope.WorkingDir, scope.WorkingDir, p, out var rel, out var error)) return (error, null);
                rels.Add(rel);
            }
            gitArgs.AddRange(rels);
            desc = staged
                ? $"Unstage {rels.Count} path(s): {string.Join(", ", rels)}"
                : $"Discard working-tree changes in {rels.Count} path(s): {string.Join(", ", rels)}";
        }

        return (null, new GitToolCall("git_restore", desc, DetailsFor(gitArgs),
            () => ExecuteMutatingAsync(scope, gitArgs, "git_restore", "Restored."), TargetPath: null));
    }

    private async Task<(object? Result, GitToolCall? Pending)> HandleStashAsync(
        GitScope scope, IDictionary<string, object?> args, CancellationToken ct)
    {
        var op = (GetOptionalStringArg(args, "operation") ?? "list").Trim().ToLowerInvariant();

        // `list` is read-only: run inline (no confirmation card), short-circuiting before the pending path.
        if (op == "list")
        {
            var repo = await ResolveContainedRepoAsync(scope, ct);
            if (RepoShortCircuit(repo) is { } sc) return (sc, null);
            var result = await RunGitAsync(scope, ["stash", "list"], GitCommandKind.ReadOnly, ct);
            LogExit("git_stash", result);
            if (!result.Succeeded) return (MapGitError(result, "stash"), null);
            var output = result.StandardOutput.TrimEnd();
            return (string.IsNullOrWhiteSpace(output) ? "No stashes." : Cap(output), null);
        }

        if (op is not ("push" or "pop"))
            return ($"Error: unknown stash operation '{op}'. Use 'list', 'push', or 'pop'.", null);

        var repoCheck = await ResolveContainedRepoAsync(scope, ct);
        if (RepoShortCircuit(repoCheck) is { } sc2) return (sc2, null);

        var gitArgs = new List<string> { "stash", op };
        var desc = op == "push" ? "Stash (save) uncommitted changes" : "Pop the most recent stash onto the working tree";
        var success = op == "push" ? "Changes stashed." : "Stash popped.";
        return (null, new GitToolCall("git_stash", desc, DetailsFor(gitArgs),
            () => ExecuteMutatingAsync(scope, gitArgs, "git_stash", success), TargetPath: null));
    }

    /// <summary>
    /// Shared execute path for mutating tools: re-runs the containment guard (TOCTOU) against the CURRENT
    /// sandbox before spawning git, then maps the result. On success returns the git output (or a fallback).
    /// </summary>
    private async Task<object?> ExecuteMutatingAsync(GitScope scope, IReadOnlyList<string> gitArgs, string tool, string emptySuccessMessage)
    {
        var repo = await ResolveContainedRepoAsync(scope, CancellationToken.None);
        if (RepoShortCircuit(repo) is { } sc) return sc;

        var result = await RunGitAsync(scope, gitArgs, GitCommandKind.Mutating, CancellationToken.None);
        LogExit(tool, result);
        if (!result.Succeeded) return MapGitError(result, ToolShort(tool));

        var output = result.StandardOutput.Trim();
        return string.IsNullOrEmpty(output) ? emptySuccessMessage : Cap(output);
    }

    private async Task<object?> ExecuteCommitAsync(GitScope scope, IReadOnlyList<string> gitArgs)
    {
        var repo = await ResolveContainedRepoAsync(scope, CancellationToken.None);
        if (RepoShortCircuit(repo) is { } sc) return sc;

        var result = await RunGitAsync(scope, gitArgs, GitCommandKind.Mutating, CancellationToken.None);
        LogExit("git_commit", result);
        if (!result.Succeeded)
        {
            // `git commit` with nothing staged writes "nothing to commit" to stdout and exits non-zero,
            // so the stderr-only mapper would miss it — check both streams for a friendly message.
            if ((result.StandardOutput + "\n" + result.StandardError).Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return "Nothing to commit — stage changes with git_add first.";
            return MapGitError(result, "commit");
        }

        var output = result.StandardOutput.Trim();
        return string.IsNullOrEmpty(output) ? "Commit created." : Cap(output);
    }

    // ---- repo resolution + containment ----

    private enum RepoStatus { Ok, NotARepo, OutsideSandbox, Failed }

    private sealed record RepoResolution(RepoStatus Status, string? Toplevel, string? Error)
    {
        public static RepoResolution Ok(string toplevel) => new(RepoStatus.Ok, toplevel, null);
        public static RepoResolution NotRepo() => new(RepoStatus.NotARepo, null, null);
        public static RepoResolution Outside() => new(RepoStatus.OutsideSandbox, null, null);
        public static RepoResolution Failed(string error) => new(RepoStatus.Failed, null, error);
    }

    /// <summary>
    /// Resolves the repository toplevel for the dispatch working directory via a single
    /// <c>git rev-parse --show-toplevel</c> spawn (this doubles as the is-repo check — gate on exit code,
    /// never on the literal <c>false</c> that <c>--is-inside-work-tree</c> only prints inside a bare repo),
    /// then enforces the absolute containment invariant against the scope sandbox (the run workspace when
    /// there is one, else the CURRENT configured folder).
    /// </summary>
    private async Task<RepoResolution> ResolveContainedRepoAsync(GitScope scope, CancellationToken ct)
    {
        GitProcessResult result;
        try
        {
            result = await RunGitAsync(scope, ["rev-parse", "--show-toplevel"], GitCommandKind.ReadOnly, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("git rev-parse failed to run");
            _logger.SensitiveDebug("git rev-parse exception: {Message}", ex.Message);
            return RepoResolution.Failed("Error: could not run git.");
        }

        if (result.TimedOut)
            return RepoResolution.Failed("Error: git timed out resolving the repository.");
        // The runner returns -1 only for a start failure (git vanished / could not launch) — that's not
        // "not a repo", so surface an honest error instead of misrouting the model to git_init.
        if (result.ExitCode == -1)
        {
            _logger.SensitiveDebug("rev-parse start failure: {Err}", result.StandardError);
            return RepoResolution.Failed("Error: could not run git.");
        }
        if (result.ExitCode != 0)
        {
            // Non-zero exit ⇒ the working dir is not (yet) in a repo (or a git error). Route to git_init.
            _logger.SensitiveDebug("rev-parse exit {Exit}: {Err}", result.ExitCode, result.StandardError);
            return RepoResolution.NotRepo();
        }

        var toplevelRaw = result.StandardOutput.Trim();
        if (string.IsNullOrEmpty(toplevelRaw))
            return RepoResolution.NotRepo();

        string canonical;
        try
        {
            // git prints a forward-slash absolute path; normalize to an OS path then canonicalize
            // (resolve reparse points). A canonicalize failure ⇒ we cannot verify containment ⇒ refuse.
            canonical = SafeFolderPath.Canonicalize(Path.GetFullPath(toplevelRaw));
        }
        catch
        {
            _logger.SensitiveDebug("could not canonicalize repo toplevel: {Top}", toplevelRaw);
            return RepoResolution.Outside();
        }

        if (!IsInsideSandbox(canonical, scope))
        {
            _logger.SensitiveDebug("repo toplevel outside sandbox: {Top}", canonical);
            return RepoResolution.Outside();
        }

        return RepoResolution.Ok(canonical);
    }

    private static object? RepoShortCircuit(RepoResolution repo) => repo.Status switch
    {
        RepoStatus.NotARepo => FreshFolderHint,
        RepoStatus.OutsideSandbox => OutsideSandboxRefusal,
        RepoStatus.Failed => repo.Error ?? "Error: git failed.",
        _ => null
    };

    /// <summary>
    /// Inclusive containment: true when <paramref name="path"/> equals the (canonicalized) sandbox root of
    /// <paramref name="scope"/> or is nested under it. Inclusive of the root so a repository whose toplevel
    /// IS the sandbox is allowed — which is also what makes worktree mode work, where
    /// <c>rev-parse --show-toplevel</c> returns the workspace root itself.
    /// <para>
    /// The sandbox is <see cref="GitScope.WorkspaceRoot"/> when an isolated run supplied one and the CURRENT
    /// <see cref="_currentFolder"/> otherwise — so the interactive TOCTOU re-guard still re-refuses after a
    /// runtime re-point, while an isolated run is judged against the workspace it was dispatched with. See
    /// <see cref="GitScope"/> for why reading the ambient here instead would refuse an already-approved
    /// mutating tool.
    /// </para>
    /// </summary>
    private bool IsInsideSandbox(string path, GitScope scope)
    {
        var sandbox = SandboxRootFor(scope);
        if (string.IsNullOrEmpty(sandbox)) return false;

        // Canonicalize BOTH sides so the comparison can't false-refuse on an 8.3/junction spelling
        // asymmetry (e.g. a working dir captured before the folder existed vs. a now-canonical sandbox).
        var candidate = TryCanonicalize(path);
        string canonSandbox;
        try { canonSandbox = Directory.Exists(sandbox) ? SafeFolderPath.Canonicalize(sandbox) : Path.GetFullPath(sandbox); }
        catch { return false; }

        if (string.Equals(candidate, canonSandbox, StringComparison.OrdinalIgnoreCase)) return true;
        var withSep = SafeFolderPath.WithTrailingSeparator(canonSandbox);
        return candidate.StartsWith(withSep, StringComparison.OrdinalIgnoreCase);
    }

    private static string TryCanonicalize(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path)) return SafeFolderPath.Canonicalize(path);
            return Path.GetFullPath(path);
        }
        catch { return path; }
    }

    /// <summary>Treats an empty or "." path argument as "no path filter" (mirrors the files search tool).</summary>
    private static string? NormalizePathArg(string? path)
        => string.IsNullOrWhiteSpace(path) || path == "." ? null : path;

    /// <summary>
    /// Resolves a model-supplied path inside the sandbox (against <paramref name="root"/>, the effective
    /// working dir), then converts it to a forward-slash path relative to <paramref name="baseDir"/>.
    /// <para>
    /// The base differs by usage: command-line pathspecs (after <c>--</c> in add/restore/diff) are
    /// interpreted relative to git's <b>cwd</b>, which is <paramref name="root"/> — so pass
    /// <paramref name="root"/> there. <c>git show</c>'s <c>&lt;rev&gt;:&lt;path&gt;</c> object spec is
    /// instead <b>repo-toplevel-relative</b> — so pass the toplevel there. Getting this wrong points git
    /// at the wrong file (a silent "no match" for diff, a wrong-file discard for restore).
    /// </para>
    /// </summary>
    private bool TryResolveRepoRelative(string root, string baseDir, string userPath, out string rel, out string error)
    {
        rel = string.Empty;
        error = string.Empty;

        if (!SafeFolderPath.TryResolveInsideAllowingAbsolute(root, userPath, out var resolved))
        {
            _logger.SensitiveDebug("git path rejected (outside sandbox): {Path}", userPath);
            return Fail(out error, "Error: path is outside the assistant files folder.");
        }

        rel = Path.GetRelativePath(baseDir, resolved).Replace('\\', '/');
        if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            _logger.SensitiveDebug("git path outside the base directory: {Path}", userPath);
            return Fail(out error, "Error: path is outside the git repository.");
        }
        return true;

        static bool Fail(out string e, string message) { e = message; return false; }
    }

    /// <summary>
    /// The EFFECTIVE sandbox root of a dispatch: the isolated run's workspace when it supplied one, else the
    /// current configured folder. The single place the two cases meet, so containment and the discovery
    /// ceiling can never disagree about which root a call is confined to.
    /// </summary>
    private string? SandboxRootFor(GitScope scope) => scope.WorkspaceRoot ?? _currentFolder;

    /// <summary>
    /// <c>GIT_CEILING_DIRECTORIES</c>: the parent of the EFFECTIVE sandbox root, so upward <c>.git</c>
    /// discovery can reach that root (a repo whose toplevel IS the sandbox is legal) but never cross above
    /// it. It must follow the scope, not <see cref="_currentFolder"/>: with a cwd under
    /// <c>%LOCALAPPDATA%\Pia\runs\&lt;id&gt;</c> a ceiling of the assistant folder's parent does not apply at
    /// all, and discovery would walk runs → Pia → Local → AppData → %USERPROFILE% and could bind a
    /// repository the user keeps in their profile.
    /// </summary>
    private string? GetCeilingDirectory(GitScope scope)
    {
        var sandbox = SandboxRootFor(scope);
        if (string.IsNullOrEmpty(sandbox)) return null;
        try { return Directory.GetParent(sandbox)?.FullName; }
        catch { return null; }
    }

    private Task<GitProcessResult> RunGitAsync(GitScope scope, IReadOnlyList<string> args, GitCommandKind kind, CancellationToken ct)
        => _runner.RunAsync(new GitProcessRequest(scope.WorkingDir, args, kind, GetCeilingDirectory(scope)), ct);

    private string ResolveEffectiveRoot(string baseRoot, string? workingSubpath)
    {
        if (string.IsNullOrWhiteSpace(workingSubpath))
            return baseRoot;

        if (SafeFolderPath.TryResolveInsideAllowingAbsolute(baseRoot, workingSubpath, out var eff)
            && Directory.Exists(eff))
        {
            return eff;
        }

        _logger.SensitiveDebug("Working subpath did not resolve to an existing folder under the sandbox: {Subpath}", workingSubpath);
        return baseRoot;
    }

    // ---- helpers ----

    private static bool IsValidRev(string rev) =>
        !string.IsNullOrWhiteSpace(rev) && !rev.StartsWith('-') && RevPattern.IsMatch(rev);

    private void LogExit(string tool, GitProcessResult result)
    {
        // LogInformation may carry only the tool name + exit code (CLAUDE.md privacy gate).
        _logger.LogInformation("GitToolHandler {Tool} exit {Exit}", tool, result.ExitCode);
        // Output/stderr = file contents, commit messages, paths → SensitiveDebug (erased in release IL).
        _logger.SensitiveDebug("{Tool} stdout: {Out} | stderr: {Err}", tool, result.StandardOutput, result.StandardError);
    }

    private static object MapGitError(GitProcessResult result, string tool)
    {
        if (result.TimedOut)
            return $"Error: git {tool} timed out.";

        var stderr = result.StandardError.Trim();
        if (stderr.Contains("index.lock", StringComparison.OrdinalIgnoreCase))
            return "Error: the repository index is locked by another git process; try again in a moment.";

        return string.IsNullOrEmpty(stderr)
            ? $"Error: git {tool} failed (exit {result.ExitCode})."
            : $"Error: git {tool} failed: {Cap(stderr)}";
    }

    private static string Cap(string s)
        => s.Length <= MaxFormattedChars ? s : s[..MaxFormattedChars] + "\n…output truncated";

    private static string ToolShort(string tool) => tool.StartsWith("git_", StringComparison.Ordinal) ? tool[4..] : tool;

    /// <summary>The exact git command about to run, for the confirmation card (the global hardening
    /// flags are prepended by the runner and intentionally not shown).</summary>
    private static string DetailsFor(IReadOnlyList<string> gitArgs) => $"Command: git {string.Join(' ', gitArgs)}";

    private static string SafeRelative(string root, string fullPath)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var rootWithSep = SafeFolderPath.WithTrailingSeparator(root);
        return fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            ? fullPath[rootWithSep.Length..]
            : fullPath;
    }

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var value) && value is not null)
        {
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Null) return null;
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }
            var str = value.ToString();
            return string.IsNullOrEmpty(str) ? null : str;
        }
        return null;
    }

    private static int GetOptionalIntArg(IDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n)) return n;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed)) return parsed;
            return defaultValue;
        }

        if (value is int i) return i;
        if (value is long l) return (int)l;
        return int.TryParse(value.ToString(), out var fallback) ? fallback : defaultValue;
    }

    private static bool GetOptionalBoolArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return false;
        if (value is bool b) return b;
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(element.GetString(), out var pb) && pb,
                _ => false
            };
        }
        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }

    private static IReadOnlyList<string> GetStringArrayArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return [];

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .ToList();
            if (element.ValueKind == JsonValueKind.String)
            {
                var s = element.GetString();
                return string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()];
            }
            return [];
        }

        if (value is string str)
            return string.IsNullOrWhiteSpace(str) ? [] : [str.Trim()];
        if (value is IEnumerable<object?> enumerable)
            return enumerable.Select(o => o?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList();
        return [];
    }

    // Schema methods — parameter signatures + [Description] define the tool metadata for AIFunctionFactory.
    // The body is never invoked (dispatch is by tool name in HandleToolCallAsync).
    [Description("Show the working-tree status of the git repository in the working directory.")]
    private static string StatusSchema() => "";

    [Description("Show recent commit history.")]
    private static string LogSchema(
        [Description("Maximum number of commits to show (default 20, maximum 100).")] int count = 20) => "";

    [Description("Show changes as a unified diff.")]
    private static string DiffSchema(
        [Description("Show staged (index) changes instead of working-tree changes.")] bool staged = false,
        [Description("Optional path (relative to the assistant files folder) to limit the diff to.")] string? path = null) => "";

    [Description("List local branches and mark the current one.")]
    private static string BranchSchema() => "";

    [Description("Show a revision's summary/diffstat, or a file's contents at that revision.")]
    private static string ShowSchema(
        [Description("Revision to show (commit hash, branch, or tag). Defaults to HEAD.")] string rev = "HEAD",
        [Description("Optional file path (relative to the folder) to show that revision's contents instead of the diffstat.")] string? path = null) => "";

    [Description("Initialize a new git repository in the working directory.")]
    private static string InitSchema() => "";

    [Description("Stage file changes for the next commit.")]
    private static string AddSchema(
        [Description("Paths (relative to the assistant files folder) to stage. Omit, or pass \".\", to stage all changes.")] string[]? paths = null) => "";

    [Description("Record the staged changes as a new commit.")]
    private static string CommitSchema(
        [Description("The commit message.")] string message = "") => "";

    [Description("Switch to another branch, optionally creating it.")]
    private static string SwitchSchema(
        [Description("The branch name to switch to.")] string branch = "",
        [Description("Create the branch first if it does not exist.")] bool create = false) => "";

    [Description("Restore working-tree or staged files, discarding changes.")]
    private static string RestoreSchema(
        [Description("Paths (relative to the assistant files folder) to restore. Pass \".\" to restore everything in the working directory.")] string[]? paths = null,
        [Description("Restore the staged (index) copy instead of the working tree.")] bool staged = false) => "";

    [Description("Manage the stash.")]
    private static string StashSchema(
        [Description("Operation: 'list' (default, read-only), 'push' (save changes), or 'pop' (restore them).")] string operation = "list") => "";
}
