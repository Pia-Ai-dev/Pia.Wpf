using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Pia.Tests.Integration.Compaction;

/// <summary>One recovered message, in the shape <c>IAssistantChatService.SearchMessagesAsync</c> returns.</summary>
internal sealed record RecoveredMessage(int Ordinal, string Role, string Snippet);

/// <summary>
/// Arms C, D and E of the recall plan. Each takes the SHIPPED compactor's output and post-processes it — none
/// of them touches <c>AgentContextCompactor</c>, so nothing here can be promoted by accident: a change to the
/// product is a separate, later decision that the sweep's numbers either justify or refuse.
/// </summary>
internal static class CompactionArms
{
    // ---- arm C: mechanical anchor index ---------------------------------------------------------

    /// <summary>
    /// Written against the plan's candidate list — paths, ids, tool names, GUIDs, quoted errors,
    /// numbers-with-units — and deliberately NOT against the synthetic generator's answer formats. An
    /// extractor tuned to the corpus can be made to score anything, which would make arm C unreadable.
    /// </summary>
    private static readonly (string Kind, Regex Pattern)[] Extractors =
    [
        ("guid", new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            RegexOptions.Compiled)),
        // A rooted or dotted path with at least one separator, so ordinary prose with a slash does not qualify.
        ("path", new Regex(@"(?:[A-Za-z]:\\|\\\\|/)[\w.\-\\/]*[\w\-]\.[A-Za-z0-9]{1,8}\b|(?:/[\w.\-]+){2,}",
            RegexOptions.Compiled)),
        // SCREAMING-HYPHEN codes: error strings and correlation ids both take this shape.
        ("code", new Regex(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+\b", RegexOptions.Compiled)),
        // snake_case, which is how every built-in tool in this product is named.
        ("symbol", new Regex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b", RegexOptions.Compiled)),
        ("quantity", new Regex(
            @"\b\d+(?:[.,]\d+)?\s?(?:%|KB|MB|GB|TB|ms|s|m|h|B|bytes|rows|tokens|files|steps)\b",
            RegexOptions.Compiled)),
        ("quoted", new Regex(@"""([^""\r\n]{3,60})""|'([^'\r\n]{3,60})'", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Arm C's context: the compactor's own output plus one appended block naming, per source message, the
    /// identifiers found verbatim in what was dropped.
    /// </summary>
    /// <remarks>The ORDINAL is carried on every line. A flat bag of identifiers would strip the association
    /// between a value and the exchange it came from, which is most of what makes an anchor answerable.</remarks>
    internal static List<ChatMessage> AnchorIndex(
        IReadOnlyList<ChatMessage> transcript,
        IReadOnlyList<ChatMessage> retained,
        IReadOnlyList<ChatMessage> removed)
    {
        var block = AnchorBlock(transcript, removed);
        var context = retained.ToList();
        if (block.Length > 0)
            context.Add(new ChatMessage(ChatRole.System, block));

        return context;
    }

    /// <summary>The block on its own, so a control arm can ask the bank with the anchors and NOTHING else —
    /// the only way to tell "the model recalled" from "the model read a list".</summary>
    internal static string AnchorBlock(IReadOnlyList<ChatMessage> transcript, IReadOnlyList<ChatMessage> removed)
    {
        var positions = Positions(transcript);
        var lines = new List<string>();

        foreach (var message in removed)
        {
            var anchors = Extract(message);
            if (anchors.Count == 0)
                continue;

            lines.Add($"#{positions.GetValueOrDefault(message, -1)} {Role(message)}: {string.Join(" · ", anchors)}");
        }

        if (lines.Count == 0)
            return string.Empty;

        return new StringBuilder()
            .AppendLine(
                $"EARLIER CONTEXT INDEX — {lines.Count} of the {removed.Count} messages dropped from this "
                + "conversation contained the identifiers below, extracted verbatim. The wording around them is "
                + "gone; the values are exact.")
            .AppendJoin(Environment.NewLine, lines)
            .ToString();
    }

    /// <summary>Ordered, de-duplicated, and structural where it can be: a tool name and a call id come off the
    /// content object rather than out of a regex over its rendering.</summary>
    internal static List<string> Extract(ChatMessage message)
    {
        var found = new List<string>();

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !found.Contains(value, StringComparer.Ordinal))
                found.Add(value);
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    Add(call.Name);
                    Add(call.CallId);
                    foreach (var argument in call.Arguments ?? new Dictionary<string, object?>())
                        Scan(argument.Value?.ToString(), Add);
                    break;
                case FunctionResultContent result:
                    Add(result.CallId);
                    Scan(result.Result?.ToString(), Add);
                    break;
                case TextContent text:
                    Scan(text.Text, Add);
                    break;
            }
        }

        return found;
    }

    private static void Scan(string? text, Action<string> add)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var (_, pattern) in Extractors)
        {
            foreach (var match in pattern.Matches(text).Cast<Match>())
            {
                // Group 1/2 for the quoted pattern, so the delimiters are not part of the anchor.
                var value = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value
                    ?? match.Value;
                add(value.Trim());
            }
        }
    }

    // ---- arm D: recovery pointer ---------------------------------------------------------------

    /// <summary>
    /// The footer, and the contract it offers. Deliberately an EXPLICIT reply form rather than a tool call: the
    /// harness answers on a single-shot completion, so a model that has no tool loop still has to be able to
    /// ask for the search — otherwise arm D would measure tool support instead of recovery.
    /// </summary>
    internal const string SearchPrefix = "SEARCH:";

    internal static List<ChatMessage> RecoveryPointer(IReadOnlyList<ChatMessage> retained, int removedCount)
    {
        var context = retained.ToList();
        context.Add(new ChatMessage(ChatRole.System,
            $"{removedCount} earlier messages of this conversation were dropped to fit the context window. They "
            + "are NOT lost: they are still stored and searchable by exact substring. If the answer is not in "
            + $"the messages above, reply with {SearchPrefix} followed by one short search term — a code, a "
            + "path, a name — and the matching earlier messages will be given to you. Search before you answer "
            + "that you do not know."));

        return context;
    }

    /// <summary>
    /// The search a recovery pointer is only honest if it actually has. Same shape as
    /// <c>IAssistantChatService.SearchMessagesAsync</c> — one chat's messages, case-insensitive substring, in
    /// transcript order, with the ordinal that makes a hit citable — over the removed set rather than the
    /// database, because a synthetic transcript was never persisted.
    /// </summary>
    internal static List<RecoveredMessage> Search(
        IReadOnlyList<ChatMessage> transcript,
        IReadOnlyList<ChatMessage> removed,
        string term,
        int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var positions = Positions(transcript);
        var removedSet = new HashSet<ChatMessage>(removed, ReferenceComparer.Instance);

        return
        [
            .. transcript
                .Where(removedSet.Contains)
                .Select(m => (Message: m, Text: SyntheticTranscript.Trace([m])))
                .Where(x => x.Text.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(x => new RecoveredMessage(
                    positions.GetValueOrDefault(x.Message, -1), Role(x.Message), x.Text.Trim())),
        ];
    }

    /// <summary>What the model is handed back. Empty is reported as such rather than silently: a search that
    /// found nothing and a search that never ran must not look the same to the answering model.</summary>
    internal static string RenderHits(string term, IReadOnlyList<RecoveredMessage> hits) =>
        hits.Count == 0
            ? $"Search for \"{term}\" in the dropped messages returned no matches."
            : new StringBuilder()
                .AppendLine($"Search for \"{term}\" in the dropped messages returned {hits.Count} message(s):")
                .AppendJoin(Environment.NewLine, hits.Select(h => $"#{h.Ordinal} {h.Role}: {h.Snippet}"))
                .ToString();

    /// <summary>The term out of a <c>SEARCH:</c> reply, or null when the model answered instead of searching.</summary>
    internal static string? SearchTerm(string reply)
    {
        var at = reply.IndexOf(SearchPrefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        var rest = reply[(at + SearchPrefix.Length)..].ReplaceLineEndings("\n");
        var line = rest.Split('\n', 2)[0].Trim().Trim('"', '\'', '`', '.');
        return line.Length == 0 ? null : line;
    }

    // ---- arm E: pin every user message ---------------------------------------------------------

    /// <summary>
    /// Every <see cref="ChatRole.User"/> message withheld from compaction, not just the head goal and the
    /// newest one. Ordered by original position, which is where this deviates from the shipped compactor: it
    /// re-attaches the pinned instruction at the END, and reproducing that re-ordering here would make arm E
    /// two changes at once rather than one.
    /// </summary>
    internal static List<ChatMessage> PinAllUserMessages(
        IReadOnlyList<ChatMessage> transcript,
        IReadOnlyList<ChatMessage> retained)
    {
        var survivors = new HashSet<ChatMessage>(retained, ReferenceComparer.Instance);
        return [.. transcript.Where(m => survivors.Contains(m) || m.Role == ChatRole.User)];
    }

    // ---- readability instrument ----------------------------------------------------------------

    /// <summary>
    /// How many of the bank's gold answers appear VERBATIM in an arm's own context. The leak filter only
    /// guarantees this is zero for arm B's retained text; an arm that appends anything reintroduces the
    /// question, and without this number its score cannot be told apart from reading a list.
    /// </summary>
    internal static int GoldAnswersPresent(
        IReadOnlyList<ChatMessage> context,
        IReadOnlyList<RecallQuestion> bank)
    {
        var trace = SyntheticTranscript.Trace(context);
        return bank.Count(q => SyntheticTranscript.CountOccurrences(trace, q.GoldAnswer) > 0);
    }

    /// <summary>Same count against a bare block of text, for the anchor-only control.</summary>
    internal static int GoldAnswersPresent(string text, IReadOnlyList<RecallQuestion> bank) =>
        bank.Count(q => SyntheticTranscript.CountOccurrences(text, q.GoldAnswer) > 0);

    // ---- shared ---------------------------------------------------------------------------------

    /// <summary>Position in the ORIGINAL transcript, by reference: compaction reorders, so an index into the
    /// retained list would cite the wrong message.</summary>
    private static Dictionary<ChatMessage, int> Positions(IReadOnlyList<ChatMessage> transcript)
    {
        var positions = new Dictionary<ChatMessage, int>(ReferenceComparer.Instance);
        for (var i = 0; i < transcript.Count; i++)
            positions[transcript[i]] = i;

        return positions;
    }

    private static string Role(ChatMessage message) => message.Role.Value.ToLowerInvariant();

    private sealed class ReferenceComparer : IEqualityComparer<ChatMessage>
    {
        internal static readonly ReferenceComparer Instance = new();

        public bool Equals(ChatMessage? left, ChatMessage? right) => ReferenceEquals(left, right);

        public int GetHashCode(ChatMessage value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
