using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Models.Vault;

namespace Pia.Services.Wiki;

/// <summary>
/// Resolves the per-category page template — the field contract a synthesized topic page must follow —
/// from <c>memory/templates.md</c>, which holds one <c>## &lt;category&gt;</c> section per category.
/// A missing file, a missing section or a blank one yields <c>""</c>, which the synthesizer treats as
/// "free-form", so a vault whose templates were never edited keeps the original behaviour.
/// </summary>
public sealed class VaultTemplateService
{
    public const string TemplatesPath = "memory/templates.md";

    private readonly IVaultStore _store;
    private readonly ILogger<VaultTemplateService> _logger;

    public VaultTemplateService(IVaultStore store, ILogger<VaultTemplateService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<string> GetTemplateAsync(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return string.Empty;
        }

        VaultDocument? doc;
        try
        {
            doc = await _store.ReadAsync(TemplatesPath);
        }
        catch (Exception ex)
        {
            // Grounding, never a precondition — a malformed templates file must not fail the ingest.
            _logger.LogWarning(ex, "Failed to read the vault page templates; synthesizing free-form");
            return string.Empty;
        }

        if (doc is null)
        {
            return string.Empty;
        }

        // Section identity is its slug, so "Person", "person" and "PERSON" resolve to one template.
        var wanted = VaultSlug.Slugify(category);
        var section = doc.Sections.FirstOrDefault(s => s.Slug == wanted);
        return StripComments(section?.Body ?? string.Empty).Trim();
    }

    // Drop the HTML-comment guidance the seeded file carries so it never reaches the prompt.
    private static string StripComments(string body)
    {
        var kept = body
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("<!--", StringComparison.Ordinal));
        return string.Join('\n', kept);
    }
}
