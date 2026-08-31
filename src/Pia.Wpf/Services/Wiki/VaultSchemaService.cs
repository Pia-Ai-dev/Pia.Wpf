using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;

namespace Pia.Services.Wiki;

/// <summary>
/// Scaffolds the vault layout of spec §1 on a fresh install: the <c>sources/</c> directory (read-only
/// to Pia, except for a corrective <c>update_source</c>), the human-editable Schema
/// <c>memory/AGENTS.md</c>, and the page-template contract <c>memory/templates.md</c>. Renamed from the
/// plan's <c>VaultSchemaDoc</c> to the <c>Service</c> suffix so it satisfies <c>NamingConventionTests</c>
/// without an allowlist change, and is a concrete singleton (no interface) so it does not trip
/// <c>DiRegistrationTests</c>.
///
/// <para>Both documents are written ONLY when they do not yet exist; once present they are co-evolved by
/// the human and Pia leaves them COMPLETELY UNTOUCHED. They are seeded independently, so a vault that
/// predates <c>templates.md</c> still gets one.</para>
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
    /// default <c>memory/AGENTS.md</c> / <c>memory/templates.md</c> documents if they are not already
    /// present. Never edits or deletes anything under <c>sources/</c>, and never overwrites either
    /// document. Idempotent.
    /// </summary>
    public async Task EnsureScaffoldingAsync()
    {
        // sources/ is the immutable RAW layer — only ever create the directory, never touch contents.
        var sourcesDir = Path.Combine(_paths.VaultRoot, "sources");
        Directory.CreateDirectory(sourcesDir);
        _logger.SensitiveDebug("Ensured vault sources directory {Dir}", sourcesDir);

        // memory/ is Pia's tree; ensure it exists so the bare writes below have a home.
        var memoryDir = Path.Combine(_paths.VaultRoot, "memory");
        Directory.CreateDirectory(memoryDir);

        await SeedIfAbsentAsync(AgentsPath, BuildDefaultAgents);
        await SeedIfAbsentAsync(VaultTemplateService.TemplatesPath, BuildDefaultTemplates);
    }

    // Write the seed ONLY when the document is absent. Each document is checked on its own so an
    // existing AGENTS.md never suppresses a missing templates.md.
    private async Task SeedIfAbsentAsync(string path, Func<string> buildDefault)
    {
        if (await _store.ReadAsync(path) is not null)
        {
            _logger.SensitiveDebug("Vault document already present; leaving it untouched at {Path}", path);
            return;
        }

        await _store.WriteAtomicAsync(path, buildDefault());
        _logger.SensitiveDebug("Wrote default vault document at {Path}", path);
    }

    private static string BuildFrontmatter(string title)
    {
        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               "type: note\n" +
               $"title: {title}\n" +
               $"created: {now}\n" +
               $"updated: {now}\n" +
               "schemaVersion: 1\n" +
               "---\n";
    }

    // Default Schema: frontmatter (pia: managed; fresh lowercase-canonical id; type note; schemaVersion 1)
    // plus a body seeded from the format spec — the canonical type set (§7), the section convention (§3),
    // the `- key: value` record format (§4), wikilinks (§5), and the memory/ vs sources/ ownership rule (§1).
    private static string BuildDefaultAgents()
    {
        return BuildFrontmatter("Conventions (AGENTS)") +
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
               "`[[contacts#John Smith]]`). Pia never rewrites your existing links.\n" +
               "\n" +
               "## Steering ingest\n" +
               "Two files steer what ingest produces, and this one is NOT among them — Pia reads it for\n" +
               "your benefit, not the model's:\n" +
               "- `memory/charter.md` — what this knowledge base is about. Decides which topics are\n" +
               "  notable enough to get a page at all. Create it yourself; Pia does not seed it.\n" +
               "- `memory/templates.md` — the field contract each topic page must follow, one section\n" +
               "  per category. Edit a section there to make every page of that category look alike.\n";
    }

    // Default page templates: one `## <category>` section per category the ingest extractor emits.
    // Only `person` ships with a contract; the rest are intentionally empty, which means "free-form".
    private static string BuildDefaultTemplates()
    {
        return BuildFrontmatter("Page templates") +
               "# Page templates\n" +
               "\n" +
               "The field contract each synthesized topic page under `memory/topics/` must follow, one\n" +
               "`## <category>` section per category. This file is yours: Pia writes it once and never\n" +
               "overwrites it.\n" +
               "\n" +
               "An EMPTY section means no contract — pages of that category are written free-form.\n" +
               "Only `person` is filled in below; edit or empty it, and fill in the others as you need.\n" +
               "\n" +
               "Write templates as `- field: value` bullets. A `##` heading INSIDE a template splits the\n" +
               "resulting page into per-section records, and the page can then no longer be edited as a\n" +
               "whole in the Vault view — so prefer bullets unless you want that split.\n" +
               "\n" +
               "## person\n" +
               "- personnel number: <value, or unknown>\n" +
               "- full name: <value, or unknown>\n" +
               "- date of birth: <YYYY-MM-DD, or unknown>\n" +
               "- department: <value, or unknown>\n" +
               "- role: <value, or unknown>\n" +
               "\n" +
               "Then one short paragraph covering anything the fields above do not.\n" +
               "\n" +
               "## organization\n" +
               "\n" +
               "## product\n" +
               "\n" +
               "## concept\n" +
               "\n" +
               "## regulation\n" +
               "\n" +
               "## technology\n" +
               "\n" +
               "## other\n";
    }
}
