using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;

namespace Pia.Services.Wiki;

/// <summary>
/// Scaffolds the vault layout of spec §1 on a fresh install: the immutable <c>sources/</c> directory
/// (Pia reads, never edits) and the human-editable Schema <c>memory/AGENTS.md</c>. Renamed from the
/// plan's <c>VaultSchemaDoc</c> to the <c>Service</c> suffix so it satisfies <c>NamingConventionTests</c>
/// without an allowlist change, and is a concrete singleton (no interface) so it does not trip
/// <c>DiRegistrationTests</c>.
///
/// <para><c>AGENTS.md</c> is written ONLY when it does not yet exist; once present it is co-evolved by
/// the human and Pia leaves it COMPLETELY UNTOUCHED. The seeded body is a concise restatement of the
/// format conventions (§4, §5, §7) so an editor (human or model) opening the vault has the contract at
/// hand without re-reading the spec.</para>
/// </summary>
public sealed class VaultSchemaService
{
    private const string AgentsPath = "memory/AGENTS.md";
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    private readonly IVaultStore _store;
    private readonly VaultPathProvider _paths;
    private readonly ILogger<VaultSchemaService> _logger;

    public VaultSchemaService(IVaultStore store, VaultPathProvider paths, ILogger<VaultSchemaService> logger)
    {
        _store = store;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Ensure the §1 scaffolding exists: the <c>sources/</c> directory, the <c>memory/</c> directory, and
    /// a default <c>memory/AGENTS.md</c> if one is not already present. Never edits or deletes anything
    /// under <c>sources/</c>, and never overwrites an existing <c>AGENTS.md</c>. Idempotent.
    /// </summary>
    public async Task EnsureScaffoldingAsync()
    {
        // sources/ is the immutable RAW layer — only ever create the directory, never touch contents.
        var sourcesDir = Path.Combine(_paths.VaultRoot, "sources");
        Directory.CreateDirectory(sourcesDir);
        _logger.SensitiveDebug("Ensured vault sources directory {Dir}", sourcesDir);

        // memory/ is Pia's tree; ensure it exists so a bare AGENTS.md write below has a home.
        var memoryDir = Path.Combine(_paths.VaultRoot, "memory");
        Directory.CreateDirectory(memoryDir);

        // AGENTS.md is human-editable and co-evolved: write the default ONLY when absent, never overwrite.
        var existing = await _store.ReadAsync(AgentsPath);
        if (existing is not null)
        {
            _logger.SensitiveDebug("AGENTS.md already present; leaving it untouched at {Path}", AgentsPath);
            return;
        }

        await _store.WriteAtomicAsync(AgentsPath, BuildDefaultAgents());
        _logger.SensitiveDebug("Wrote default AGENTS.md at {Path}", AgentsPath);
    }

    // Default Schema: frontmatter (pia: managed; fresh lowercase-canonical id; type note; schemaVersion 1)
    // plus a body seeded from the format spec — the canonical type set (§7), the section convention (§3),
    // the `- key: value` record format (§4), wikilinks (§5), and the memory/ vs sources/ ownership rule (§1).
    private static string BuildDefaultAgents()
    {
        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               "type: note\n" +
               "title: Conventions (AGENTS)\n" +
               $"created: {now}\n" +
               $"updated: {now}\n" +
               "schemaVersion: 1\n" +
               "---\n" +
               "# Conventions (AGENTS)\n" +
               "\n" +
               "This vault follows the Pia memory-vault format (schemaVersion 1). This file is\n" +
               "human-editable and co-evolved; Pia never overwrites it once it exists.\n" +
               "\n" +
               "## Ownership\n" +
               "- Pia writes ONLY under `memory/`. Every file Pia writes carries `pia: managed`.\n" +
               "- Pia READS `sources/` (the raw layer) but never edits or deletes anything there.\n" +
               "- Your own `.md` files anywhere in the vault are read-only to Pia, indexed for recall.\n" +
               "\n" +
               "## Page types\n" +
               "The canonical `type` set is exactly six values:\n" +
               "- `personal_profile` — structured, `memory/profile.md`.\n" +
               "- `contact_list` — structured, `memory/contacts.md` (one `##` section per person).\n" +
               "- `preference` — structured, `memory/preferences.md`.\n" +
               "- `note` — freeform, one file per item under `memory/notes/`.\n" +
               "- `project` — freeform, one file per item under `memory/projects/`.\n" +
               "- `topic` — compiled wiki entity, one file per entity under `memory/topics/`.\n" +
               "\n" +
               "## Sections\n" +
               "Within a document, level-2 headings (`## Heading`) start sections. A section's identity\n" +
               "is its slug (the heading, lowercased and hyphenated). Levels other than `##` are body.\n" +
               "\n" +
               "## Record format\n" +
               "Structured records and topic pages use `- key: value` bullet lines for field-level merge,\n" +
               "with free prose allowed below the bullets:\n" +
               "\n" +
               "    ## John Smith\n" +
               "    - email: john@example.com\n" +
               "    - company: Acme\n" +
               "\n" +
               "    Met at the Q2 offsite.\n" +
               "\n" +
               "## Wikilinks\n" +
               "Link other pages with `[[file]]`, sections with `[[file#Heading]]`. The target is a\n" +
               "vault-root-relative path without the `.md` extension (e.g. `[[topics/acme]]`,\n" +
               "`[[contacts#John Smith]]`). Pia never rewrites your existing links.\n";
    }
}
