using System.IO;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary><see cref="AssistantWorkspace.RunsRoot"/> sits inside the otherwise-blocked <c>%LOCALAPPDATA%\Pia</c>
/// tree, so without the same carve-out the legacy workdir has, an isolated run's every write is rejected.</summary>
public sealed class SensitivePathGuardRunsCarveOutTests
{
    // The "siblings stay blocked" half is the non-vacuity control: a carve-out that accidentally widened to
    // cover %LOCALAPPDATA%\Pia would turn only the first half green.
    [Fact]
    public void RunsRoot_IsCarvedOut_WhileTheDataRootAndDbStayBlocked()
    {
        var runsRoot = AssistantWorkspace.RunsRoot;         // %LOCALAPPDATA%\Pia\runs
        var piaDir = Path.GetDirectoryName(runsRoot)!;      // %LOCALAPPDATA%\Pia

        // Create the runs root so canonicalization resolves the same handle the resolver would.
        Directory.CreateDirectory(runsRoot);

        var canonRunsRoot = SafeFolderPath.Canonicalize(runsRoot);
        var canonPia = SafeFolderPath.Canonicalize(piaDir);

        // The Pia data root and its sensitive siblings stay blocked...
        Assert.True(SensitivePathGuard.IsBlocked(canonPia, out var rootReason));
        Assert.NotEmpty(rootReason);
        Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(canonPia, "history.db"), out _));

        // ...but the runs island (which sits INSIDE that blocked root) is carved back out.
        var runId = Guid.NewGuid().ToString();
        Assert.False(SensitivePathGuard.IsBlocked(canonRunsRoot, out _));
        Assert.False(SensitivePathGuard.IsBlocked(Path.Combine(canonRunsRoot, runId), out _));
        Assert.False(SensitivePathGuard.IsBlocked(Path.Combine(canonRunsRoot, runId, "nested", "a.md"), out _));
    }

    // A test cannot control when SensitivePathGuard's statics initialize, and the runs root routinely does not
    // exist yet when the array is built, so the HELPER gets the fact rather than the static array.
    [Fact]
    public void CanonicalizeAllowedIsland_ResolvesAnIslandWhoseTailDoesNotExist()
    {
        var existingTemp = Path.Combine(Path.GetTempPath(), "PiaTests_CAI_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existingTemp);
        try
        {
            var missingTail = Path.Combine(existingTemp, "nope", "deeper");

            var result = SensitivePathGuard.CanonicalizeAllowedIsland(missingTail);

            var expected = Path.Combine(SafeFolderPath.Canonicalize(existingTemp), "nope", "deeper");
            Assert.Equal(expected, result);
        }
        finally
        {
            TempPath.Remove(existingTemp);
        }
    }
}
