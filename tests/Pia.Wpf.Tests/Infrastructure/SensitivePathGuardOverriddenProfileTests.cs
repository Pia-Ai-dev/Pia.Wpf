using System.IO;
using Pia.Infrastructure;
using Pia.Paths;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// A redirected data directory holds the same secrets as the real one, so the denylist has to cover it. Asserted
/// against the builder rather than <see cref="SensitivePathGuard.IsBlocked"/> because the blocked-root array is
/// built once per process, before any test could swing the roots.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class SensitivePathGuardOverriddenProfileTests
{
    /// <summary>
    /// Builds the guard's two process-wide arrays from the REAL profile before any override is applied. The type
    /// is <c>beforefieldinit</c>, so the runtime may run those initializers at any point up to the first field
    /// read — today that lands inside <see cref="SensitivePathGuard.IsBlocked"/>, after the override below is
    /// gone, but that is latitude rather than a guarantee. Forcing the order here costs nothing and stops a
    /// future runtime choice from freezing a temp root into the array for every other guard test in the process.
    /// </summary>
    public SensitivePathGuardOverriddenProfileTests()
    {
        SensitivePathGuard.IsBlocked(Path.GetTempPath(), out _);
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
