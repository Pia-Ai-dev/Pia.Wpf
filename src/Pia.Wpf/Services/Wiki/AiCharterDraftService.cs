using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Wiki;

/// <summary>
/// Drafts a vault charter from the documents already in <c>sources/</c>. The charter decides which
/// topics earn a page, and it is the one ingest lever with no UI — an empty box was judged unlikely
/// to get filled in, so Pia proposes the text and the user edits it.
///
/// <para>Drafting only: nothing is written here. <see cref="IVaultCharterService.SaveCharterAsync"/>
/// persists what the user actually approves.</para>
/// </summary>
public sealed class AiCharterDraftService : ICharterDrafter
{
    // A charter describes the vault, so breadth across sources beats depth in any one of them.
    private const int MaxSources = 12;
    private const int MaxCharsPerSource = 2000;

    private readonly IAiClientService _aiClient;
    private readonly IProviderService _providers;
    private readonly IVaultSourcesService _sources;
    private readonly IVaultStore _store;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<AiCharterDraftService> _logger;

    public AiCharterDraftService(
        IAiClientService aiClient,
        IProviderService providers,
        IVaultSourcesService sources,
        IVaultStore store,
        Func<ITokenMapService> tokenMapFactory,
        ISettingsService settings,
        ILogger<AiCharterDraftService> logger)
    {
        _aiClient = aiClient;
        _providers = providers;
        _sources = sources;
        _store = store;
        _tokenMapFactory = tokenMapFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> DraftAsync(CancellationToken ct = default)
    {
        var provider = await _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        if (provider is null)
        {
            _logger.LogInformation("Charter draft skipped: no provider configured");
            return string.Empty;
        }

        var excerpts = await ReadExcerptsAsync(ct);
        if (excerpts.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append(
            "Below are excerpts from the documents a person has collected in their knowledge base. " +
            "Write that knowledge base's CHARTER: a short statement of what it is about and, " +
            "crucially, which kinds of things deserve their own wiki page in it and which do not. " +
            "It is read by an extraction step that decides, for each new document, which topics to " +
            "create pages for — so be concrete about the subject matter and name the kinds of " +
            "entities that matter here. 3 to 6 sentences, plain prose, no heading, no preamble, no " +
            "bullet list. Write it in the language the documents are written in.\n\n");

        foreach (var (name, text) in excerpts)
        {
            sb.Append("--- DOCUMENT: ").Append(name).Append(" ---\n").Append(text).Append("\n\n");
        }

        var result = await SendWithReidentificationAsync(provider, sb.ToString(), ct);
        var draft = result.Text.Trim();
        _logger.SensitiveDebug("Charter draft produced {Length} chars", draft.Length);
        return draft;
    }

    // Same reasoning as AiIngestSynthesisService: this runs off no chat turn, so nothing has published
    // a TokenMapAmbient. The excerpts are meeting transcripts, and the model REWRITES them into prose,
    // where it readily mangles a placeholder ("[person-1]") past the decorator's strict detokenize —
    // and the charter is written to a synced page. Publish this run's map, then re-identify loosely.
    private async Task<AiCompletionResult> SendWithReidentificationAsync(
        AiProvider provider, string prompt, CancellationToken ct)
    {
        var settings = await _settings.GetSettingsAsync();
        if (!settings.Privacy.TokenizationEnabled)
        {
            return await _aiClient.SendRequestAsync(provider, prompt, ct, mode: nameof(WindowMode.Assistant));
        }

        var tokenMap = _tokenMapFactory();
        try
        {
            await tokenMap.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize token map for the charter draft");
        }

        var previousAmbient = TokenMapAmbient.Current;
        TokenMapAmbient.Current = tokenMap;
        AiCompletionResult result;
        try
        {
            result = await _aiClient.SendRequestAsync(provider, prompt, ct, mode: nameof(WindowMode.Assistant));
        }
        finally
        {
            TokenMapAmbient.Current = previousAmbient;
        }

        return result with { Text = tokenMap.DetokenizeLoose(result.Text) };
    }

    private async Task<List<(string Name, string Text)>> ReadExcerptsAsync(CancellationToken ct)
    {
        var excerpts = new List<(string, string)>();
        foreach (var source in await _sources.ListSourcesAsync())
        {
            if (excerpts.Count == MaxSources)
            {
                break;
            }

            if (!source.IsText)
            {
                continue;
            }

            var absolute = Path.Combine(_store.Root, source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute))
            {
                continue;
            }

            try
            {
                var text = await File.ReadAllTextAsync(absolute, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    excerpts.Add((source.Name,
                        text.Length <= MaxCharsPerSource ? text : text[..MaxCharsPerSource]));
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Charter draft skipped an unreadable source");
            }
        }

        return excerpts;
    }
}
