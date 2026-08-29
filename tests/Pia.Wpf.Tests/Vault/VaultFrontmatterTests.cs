using Pia.Infrastructure.Vault;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultFrontmatterTests
{
    private readonly MarkdownVaultParser _parser = new();

    [Fact]
    public void Build_with_category_writes_type_and_category_after_title()
    {
        var fm = VaultFrontmatter.Build("topic", "Pia", "product");

        Assert.Contains("type: topic\n", fm);
        Assert.Contains("category: product\n", fm);

        var titleIndex = fm.IndexOf("title: Pia\n", StringComparison.Ordinal);
        var categoryIndex = fm.IndexOf("category: product\n", StringComparison.Ordinal);
        Assert.True(titleIndex >= 0);
        Assert.True(categoryIndex > titleIndex, "category line must appear after the title line");
    }

    [Fact]
    public void Build_and_BuildPreserving_mark_the_page_as_ai_generated()
    {
        foreach (var fm in new[] { VaultFrontmatter.Build("note", "X"), VaultFrontmatter.BuildPreserving(null, "T", "concept") })
        {
            Assert.Contains($"generator: {AppVersionInfo.Generator}\n", fm);
            Assert.Contains("aiGenerated: true\n", fm);
            Assert.EndsWith("schemaVersion: 1\n---\n", fm);
        }
    }

    [Fact]
    public void Build_two_arg_emits_no_category_line()
    {
        var fm = VaultFrontmatter.Build("note", "X");

        Assert.DoesNotContain("category:", fm);
    }

    [Fact]
    public void BuildPreserving_reuses_id_and_created_sets_fresh_updated()
    {
        var original = VaultFrontmatter.Build("topic", "Pia", "product");
        var existing = _parser.Parse(original + "\nbody\n");
        var originalId = existing.Frontmatter["id"];
        var originalCreated = existing.Frontmatter["created"];

        var rebuilt = VaultFrontmatter.BuildPreserving(existing, "Pia", "regulation");
        var reparsed = _parser.Parse(rebuilt + "\nbody\n");

        Assert.Contains("pia: managed\n", rebuilt);
        Assert.Contains("schemaVersion: 1\n", rebuilt);
        Assert.Equal("topic", reparsed.Frontmatter["type"]);
        Assert.Equal("regulation", reparsed.Frontmatter["category"]);
        Assert.Equal(originalId, reparsed.Frontmatter["id"]);
        Assert.Equal(originalCreated, reparsed.Frontmatter["created"]);
        Assert.True(
            string.CompareOrdinal(reparsed.Frontmatter["updated"], originalCreated) >= 0,
            "updated must be >= created");
    }

    [Fact]
    public void BuildPreserving_mints_fresh_id_when_no_existing()
    {
        var rebuilt = VaultFrontmatter.BuildPreserving(null, "New Topic", "concept");
        var reparsed = _parser.Parse(rebuilt + "\nbody\n");

        Assert.Equal("topic", reparsed.Frontmatter["type"]);
        Assert.Equal("concept", reparsed.Frontmatter["category"]);
        Assert.False(string.IsNullOrWhiteSpace(reparsed.Frontmatter["id"]));
    }
}
