namespace Pia.Models;

public class AutoUpdateOptions
{
    public const string SectionName = "Update";

    /// <summary>Base URL of a static-file release feed. Set, it wins over <see cref="GitHubRepoUrl"/>.</summary>
    public string? FeedUrl { get; set; }

    public string GitHubRepoUrl { get; set; } = "https://github.com/Pia-Ai-dev/Pia.Wpf";
    public string? AccessToken { get; set; }

    /// <summary>GitHub only — a static feed separates pre-releases by channel instead.</summary>
    public bool Prerelease { get; set; }

    /// <summary>Signer organizations (O=) an update's binaries may carry; empty means <see cref="DefaultTrustedPublishers"/>.</summary>
    public string[] TrustedPublishers { get; set; } = [];

    // neo42 signs everything it builds; the rest arrive pre-signed with the .NET runtime, ONNX Runtime and Playwright.
    public static readonly string[] DefaultTrustedPublishers = ["neo42 GmbH", "Microsoft Corporation", "OpenJS Foundation"];

    public IReadOnlyCollection<string> EffectiveTrustedPublishers
        => TrustedPublishers.Length > 0 ? TrustedPublishers : DefaultTrustedPublishers;
}
