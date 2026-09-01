using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Save-side tests for <see cref="VaultCharterService"/> — the charter the user approves is what
/// grounds every later topic-discovery call, so what lands on disk has to round-trip exactly.
/// </summary>
public class VaultCharterServiceSaveTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly VaultStore _store;
    private readonly VaultCharterService _charter;

    public VaultCharterServiceSaveTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-charter-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _store = new VaultStore(_tmpDir, new MarkdownVaultParser());
        _charter = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Saved_charter_reads_back_verbatim()
    {
        const string body = "This vault is about the Pia desktop client: its architecture decisions, "
            + "the customers it is sold to, and the regulations that constrain it.";

        await _charter.SaveCharterAsync(body);

        Assert.Equal(body, await _charter.GetCharterAsync());
    }

    [Fact]
    public async Task Saving_twice_keeps_the_page_identity()
    {
        await _charter.SaveCharterAsync("First.");
        var first = await _store.ReadAsync(VaultCharterService.CharterPath);

        await _charter.SaveCharterAsync("Second.");
        var second = await _store.ReadAsync(VaultCharterService.CharterPath);

        Assert.Equal(first!.Frontmatter["id"], second!.Frontmatter["id"]);
        Assert.Equal(first.Frontmatter["created"], second.Frontmatter["created"]);
        Assert.Equal("Second.", await _charter.GetCharterAsync());
    }

    // An empty charter must mean "no grounding at all", not a page whose body is whitespace —
    // the extraction prompt drops the charter block entirely when the body is blank.
    [Fact]
    public async Task Clearing_the_charter_removes_the_page()
    {
        await _charter.SaveCharterAsync("Something.");

        await _charter.SaveCharterAsync("   ");

        Assert.Null(await _store.ReadAsync(VaultCharterService.CharterPath));
        Assert.Equal(string.Empty, await _charter.GetCharterAsync());
    }

    // A drafted charter is model output — it can easily open with a colon phrase or a quote.
    [Fact]
    public async Task A_charter_whose_first_line_would_break_yaml_still_reads_back()
    {
        const string body = "Scope: everything about \"Acme GmbH\" #1 supplier — and nothing else.";

        await _charter.SaveCharterAsync(body);

        Assert.Equal(body, await _charter.GetCharterAsync());
    }
}
