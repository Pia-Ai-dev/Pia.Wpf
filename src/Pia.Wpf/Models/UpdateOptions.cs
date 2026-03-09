namespace Pia.Models;

public class AutoUpdateOptions
{
    public const string SectionName = "Update";

    public string GitHubRepoUrl { get; set; } = "https://github.com/Pia-Ai-dev/Pia.Wpf";
    public string? AccessToken { get; set; }
    public bool Prerelease { get; set; }
}
