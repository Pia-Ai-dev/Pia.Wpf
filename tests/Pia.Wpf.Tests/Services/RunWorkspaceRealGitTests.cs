using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

[Collection("GitLocatorStatic")]
public sealed class RunWorkspaceRealGitTests : IDisposable
{
    private readonly string _dir;
    private readonly string _source;
    private readonly string _runsBase;

    public RunWorkspaceRealGitTests()
    {
        GitLocator.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "PiaRunRealGit_" + Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_dir, "files");
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        GitLocator.ResetForTests();
        TempPath.Remove(_dir);
    }

    private static string Git(string dir, params string[] args)
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
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return stdout;
    }

    private void SeedRepo()
    {
        Git(_source, "init");
        Git(_source, "config", "user.email", "t@e.st");
        Git(_source, "config", "user.name", "Test");
        Git(_source, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_source, "a.md"), "hello\n");
        Git(_source, "add", "-A");
        Git(_source, "commit", "-m", "seed");
    }

    private async Task<(RunWorkspaceService Svc, Guid RunId, RunWorkspace Ws)> ProvisionAsync()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { AssistantFilesFolder = _source });
        var svc = new RunWorkspaceService(new GitProcessRunner(), settings, NullLogger<RunWorkspaceService>.Instance, _runsBase);
        var runId = Guid.NewGuid();
        var ws = await svc.ProvisionAsync(runId, null, TestContext.Current.CancellationToken);
        Assert.NotNull(ws);
        Assert.Equal(RunWorkspaceMode.Worktree, ws!.Mode);
        return (svc, runId, ws);
    }

    // --no-verify skips pre-commit and commit-msg only; a failing prepare-commit-msg hook still fails the commit.
    [Fact]
    public async Task Promotion_CommitsDespiteAFailingPrepareCommitMsgHook()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        SeedRepo();
        File.WriteAllText(
            Path.Combine(_source, ".git", "hooks", "prepare-commit-msg"), "#!/bin/sh\necho no >&2\nexit 1\n");
        var (svc, runId, ws) = await ProvisionAsync();
        File.WriteAllText(Path.Combine(ws.Root, "report.md"), "r\n");
        File.AppendAllText(Path.Combine(ws.Root, "a.md"), "more\n");

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Promoted);
        Assert.False(result.RetainWorkspace);
        var files = Git(_source, "show", "--name-only", "--format=", "pia/run/" + runId);
        Assert.Contains("report.md", files);
        Assert.Contains("a.md", files);
    }

    [Fact]
    public async Task Promotion_WithChangesOnlyInsideANestedRepo_CommitsNothingAndKeepsTheWorkspace()
    {
        Assert.SkipUnless(GitLocator.IsAvailable, "git is not installed on this machine");
        SeedRepo();
        var nested = Path.Combine(_source, "nest");
        Directory.CreateDirectory(nested);
        Git(nested, "init");
        Git(nested, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(nested, "z.md"), "z\n");
        Git(nested, "add", "-A");
        Git(nested, "-c", "user.name=T", "-c", "user.email=t@e.st", "commit", "-m", "z");
        Git(_source, "add", "-A");
        Git(_source, "commit", "-m", "gitlink");
        var (svc, runId, ws) = await ProvisionAsync();
        var runNest = Path.Combine(ws.Root, "nest");
        Directory.CreateDirectory(runNest);
        Git(runNest, "init");
        File.WriteAllText(Path.Combine(runNest, "z.md"), "changed\n");

        var result = await svc.PromoteAsync(runId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Promoted);
        Assert.True(result.RetainWorkspace);
    }
}
