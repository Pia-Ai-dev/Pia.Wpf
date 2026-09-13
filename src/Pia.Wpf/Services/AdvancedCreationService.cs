using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AdvancedCreationService : IAdvancedCreationService
{
    /// <summary>Six is enough for the model to close every gap it actually has, and low enough that a
    /// model which never converges cannot bill the user indefinitely.</summary>
    public int MaxAskTurns => 6;

    private const int MaxQuestionsPerTurn = 3;

    private readonly IProviderService _providerService;
    private readonly IAiClientService _aiClientService;
    private readonly ILogger<AdvancedCreationService> _logger;

    public AdvancedCreationService(
        IProviderService providerService,
        IAiClientService aiClientService,
        ILogger<AdvancedCreationService> logger)
    {
        _providerService = providerService;
        _aiClientService = aiClientService;
        _logger = logger;
    }

    public async Task<AdvancedCreationTurn> StartAsync(
        AdvancedCreationSession session,
        string opening,
        CancellationToken cancellationToken = default)
    {
        session.Messages.Clear();
        session.AskTurns = 0;
        session.Messages.Add(new ChatMessage(ChatRole.System, SystemPrompt(session.Mode)));
        session.Messages.Add(new ChatMessage(ChatRole.User, $"What I want:\n{opening}"));

        _logger.SensitiveDebug("Advanced creation opened for {Subject}: {Opening}", session.Mode.Subject, opening);

        return await RunTurnAsync(session, cancellationToken);
    }

    public async Task<AdvancedCreationTurn> AnswerAsync(
        AdvancedCreationSession session,
        IReadOnlyDictionary<string, string> answers,
        IReadOnlyList<string> skipped,
        CancellationToken cancellationToken = default)
    {
        if (session.Messages.Count == 0)
            throw new InvalidOperationException("The interview has not been started.");

        var payload = JsonSerializer.Serialize(new { answers, skipped });
        var reply = new StringBuilder(payload);

        // Said on the turn that reaches the cap rather than after it: the model has to produce the draft
        // in the same reply, or the user pays for one more round that only announces the end.
        if (session.AskTurns >= MaxAskTurns)
            reply.Append("\n\nThat is everything. Produce the final draft now — state \"done\" and ask nothing further.");

        session.Messages.Add(new ChatMessage(ChatRole.User, reply.ToString()));

        _logger.SensitiveDebug("Advanced creation answers for {Subject}: {Payload}", session.Mode.Subject, payload);
        _logger.LogInformation(
            "Advanced creation turn {Turn}/{Max} for {Subject}: {Answered} answered, {Skipped} skipped",
            session.AskTurns, MaxAskTurns, session.Mode.Subject, answers.Count, skipped.Count);

        return await RunTurnAsync(session, cancellationToken);
    }

    private async Task<AdvancedCreationTurn> RunTurnAsync(
        AdvancedCreationSession session, CancellationToken cancellationToken)
    {
        var provider = session.ProviderId.HasValue
            ? await _providerService.GetProviderAsync(session.ProviderId.Value)
            : await _providerService.GetDefaultProviderForModeAsync(session.Mode.ProviderMode);

        if (provider is null)
            throw new InvalidOperationException("No AI provider configured");

        // Retried once for the reason the routine draft is: an upstream error frame is dropped rather
        // than thrown, so a failed turn is indistinguishable from a silent one here.
        var raw = await CollectTextAsync(session, provider, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
            raw = await CollectTextAsync(session, provider, cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("The model returned nothing.");

        session.Messages.Add(new ChatMessage(ChatRole.Assistant, raw));

        var turn = ParseTurn(raw, session.AskTurns >= MaxAskTurns);
        if (!turn.IsComplete)
            session.AskTurns++;

        _logger.LogInformation(
            "Advanced creation replied for {Subject}: complete={Complete}, {Questions} questions",
            session.Mode.Subject, turn.IsComplete, turn.Questions.Count);
        _logger.SensitiveDebug("Advanced creation raw reply: {Raw}", raw);

        return turn;
    }

    private async Task<string> CollectTextAsync(
        AdvancedCreationSession session, AiProvider provider, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            session.Messages, provider, tools: null, toolHandler: null,
            mode: nameof(WindowMode.Assistant)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is TextDelta delta)
                buffer.Append(delta.Text);
        }

        return buffer.ToString();
    }

    /// <param name="capReached">At the cap an answerless reply can only mean the model ignored the
    /// instruction, so the whole reply is treated as the draft rather than asked about again.</param>
    internal static AdvancedCreationTurn ParseTurn(string raw, bool capReached)
    {
        var json = DraftParsing.ExtractJsonObject(raw);
        if (json is null)
            return Unusable(raw, capReached);

        TurnDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TurnDto>(json, DraftParsing.Options);
        }
        catch (JsonException)
        {
            return Unusable(raw, capReached);
        }

        if (dto is null)
            return Unusable(raw, capReached);

        var summary = DraftParsing.Clean(dto.Summary);
        var isDone = string.Equals(dto.State, "done", StringComparison.OrdinalIgnoreCase);

        if (isDone)
        {
            // "done" with no draft is the one shape that would hand the caller an empty editor. The raw
            // reply is the better guess — the one-shot parsers already treat loose text that way.
            var draft = dto.Draft?.GetRawText();
            return new AdvancedCreationTurn(true, summary, [], string.IsNullOrWhiteSpace(draft) ? json : draft);
        }

        var questions = (dto.Questions ?? [])
            .Where(q => !string.IsNullOrWhiteSpace(q.Id) && !string.IsNullOrWhiteSpace(q.Label))
            .Take(MaxQuestionsPerTurn)
            .Select(ToQuestion)
            .ToList();

        // An "ask" with nothing to ask would leave the panel with no control and no way forward.
        if (questions.Count == 0)
            return Unusable(raw, capReached);

        return new AdvancedCreationTurn(false, summary, questions, null);
    }

    /// <summary>At the cap the raw reply is the best draft available and ending on it beats losing the
    /// interview; before the cap the transcript is still good, so the panel can just ask again.</summary>
    private static AdvancedCreationTurn Unusable(string raw, bool capReached) =>
        capReached
            ? new AdvancedCreationTurn(true, null, [], raw.Trim())
            : throw new AdvancedCreationReplyException("The model's reply was neither a question nor a draft.");

    private static AdvancedCreationQuestion ToQuestion(QuestionDto q)
    {
        // An unknown kind degrades to a plain box rather than dropping the question: the model still
        // wants an answer, and a text box can carry any of them.
        var kind = Enum.TryParse<AdvancedCreationAnswerKind>(q.Kind?.Replace("_", ""), ignoreCase: true, out var parsed)
            ? parsed
            : AdvancedCreationAnswerKind.Text;

        var options = (q.Options ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .ToList();

        // A choice with nothing to choose from is a text question wearing the wrong hat.
        if (options.Count == 0 && kind is AdvancedCreationAnswerKind.Choice or AdvancedCreationAnswerKind.MultiChoice)
            kind = AdvancedCreationAnswerKind.Text;

        return new AdvancedCreationQuestion(
            q.Id!.Trim(), kind, q.Label!.Trim(), DraftParsing.Clean(q.Help), options, q.Optional);
    }

    private static string SystemPrompt(AdvancedCreationMode mode)
    {
        var builder = new StringBuilder();
        builder.Append("You are helping someone design ").Append(mode.SubjectPreamble).AppendLine();
        builder.AppendLine();
        builder.AppendLine(
            "Work in the language the person writes in. Reply with ONLY a JSON object — no prose, no code "
            + "fences. There are exactly two shapes you may reply with.");
        builder.AppendLine();
        builder.AppendLine("To ask for what you cannot reasonably infer:");
        builder.AppendLine("""
            {"state":"ask","summary":"<one line on what you understand so far>","questions":[
              {"id":"<short key>","kind":"text|longtext|choice|multichoice|sample",
               "label":"<the question>","help":"<one line of context, optional>",
               "options":["<only for choice/multichoice>"],"optional":true|false}]}
            """);
        builder.AppendLine();
        builder.AppendLine(
            $"Ask at most {MaxQuestionsPerTurn} questions per reply, and only what genuinely changes the "
            + "result — never what you can infer, and never the same ground twice. Use \"sample\" when a "
            + "real example would teach you more than a description. Stop asking as soon as you can write "
            + "something good.");
        builder.AppendLine();
        builder.AppendLine("When you have enough, reply instead with:");
        builder.AppendLine("""{"state":"done","summary":"<one line>","draft":{ ... }}""");
        builder.AppendLine();
        builder.AppendLine("where \"draft\" has exactly these keys:");
        builder.AppendLine(mode.DraftKeysBlock);

        if (!string.IsNullOrWhiteSpace(mode.ExtraContext))
        {
            builder.AppendLine();
            builder.AppendLine(mode.ExtraContext);
        }

        return builder.ToString();
    }

    private sealed class TurnDto
    {
        public string? State { get; set; }
        public string? Summary { get; set; }
        public List<QuestionDto>? Questions { get; set; }
        public JsonElement? Draft { get; set; }
    }

    private sealed class QuestionDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Label { get; set; }
        public string? Help { get; set; }
        public List<string>? Options { get; set; }
        public bool Optional { get; set; }
    }
}
