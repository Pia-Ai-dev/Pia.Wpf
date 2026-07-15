using Pia.Helpers;

namespace Pia.Tests.Services;

/// <summary>
/// Substitutable <see cref="IGitProcessRunner"/> for git-free handler tests: it returns canned results
/// keyed on the git subcommand, so containment/availability logic can be exercised without a real git
/// (and without touching the process-global <see cref="GitLocator"/>). Records every request for assertions.
/// </summary>
internal sealed class FakeGitProcessRunner : IGitProcessRunner
{
    public bool IsGitInstalled { get; set; } = true;

    /// <summary>Per-request responder. Defaults to a clean exit-0 with empty output.</summary>
    public Func<GitProcessRequest, GitProcessResult> Responder { get; set; } =
        _ => new GitProcessResult(0, string.Empty, string.Empty, TimedOut: false);

    public List<GitProcessRequest> Calls { get; } = [];

    public Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request);
        return Task.FromResult(Responder(request));
    }

    public bool WasCalledWith(string subcommand) =>
        Calls.Any(c => c.Arguments.Count > 0 && c.Arguments[0] == subcommand);

    /// <summary>Convenience: exit-0 rev-parse returning <paramref name="toplevel"/>, everything else exit-0 empty.</summary>
    public void RepoAt(string toplevel) => Responder = req =>
        req.Arguments.Count > 0 && req.Arguments[0] == "rev-parse"
            ? new GitProcessResult(0, toplevel + "\n", string.Empty, false)
            : new GitProcessResult(0, string.Empty, string.Empty, false);

    /// <summary>Convenience: rev-parse fails (not a repo); everything else exit-0 empty.</summary>
    public void NotARepo() => Responder = req =>
        req.Arguments.Count > 0 && req.Arguments[0] == "rev-parse"
            ? new GitProcessResult(128, string.Empty, "fatal: not a git repository", false)
            : new GitProcessResult(0, string.Empty, string.Empty, false);
}
