using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure.Vault;
using Pia.Services.Wiki;
using Xunit;

namespace Pia.Tests.Wiki;

/// <summary>
/// Tests for <see cref="VaultCharterService"/>: resolves the vault charter body (charter.md → empty)
/// over a real temp <see cref="VaultStore"/>, mirroring the setup shape of
/// <see cref="IngestServiceTests"/>. profile.md is deliberately NOT a fallback.
/// </summary>
public class VaultCharterServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly MarkdownVaultParser _parser = new();
    private readonly VaultStore _store;

    public VaultCharterServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-charter-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _store = new VaultStore(_vaultRoot, _parser);
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

    [Fact]
    public async Task Returns_charter_when_present()
    {
        await _store.WriteAtomicAsync("memory/charter.md",
            VaultFrontmatter.Build("note", "Charter") + "\nPia is a privacy-first AI assistant.");
        var svc = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);
        Assert.Contains("privacy-first", await svc.GetCharterAsync());
    }

    [Fact]
    public async Task Empty_when_no_charter_file()
    {
        var svc = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);
        Assert.Equal(string.Empty, await svc.GetCharterAsync()); // no charter.md present
    }

    [Fact]
    public async Task Does_not_fall_back_to_profile()
    {
        // profile.md is the user's personal profile; feeding it into ingest leaked personal facts into
        // topic pages. Only charter.md counts — a lone profile.md must yield an empty charter.
        await _store.WriteAtomicAsync("memory/profile.md",
            VaultFrontmatter.Build("personal_profile", "Profile") + "\nOwner is a solo dev.");
        var svc = new VaultCharterService(_store, NullLogger<VaultCharterService>.Instance);
        Assert.Equal(string.Empty, await svc.GetCharterAsync());
    }
}
