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

    // Rebuild re-synthesizes from a page's recorded sources, so it only means anything for the
    // compiled topic pages — offering it on a hand-written note would be a no-op button.
    [Theory]
    [InlineData("memory/topics/acme.md", true)]
    [InlineData(@"memory\topics\acme.md", true)]
    [InlineData("memory/notes/mine.md", false)]
    [InlineData("memory/contacts.md", false)]
    public void IsRebuildable_is_true_only_for_topic_pages(string filePath, bool expected)
    {
        var item = new VaultMemoryItem(filePath, filePath, "topic", "X", "body", null);

        Assert.Equal(expected, item.IsRebuildable);
    }

    [Fact]
    public void Gist_returns_the_opening_prose_of_a_free_form_page()
    {
        var item = WithBody("<!-- pia:managed -->\nAcme Corp is a logistics customer since 2024.\n\nMore.");

        Assert.Equal("Acme Corp is a logistics customer since 2024.", item.Gist);
    }

    // A templated page opens with its field list, and a field value is neither a summary nor
    // something to put in a map the model reads wholesale.
    [Fact]
    public void Gist_skips_field_bullets_and_headings()
    {
        var item = WithBody(string.Join(
            "\n",
            "<!-- pia:managed -->",
            "# Ilka Brenner",
            "- personnel number: 4711",
            "- full name: Ilka Brenner",
            "- role: unknown",
            "",
            "Joined the logistics team in 2024 and owns the Acme account."));

        Assert.Equal("Joined the logistics team in 2024 and owns the Acme account.", item.Gist);
    }

    [Fact]
    public void Gist_is_empty_when_the_page_is_only_fields()
    {
        Assert.Empty(WithBody("<!-- pia:managed -->\n- full name: Ilka Brenner\n").Gist);
    }

    [Fact]
    public void Gist_truncates_a_long_opening_line()
    {
        var gist = WithBody(new string('x', 400)).Gist;

        Assert.EndsWith("…", gist, StringComparison.Ordinal);
        Assert.Equal(161, gist.Length);
    }

    // A dash that is not a field bullet is prose, and must survive.
    [Fact]
    public void Gist_keeps_a_plain_bullet_that_is_not_a_field()
    {
        Assert.Equal("- a plain bullet of prose", WithBody("- a plain bullet of prose").Gist);
    }
}
