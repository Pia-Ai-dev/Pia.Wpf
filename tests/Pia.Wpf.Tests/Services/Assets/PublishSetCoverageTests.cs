using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Pia.Tests.Services.Assets;

/// <summary>
/// <c>RuntimeAssetCatalogTests</c> pins the keys the client asks for against the ones the catalogue
/// holds. Neither notices a catalogue group the publishing script's default set leaves out — that
/// group is simply never uploaded, and the client falls back to upstream forever without erroring.
/// The TTS voices were added and missed exactly that way.
/// </summary>
public class PublishSetCoverageTests
{
    private static readonly string ScriptsRoot = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "scripts");

    private static string Read(string name) => File.ReadAllText(Path.Combine(ScriptsRoot, name));

    /// <summary>Group name → whether its block declares any <c>MirrorKey</c>.</summary>
    private static Dictionary<string, bool> CatalogueGroups()
    {
        var text = Read("RuntimeAssetCatalogue.ps1");
        // \r? because the script's line endings are mixed, so an LF-only anchor matches nothing.
        var heads = Regex.Matches(text, @"(?m)^        (\w+) = @\(\r?$");
        Assert.NotEmpty(heads);

        var groups = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < heads.Count; i++)
        {
            var start = heads[i].Index;
            var end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
            groups[heads[i].Groups[1].Value] = text[start..end].Contains("MirrorKey", StringComparison.Ordinal);
        }

        return groups;
    }

    private static List<string> PublishDefaultInclude()
    {
        var text = Read("Publish-RuntimeAssets.ps1");
        var block = Regex.Match(text, @"\[string\[\]\]\$Include = @\((.*?)\),", RegexOptions.Singleline);
        Assert.True(block.Success, "could not find the default -Include list");

        return [.. Regex.Matches(block.Groups[1].Value, @"'([^']+)'").Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void The_publish_default_covers_every_mirror_bearing_group()
    {
        var mirrored = CatalogueGroups().Where(g => g.Value).Select(g => g.Key).Order(StringComparer.Ordinal);

        Assert.Equal(mirrored, PublishDefaultInclude().Order(StringComparer.Ordinal));
    }

    /// <summary>Chromium carries no key on purpose, so publishing it would fail rather than no-op.</summary>
    [Fact]
    public void The_publish_default_names_no_mirror_exempt_group()
    {
        var exempt = CatalogueGroups().Where(g => !g.Value).Select(g => g.Key).ToList();

        Assert.NotEmpty(exempt);
        Assert.DoesNotContain(PublishDefaultInclude(), exempt.Contains);
    }

    /// <summary>
    /// Tab-completion is how these names get typed, so a group absent from it reads as not existing.
    /// Both scripts list every group; only the defaults differ.
    /// </summary>
    [Theory]
    [InlineData("Publish-RuntimeAssets.ps1")]
    [InlineData("Save-RuntimeAssets.ps1")]
    public void Argument_completions_offer_every_catalogue_group(string script)
    {
        var text = Read(script);
        var block = Regex.Match(text, @"\[ArgumentCompletions\((.*?)\)\]", RegexOptions.Singleline);
        Assert.True(block.Success, $"no ArgumentCompletions in {script}");

        var offered = Regex.Matches(block.Groups[1].Value, @"'([^']+)'").Select(m => m.Groups[1].Value).ToList();

        // Publish cannot upload the mirror-exempt group, so it is the one name it may omit.
        var expected = CatalogueGroups()
            .Where(g => g.Value || script.StartsWith("Save", StringComparison.Ordinal))
            .Select(g => g.Key);

        foreach (var group in expected)
            Assert.Contains(group, offered);
    }
}
