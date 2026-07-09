using Pia.Models.Vault;
using Xunit;

namespace Pia.Tests.Vault;

/// <summary>
/// <see cref="VaultMemoryItem.DisplayBody"/> is the inspector's read projection: it drops the internal
/// <c>&lt;!-- pia:managed --&gt;</c> sentinel so it never shows as literal text, while the stored
/// <see cref="VaultMemoryItem.Body"/> (edit/copy source) keeps the raw markdown.
/// </summary>
public class VaultMemoryItemTests
{
    private static VaultMemoryItem WithBody(string body) =>
        new("topics/x.md", "memory/topics/x.md", "topic", "X", body, null);

    [Fact]
    public void DisplayBody_strips_managed_sentinel_keeping_preamble_and_body()
    {
        var body = string.Join("\n",
            "Manual preamble.", "", "<!-- pia:managed -->", "", "# Synthesized", "Prose.");
        var item = WithBody(body);

        var display = item.DisplayBody;

        Assert.DoesNotContain("pia:managed", display);
        Assert.Contains("Manual preamble.", display);
        Assert.Contains("# Synthesized", display);
        Assert.Contains("Prose.", display);
        // The stored body is untouched — edit and copy still see the sentinel.
        Assert.Contains("<!-- pia:managed -->", item.Body);
    }

    [Fact]
    public void DisplayBody_strips_sentinel_when_it_opens_the_page()
    {
        var body = string.Join("\n", "<!-- pia:managed -->", "", "Body only.");

        var display = WithBody(body).DisplayBody;

        Assert.DoesNotContain("pia:managed", display);
        Assert.Contains("Body only.", display);
    }

    [Fact]
    public void DisplayBody_returns_body_unchanged_when_no_sentinel()
    {
        var body = string.Join("\n", "# Heading", "Just prose, no marker.");
        var item = WithBody(body);

        Assert.Equal(body, item.DisplayBody);
    }
}
