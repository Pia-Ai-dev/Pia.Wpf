using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultReferenceTests
{
    [Fact]
    public void Parse_path_with_heading_splits_and_slugifies()
    {
        var (path, slug) = VaultReference.Parse("memory/contacts.md#John Smith");
        Assert.Equal("memory/contacts.md", path);
        Assert.Equal("john-smith", slug);
    }

    [Fact]
    public void Parse_bare_path_has_null_slug()
    {
        var (path, slug) = VaultReference.Parse("memory/notes/x.md");
        Assert.Equal("memory/notes/x.md", path);
        Assert.Null(slug);
    }

    [Fact]
    public void Parse_splits_on_first_hash_only()
    {
        // Only the FIRST '#' separates path from heading; later '#' chars belong to the heading text.
        var (path, slug) = VaultReference.Parse("memory/notes/x.md#C# Tips");
        Assert.Equal("memory/notes/x.md", path);
        Assert.Equal("c-tips", slug);
    }
}
