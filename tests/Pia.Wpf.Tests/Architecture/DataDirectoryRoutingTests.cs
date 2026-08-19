using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Pia.Tests.Architecture;

/// <summary>
/// The hermetic-profile override only holds while <c>PiaPaths</c> is the only place that names a profile folder.
/// A new call site that reads <c>SpecialFolder</c> directly compiles and passes every other test while silently
/// writing to the developer's real profile, so it is caught here instead.
/// </summary>
public class DataDirectoryRoutingTests
{
    /// <summary>Five levels up from the test binary: <c>bin/{config}/{tfm}</c> → project → <c>tests</c> → root.</summary>
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string ClientSourceRoot = Path.Combine(RepositoryRoot, "src", "Pia.Wpf");

    private static readonly Regex ProfileFolderRead = new(
        @"SpecialFolder\.(Local)?ApplicationData", RegexOptions.Compiled);

    private static readonly string Resolver = Path.Combine("Paths", "PiaPaths.cs");

    /// <summary>Both locate an installed program under <c>%LOCALAPPDATA%\Programs</c> — neither reads Pia's own
    /// data, and routing them would break git / VS Code discovery under an override.</summary>
    private static readonly string[] Allowed =
    [
        Resolver,
        Path.Combine("Helpers", "GitLocator.cs"),
        Path.Combine("Helpers", "VsCodeLauncher.cs"),
    ];

    [Fact]
    public void OnlyPiaPaths_ReadsTheProfileFolders()
    {
        Assert.True(Directory.Exists(ClientSourceRoot), $"source root not found: {ClientSourceRoot}");

        var offenders = new List<string>();
        var resolverMatched = false;

        foreach (var file in Directory.EnumerateFiles(ClientSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(ClientSourceRoot, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ProfileFolderRead.IsMatch(File.ReadAllText(file)))
                continue;

            if (relative.Equals(Resolver, StringComparison.OrdinalIgnoreCase))
                resolverMatched = true;

            if (!Allowed.Contains(relative, StringComparer.OrdinalIgnoreCase))
                offenders.Add(relative);
        }

        // Positive control: an empty offender list means nothing at all matched — a broken scan, not a clean tree.
        Assert.True(resolverMatched,
            $"the scan did not even find {Resolver}, so it cannot distinguish 'routed' from 'not scanned'");

        Assert.True(offenders.Count == 0,
            "these files read a profile folder directly instead of going through PiaPaths, so they ignore the "
            + $"PIA_DATA_DIR / PIA_LOCAL_DATA_DIR override: {string.Join(", ", offenders)}. Route them through "
            + "PiaPaths, or add the file to the allowlist in this test with a reason.");
    }
}
