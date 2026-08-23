using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Paths;
using Pia.Services;
using Pia.Services.Providers;

namespace Pia.Tests.Integration.Compaction;

/// <summary>The budget under test, plus where it came from — a number that does not name its budget is unreadable.</summary>
internal sealed record RecallBudget(string Label, AgentContextBudget Budget, string Source)
{
    internal string CacheKey => $"{Budget.WindowTokens}x{Budget.MaxOutputTokens}";
}

internal sealed record RecallQuestion(string Id, string Question, string GoldAnswer);

/// <summary>
/// One corpus entry. <c>Facts</c> is empty for a fixture loaded from disk, which is what makes the harness ask
/// a model for its bank instead of deriving one.
/// </summary>
internal sealed record RecallTranscript(
    string Id,
    string Fingerprint,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<PlantedFact> Facts);

internal sealed record ArmResult(
    string Arm,
    int Messages,
    int ApproximateTokens,
    double Score,
    int Answered,
    long ElapsedMs);

internal sealed record RecallRow(string TranscriptId, int BankSize, ArmResult Uncompacted, ArmResult Current);

/// <summary>
/// Measures what compaction costs recall: build the post-compaction request through the SHIPPED
/// <see cref="AgentContextCompactor"/>, ask a fresh model one question per call about what it removed, and have
/// a second call judge the answer against the gold string.
/// <para>
/// Provider-agnostic by design, not by accident: the answering and judging provider is named in configuration
/// and there is no fallback, least of all to a local model — a contributor with only cloud providers has to be
/// able to run the sweep, and an owner who wants a local judge has to be able to point at one without editing
/// code.
/// </para>
/// </summary>
internal static class CompactionRecallHarness
{
    internal const string ProviderVariable = "PIA_COMPACTION_PROVIDER";

    internal const string JudgeProviderVariable = "PIA_COMPACTION_JUDGE_PROVIDER";

    internal const string ProvidersFileVariable = "PIA_COMPACTION_PROVIDERS_FILE";

    internal const string CorpusDirectoryVariable = "PIA_COMPACTION_CORPUS_DIR";

    internal const string OutputVariable = "PIA_COMPACTION_EVAL_OUT";

    internal const string WindowVariable = "PIA_COMPACTION_WINDOW";

    internal const string MaxOutputVariable = "PIA_COMPACTION_MAX_OUTPUT";

    /// <summary>Answered without the fact in context, so an "I cannot find it" is scored as a miss, not a fudge.</summary>
    private const string Unknown = "UNKNOWN";

    private const int MaxAttempts = 6;

    /// <summary>Two at a time, paced below: three earned a 429 from Mistral 95 seconds into a 240-call sweep.</summary>
    private const int Concurrency = 2;

    /// <summary>A floor on the gap between calls, because a provider rate limit is a property of the account
    /// and not of this sweep - the retry below is the second line of defence, not the first.</summary>
    private static readonly TimeSpan MinimumCallInterval = TimeSpan.FromMilliseconds(1100);

    // ---- corpus ---------------------------------------------------------------------------------

    /// <summary>The four shapes of the plan's corpus table, from the committed generator — no real data, no
    /// privacy decision, and every planted answer occurs exactly once by construction.</summary>
    internal static List<RecallTranscript> SyntheticCorpus(int turns = 40) =>
    [
        .. new[]
        {
            SyntheticTranscriptShape.ChatToolLight,
            SyntheticTranscriptShape.ChatToolHeavy,
            SyntheticTranscriptShape.AgentRun,
            SyntheticTranscriptShape.AgentRunWithImage,
        }
        .Select(shape => SyntheticTranscript.Build(new SyntheticTranscriptOptions { Shape = shape, TurnCount = turns }))
        .Select(built => new RecallTranscript(built.Id, built.Fingerprint, built.Messages, built.Facts)),
    ];

    /// <summary>Fixtures hold real conversation content, so they live outside the repo and are passed by path.</summary>
    internal static List<RecallTranscript> FixtureCorpus()
    {
        var directory = CorpusDirectory();
        if (!Directory.Exists(directory))
            return [];

        var loaded = new List<RecallTranscript>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.corpus.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var messages = new List<ChatMessage>();

            if (root.TryGetProperty("messages", out var list))
            {
                foreach (var message in list.EnumerateArray())
                {
                    var role = message.TryGetProperty("role", out var r) ? r.GetString() : null;
                    var content = message.TryGetProperty("content", out var c) ? c.GetString() : null;
                    if (string.IsNullOrEmpty(content))
                        continue;

                    messages.Add(new ChatMessage(
                        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? ChatRole.User : ChatRole.Assistant,
                        content));
                }
            }

            if (messages.Count > 0)
                loaded.Add(new RecallTranscript(id ?? Path.GetFileName(file), Fingerprint(messages), messages, []));
        }

