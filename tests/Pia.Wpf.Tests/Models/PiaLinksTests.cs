using System.Globalization;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

/// <summary>The docs site keeps English at the root and the other two behind a language segment, so the link
/// is a lookup rather than a prefix — and it is read at click time, because the UI language moves at runtime.
/// </summary>
public class PiaLinksTests
{
    private static string DocumentationIn(string culture)
    {
        var previous = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            // The property LocalizationService.SetLanguage writes, which is what the resolver reads.
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(culture);
            return PiaLinks.Documentation;
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("en", "https://docs.pia-ai.de/wpf/")]
    [InlineData("de", "https://docs.pia-ai.de/de/wpf/")]
    [InlineData("fr", "https://docs.pia-ai.de/fr/wpf/")]
    [InlineData("de-DE", "https://docs.pia-ai.de/de/wpf/")]
    [InlineData("fr-CH", "https://docs.pia-ai.de/fr/wpf/")]
    public void TheDocumentationLink_FollowsTheUiLanguage(string culture, string expected) =>
        Assert.Equal(expected, DocumentationIn(culture));

    /// <summary>A language the docs site has no tree for must land on the English guide, not a 404.</summary>
    [Fact]
    public void AnUntranslatedUiLanguage_FallsBackToEnglish() =>
        Assert.Equal("https://docs.pia-ai.de/wpf/", DocumentationIn("es"));
}
