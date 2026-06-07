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
}
