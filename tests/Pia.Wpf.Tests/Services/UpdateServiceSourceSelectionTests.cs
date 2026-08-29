using Pia.Models;
using Pia.Services;
using Velopack.Sources;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The feed is picked from config, so a deployment moves off GitHub without a code change.</summary>
public class UpdateServiceSourceSelectionTests
{
    [Fact]
    public void FeedUrl_wins_over_the_github_settings()
    {
        var source = UpdateService.CreateSource(new AutoUpdateOptions
        {
            FeedUrl = "https://storage.pia-ai.de/f/wpf/",
            GitHubRepoUrl = "https://github.com/Pia-Ai-dev/Pia.Wpf",
            AccessToken = "irrelevant"
        });

        var web = Assert.IsType<SimpleWebSource>(source);
        Assert.Equal("https://storage.pia-ai.de/f/wpf/", web.BaseUri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_feed_url_keeps_github(string? feedUrl)
    {
        var source = UpdateService.CreateSource(new AutoUpdateOptions { FeedUrl = feedUrl });

        Assert.IsType<GithubSource>(source);
    }

    /// <summary>Velopack appends the channel file to the base, so a missing trailing slash must not eat a path segment.</summary>
    [Fact]
    public void A_feed_url_without_a_trailing_slash_keeps_its_last_segment()
    {
        var source = UpdateService.CreateSource(new AutoUpdateOptions { FeedUrl = "https://storage.pia-ai.de/f/wpf" });

        var web = Assert.IsType<SimpleWebSource>(source);
        Assert.Equal("/f/wpf", web.BaseUri.AbsolutePath);
    }
}
