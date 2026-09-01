using System.Diagnostics;
using System.IO;
using System.Text;
using Pia.Helpers;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Two concerns, kept separate on purpose (a substituted runner would certify neither):
/// the git-specific hardening composition (<see cref="GitProcessRunner.BuildArguments"/> /
/// <see cref="GitProcessRunner.BuildEnvironment"/>), asserted directly; and the exe-agnostic process
/// mechanics (<see cref="GitProcessRunner.RunProcessAsync"/>) — no-deadlock on large output and
/// timeout/kill — exercised with <c>cmd.exe</c> so they don't depend on git being on the box.
/// </summary>
public sealed class GitProcessRunnerTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    // ---- hardening composition ----

    [Fact]
    public void BuildArguments_PrependsGlobalHardeningBeforeSubcommand()
    {
        var args = GitProcessRunner.BuildArguments(["status", "--porcelain=v2", "--branch"]);

        Assert.Equal(
            new[]
            {
                "--no-pager",
                "-c", "core.fsmonitor=false",
                "-c", "protocol.ext.allow=never",
                "-c", "protocol.file.allow=user",
                "status", "--porcelain=v2", "--branch",
            },
            args);
    }

    [Fact]
    public void BuildEnvironment_SetsNonInteractiveAndScrubsRedirectionVars()
    {
        var env = GitProcessRunner.BuildEnvironment(GitCommandKind.ReadOnly, ceilingDirectory: @"C:\sandbox-parent");

        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(string.Empty, env["GIT_ASKPASS"]);
        Assert.Equal(string.Empty, env["SSH_ASKPASS"]);
        Assert.Equal("cat", env["GIT_PAGER"]);
        Assert.Equal("true", env["GIT_EDITOR"]);

        // Redirection scrub: present as keys mapping to null (so the runner removes the inherited var).
        foreach (var scrubbed in new[]
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_EXTERNAL_DIFF",
        })
        {
            Assert.True(env.ContainsKey(scrubbed) && env[scrubbed] is null, $"{scrubbed} must be scrubbed (null)");
        }

        Assert.Equal(@"C:\sandbox-parent", env["GIT_CEILING_DIRECTORIES"]);
    }

    [Fact]
    public void BuildEnvironment_OptionalLocksOnlyOnReadPaths()
    {
        Assert.Equal("0", GitProcessRunner.BuildEnvironment(GitCommandKind.ReadOnly, null)["GIT_OPTIONAL_LOCKS"]);
        Assert.DoesNotContain("GIT_OPTIONAL_LOCKS", GitProcessRunner.BuildEnvironment(GitCommandKind.Mutating, null).Keys);
    }

    [Fact]
    public void BuildEnvironment_OmitsCeilingWhenNotProvided()
        => Assert.DoesNotContain("GIT_CEILING_DIRECTORIES", GitProcessRunner.BuildEnvironment(GitCommandKind.ReadOnly, null).Keys);

    // ---- exe-agnostic mechanics ----

    [Fact]
    public async Task RunProcessAsync_ReadsLargeOutputWithoutDeadlock()
    {
        // A payload well past the OS pipe buffer (~64 KB): a sequential ReadToEnd-then-Wait would hang here.
        var dir = Path.Combine(Path.GetTempPath(), "pia-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "big.txt");
        var sb = new StringBuilder();
        for (var i = 0; i < 20_000; i++) sb.Append("line-").Append(i).Append('\n');
        var expectedLast = "line-19999";
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));

        try
        {
            var result = await GitProcessRunner.RunProcessAsync(
                Cmd,
                ["/c", "type", file],
                dir,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.True(result.StandardOutput.Length > 100_000, $"expected large output, got {result.StandardOutput.Length} chars");
            Assert.Contains(expectedLast, result.StandardOutput);
        }
        finally
        {
            TempPath.Remove(dir);
        }
    }

    [Fact]
    public async Task RunProcessAsync_TimesOutAndKillsTree()
    {
        var sw = Stopwatch.StartNew();
        var result = await GitProcessRunner.RunProcessAsync(
            Cmd,
            ["/c", "ping", "-n", "30", "127.0.0.1"], // ~29s; must be killed well before that
            Path.GetTempPath(),
            new Dictionary<string, string?>(),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        sw.Stop();

        Assert.True(result.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"timeout+kill should return promptly, took {sw.Elapsed}");
    }

    [Fact]
    public async Task RunProcessAsync_ExternalCancellation_KillsAndPropagates()
    {
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        var run = GitProcessRunner.RunProcessAsync(
            Cmd,
            ["/c", "ping", "-n", "30", "127.0.0.1"],
            Path.GetTempPath(),
            new Dictionary<string, string?>(),
            TimeSpan.FromSeconds(30), // generous timeout; the EXTERNAL cancel must fire first
            cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        // External cancellation propagates as OperationCanceledException carrying the caller's token.
        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        sw.Stop();

        Assert.Equal(cts.Token, oce.CancellationToken);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"cancel+kill should return promptly, took {sw.Elapsed}");
    }

    [Fact]
    public async Task RunProcessAsync_ReturnsStartFailure_ForMissingExecutable()
    {
        var result = await GitProcessRunner.RunProcessAsync(
            Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".exe"),
            [],
            Path.GetTempPath(),
            new Dictionary<string, string?>(),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
    }
}
