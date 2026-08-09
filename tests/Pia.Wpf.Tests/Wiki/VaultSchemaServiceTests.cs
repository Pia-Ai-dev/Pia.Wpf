using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

public class VaultSchemaServiceTests : IDisposable
{
    private readonly string _root;
    private readonly MarkdownVaultParser _parser = new();

    public VaultSchemaServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pia-schema-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private VaultSchemaService NewSchema() => new(
        new VaultStore(_root, _parser),
        new VaultPathProvider(_root),
        NullLogger<VaultSchemaService>.Instance);

    [Fact]
    public async Task First_run_creates_AGENTS_with_valid_frontmatter_and_sources_directory()
    {
        var schema = NewSchema();

        await schema.EnsureScaffoldingAsync();

        Assert.True(Directory.Exists(Path.Combine(_root, "sources")));

        var store = new VaultStore(_root, _parser);
        var doc = await store.ReadAsync("memory/AGENTS.md");
        Assert.NotNull(doc);

        Assert.Equal("managed", doc!.Frontmatter["pia"]);
        Assert.Equal("note", doc.Frontmatter["type"]);
        Assert.Equal("1", doc.Frontmatter["schemaVersion"]);
        Assert.True(doc.Frontmatter.TryGetValue("id", out var id) && Guid.TryParse(id, out _));
        Assert.True(doc.Frontmatter.ContainsKey("created"));
        Assert.True(doc.Frontmatter.ContainsKey("updated"));
    }

    [Fact]
    public async Task Existing_AGENTS_is_preserved_byte_identical()
    {
        var custom =
            "---\n" +
            "pia: managed\n" +
            "id: 6f9c0b3e-7c1a-4f2e-9a8b-000000000001\n" +
            "type: note\n" +
            "title: Conventions (AGENTS)\n" +
            "created: 2026-06-07T09:00:00Z\n" +
            "updated: 2026-06-07T09:30:00Z\n" +
            "schemaVersion: 1\n" +
            "---\n" +
            "# My hand-written conventions\n" +
            "\n" +
            "Do not touch this file, it is co-evolved by a human.\n";

        var memoryDir = Path.Combine(_root, "memory");
        Directory.CreateDirectory(memoryDir);
        var agentsPath = Path.Combine(memoryDir, "AGENTS.md");
        await File.WriteAllTextAsync(agentsPath, custom, TestContext.Current.CancellationToken);

        var schema = NewSchema();
        await schema.EnsureScaffoldingAsync();

        var after = await File.ReadAllTextAsync(agentsPath, TestContext.Current.CancellationToken);
        Assert.Equal(custom, after);
    }
}
