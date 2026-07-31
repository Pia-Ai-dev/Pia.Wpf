using System.IO;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="SensitivePathGuard"/>'s SECOND carve-out (Batch 06 B1): the per-run agent workspace
/// root (<see cref="AssistantWorkspace.RunsRoot"/>) sits inside the otherwise-blocked
/// <c>%LOCALAPPDATA%\Pia</c> tree exactly like the legacy workdir does, and must be carved out the same
/// way — or an isolated run's every read/write/delete/list/search would pass containment and then be
/// rejected by the guard's denylist (§0.2a). Mirrors <c>SensitivePathGuardTests</c>'s workdir shape.
/// </summary>
public sealed class SensitivePathGuardRunsCarveOutTests
{
    // REGRESSION: pins the carve-out itself. The "siblings stay blocked" half is the non-vacuity
    // control — a carve-out that accidentally widened to cover %LOCALAPPDATA%\Pia would turn only the
    // first half green.
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

    // GUARD: pins CanonicalizeAllowedIsland's missing-tail behavior directly, in isolation from
    // BuildAllowedExceptions. A test cannot control when SensitivePathGuard's statics initialize — that
    // is exactly the hazard B2 exists for (the runs root routinely does not exist yet when the array is
    // built) — so the *helper* is what gets the fact, not an assertion against the static array.
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
            try { Directory.Delete(existingTemp, recursive: true); } catch { /* best effort */ }
        }
    }
}
