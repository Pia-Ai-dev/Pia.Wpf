using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Pia.Tests.Integration.Compaction;

internal enum SyntheticTranscriptShape
{
    ChatToolLight,
    ChatToolHeavy,
    AgentRun,
    AgentRunWithImage,
}

internal enum PlantedFactKind
{
    FilePath,
    Identifier,
    ErrorString,
    Quantity,
    Decision,
    ToolName,
}

// init-only members so a new knob can be added without touching a call site.
internal sealed record SyntheticTranscriptOptions
{
    public SyntheticTranscriptShape Shape { get; init; } = SyntheticTranscriptShape.ChatToolLight;

    public int Seed { get; init; } = 20260822;

    public int TurnCount { get; init; } = 40;

    public int FactCount { get; init; } = 15;

    public int FillerTokensPerMessage { get; init; } = 400;
}

/// <summary><c>Answer</c> is the verbatim gold string; <c>Statement</c> is the sentence it was planted in.</summary>
internal sealed record PlantedFact(
    string Id,
    PlantedFactKind Kind,
    string Answer,
    string Statement,
    string SuggestedQuestion,
    int MessageIndex);

/// <summary><c>Fingerprint</c> is the cache key a derived question bank is stored under.</summary>
internal sealed record SyntheticTranscriptResult(
    string Id,
    string Fingerprint,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<PlantedFact> Facts);

/// <summary>Builds a long, deterministic transcript with facts planted at known unpinned positions.</summary>
internal static class SyntheticTranscript
{
    private const string BaseDate = "2026-01-05";

    private const int HeadCount = 2;

    private const int ImageByteCount = 64 * 1024;

    // Lowercase letters, spaces and periods only, so filler can never reproduce a planted path, id, error code
    // or quantity by accident and the exactly-once postcondition below stays honest.
    private static readonly string[] FillerWords =
    [
        "alder", "amber", "banter", "brindle", "cinder", "cobble", "dapple", "drift",
        "eddy", "ember", "fathom", "furrow", "gable", "girth", "harbour", "hollow",
        "inlet", "ivory", "jetty", "juniper", "kelp", "kindling", "lantern", "loam",
        "mantle", "marsh", "nettle", "nimbus", "orchard", "osier", "pebble", "quarry",
        "ridge", "sable", "thicket", "umber", "vellum", "willow", "yarrow", "zephyr",
    ];

    private static readonly string[] ToolNames =
        ["read_file", "list_files", "web_fetch", "run_command", "search_notes"];

    private static readonly string[] StepIntents =
    [
        "reconcile the shard manifest",
        "replay the failed partitions",
        "compare the checksum ledger",
        "trace the dropped records",
        "rebuild the column statistics",
        "review the retry policy",
        "collect the spill metrics",
        "confirm the writer version",
    ];

    internal static SyntheticTranscriptResult Build(SyntheticTranscriptOptions options)
    {
        var rng = new Random(options.Seed);

        var guidSeed = new byte[16];
        rng.NextBytes(guidSeed);
        var guidPrefix = new Guid(guidSeed).ToString("N")[..8].ToUpperInvariant();

        var drafts = new List<Draft>
        {
            new()
            {
                Role = ChatRole.System,
                Text = $"You are Pia, an agent auditing a data-ingest pipeline. The audit window opens {BaseDate}.",
            },
            new()
            {
                Role = ChatRole.User,
                Text = "THE GOAL: audit the ingest pipeline and report every stage that failed.",
            },
        };

        for (var turn = 1; turn <= options.TurnCount; turn++)
            AppendTurn(drafts, options, rng, turn);

        drafts.Add(new Draft { Role = ChatRole.User, Text = TailText(options) });

        var facts = Plant(drafts, options, guidPrefix);
        var messages = drafts.Select(Materialize).ToList();

        // A generator that silently restates a fact makes every arm answerable by luck, so this fails at build
        // time rather than at scoring time.
        var trace = Trace(messages);
        foreach (var fact in facts)
        {
            var occurrences = CountOccurrences(trace, fact.Answer);
            if (occurrences != 1)
            {
                throw new InvalidOperationException(
                    $"the planted answer for {fact.Id} must occur exactly once in the transcript, but occurred {occurrences} times");
            }
        }

        return new SyntheticTranscriptResult(
            $"synthetic-{Kebab(options.Shape)}-{options.Seed}-{options.TurnCount}",
            Fingerprint(messages),
            messages,
            facts);
    }

