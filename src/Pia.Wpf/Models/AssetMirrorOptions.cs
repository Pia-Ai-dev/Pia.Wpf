namespace Pia.Models;

/// <summary>Where runtime assets (models, tokenizers) are fetched from.</summary>
public class AssetMirrorOptions
{
    public const string SectionName = "Assets";

    /// <summary>
    /// Base URL of a mirror serving the keys in <c>RuntimeAssetCatalog</c>, and the only source a
    /// download has. Blank sends every asset to its upstream host instead, for a deployment that runs
    /// no mirror of its own.
    /// </summary>
    public string? MirrorBaseUrl { get; set; }

    /// <summary>
    /// Bounds the mirror's <em>response headers</em>, not the transfer. A black-holed host would otherwise
    /// hang forever — every client here runs on an infinite <see cref="System.Net.Http.HttpClient.Timeout"/>.
    /// </summary>
    public int MirrorTimeoutSeconds { get; set; } = 60;
}
