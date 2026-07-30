using System.IO;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Certifies that <see cref="GitProcessRunner.RunAsync"/> actually threads the pinned executable +
/// composed args/env through to <see cref="GitProcessRunner.RunProcessAsync"/> (a wiring regression
/// would otherwise pass the per-part unit tests). Reads/mutates <see cref="GitLocator"/>'s static, so
/// it lives in the non-parallel "GitLocatorStatic" collection and resets in <see cref="Dispose"/>.
/// </summary>
[Collection("GitLocatorStatic")]
public sealed class GitProcessRunnerWiringTests : IDisposable
{
    public void Dispose() => GitLocator.ResetForTests();

    [Fact]
    public async Task RunAsync_ReturnsNotInstalled_WhenNoExecutablePinned()
    {
        GitLocator.SetExecutableForTests(null);

        var result = await new GitProcessRunner().RunAsync(
            new GitProcessRequest(Path.GetTempPath(), ["status"], GitCommandKind.ReadOnly, CeilingDirectory: null), TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("not installed", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ThreadsThroughToRealGit()
    {
        // Force a real probe (clears any override a sibling test left), then require git on the box.
        GitLocator.ResetForTests();
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");

        var result = await new GitProcessRunner().RunAsync(
            new GitProcessRequest(Path.GetTempPath(), ["--version"], GitCommandKind.ReadOnly, CeilingDirectory: null), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, $"exit={result.ExitCode} stderr={result.StandardError}");
        Assert.Contains("git version", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }
}