    /// <summary>Text, tool-call arguments and tool-result payloads: every place a planted answer can hide.</summary>
    internal static string Trace(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();

        foreach (var message in messages)
        {
            builder.Append(message.Text).Append('\n');

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        builder.Append(call.Name).Append('\n');
                        if (call.Arguments is { } arguments)
                        {
                            foreach (var argument in arguments)
                                builder.Append(argument.Key).Append('=').Append(argument.Value).Append('\n');
                        }

                        break;

                    case FunctionResultContent { Result: { } payload }:
                        builder.Append(payload).Append('\n');
                        break;
                }
            }
        }

        return builder.ToString();
    }

    internal static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;

        while (at <= haystack.Length - needle.Length)
        {
            var found = haystack.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0)
                break;

            count++;
            at = found + needle.Length;
        }

        return count;
    }

    internal static string Filler(Random rng, int approximateTokens)
    {
        var target = approximateTokens * 4;
        var builder = new StringBuilder(target + 16);

        while (builder.Length < target)
        {
            var words = 6 + rng.Next(9);
            for (var w = 0; w < words; w++)
            {
                if (w > 0)
                    builder.Append(' ');

                builder.Append(FillerWords[rng.Next(FillerWords.Length)]);
            }

            builder.Append(". ");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendTurn(List<Draft> drafts, SyntheticTranscriptOptions options, Random rng, int turn)
    {
        switch (options.Shape)
        {
            case SyntheticTranscriptShape.ChatToolLight:
                drafts.Add(new Draft { Role = ChatRole.User, Text = Filler(rng, 40), CanCarryFact = true });
                drafts.Add(new Draft
                {
                    Role = ChatRole.Assistant,
                    Text = Filler(rng, options.FillerTokensPerMessage),
                    CanCarryFact = true,
                });
                break;

            case SyntheticTranscriptShape.ChatToolHeavy:
                var callId = $"call_{turn:D3}";
                drafts.Add(new Draft { Role = ChatRole.User, Text = Filler(rng, 40), CanCarryFact = true });
                drafts.Add(new Draft
                {
                    Role = ChatRole.Assistant,
                    CallId = callId,
                    ToolName = ToolNames[rng.Next(ToolNames.Length)],
                    ArgumentPath = $"/workspace/inputs/batch-{turn:D3}.jsonl",
                });
                drafts.Add(new Draft
                {
                    Role = ChatRole.Tool,
                    CallId = callId,
                    IsResult = true,
                    ResultRows = rng.Next(100, 100_000),
                    Text = Filler(rng, options.FillerTokensPerMessage),
                    CanCarryFact = true,
                });
                drafts.Add(new Draft
                {
                    Role = ChatRole.Assistant,
                    Text = Filler(rng, options.FillerTokensPerMessage),
                    CanCarryFact = true,
                });
                break;

            default:
                if (options.Shape == SyntheticTranscriptShape.AgentRunWithImage && turn == 2)
                {
                    var image = new byte[ImageByteCount];
                    rng.NextBytes(image);

                    // One fused [text, image] turn, the unit the compactor's image pin protects. Never a fact
                    // carrier: a pinned message is answerable by every arm and measures nothing.
                    drafts.Add(new Draft
                    {
                        Role = ChatRole.User,
                        Text = "here is the failing stage dashboard",
                        Image = image,
                    });
                }

                drafts.Add(new Draft
                {
                    Role = ChatRole.User,
                    Text = $"Execute step {turn}: {StepIntents[(turn - 1) % StepIntents.Length]}. "
                        + $"Expected: stage-{turn:D2}-notes.md",
                    CanCarryFact = true,
                });
                drafts.Add(new Draft
                {
                    Role = ChatRole.Assistant,
                    Text = Filler(rng, options.FillerTokensPerMessage),
                    CanCarryFact = true,
                });
                break;
        }
    }

    private static List<PlantedFact> Plant(
        List<Draft> drafts,
        SyntheticTranscriptOptions options,
        string guidPrefix)
    {
        var candidates = new List<int>();
        for (var i = HeadCount + 1; i < drafts.Count - 1; i++)
        {
            if (drafts[i].CanCarryFact)
                candidates.Add(i);
        }

        var stride = candidates.Count / (options.FactCount + 1);
        if (stride < 1)
        {
            throw new InvalidOperationException(
                $"{options.FactCount} facts need more room than the {candidates.Count} unpinned messages give; raise TurnCount");
        }

        var facts = new List<PlantedFact>(options.FactCount);
        for (var f = 1; f <= options.FactCount; f++)
        {
            var index = candidates[stride * f];
            var (kind, answer, statement, question) = Describe(f, guidPrefix);

            drafts[index].Text = $"{drafts[index].Text} {statement}";
            facts.Add(new PlantedFact($"fact-{f:D2}", kind, answer, statement, question, index));
        }

        return facts;
    }

    // Every answer embeds its own fact index, so two answers can never collide.
    private static (PlantedFactKind Kind, string Answer, string Statement, string Question) Describe(
        int index,
        string guidPrefix)
    {
        switch ((PlantedFactKind)((index - 1) % 6))
        {
            case PlantedFactKind.FilePath:
            {
                var answer = $"/workspace/reports/ingest-{index:D2}.md";
                return (PlantedFactKind.FilePath, answer,
                    $"The stage {index:D2} summary was written to {answer}.",
                    $"Which file holds the stage {index:D2} summary?");
            }

            case PlantedFactKind.Identifier:
            {
                var answer = $"RUN-{guidPrefix}-{index:D2}";
                return (PlantedFactKind.Identifier, answer,
                    $"The stage {index:D2} rerun was recorded under {answer}.",
                    $"Under which identifier was the stage {index:D2} rerun recorded?");
            }

            case PlantedFactKind.ErrorString:
            {
                var answer = $"PIA-E{4000 + index}";
                return (PlantedFactKind.ErrorString, answer,
                    $"Stage {index:D2} aborted with {answer}.",
                    $"Which error code did stage {index:D2} abort with?");
            }

            case PlantedFactKind.Quantity:
            {
                var answer = $"{184 + index} MB";
                return (PlantedFactKind.Quantity, answer,
                    $"Stage {index:D2} spilled {answer} to the scratch volume.",
                    $"How much did stage {index:D2} spill to the scratch volume?");
            }

            case PlantedFactKind.Decision:
            {
                var answer = $"switch stage {index:D2} to the columnar writer";
                return (PlantedFactKind.Decision, answer,
                    $"We agreed to {answer} before the next release.",
                    $"What was agreed for stage {index:D2} before the next release?");
            }

            default:
            {
                var answer = $"probe_stage_{index:D2}";
                return (PlantedFactKind.ToolName, answer,
                    $"Stage {index:D2} was inspected with the {answer} tool.",
                    $"Which tool was stage {index:D2} inspected with?");
            }
        }
    }

    private static ChatMessage Materialize(Draft draft)
    {
        if (draft.Image is { } image)
            return new ChatMessage(draft.Role, [new TextContent(draft.Text), new DataContent(image, "image/png")]);

        if (draft.IsResult && draft.CallId is { } resultCallId)
        {
            return new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(resultCallId, $"{{\"rows\": {draft.ResultRows}, \"notes\": \"{draft.Text}\"}}")]);
        }

        if (draft.CallId is { } callId && draft.ToolName is { } toolName)
        {
            return new ChatMessage(
                draft.Role,
                [new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { ["path"] = draft.ArgumentPath })]);
        }

        return new ChatMessage(draft.Role, draft.Text);
    }

    private static string Fingerprint(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
            builder.Append(message.Role.Value).Append('\u001f').Append(message.Text).Append('\u001e');

        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant()[..16];
    }

    private static string TailText(SyntheticTranscriptOptions options) =>
        options.Shape is SyntheticTranscriptShape.AgentRun or SyntheticTranscriptShape.AgentRunWithImage
            ? $"Execute step {options.TurnCount + 1}: summarise the audit. Expected: audit-summary.md"
            : "so what did we decide about the ingest pipeline?";

    private static string Kebab(SyntheticTranscriptShape shape) => shape switch
    {
        SyntheticTranscriptShape.ChatToolLight => "chat-tool-light",
        SyntheticTranscriptShape.ChatToolHeavy => "chat-tool-heavy",
        SyntheticTranscriptShape.AgentRun => "agent-run",
        _ => "agent-run-with-image",
    };

    private sealed class Draft
    {
        internal ChatRole Role { get; init; } = ChatRole.Assistant;

        internal string Text { get; set; } = string.Empty;

        internal string? CallId { get; init; }

        internal string? ToolName { get; init; }

        internal string? ArgumentPath { get; init; }

        internal bool IsResult { get; init; }

        internal int ResultRows { get; init; }

        internal byte[]? Image { get; init; }

        internal bool CanCarryFact { get; init; }
    }
}
