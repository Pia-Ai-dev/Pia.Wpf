using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pia.Services.Search;

namespace Pia.Services.Help;

/// <summary>
/// Searches the bundled Pia user guide. The corpus is an embedded resource refreshed by
/// scripts/Update-HelpCorpus.ps1; it is loaded and indexed on first use, never at startup.
/// </summary>
public class HelpSearchService : IDisposable
{
    private const string ResourceName = "Pia.HelpCorpus";
    private const int MaxSectionChars = 6000;

    // What a user types versus what the guide is written in. Prefix matching only closes this gap in
    // one direction - "agentic*" never reaches "agent" - so the common terms are mapped by hand.
    private static readonly Dictionary<string, string[]> QueryAliases = new(StringComparer.Ordinal)
    {
        ["agentic"] = ["agent", "run", "plan"],
        ["agents"] = ["agent"],
        ["autonomous"] = ["agent", "autonomy"],
        ["tts"] = ["speech", "voice", "piper", "aloud"],
        ["stt"] = ["speech", "transcription", "whisper", "parakeet", "dictation"],
        ["dictation"] = ["speech", "transcription", "recording"],
        ["aloud"] = ["speech", "voice"],
        ["speak"] = ["speech", "voice"],
        ["speaking"] = ["speech", "voice"],
        ["tone"] = ["persona", "output", "format", "style"],
        ["style"] = ["persona", "output", "format"],
        ["wording"] = ["persona", "output", "format"],
        ["personality"] = ["persona"],
        ["shortcut"] = ["hotkey"],
        ["shortcuts"] = ["hotkey"],
        ["folder"] = ["directory", "files"],
        ["backup"] = ["sync", "recovery", "encryption"],
        ["plugin"] = ["tool", "mcp"],
        ["plugins"] = ["tool", "mcp"],
        ["schedule"] = ["routine", "scheduled"],
        ["scheduling"] = ["routine", "scheduled"],
        ["answer"] = ["persona", "output", "format"],
        ["answers"] = ["persona", "output", "format"],
        ["respond"] = ["persona", "output", "format"],
        ["response"] = ["persona", "output", "format"],
        ["provider"] = ["provider", "model", "api"],
        ["providers"] = ["provider", "model", "api"],
    };