        return loaded;
    }

    // ---- budgets --------------------------------------------------------------------------------

    /// <summary>The compaction-forcing window: cheap, hardest to fake, and what the structural tests already use.</summary>
    internal static RecallBudget SmallWindow { get; } =
        new("small-8000x2000", new AgentContextBudget(8_000, 2_000), "harness default");

    /// <summary>
    /// The env-supplied window, or the provider's own configured one. Null when neither exists — which is not a
    /// harness failure but the plan's §2.3 in the open: without <c>MaxContextWindowTokens</c> the provider never
    /// compacts, so there is no real window to measure against.
    /// </summary>
    internal static RecallBudget? ConfiguredWindow(AiProvider? provider)
    {
        var window = ReadInt(WindowVariable);
        var maxOutput = ReadInt(MaxOutputVariable);
        if (window is > 0 && maxOutput is >= 0 && maxOutput < window)
            return new RecallBudget($"env-{window}x{maxOutput}", new AgentContextBudget(window.Value, maxOutput.Value), WindowVariable);

        return AgentContextBudget.From(provider) is { } configured
            ? new RecallBudget(
                $"provider-{configured.WindowTokens}x{configured.MaxOutputTokens}",
                configured,
                $"{provider!.Name} MaxContextWindowTokens")
            : null;
    }

    // ---- compaction -----------------------------------------------------------------------------

    /// <summary>Runs the shipped compactor, then splits the input by REFERENCE identity: compaction reorders, so
    /// an index-based diff reports the pinned instruction as removed.</summary>
    internal static async Task<(List<ChatMessage> Retained, List<ChatMessage> Removed)> CompactAsync(
        RecallTranscript transcript,
        RecallBudget budget,
        CancellationToken ct)
    {
        var retained = await AgentContextCompactor.CompactAsync(
            transcript.Messages, budget.Budget, NullLogger.Instance, ct);

        var survivors = new HashSet<ChatMessage>(retained, ByReference<ChatMessage>.Instance);
        var removed = transcript.Messages.Where(m => !survivors.Contains(m)).ToList();
        return (retained, removed);
    }

    // ---- question bank --------------------------------------------------------------------------

    /// <summary>
    /// The bank for one (transcript, budget). Cached under BOTH, because "the removed set" is a property of the
    /// pair — a transcript-only key would ask the second budget about content it never removed.
    /// </summary>
    internal static async Task<List<RecallQuestion>> BankAsync(
        RecallTranscript transcript,
        RecallBudget budget,
        AiProvider? generator,
        CancellationToken ct,
        string? cacheDirectory = null)
    {
        var directory = cacheDirectory ?? CorpusDirectory();
        var cache = Path.Combine(directory, $"{transcript.Fingerprint}-{budget.CacheKey}.bank.json");
        if (File.Exists(cache))
        {
            var cached = JsonSerializer.Deserialize<List<RecallQuestion>>(File.ReadAllText(cache));
            if (cached is { Count: > 0 })
                return cached;
        }

        var (retained, removed) = await CompactAsync(transcript, budget, ct);
        var removedSet = new HashSet<ChatMessage>(removed, ByReference<ChatMessage>.Instance);
        var retainedTrace = SyntheticTranscript.Trace(retained);

        var candidates = transcript.Facts.Count > 0
            ? PlantedCandidates(transcript, removedSet)
            : await GeneratedCandidatesAsync(removed, generator, ct);

        // THE LEAK FILTER, and the load-bearing part of the instrument: a fact that also survives in the
        // retained text is answerable by every arm, which is exactly the restatement luck that scored one of
        // hermes's arms 93.3% on nothing. Trace covers tool-call arguments and tool-result payloads too, so an
        // answer hiding in a tool result cannot pass a text-only check.
        var bank = candidates
            .Where(q => SyntheticTranscript.CountOccurrences(retainedTrace, q.GoldAnswer) == 0)
            .ToList();

        Directory.CreateDirectory(directory);
        File.WriteAllText(cache, JsonSerializer.Serialize(bank));
        return bank;
    }

    private static List<RecallQuestion> PlantedCandidates(RecallTranscript transcript, HashSet<ChatMessage> removed) =>
    [
        .. transcript.Facts
            .Where(f => f.MessageIndex >= 0
                && f.MessageIndex < transcript.Messages.Count
                && removed.Contains(transcript.Messages[f.MessageIndex]))
            .Select(f => new RecallQuestion(f.Id, f.SuggestedQuestion, f.Answer)),
    ];

    /// <summary>The fixture path: one generation call over the removed region, since a real transcript plants
    /// nothing. Unused by the synthetic corpus, which ships its own gold answers.</summary>
    private static async Task<List<RecallQuestion>> GeneratedCandidatesAsync(
        List<ChatMessage> removed,
        AiProvider? generator,
        CancellationToken ct)
    {
        if (generator is null || removed.Count == 0)
            return [];

        var prompt = new StringBuilder()
            .AppendLine("Below is an excerpt of a conversation. Write 15 short factual recall questions that can")
            .AppendLine("be answered ONLY from this excerpt, each with its exact answer as it appears in the text.")
            .AppendLine("Prefer checkable answers: a filename, a number, a name, a decision, an error string.")
            .AppendLine("Reply as one question per line in the form: question | answer")
            .AppendLine()
            .AppendLine(SyntheticTranscript.Trace(removed))
            .ToString();

        var reply = await CompleteAsync(generator, [new ChatMessage(ChatRole.User, prompt)], ct);

        var questions = new List<RecallQuestion>();
        foreach (var line in reply.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (split.Length == 2 && split[0].Length > 0 && split[1].Length > 0)
                questions.Add(new RecallQuestion($"generated-{questions.Count + 1:D2}", split[0], split[1]));
        }

        return questions;
    }

    // ---- arms -----------------------------------------------------------------------------------

    /// <summary>One arm over one bank: a fresh single-question call per entry, then a separate judging call.</summary>
    internal static async Task<ArmResult> RunArmAsync(
        string arm,
        IReadOnlyList<ChatMessage> context,
        IReadOnlyList<RecallQuestion> bank,
        AiProvider answering,
        AiProvider judging,
        CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var scores = new double[bank.Count];
        using var gate = new SemaphoreSlim(Concurrency);

        await Task.WhenAll(bank.Select(async (question, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var answer = await AnswerAsync(answering, context, question, ct);
                scores[index] = await JudgeAsync(judging, question, answer, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        return new ArmResult(
            arm,
            context.Count,
            ApproximateTokens(context),
            bank.Count == 0 ? 0 : scores.Sum() / bank.Count,
            bank.Count,
            started.ElapsedMilliseconds);
    }

    private static Task<string> AnswerAsync(
        AiProvider provider,
        IReadOnlyList<ChatMessage> context,
        RecallQuestion question,
        CancellationToken ct)
    {
        // One question per call, on a fresh conversation: batching lets one answer leak into the next.
        var messages = new List<ChatMessage>(Sendable(context))
        {
            new(ChatRole.User,
                $"{question.Question}{Environment.NewLine}{Environment.NewLine}"
                + "Answer only from the conversation above. Reply with the value alone, no sentence. "
                + $"If the conversation does not contain it, reply exactly {Unknown}."),
        };

        return CompleteAsync(provider, messages, ct);
    }

    /// <summary>
    /// Text parts only. The generator's image is random bytes rather than a decodable PNG, so a provider
    /// cannot be asked to look at it - and it does not need to be: the bank asks about removed TEXT, and the
    /// image's whole effect on WHAT was removed (its token charge, its pin) was already applied by the
    /// compactor before this point.
    /// </summary>
    private static List<ChatMessage> Sendable(IEnumerable<ChatMessage> context)
    {
        var stripped = new List<ChatMessage>();
        foreach (var message in context)
        {
            var keep = message.Contents.Where(c => c is not DataContent).ToList();
            if (keep.Count == message.Contents.Count)
            {
                stripped.Add(message);
                continue;
            }

            if (keep.Count > 0)
                stripped.Add(new ChatMessage(message.Role, keep));
        }

        return stripped;
    }

    /// <summary>correct = 1, partial = 0.5, anything else = 0. One judge, one prompt, across every arm.</summary>
    private static async Task<double> JudgeAsync(
        AiProvider provider,
        RecallQuestion question,
        string answer,
        CancellationToken ct)
    {
        var prompt =
            $"Question: {question.Question}{Environment.NewLine}"
            + $"Expected answer: {question.GoldAnswer}{Environment.NewLine}"
            + $"Given answer: {answer}{Environment.NewLine}{Environment.NewLine}"
            + "Does the given answer state the expected answer? Reply with exactly one word: "
            + "correct, partial, or wrong. Use partial only when it names part of the expected answer.";

        var verdict = await CompleteAsync(provider, [new ChatMessage(ChatRole.User, prompt)], ct);

        // Exact-token comparison, because "incorrect" contains "correct".
        var word = new string([.. verdict.Trim().ToLowerInvariant().TakeWhile(char.IsLetter)]);
        return word switch
        {
            "correct" => 1,
            "partial" => 0.5,
            _ => 0,
        };
    }

    // ---- provider -------------------------------------------------------------------------------

    /// <summary>The named provider from configuration, or null — never a fallback, and never a local model.</summary>
    internal static AiProvider? ResolveProvider(string variable = ProviderVariable)
    {
        var wanted = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(wanted))
            return null;

        var file = Environment.GetEnvironmentVariable(ProvidersFileVariable);
        if (string.IsNullOrWhiteSpace(file))
            file = Path.Combine(PiaPaths.RoamingDataDirectory, "providers.json");

        if (!File.Exists(file))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(file));

        // Read field by field rather than deserializing AiProvider: the persisted enum shapes are the
        // persistence layer's business, and a converter mismatch here would look like "no provider configured".
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = Text(element, "name");
            var id = Text(element, "id");
            if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new AiProvider
            {
                Id = Guid.TryParse(id, out var parsed) ? parsed : Guid.NewGuid(),
                Name = name ?? wanted,
                ProviderType = (AiProviderType)Number(element, "providerType"),
                Endpoint = Text(element, "endpoint") ?? string.Empty,
                ModelName = Text(element, "modelName"),
                EncryptedApiKey = Text(element, "encryptedApiKey"),
                AzureDeploymentName = Text(element, "azureDeploymentName"),
                SupportsToolCalling = false,
                SupportsStreaming = false,
                TimeoutSeconds = 600,
                MaxContextWindowTokens = Positive(element, "maxContextWindowTokens"),
                MaxOutputTokens = Positive(element, "maxOutputTokens"),
            };
        }

        return null;
    }

    private static async Task<string> CompleteAsync(AiProvider provider, List<ChatMessage> messages, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var client = await ClientAsync(provider, ct);
                await PaceAsync(ct);

                // Temperature 0 here rather than through AiClientService: the shipped CreateChatOptions sets no
                // temperature, and a measurement wants the least run-to-run noise the provider will give.
                var response = await client.GetResponseAsync(messages, new ChatOptions { Temperature = 0 }, ct);
                return response.Text ?? string.Empty;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                // Exponential, capped: a 429 mid-sweep must cost wall-clock rather than the whole run, and a
                // partial sweep is worse than a slow one - the arms have to face the identical bank.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))), ct);
            }
        }
    }

    private static readonly SemaphoreSlim PaceGate = new(1);

    private static DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

    /// <summary>Serialises the moment each request leaves, so concurrency hides latency without doubling rate.</summary>
    private static async Task PaceAsync(CancellationToken ct)
    {
        TimeSpan wait;
        await PaceGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            wait = _nextSlot > now ? _nextSlot - now : TimeSpan.Zero;
            _nextSlot = (now > _nextSlot ? now : _nextSlot) + MinimumCallInterval;
        }
        finally
        {
            PaceGate.Release();
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }

    private static readonly Dictionary<Guid, IChatClient> Clients = [];

    private static readonly SemaphoreSlim ClientGate = new(1);

    private static async Task<IChatClient> ClientAsync(AiProvider provider, CancellationToken ct)
    {
        await ClientGate.WaitAsync(ct);
        try
        {
            if (Clients.TryGetValue(provider.Id, out var existing))
                return existing;

            var key = new DpapiHelper(NullLogger<DpapiHelper>.Instance).Decrypt(provider.EncryptedApiKey ?? string.Empty);

            // InfiniteTimeSpan, and not by taste: HttpClient's 100 s default has already been mistaken for a
            // user cancellation once in this codebase, and an uncompacted arm sends the whole transcript.
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            var handlers = new IAiProviderHandler[]
            {
                new OpenAiProviderHandler(),
                new AzureOpenAiProviderHandler(),
                new OllamaProviderHandler(),
                new MistralProviderHandler(),
                new OpenRouterProviderHandler(),
                new OpenAiCompatibleProviderHandler(),
                new VLlmProviderHandler(),
            };

            var client = await new AiProviderHandlerResolver(handlers)
                .Get(provider.ProviderType)
                .CreateChatClientAsync(provider, key, http, mode: null, managedPersonaId: null, personaModelType: null, ct);

            Clients[provider.Id] = client;
            return client;
        }
        finally
        {
            ClientGate.Release();
        }
    }

    // ---- scorecard ------------------------------------------------------------------------------

    /// <summary>Numbers only: a question string is transcript-derived, so it never reaches the file.</summary>
    internal static string WriteScorecard(
        string corpusLabel,
        RecallBudget budget,
        AiProvider answering,
        AiProvider judging,
        IReadOnlyList<RecallRow> rows)
    {
        var report = new StringBuilder()
            .AppendLine("# Compaction recall scorecard")
            .AppendLine()
            .AppendLine($"- corpus: {corpusLabel}")
            .AppendLine($"- budget: {budget.Label} (window {budget.Budget.WindowTokens}, max output {budget.Budget.MaxOutputTokens}, from {budget.Source})")
            .AppendLine($"- answering model: {answering.ModelName} ({answering.ProviderType})")
            .AppendLine($"- judging model: {judging.ModelName} ({judging.ProviderType})")
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- thresholds: tool eviction {AgentContextCompactor.ToolEvictionThreshold}, truncation {AgentContextCompactor.TruncationThreshold}"))
            .AppendLine()
            .AppendLine("| transcript | bank | A:uncompacted | B:current | B/A |")
            .AppendLine("|---|---|---|---|---|");

        foreach (var row in rows)
        {
            report.AppendLine(
                $"| {row.TranscriptId} | {row.BankSize} "
                + $"| {Percent(row.Uncompacted.Score)} @ {Thousands(row.Uncompacted.ApproximateTokens)} / {row.Uncompacted.Messages} msg "
                + $"| {Percent(row.Current.Score)} @ {Thousands(row.Current.ApproximateTokens)} / {row.Current.Messages} msg "
                + $"| {Ratio(row.Current.Score, row.Uncompacted.Score)} |");
        }

        if (rows.Count > 0)
        {
            report.AppendLine(
                $"| **AVG** | {rows.Sum(r => r.BankSize)} "
                + $"| {Percent(rows.Average(r => r.Uncompacted.Score))} "
                + $"| {Percent(rows.Average(r => r.Current.Score))} "
                + $"| {Ratio(rows.Average(r => r.Current.Score), rows.Average(r => r.Uncompacted.Score))} |");
        }

        var directory = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Path.GetTempPath(), "pia-compaction-eval");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"scorecard-{corpusLabel}-{budget.CacheKey}.md");
        File.WriteAllText(path, report.ToString());
        return path;
    }

    /// <summary>Invariant on purpose: a scorecard read on a German machine printed "100,0%" and "0,45".</summary>
    internal static string Percent(double score) => string.Create(CultureInfo.InvariantCulture, $"{score * 100:0.0}%");

    private static string Ratio(double current, double ceiling) =>
        ceiling <= 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{current / ceiling * 100:0.0}%");

    private static string Thousands(int tokens) => tokens >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{tokens / 1000.0:0.0}K")
        : tokens.ToString(CultureInfo.InvariantCulture);

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Text length / 4, the same shape the compaction library charges by, so the column is comparable.</summary>
    internal static int ApproximateTokens(IEnumerable<ChatMessage> messages) =>
        SyntheticTranscript.Trace(messages).Length / 4;

    internal static string CorpusDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(CorpusDirectoryVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "pia-compaction-corpus")
            : configured;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Positive(JsonElement element, string name) => Number(element, name) is var value and > 0 ? value : null;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static int? ReadInt(string variable) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var parsed) ? parsed : null;

    private static string Fingerprint(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
            builder.Append(message.Role.Value).Append('').Append(message.Text).Append('');

        return Convert
            .ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant()[..16];
    }

    private sealed class ByReference<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly ByReference<T> Instance = new();

        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);

        public int GetHashCode(T value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
