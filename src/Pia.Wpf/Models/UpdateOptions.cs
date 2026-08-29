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
}
