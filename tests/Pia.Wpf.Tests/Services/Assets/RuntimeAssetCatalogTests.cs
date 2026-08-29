using System.IO;
using System.Text.RegularExpressions;
using Pia.Services.Assets;
using Xunit;

namespace Pia.Tests.Services.Assets;

/// <summary>
/// The client asks the mirror for <c>RuntimeAssetCatalog</c>'s keys; the publishing script uploads
/// <c>scripts/RuntimeAssetCatalogue.ps1</c>'s. A key present in one and not the other costs a silent
/// fallback to the upstream host — the mirror is a performance and control path, so nothing fails.
/// </summary>
public class RuntimeAssetCatalogTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string Script =
        File.ReadAllText(Path.Combine(RepoRoot, "scripts", "RuntimeAssetCatalogue.ps1"));

    [Fact]
    public void The_publishing_script_uploads_exactly_the_keys_the_client_asks_for()
    {
        var fromScript = Regex.Matches(Script, @"MirrorKey\s*=\s*'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal);

        var fromClient = RuntimeAssetCatalog.All.Select(a => a.MirrorKey).Order(StringComparer.Ordinal);

        Assert.Equal(fromClient, fromScript);
    }

    /// <summary>
    /// The keys agreeing is not enough. If the script's <c>Url</c> drifts from the client's upstream URL
    /// for the same key, the mirror serves the wrong bytes under a right-looking name and every client
    /// takes them happily — the fallback never fires and nothing errors. It is the one drift here that
    /// produces wrong content rather than a silent fetch from upstream.
    /// </summary>
    [Fact]
    public void The_publishing_script_stages_from_the_same_upstream_urls_the_client_falls_back_to()
    {
        var bases = Regex.Matches(Script, @"\$script:(\w+)\s*=\s*'([^']+)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

        var fromScript = Regex.Matches(Script, """Url\s*=\s*(?:'([^']+)'|"([^"]+)")""")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Select(url => Regex.Replace(url, @"\$script:(\w+)", m => bases[m.Groups[1].Value]))
            .Order(StringComparer.Ordinal);

        var fromClient = RuntimeAssetCatalog.All.Select(a => a.UpstreamUrl).Order(StringComparer.Ordinal);

        Assert.Equal(fromClient, fromScript);
    }

    /// <summary>
    /// The storage service rejects anything else on its upload path, and the refusal would land on the
    /// publishing run rather than on whoever added the key.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_key_is_a_legal_storage_upload_path(string key)
    {
        Assert.Matches("^[A-Za-z0-9._/-]+$", key);
        Assert.DoesNotContain("..", key, StringComparison.Ordinal);
        Assert.False(key.StartsWith('/'));
    }

    /// <summary>
    /// A sherpa bundle is mirrored as the archive it is published as, not as the extracted tree, so
    /// the client's extract step is the same on both paths.
    /// </summary>
    [Fact]
    public void A_key_ends_in_the_same_file_name_as_its_upstream_url()
    {
        foreach (var asset in RuntimeAssetCatalog.All)
        {
            var upstreamName = asset.UpstreamUrl[(asset.UpstreamUrl.LastIndexOf('/') + 1)..];
            var keyName = asset.MirrorKey[(asset.MirrorKey.LastIndexOf('/') + 1)..];

            // The one renamed asset: EmbeddingService looks for the model under the model's own name,
            // and a bare "model.onnx" would collide with anything else mirrored beside it.
            if (upstreamName == "model.onnx")
            {
                Assert.Equal("paraphrase-multilingual-MiniLM-L12-v2.onnx", keyName);
                continue;
            }

            Assert.Equal(upstreamName, keyName);
        }
    }

    [Fact]
    public void Keys_are_unique()
    {
        var keys = RuntimeAssetCatalog.All.Select(a => a.MirrorKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var asset in RuntimeAssetCatalog.All) data.Add(asset.MirrorKey);
        return data;
    }
}
