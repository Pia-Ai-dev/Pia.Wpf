using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Tests for <see cref="VaultTemplateService"/>: resolves the per-category page template out of
/// <c>memory/templates.md</c> over a real temp <see cref="VaultStore"/>. Every "no contract" path
/// (missing file, missing section, blank section) must yield <c>""</c>, because that is what keeps
/// synthesis free-form for a vault whose templates were never edited.
/// </summary>
public class VaultTemplateServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly VaultStore _store;

    public VaultTemplateServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-templates-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        var vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(vaultRoot);
        _store = new VaultStore(vaultRoot, new MarkdownVaultParser());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmpDir))
            {
                Directory.Delete(_tmpDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp dir.
        }
    }

    private VaultTemplateService NewService()
        => new(_store, NullLogger<VaultTemplateService>.Instance);

    private Task SeedTemplatesAsync(string body)
        => _store.WriteAtomicAsync(
            VaultTemplateService.TemplatesPath,
            VaultFrontmatter.Build("note", "Page templates") + "\n" + body);

    [Fact]
    public async Task Returns_the_section_body_for_the_category()
    {
        await SeedTemplatesAsync(
            "## person\n- personnel number: <value>\n- date of birth: <YYYY-MM-DD>\n\n## product\n- vendor: <value>\n");

        var template = await NewService().GetTemplateAsync("person");

        Assert.Contains("personnel number", template);
        Assert.Contains("date of birth", template);
        Assert.DoesNotContain("vendor", template); // the sibling section must not bleed in
    }

    [Theory]
    [InlineData("Person")]
    [InlineData("PERSON")]
    [InlineData("  person  ")]
    public async Task Category_match_is_slug_based(string category)
    {
        await SeedTemplatesAsync("## person\n- role: <value>\n");

        Assert.Contains("role", await NewService().GetTemplateAsync(category));
    }

    [Fact]
    public async Task Empty_when_the_templates_file_is_absent()
        => Assert.Equal(string.Empty, await NewService().GetTemplateAsync("person"));

    [Fact]
    public async Task Empty_when_the_category_has_no_section()
    {
        await SeedTemplatesAsync("## person\n- role: <value>\n");

        Assert.Equal(string.Empty, await NewService().GetTemplateAsync("organization"));
    }

    [Fact]
    public async Task Empty_when_the_section_is_blank()
    {
        // The seeded file ships every category with an empty section; those must stay free-form.
        await SeedTemplatesAsync("## person\n- role: <value>\n\n## organization\n\n## product\n");

        Assert.Equal(string.Empty, await NewService().GetTemplateAsync("organization"));
    }

    [Fact]
    public async Task Empty_for_a_null_or_blank_category()
    {
        await SeedTemplatesAsync("## person\n- role: <value>\n");
        var svc = NewService();

        Assert.Equal(string.Empty, await svc.GetTemplateAsync(null));
        Assert.Equal(string.Empty, await svc.GetTemplateAsync("   "));
    }

    [Fact]
    public async Task Html_comment_guidance_never_reaches_the_prompt()
    {
        await SeedTemplatesAsync("## person\n<!-- keep this short -->\n- role: <value>\n");

        var template = await NewService().GetTemplateAsync("person");

        Assert.DoesNotContain("keep this short", template);
        Assert.Contains("role", template);
    }
}
