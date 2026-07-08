using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

public class SourcesProvenanceTests
{
    [Fact]
    public void ReadSourceRefs_parses_lf_frontmatter()
    {
        var raw = "---\ntype: topic\nsources: [sources/a.txt, sources/b.md]\n---\nBody.\n";
        var refs = SourcesProvenance.ReadSourceRefs(raw);
        Assert.Equal(["sources/a.txt", "sources/b.md"], refs);
    }

    [Fact]
    public void ReadSourceRefs_parses_crlf_frontmatter_identically()
    {
        var raw = "---\r\ntype: topic\r\nsources: [sources/a.txt, sources/b.md]\r\n---\r\nBody.\r\n";
        var refs = SourcesProvenance.ReadSourceRefs(raw);
        Assert.Equal(["sources/a.txt", "sources/b.md"], refs);
    }

    [Fact]
    public void ReadSourceRefs_returns_empty_without_frontmatter()
    {
        Assert.Empty(SourcesProvenance.ReadSourceRefs("No frontmatter here.\n"));
        Assert.Empty(SourcesProvenance.ReadSourceRefs("---\nunclosed\n"));
    }
}
