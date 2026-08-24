using System.IO;
using Pia.Infrastructure;
using Pia.Paths;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// A redirected data directory holds the same secrets as the real one, so the denylist has to cover it. Most
/// facts here still go through the builder, which asserts the composition directly; the two that go through
/// <see cref="SensitivePathGuard.IsBlocked"/> are the ones that prove the guard REBUILDS — it used to hold both
/// arrays in <c>static readonly</c> fields frozen at type load.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class SensitivePathGuardOverriddenProfileTests
{
    /// <summary>
    /// Reads the guard once from the REAL profile before any override is applied, which is what makes the two
    /// rebuild facts below non-vacuous: with the arrays cached against the real roots, a test that then swings
    /// the roots is asking the exact question a frozen <c>static readonly</c> answers wrongly.
    /// </summary>
    public SensitivePathGuardOverriddenProfileTests()
    {
        SensitivePathGuard.IsBlocked(Path.GetTempPath(), out _);
    }

    /// <summary>
    /// The initialization-order trap, as behaviour rather than as a string comparison. Both halves matter: the
    /// redirected profile becomes blocked, and its runs carve-out becomes allowed. Frozen arrays get the first
    /// wrong (a redirected profile is unprotected) AND the second (a redirected run's own workspace is blocked,
    /// which is why a test wanting one had to use the real runs root and stamp its mtime).
    /// </summary>
    [Fact]
    public void IsBlocked_FollowsAnOverrideAppliedAfterTheGuardHasAlreadyAnswered()
    {
        var local = Path.Combine(Path.GetTempPath(), $"pia-guard-local-{Guid.NewGuid():N}");
        var insideProfile = Path.Combine(local, "history.db");
        var insideRuns = Path.Combine(local, "runs", Guid.NewGuid().ToString("N"), "out.md");

        // Before: neither path is anywhere the guard knows about, so both are simply outside the denylist.
        Assert.False(SensitivePathGuard.IsBlocked(insideProfile, out _));

        using (PiaPaths.OverrideForTests(null, local))
        {
            Assert.True(SensitivePathGuard.IsBlocked(insideProfile, out _));
            Assert.False(SensitivePathGuard.IsBlocked(insideRuns, out _));
        }

        // And back: the cache key is the roots, so dropping the override restores the real arrays rather than
        // leaving a temp directory blocked for the rest of the process.
        Assert.False(SensitivePathGuard.IsBlocked(insideProfile, out _));
    }

    /// <summary>The carve-out has to move WITH the profile, not merely exist: a redirected run whose workspace
    /// root is blocked dead-ends every file tool in it.</summary>
    [Fact]
    public void IsBlocked_UnderAnOverride_StillBlocksTheRealRunsSibling()
    {
        var local = Path.Combine(Path.GetTempPath(), $"pia-guard-local-{Guid.NewGuid():N}");

        using (PiaPaths.OverrideForTests(null, local))
        {
            // The island is `runs` under the OVERRIDDEN root only. A sibling of it stays blocked, so the
            // carve-out did not widen to the whole redirected profile.
            Assert.True(SensitivePathGuard.IsBlocked(Path.Combine(local, "Logs", "pia.log"), out _));
        }
    }

    /// <summary>The real profile stays blocked no matter what the facts below do to the roots.</summary>
    [Fact]
    public void LiveGuard_StillBlocksTheRealProfile()
    {
        var realPiaData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia", "history.db");

        Assert.True(SensitivePathGuard.IsBlocked(realPiaData, out _));
    }

    [Fact]
    public void BlockedRoots_WithNoOverride_AreUnchanged()
    {
        using (PiaPaths.OverrideForTests(null, null))
        {
            var roots = SensitivePathGuard.BuildBlockedRoots();

            Assert.Contains(roots, r => r.EndsWith(Path.Combine("AppData", "Local", "Pia"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(roots, r => r.EndsWith(Path.Combine("AppData", "Roaming", "Pia"), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>A throwaway data directory routinely does not exist yet when the array is built, which is exactly
    /// the case <c>SafeCanonical</c> drops — so this fact deliberately never creates the directories.</summary>
    [Fact]
    public void BlockedRoots_WithOverride_CoverTheRedirectedRootsEvenWhenMissing()
    {
        var roaming = Path.Combine(Path.GetTempPath(), $"pia-guard-roaming-{Guid.NewGuid():N}");
        var local = Path.Combine(Path.GetTempPath(), $"pia-guard-local-{Guid.NewGuid():N}");

        using (PiaPaths.OverrideForTests(roaming, local))
        {
            Assert.False(Directory.Exists(roaming));
            Assert.False(Directory.Exists(local));

            var roots = SensitivePathGuard.BuildBlockedRoots();

            Assert.Contains(roots, r => r.Equals(roaming, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(roots, r => r.Equals(local, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The real profile stays blocked under an override — a redirected run must not unlock it.</summary>
    [Fact]
    public void BlockedRoots_WithOverride_StillCoverTheRealProfile()
    {
        using (PiaPaths.OverrideForTests(
            Path.Combine(Path.GetTempPath(), $"pia-guard-roaming-{Guid.NewGuid():N}"),
            Path.Combine(Path.GetTempPath(), $"pia-guard-local-{Guid.NewGuid():N}")))
        {
            var roots = SensitivePathGuard.BuildBlockedRoots();

            Assert.Contains(roots, r => r.EndsWith(Path.Combine("AppData", "Local", "Pia"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(roots, r => r.EndsWith(Path.Combine("AppData", "Roaming", "Pia"), StringComparison.OrdinalIgnoreCase));
        }
    }
}