    // Question scaffolding carries no signal but plenty of frequency, and an OR query lets it outvote
    // the one word that actually names the feature.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "about", "all", "am", "an", "and", "any", "are", "as", "at", "be", "been", "but", "by",
        "can", "could", "did", "do", "does", "for", "from", "get", "happen", "happens", "have",
        "how", "i", "if", "in",
        "is", "it", "its", "let", "make", "me", "my", "need", "not", "of", "on", "one", "or", "out",
        "pia", "please", "second", "should", "so", "some", "that", "the", "their", "them", "then",
        "there", "these", "they", "this", "to", "up", "want", "was", "way", "we", "what", "when",
        "where", "which", "who", "why", "will", "with", "would", "you", "your",
    };

    private const string SearchSql = """
        SELECT Ref, snippet(HelpFts, 3, '', '', '...', 28)
        FROM HelpFts
        WHERE HelpFts MATCH @Match
        ORDER BY bm25(HelpFts, 0.0, 12.0, 6.0, 1.0)
        LIMIT @Limit;
        """;

    // Porter stemming so "answers" reaches "answer"; without it the guide's own plurals miss.
    private const string CreateSql = """
        CREATE VIRTUAL TABLE HelpFts USING fts5(
            Ref UNINDEXED,
            Title,
            Heading,
            Body,
            tokenize='porter unicode61'
        );
        """;

    // One connection serves every window and every background run, and a SqliteConnection cannot
    // carry two commands at once.
    private readonly Lock _searchGate = new();
    private readonly Lazy<CorpusIndex?> _index;
    private bool _disposed;

    public HelpSearchService()
    {
        _index = new Lazy<CorpusIndex?>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _index.Value is not null;

    public int PageCount => _index.Value?.Pages.Count ?? 0;

    /// <summary>The docs commit the bundled snapshot was taken from.</summary>
    public string SourceCommit => _index.Value?.SourceCommit ?? "unknown";

    public IReadOnlyList<HelpPage> Pages => _index.Value is { } index ? [.. index.Pages.Values] : [];

    public IReadOnlyList<HelpHit> Search(string query, int limit)
    {
        var index = _index.Value;
        if (index is null) return [];

        var match = BuildMatchExpression(query);
        if (match.Length == 0) return [];

        var hits = new List<HelpHit>();
        lock (_searchGate)
        {
            using var command = index.Connection.CreateCommand();
            command.CommandText = SearchSql;
            command.Parameters.AddWithValue("@Match", match);
            command.Parameters.AddWithValue("@Limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var reference = reader.GetString(0);
                if (!index.Sections.TryGetValue(reference, out var section)) continue;

                var snippet = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                hits.Add(new HelpHit(reference, section.PageTitle, section.Heading, Flatten(snippet), section.Url));
            }
        }
        return hits;
    }

    /// <summary>Resolves a page path or a page#section reference; null when neither exists.</summary>
    public HelpSection? Read(string reference)
    {
        var index = _index.Value;
        if (index is null) return null;

        var key = reference.Trim().TrimStart('/');
        if (index.Sections.TryGetValue(key, out var section)) return Truncate(section);
        if (index.Pages.TryGetValue(key, out var page)) return Truncate(WholePage(page));

        // A model that saw "guides/speech#selecting-a-voice" often asks back for "speech" or "guides/speech.md".
        var relaxed = key.Split('#')[0].Replace(".md", string.Empty, StringComparison.OrdinalIgnoreCase);
        var candidate = index.Pages.Keys.FirstOrDefault(p =>
            p.Equals(relaxed, StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith("/" + relaxed, StringComparison.OrdinalIgnoreCase));

        return candidate is null ? null : Truncate(WholePage(index.Pages[candidate]));
    }

    internal static string BuildMatchExpression(string query)
    {
        var tokens = FtsQueryBuilder.Tokenize(query);

        // Drop the scaffolding, but never everything: a query that is nothing but stop words still gets
        // to match rather than silently returning the whole corpus' worth of nothing.
        var meaningful = tokens.Where(t => !StopWords.Contains(t) && t.Length > 1).ToList();
        if (meaningful.Count == 0) meaningful = [.. tokens];

        var expanded = new List<string>(meaningful);
        foreach (var token in meaningful)
        {
            if (QueryAliases.TryGetValue(token, out var aliases)) expanded.AddRange(aliases);
        }
        return FtsQueryBuilder.PrefixOr(expanded);
    }

    private static HelpSection WholePage(HelpPage page) =>
        new(page.Path, page.Path, page.Title, string.Empty, page.Body, page.Url);

    private static HelpSection Truncate(HelpSection section)
    {
        if (section.Body.Length <= MaxSectionChars) return section;
        var tail = section.Body.Length - MaxSectionChars;
        return section with
        {
            Body = section.Body[..MaxSectionChars] + "\n\n[truncated - " + tail + " more characters at " + section.Url + "]"
        };
    }

    private static string Flatten(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static CorpusIndex? Build()
    {
        var pages = LoadPages(out var sourceCommit);
        if (pages is null) return null;

        var sections = new Dictionary<string, HelpSection>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages.Values)
        {
            foreach (var section in HelpSectionParser.Split(page)) sections[section.Reference] = section;
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = CreateSql;
            create.ExecuteNonQuery();
        }

        using (var transaction = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO HelpFts (Ref, Title, Heading, Body) VALUES (@Ref, @Title, @Heading, @Body);";
            var refParam = insert.Parameters.Add("@Ref", SqliteType.Text);
            var titleParam = insert.Parameters.Add("@Title", SqliteType.Text);
            var headingParam = insert.Parameters.Add("@Heading", SqliteType.Text);
            var bodyParam = insert.Parameters.Add("@Body", SqliteType.Text);

            foreach (var section in sections.Values)
            {
                refParam.Value = section.Reference;
                titleParam.Value = section.PageTitle;
                headingParam.Value = section.Heading;
                bodyParam.Value = section.Body;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        return new CorpusIndex(connection, pages, sections, sourceCommit);
    }

    private static Dictionary<string, HelpPage>? LoadPages(out string sourceCommit)
    {
        sourceCommit = "unknown";
        try
        {
            using var stream = typeof(HelpSearchService).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                // A packaging slip that dropped the resource would otherwise turn every how-do-I answer
                // back into a guess; a unit test asserts the resource loads.
                Debug.WriteLine("[HelpSearchService] Embedded help corpus not found.");
                return null;
            }

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(gzip);
            var root = document.RootElement;

            if (root.TryGetProperty("sourceCommit", out var commit) && commit.GetString() is { Length: > 0 } value)
            {
                sourceCommit = value;
            }

            var pages = new Dictionary<string, HelpPage>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in root.GetProperty("pages").EnumerateArray())
            {
                var page = new HelpPage(
                    element.GetProperty("path").GetString() ?? string.Empty,
                    element.GetProperty("title").GetString() ?? string.Empty,
                    element.GetProperty("description").GetString() ?? string.Empty,
                    element.GetProperty("url").GetString() ?? string.Empty,
                    element.GetProperty("body").GetString() ?? string.Empty);

                if (page.Path.Length > 0) pages[page.Path] = page;
            }
            return pages.Count > 0 ? pages : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException)
        {
            Debug.WriteLine("[HelpSearchService] Help corpus could not be read: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_index.IsValueCreated) _index.Value?.Connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <param name="Connection">Held open for the life of the service: closing it drops the in-memory index.</param>
    private sealed record CorpusIndex(
        SqliteConnection Connection,
        Dictionary<string, HelpPage> Pages,
        Dictionary<string, HelpSection> Sections,
        string SourceCommit);
}
