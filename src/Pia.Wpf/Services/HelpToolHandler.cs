using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Services.Help;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>Answers questions about Pia itself, from the bundled user guide and this install's settings.</summary>
public class HelpToolHandler : IHelpToolHandler
{
    private const int SearchLimit = 5;

    // A miss here is usually a model that translated nothing, or one that reached for this tool looking
    // for a web search. Spend the empty envelope saying what was actually searched.
    private const string EmptySearchNote =
        "Nothing matched. This searches Pia's own user guide, which is written in ENGLISH — retry with " +
        "English keywords (for example \"text to speech voice\" rather than a German phrasing), or call " +
        "pia_help with no arguments for the table of contents.";

    private const string UnknownReferenceNote =
        "No such page. Pass a reference from a pia_help search hit, or call pia_help with no arguments " +
        "for the table of contents.";

    private const string CorpusMissingNote =
        "The built-in user guide is not available in this build. Say you cannot look it up rather than " +
        "guessing, and point the user at https://docs.pia-ai.de/wpf/.";

    private readonly HelpSearchService _search;
    private readonly HelpSettingsResolver _settings;
    private readonly HelpLabelResolver _labelResolver;
    private readonly ILogger<HelpToolHandler> _logger;

    public HelpToolHandler(
        HelpSearchService search,
        HelpSettingsResolver settings,
        HelpLabelResolver labelResolver,
        ILogger<HelpToolHandler> logger)
    {
        _search = search;
        _settings = settings;
        _labelResolver = labelResolver;
        _logger = logger;
    }

    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(PiaHelpSchema, "pia_help"),
        AIFunctionFactory.Create(PiaSettingsSchema, "pia_settings"),
    ];

    public async Task<object?> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("HelpToolHandler dispatching: {ToolName}", toolCall.Name);
        var args = toolCall.Arguments ?? new Dictionary<string, object?>();

        return toolCall.Name switch
        {
            "pia_help" => HandleHelp(args),
            "pia_settings" => await HandleSettingsAsync(args, cancellationToken),
            _ => $"Unknown tool: {toolCall.Name}",
        };
    }

    private string HandleHelp(IDictionary<string, object?> args)
    {
        if (!_search.IsAvailable) return CorpusMissingNote;

        var reference = GetOptionalStringArg(args, "reference");
        if (!string.IsNullOrWhiteSpace(reference))
        {
            _logger.SensitiveDebug("pia_help reading {Reference}", reference);
            var section = _search.Read(reference);
            return section is null
                ? UnknownReferenceNote
                : WithGlossary(FormatSection(section), LabelCandidates(section));
        }

        var query = GetOptionalStringArg(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return FormatTableOfContents();

        _logger.SensitiveDebug("pia_help searching {Query}", query);
        var hits = _search.Search(query, SearchLimit);
        _logger.LogInformation("pia_help returned {Count} hit(s)", hits.Count);

        if (hits.Count == 0) return EmptySearchNote;

        // A snippet cuts labels mid-path, so gloss the sections the hits point at.
        var candidates = hits
            .Select(h => _search.Read(h.Reference))
            .OfType<HelpSection>()
            .SelectMany(LabelCandidates);
        return WithGlossary(FormatHits(query, hits), candidates);
    }

    private static IEnumerable<string> LabelCandidates(HelpSection section) =>
        HelpLabelResolver.Candidates(section.Body).Append(section.Heading);

    private string WithGlossary(string text, IEnumerable<string> candidates)
    {
        var labels = _labelResolver.For(candidates);
        if (labels.Count == 0) return text;

        var sb = new StringBuilder(text);
        sb.Append("\n\nThe user's screen is in ").Append(HelpSettingsResolver.LanguageName(_labelResolver.Language))
          .Append(", where the guide's labels read as below — quote the right-hand side, never your own translation:\n");
        foreach (var label in labels)
        {
            sb.Append("- ").Append(label.English).Append(" = ").Append(label.Localized).Append('\n');
        }
        return sb.ToString();
    }

    private async Task<string> HandleSettingsAsync(IDictionary<string, object?> args, CancellationToken ct)
    {
        var area = HelpSettingsResolver.Normalize(GetOptionalStringArg(args, "area"));
        var rows = await _settings.DescribeAsync(area, ct);
        _logger.LogInformation("pia_settings returned {Count} row(s) for area {Area}", rows.Count, area);

        var sb = new StringBuilder();
        sb.Append("How this installation is configured");
        if (area != HelpSettingsResolver.AllAreas) sb.Append(" (").Append(area).Append(')');
        sb.Append(". The bracketed path is written in the user's own interface language — quote it as-is.\n\n");

        foreach (var row in rows)
        {
            sb.Append("- ").Append(row.Label).Append(": ").Append(row.Value)
              .Append("  [").Append(row.Path).Append("]\n");
        }

        if (area == HelpSettingsResolver.AllAreas)
        {
            sb.Append("\nNarrow this with area: ")
              .Append(string.Join(", ", HelpSettingsResolver.Areas)).Append('.');
        }
        return sb.ToString();
    }

    private string FormatTableOfContents()
    {
        var sb = new StringBuilder();
        sb.Append("The Pia user guide, ").Append(_search.PageCount)
          .Append(" pages. Read one with pia_help(reference=...), or search it with pia_help(query=...).\n\n");

        foreach (var page in _search.Pages.OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            sb.Append(page.Path).Append(" — ").Append(page.Title).Append('\n');
        }
        return sb.ToString();
    }

    private static string FormatHits(string query, IReadOnlyList<HelpHit> hits)
    {
        var sb = new StringBuilder();
        sb.Append(hits.Count).Append(" result(s) in the Pia user guide for \"").Append(query).Append("\".\n\n");

        var index = 1;
        foreach (var hit in hits)
        {
            sb.Append(index++).Append(". ").Append(hit.PageTitle);
            if (hit.Heading.Length > 0) sb.Append(" — ").Append(hit.Heading);
            sb.Append("\n   reference: ").Append(hit.Reference)
              .Append("\n   ").Append(hit.Snippet)
              .Append("\n   ").Append(hit.Url).Append("\n\n");
        }

        sb.Append("Call pia_help(reference=...) for a hit's full text. ")
          .Append("These pages describe the current release; if the user's screen disagrees, say so.");
        return sb.ToString();
    }

    private static string FormatSection(HelpSection section)
    {
        var sb = new StringBuilder();
        sb.Append(section.PageTitle);
        if (section.Heading.Length > 0) sb.Append(" — ").Append(section.Heading);
        sb.Append("\nreference: ").Append(section.Reference)
          .Append('\n').Append(section.Url).Append("\n\n")
          .Append(section.Body);
        return sb.ToString();
    }

    private static string? GetOptionalStringArg(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
        }

        var text = value.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    // Schema methods — the parameter signature and [Description] attributes ARE the tool metadata for
    // AIFunctionFactory. The body is never invoked (dispatch is by tool name in HandleToolCallAsync).
    [Description("Answer a question about Pia itself: its features, settings, screens, modes, and what you can or cannot do. Never guess or web-search these.")]
    private static string PiaHelpSchema(
        [Description("What to look up, as ENGLISH keywords — the guide is English. Omit to get the table of contents.")] string? query = null,
        [Description("A reference from a previous hit, to read that section in full.")] string? reference = null) => "";

    [Description("Report how THIS installation is configured, with the path to change each value in the user's own interface language. Use whenever the answer depends on the user's current settings.")]
    private static string PiaSettingsSchema(
        [Description("One of: language, application, hotkeys, speech, privacy, providers, optimize, assistant, personas, tools, meetings, agent, sync, about. Omit for all.")] string? area = null) => "";
}
