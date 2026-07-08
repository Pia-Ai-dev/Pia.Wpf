using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Models.Vault;

namespace Pia.Services.Wiki;

/// <summary>
/// Resolves the vault "charter" — a short statement of what this vault is about — fed into ingest
/// extraction so only topics notable to the vault's purpose become pages. Resolution order:
/// memory/charter.md → memory/profile.md → empty. Returns the page BODY (preamble + sections), not
/// frontmatter. Never throws; a missing/empty vault yields "".
/// </summary>
public sealed class VaultCharterService
{
    private readonly IVaultStore _store;
    private readonly ILogger<VaultCharterService> _logger;

    public VaultCharterService(IVaultStore store, ILogger<VaultCharterService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<string> GetCharterAsync()
    {
        foreach (var path in new[] { "memory/charter.md", "memory/profile.md" })
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
