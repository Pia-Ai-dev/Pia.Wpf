using System.IO;
using System.Text.RegularExpressions;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// <c>PolicyLock</c> reports "editable" for any name that is not in the enforced set, so a misspelled
/// <c>Policy[…]</c> binding silently never locks and nothing else would catch it. This reads the names
/// straight out of the markup and pins each one to a real <see cref="AppSettings"/> property.
/// </summary>
public class PolicyBindingNameTests
{
    private static readonly Regex PolicyBinding = new(@"Policy\[(?<name>\w+)\]", RegexOptions.Compiled);

    private static string ViewsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pia.Wpf", "Views")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Pia.Wpf", "Views");
    }

    private static (string File, string Name)[] BoundNames() =>
        Directory.EnumerateFiles(ViewsRoot(), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => PolicyBinding.Matches(File.ReadAllText(f))
                .Select(m => (File: Path.GetFileName(f), Name: m.Groups["name"].Value)))
            .ToArray();

    [Fact]
    public void EveryPolicyBindingNamesARealSetting()
    {
        var settable = typeof(AppSettings)
            .GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = BoundNames().Where(b => !settable.Contains(b.Name)).ToArray();

        Assert.True(
            unknown.Length == 0,
            "Policy[...] bindings naming no AppSettings property: " +
            string.Join(", ", unknown.Select(u => $"{u.File}:{u.Name}")));
    }

    [Fact]
    public void TheSettingsViewsAreActuallyWired()
    {
        // Non-vacuity: without this, deleting every binding would leave the test above passing.
        var names = BoundNames();

        Assert.True(names.Length >= 20, $"expected the settings views to carry policy bindings, found {names.Length}");
        Assert.Contains(names, n => n.Name == nameof(AppSettings.ChatHistoryRetentionDays));
        Assert.Contains(names, n => n.Name == nameof(AppSettings.AssistantFileToolsEnabled));
    }
}
