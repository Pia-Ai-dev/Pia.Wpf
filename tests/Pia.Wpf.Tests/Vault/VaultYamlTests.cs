using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultYamlTests
{
    private readonly MarkdownVaultParser _parser = new();

    // The whole point of the encoder: whatever goes into title: comes back out of the parser. A
    // JSON object once reached this key verbatim and made the page — and its re-ingest — unreadable.
    [Theory]
    [InlineData("Pia")]
    [InlineData("Pia: the assistant")]
    [InlineData("{\"subject\": \"Ilka Brenner\", \"category\": \"person\"},")]
    [InlineData("[Person_1]")]
    [InlineData("#hashtag")]
    [InlineData("\"already quoted\"")]
    [InlineData("'single quoted'")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("%directive")]
    [InlineData("!tag")]
    [InlineData("- leading dash")]
    [InlineData("trailing colon:")]
    [InlineData("comment # here")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("~")]
    [InlineData("Kurs-Gewinn-Verhältnis")]
    [InlineData("a | b > c")]
    [InlineData("")]
    public void Encoded_title_round_trips_through_the_parser(string title)
    {
        var text = VaultFrontmatter.Build("topic", title) + "\nbody\n";

        var doc = _parser.Parse(text);

        Assert.Equal(title, doc.Frontmatter.GetValueOrDefault("title"));
    }

    [Theory]
    [InlineData("person")]
    [InlineData("weird: category")]
    public void Encoded_category_round_trips_through_the_parser(string category)
    {
        var text = VaultFrontmatter.Build("topic", "Pia", category) + "\nbody\n";

        var doc = _parser.Parse(text);

        Assert.Equal(category, doc.Frontmatter.GetValueOrDefault("category"));
    }

    // A frontmatter value must stay on one line, so a multi-line title collapses rather than
    // emitting a block scalar that would break the hand-built layout.
    [Fact]
    public void Multi_line_title_collapses_to_one_line()
    {
        var text = VaultFrontmatter.Build("topic", "line one\nline two") + "\nbody\n";

        var doc = _parser.Parse(text);

        Assert.Equal("line one line two", doc.Frontmatter.GetValueOrDefault("title"));
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("title:", StringComparison.Ordinal)));
    }

    // An ordinary title must not acquire quotes — the on-disk format stays readable, and the
    // existing golden assertions elsewhere expect the plain form.
    [Fact]
    public void Ordinary_title_is_not_quoted()
    {
        Assert.Contains("title: Pia\n", VaultFrontmatter.Build("topic", "Pia"), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreserving_encodes_a_hostile_title()
    {
        var text = VaultFrontmatter.BuildPreserving(null, "Pia: the assistant", "product") + "\nbody\n";

        var doc = _parser.Parse(text);

        Assert.Equal("Pia: the assistant", doc.Frontmatter.GetValueOrDefault("title"));
        Assert.Equal("product", doc.Frontmatter.GetValueOrDefault("category"));
    }
}
