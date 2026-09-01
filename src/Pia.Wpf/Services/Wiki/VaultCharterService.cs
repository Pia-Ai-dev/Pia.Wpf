using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Models.Vault;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Resolves the vault "charter" — a short statement of what this vault is about — fed into ingest
/// extraction so only topics notable to the vault's purpose become pages. Resolution order:
/// memory/charter.md → empty. (memory/profile.md is deliberately NOT a fallback: it is the user's
/// personal profile, and feeding it here caused personal facts to bleed into topic pages.) Returns
/// the page BODY (preamble + sections), not frontmatter. Never throws; a missing/empty vault yields "".
/// </summary>
public sealed class VaultCharterService : IVaultCharterService
{
    private readonly IVaultStore _store;
    private readonly ILogger<VaultCharterService> _logger;

    public VaultCharterService(IVaultStore store, ILogger<VaultCharterService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public const string CharterPath = "memory/charter.md";

    public async Task<string> GetCharterAsync()
    {
        foreach (var path in new[] { CharterPath })
        {
            var doc = await _store.ReadAsync(path);
            var body = BodyOf(doc);
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Write the charter the user approved, or delete the page when they clear it — an empty
    /// charter must mean "no grounding", not a page whose body is whitespace.
    /// </summary>
    public async Task SaveCharterAsync(string body)
    {
        var trimmed = (body ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            await _store.DeleteAsync(CharterPath);
            _logger.LogInformation("Vault charter cleared");
            return;
        }

        var existing = await _store.ReadAsync(CharterPath);
        await _store.WriteAtomicAsync(
            CharterPath,
            VaultFrontmatter.BuildPreservingNote(existing, "Charter") + "\n" + trimmed + "\n");
        _logger.LogInformation("Vault charter saved ({Length} chars)", trimmed.Length);
    }

    private static string BodyOf(VaultDocument? doc)
    {
        if (doc is null)
        {
            return string.Empty;
        }

        var parts = new List<string> { doc.Preamble };
        parts.AddRange(doc.Sections.Select(s => "## " + s.Heading + "\n" + s.Body));
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
