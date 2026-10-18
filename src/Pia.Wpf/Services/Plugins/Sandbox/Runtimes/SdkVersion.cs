using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Pia.Services.Plugins.Sandbox.Runtimes;

/// <summary>SemVer ordering of .NET SDK and runtime folder names; an unparseable name sorts lowest.</summary>
public static partial class SdkVersion
{
    public static IComparer<string?> Comparer { get; } = Comparer<string?>.Create(Compare);

    [GeneratedRegex(@"^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)(-(?<pre>[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*))?\z")]
    private static partial Regex VersionPattern();

    public static bool TryParse(string? text, out int major, out int minor, out int patch, out string? prerelease)
    {
        major = minor = patch = 0;
        prerelease = null;
        var match = VersionPattern().Match(text ?? "");
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out minor)
            || !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;

        prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        return true;
    }

    public static int? MajorOf(string? text) =>
        TryParse(text, out var major, out _, out _, out _) ? major : null;

    public static int Compare(string? left, string? right)
    {
        var leftParsed = TryParse(left, out var leftMajor, out var leftMinor, out var leftPatch, out var leftPre);
        var rightParsed = TryParse(right, out var rightMajor, out var rightMinor, out var rightPatch, out var rightPre);
        if (!leftParsed || !rightParsed)
            return leftParsed == rightParsed ? string.CompareOrdinal(left, right) : leftParsed ? 1 : -1;

        var result = leftMajor.CompareTo(rightMajor);
        if (result == 0)
            result = leftMinor.CompareTo(rightMinor);
        if (result == 0)
            result = leftPatch.CompareTo(rightPatch);
        return result != 0 ? result : ComparePrerelease(leftPre, rightPre);
    }

    /// <returns>The full path of the highest <c>sdk\&lt;v&gt;</c> folder holding dotnet.dll; null when none does.</returns>
    public static string? HighestUsableSdk(IRuntimeHostService host, string root)
    {
        var sdkRoot = Path.Combine(root, "sdk");
        if (!host.DirectoryExists(sdkRoot))
            return null;

        return host.EnumerateDirectories(sdkRoot, "*")
            .Where(folder => host.FileExists(Path.Combine(folder, "dotnet.dll")))
            .MaxBy(folder => Path.GetFileName(folder), Comparer);
    }

    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null || right is null)
            return left is null ? (right is null ? 0 : 1) : -1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            var result = CompareIdentifier(leftParts[i], rightParts[i]);
            if (result != 0)
                return result;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var lengths = left.TrimStart('0').Length.CompareTo(right.TrimStart('0').Length);
            return lengths != 0 ? lengths : string.CompareOrdinal(left.TrimStart('0'), right.TrimStart('0'));
        }

        return leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
    }
}
