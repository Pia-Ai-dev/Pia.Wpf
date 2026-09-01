using System.IO;
using Pia.Paths;

namespace Pia.Tests.TestInfrastructure;

/// <summary>
/// Points Pia's data roots at a throwaway directory for the lifetime of one test class, so a test that needs a
/// real path inside <c>AssistantWorkspace.RunsRoot</c> — because <c>RunWorkspaceRedirects.Record</c>'s
/// containment gate and <c>SensitivePathGuard</c>'s carve-out both key on it — stops reaching into the
/// developer's actual profile to get one.
/// <para>
/// A class using this MUST also join the <c>PiaPathsStatic</c> collection. <c>PiaPaths.OverrideForTests</c> sets
/// process-wide environment variables, and that collection is the only thing in the suite that stops another
/// test resolving a Pia path while the override is live. Without it this fixture is a race, not an isolation.
/// </para>
/// </summary>
public sealed class RedirectedProfileFixture : IDisposable
{
    private readonly IDisposable _override;

    public RedirectedProfileFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "pia-profile-" + Guid.NewGuid().ToString("N"));
        // Separate roaming/local children rather than one shared directory: pointing both variables at a single
        // empty directory is what made F1's first reading look like a settings leak.
        _override = PiaPaths.OverrideForTests(
            Path.Combine(Root, "roaming"), Path.Combine(Root, "local"));
    }

    /// <summary>The throwaway profile's parent, so a test can assert it wrote inside it and nowhere else.</summary>
    public string Root { get; }

    public void Dispose()
    {
        // Override first: a test's own teardown may still resolve a Pia path, and it must resolve to the
        // directory about to be deleted rather than to the real profile.
        _override.Dispose();
        TempPath.Remove(Root);
    }
}
