using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class MarkdownVaultParserTests
{
    private readonly MarkdownVaultParser _parser = new();

    [Fact]
    public void Parses_frontmatter_and_sections()
    {
        var md = "---\nid: 11111111-1111-1111-1111-111111111111\ntype: contact_list\ntitle: Contacts\nschemaVersion: 1\n---\nIntro line.\n\n## John Smith\n- email: john@x.com\n\n## Alice Jones\n- phone: 555\n";
        var doc = _parser.Parse(md);
        Assert.Equal("contact_list", doc.Type);
        Assert.Equal("Intro line.", doc.Preamble.Trim());
        Assert.Equal(2, doc.Sections.Count);
        Assert.Equal("john-smith", doc.Sections[0].Slug);
        Assert.Contains("email: john@x.com", doc.Sections[0].Body);
    }

    [Fact]
    public void RawText_is_preserved_exactly()
    {
        var md = "---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\nbody\n";
        Assert.Equal(md, _parser.Parse(md).RawText);
    }

    [Fact]
    public void Slug_collision_and_punctuation_rules()
    {
        var doc = _parser.Parse("---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\n## Café (work)!\n## Café (work)!\n");
        Assert.Equal("cafe-work", doc.Sections[0].Slug);
        Assert.Equal("cafe-work-2", doc.Sections[1].Slug);
    }

    [Fact]
    public void Slug_global_uniqueness_smallest_free_suffix() // spec §6.1 multi-section collision fixture
    {
        // Headings "Cafe Work", "Cafe Work", "Cafe Work 2" must yield globally-unique slugs:
        // the 2nd takes cafe-work-2; the 3rd's natural slug is also cafe-work-2 (taken), so it
        // takes the smallest free suffix cafe-work-2-2 — never colliding with the 2nd section.
        var doc = _parser.Parse("---\nid: 11111111-1111-1111-1111-111111111111\ntype: note\nschemaVersion: 1\n---\n## Cafe Work\n## Cafe Work\n## Cafe Work 2\n");
        Assert.Equal("cafe-work", doc.Sections[0].Slug);
        Assert.Equal("cafe-work-2", doc.Sections[1].Slug);
        Assert.Equal("cafe-work-2-2", doc.Sections[2].Slug);
    }

    // A page whose frontmatter got corrupted must stay READABLE, or it can never be rewritten:
    // ingest reads the target page before overwriting it, so a throw here left the page stuck.
    [Theory]
    [InlineData("pia: managed\ntitle: {\"subject\": \"Ilka Brenner\", \"category\": \"person\"},")]
    [InlineData("- a sequence\n- not a mapping")]
    [InlineData("just a bare scalar")]
    [InlineData("title: [unclosed")]
    public void Malformed_frontmatter_yields_no_keys_instead_of_throwing(string block)
    {
        var doc = _parser.Parse($"---\n{block}\n---\n## Body\ntext\n");

        Assert.Empty(doc.Frontmatter);
        Assert.Equal("Body", doc.Sections[0].Heading);
    }

    // Odd-but-legal blocks must keep parsing normally — the guard above is for invalid YAML only.
    [Theory]
    [InlineData("dup: 1\ndup: 2")]
    [InlineData("\ttab indented: 1")]
    public void Unusual_but_valid_frontmatter_still_parses(string block)
    {
        Assert.NotEmpty(_parser.Parse($"---\n{block}\n---\n## Body\ntext\n").Frontmatter);
    }
}
