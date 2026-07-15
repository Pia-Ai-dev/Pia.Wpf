using System.Diagnostics;
using System.Text;

namespace Pia.Helpers;

/// <summary>Whether a git invocation only reads (so index locks can be skipped) or mutates.</summary>
public enum GitCommandKind
{
    ReadOnly,
    Mutating
}

/// <summary>
/// A single git invocation. <see cref="Arguments"/> is the subcommand plus its own options
/// (e.g. <c>["status", "--porcelain=v2", "--branch"]</c>) — the runner prepends the global
/// hardening options (<c>--no-pager</c>, <c>-c protocol.*</c>, …) itself.
/// </summary>
public sealed record GitProcessRequest(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    GitCommandKind Kind,
    string? CeilingDirectory);

/// <summary>The outcome of a git invocation. <see cref="TimedOut"/> is set when the process was killed on timeout.</summary>
public sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}

/// <summary>
/// Runs the pinned <see cref="GitLocator.Executable"/> under a hardened, non-interactive environment.
/// Injected into the git tool handler so tests can substitute canned results without a real git
/// (and without touching process-global state).
/// </summary>
public interface IGitProcessRunner
{
    /// <summary>Mirror of <see cref="GitLocator.IsAvailable"/> — the git-installed gate the handler reads.</summary>
    bool IsGitInstalled { get; }

    Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IGitProcessRunner"/>. Two layers, kept separate so each is testable in isolation:
/// <list type="bullet">
/// <item><see cref="BuildArguments"/> / <see cref="BuildEnvironment"/> compose the git-specific hardening
///   (asserted directly by unit tests, because a substituted runner would otherwise certify nothing about it).</item>
/// <item><see cref="RunProcessAsync"/> is exe-agnostic process mechanics (async concurrent reads to avoid the
///   pipe-buffer deadlock, timeout + <c>Kill(entireProcessTree)</c> + drain) — testable with <c>cmd.exe</c>.</item>
/// </list>
/// </summary>
public sealed class GitProcessRunner : IGitProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Upper bound on draining the (already-buffered) stdout/stderr reads. After a normal exit these
    // complete near-instantly; the bound only matters when Kill misses an orphaned descendant that
    // inherited the pipe write handle, so the drain can never hang the turn indefinitely.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    public bool IsGitInstalled => GitLocator.IsAvailable;

    public async Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken = default)
    {
        var exe = GitLocator.Executable;
        if (exe is null)
            return new GitProcessResult(-1, string.Empty, "Git is not installed.", TimedOut: false);

        var args = BuildArguments(request.Arguments);
        var env = BuildEnvironment(request.Kind, request.CeilingDirectory);
        return await RunProcessAsync(exe, args, request.WorkingDirectory, env, Timeout, cancellationToken);
    }

    /// <summary>
    /// Prepends the git-global hardening options that must appear BEFORE the subcommand. Per-subcommand
    /// options (<c>--no-textconv</c> on diff/show, <c>--no-verify</c> on commit) are added by the handler.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(IReadOnlyList<string> subcommandArgs)
    {
        var args = new List<string>
        {
            "--no-pager",
            "-c", "core.fsmonitor=false",
            "-c", "protocol.ext.allow=never",
            "-c", "protocol.file.allow=user",
        };
        args.AddRange(subcommandArgs);
        return args;
    }

    /// <summary>
    /// The hardened environment overlaid on the inherited env. A null value means "remove the inherited
    /// variable" (the GIT_DIR/GIT_WORK_TREE/GIT_COMMON_DIR redirection scrub). Non-interactive by design:
    /// git can neither prompt for input nor be steered by ambient/repo config.
    /// </summary>
    internal static Dictionary<string, string?> BuildEnvironment(GitCommandKind kind, string? ceilingDirectory)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Never block on / reach out for credentials (also a belt-and-braces network guard).
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ASKPASS"] = string.Empty,
            ["SSH_ASKPASS"] = string.Empty,
            // Never wait on a pager or open an editor.
            ["GIT_PAGER"] = "cat",
            ["GIT_EDITOR"] = "true",
            // Scrub inherited redirection vars so --show-toplevel can't report an in-sandbox path while
            // git actually reads/writes objects/refs/index elsewhere (env-redirect sandbox escape), plus
            // the config-file overrides in the same class (config steering).
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
            ["GIT_COMMON_DIR"] = null,
            ["GIT_INDEX_FILE"] = null,
            ["GIT_OBJECT_DIRECTORY"] = null,
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null,
            ["GIT_CONFIG_GLOBAL"] = null,
            ["GIT_CONFIG_SYSTEM"] = null,
            // External-diff driver is a code-exec vector (git diff honors it by default). --no-ext-diff on
            // the subcommand disables the config key; scrubbing the env var closes the inherited-env path.
            ["GIT_EXTERNAL_DIFF"] = null,
        };

        // Stop upward .git discovery from crossing out of the sandbox. A single directory needs no
        // list separator, sidestepping the Unix ':' vs Git-for-Windows ';' ambiguity.
        if (!string.IsNullOrEmpty(ceilingDirectory))
            env["GIT_CEILING_DIRECTORIES"] = ceilingDirectory;

        // Avoid touching the index lock on pure reads; writers legitimately need it.
        if (kind == GitCommandKind.ReadOnly)
            env["GIT_OPTIONAL_LOCKS"] = "0";

        return env;
    }

    /// <summary>
    /// Exe-agnostic process mechanics. Reads stdout and stderr concurrently with the wait (so a git diff
    /// larger than the OS pipe buffer cannot deadlock), bounds the run with a timeout, and on expiry
    /// kills the whole tree then drains the pending reads before returning.
    /// </summary>
    internal static async Task<GitProcessResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new GitProcessResult(-1, string.Empty, "Failed to start git process.", TimedOut: false);
        }
        catch (Exception ex)
        {
            return new GitProcessResult(-1, string.Empty, $"Failed to start git process: {ex.Message}", TimedOut: false);
        }

        // Pass the EXTERNAL token (not the timeout token) to the reads: on timeout we kill the process,
        // which closes the pipes and lets these complete naturally with whatever was buffered.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            catch { /* best effort */ }

            // Bounded secondary wait so a pending read can never hang the turn or leak on dispose.
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }

            if (cancellationToken.IsCancellationRequested)
            {
                // External cancellation (turn cancelled): drain then propagate with the CALLER's token
                // so a caller matching oce.CancellationToken recognizes its own cancellation.
                await SafeAwait(stdoutTask);
                await SafeAwait(stderrTask);
                throw new OperationCanceledException(cancellationToken);
            }

            timedOut = true;
        }

        var stdout = await SafeAwait(stdoutTask);
        var stderr = await SafeAwait(stderrTask);

        int exitCode;
        try { exitCode = process.HasExited ? process.ExitCode : -1; }
        catch { exitCode = -1; }

        return new GitProcessResult(exitCode, stdout, stderr, timedOut);
    }

    private static async Task<string> SafeAwait(Task<string> readTask)
    {
        // Bounded: a Kill that misses an orphaned pipe-holder would otherwise leave ReadToEndAsync
        // awaiting EOF forever. On timeout/fault, fall back to whatever we have (empty).
        try { return await readTask.WaitAsync(DrainTimeout); }
        catch { return string.Empty; }
    }
}
