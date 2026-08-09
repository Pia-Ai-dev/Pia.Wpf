using System.IO;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>The agent's workdir lives inside the blocked %LOCALAPPDATA%\Pia tree and must be carved out.</summary>
public sealed class SensitivePathGuardTests
{
    [Fact]
    public void Workdir_IsCarvedOut_WhileSiblingsStayBlocked()
    {
        var workdir = AssistantWorkspace.LegacyWorkdir;        // %LOCALAPPDATA%\Pia\workdir
        var piaDir = Path.GetDirectoryName(workdir)!;          // %LOCALAPPDATA%\Pia

        // Create the workdir so canonicalization resolves the same handle the resolver would.
        // This is the app's own default folder (created at startup); leave it in place.
        Directory.CreateDirectory(workdir);

        var canonWorkdir = SafeFolderPath.Canonicalize(workdir);
        var canonPia = SafeFolderPath.Canonicalize(piaDir);

        // The Pia data root and its sensitive siblings stay blocked...
        Assert.True(SensitivePathGuard.IsBlocked(canonPia, out var rootReason));
        Assert.NotEmpty(rootReason);
        Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(canonPia, "history.db"), out _));

        // ...but the workdir island (which sits INSIDE that blocked root) is carved back out.
        Assert.False(SensitivePathGuard.IsBlocked(canonWorkdir, out _));
        Assert.False(SensitivePathGuard.IsBlocked(Path.Combine(canonWorkdir, "test.ps1"), out _));
        Assert.False(SensitivePathGuard.IsBlocked(Path.Combine(canonWorkdir, "nested", "deep.txt"), out _));
    }

    [Fact]
    public void WindowsDirectory_IsStillBlocked()
    {
        var windir = Environment.GetEnvironmentVariable("WINDIR");
        Assert.False(string.IsNullOrEmpty(windir));

        Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(windir!, "System32", "drivers", "etc", "hosts"), out _));
    }
}
