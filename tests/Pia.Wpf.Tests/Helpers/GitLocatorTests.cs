using System.IO;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Covers <see cref="GitLocator"/>'s executable acceptance rule (a real <c>.exe</c> only, never a
/// <c>.cmd</c>/<c>.bat</c> shim) and the probe-override caching seam — git-free, so it runs on a box
/// without git. Lives in the non-parallel "GitLocatorStatic" collection because the caching tests
/// mutate the process-global probe.
/// </summary>
[Collection("GitLocatorStatic")]
public sealed class GitLocatorTests : IDisposable
{
    private readonly string _dir;

    public GitLocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pia-gitlocator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        GitLocator.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void AcceptExecutable_AcceptsRealExe()
    {
        var exe = Touch("git.exe");
        Assert.Equal(exe, GitLocator.AcceptExecutable(exe));
    }

    [Theory]
    [InlineData("git.cmd")]
    [InlineData("git.bat")]
    [InlineData("git.ps1")]
    [InlineData("git")] // no extension
    public void AcceptExecutable_RefusesShimsAndNonExe(string name)
    {
        var shim = Touch(name);
        Assert.Null(GitLocator.AcceptExecutable(shim));
    }

    [Fact]
    public void AcceptExecutable_RefusesMissingFile()
        => Assert.Null(GitLocator.AcceptExecutable(Path.Combine(_dir, "does-not-exist.exe")));

    [Fact]
    public void AcceptExecutable_RefusesNullOrEmpty()
    {
        Assert.Null(GitLocator.AcceptExecutable(null));
        Assert.Null(GitLocator.AcceptExecutable(string.Empty));
    }

    [Fact]
    public void ProbeOverride_ReturningExe_MakesAvailableAndPinsIt()
    {
        var exe = Touch("git.exe");
        GitLocator.ResetForTests();
        GitLocator.ProbeOverride = () => exe;
        try
        {
            Assert.True(GitLocator.IsAvailable);
            Assert.Equal(exe, GitLocator.Executable);
        }
        finally
        {
            GitLocator.ResetForTests();
        }
    }

    [Fact]
    public void ProbeOverride_ReturningCmdShim_IsRefused()
    {
        var shim = Touch("git.cmd");
        GitLocator.ResetForTests();
        GitLocator.ProbeOverride = () => shim;
        try
        {
            Assert.False(GitLocator.IsAvailable);
            Assert.Null(GitLocator.Executable);
        }
        finally
        {
            GitLocator.ResetForTests();
        }
    }

    [Fact]
    public void Executable_IsCached_AfterFirstResolve()
    {
        var exe = Touch("git.exe");
        GitLocator.ResetForTests();
        GitLocator.ProbeOverride = () => exe;
        try
        {
            var first = GitLocator.Executable;
            // Change the probe: the cached value must win until an explicit reset.
            GitLocator.ProbeOverride = () => Touch("other.exe");
            Assert.Equal(first, GitLocator.Executable);
        }
        finally
        {
            GitLocator.ResetForTests();
        }
    }
}
