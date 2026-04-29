using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchService : IResearchService
{
    private readonly IAiClientService _aiClientService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ResearchService> _logger;

    public ResearchService(
        IAiClientService aiClientService,
        ILocalizationService localizationService,
        ILogger<ResearchService> logger)
    {
        _aiClientService = aiClientService;
        _localizationService = localizationService;
        _logger = logger;
    }

    private static string GetLanguageName(TargetLanguage language) => language switch
    {
        TargetLanguage.DE => "German",
        TargetLanguage.FR => "French",
        _ => "English"
    };

    private string BuildLanguageInstruction()
    {
        var languageName = GetLanguageName(_localizationService.CurrentLanguage);
        return $"Always respond to the user in {languageName} unless the user explicitly writes in another language or asks you to switch.";
    }

    private static string BuildLengthInstruction(ResearchAnswerLength length) => length switch
    {
        ResearchAnswerLength.Concise => "Keep the answer brief and to the point: 1-2 short paragraphs or a tight bullet list. Skip preamble and obvious context. Prefer signal over completeness.",
        ResearchAnswerLength.Detailed => "Provide a thorough, well-structured answer. Include relevant nuance, examples, and edge cases where they aid understanding.",
        _ => "Provide a clear, well-structured answer with the necessary detail. Include essential context and key points without padding or excessive elaboration."
    };

    public async Task ExecuteResearchAsync(ResearchSession session, AiProvider provider, ResearchAnswerLength answerLength, CancellationToken ct)
    {
        var stepNumber = 1;
        session.Status = ResearchStatus.InProgress;

        try
        {
            // Phase 1: Decompose
            var decomposeStep = new ResearchStep(stepNumber++, _localizationService["Research_Step_Decompose"])
            {
                IsExpanded = true
            };
            session.Steps.Add(decomposeStep);
            decomposeStep.Status = ResearchStatus.InProgress;
            decomposeStep.StartedAt = DateTime.Now;
            decomposeStep.IsStreaming = true;

            var decomposePrompt = $"""
                ## Identity

                You are Pia, a research assistant.
                The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

                ## Language

                {BuildLanguageInstruction()}

                ## Task

                When given a research question, break it down into 2-4 specific sub-questions that need to be answered to fully address the main question. Output ONLY a numbered list (1. 2. 3. etc.) with one sub-question per line. Do not include any other text.
                Every sub-question needs to address a very specific aspect of the prompt and should avoid duplicating content with other sub-questions. If not confident: less is more!
                """;

            var decomposeHistory = new List<ChatMessage>
            {
                new(ChatRole.System, decomposePrompt),
                new(ChatRole.User, session.Query)
            };

            var decomposeStartTicks = Environment.TickCount64;
            var decomposeTokens = 0;
            _logger.LogInformation("Research step {StepNumber} '{Title}' streaming start", decomposeStep.StepNumber, decomposeStep.Title);
            await foreach (var token in _aiClientService.StreamChatCompletionAsync(decomposeHistory, provider, nameof(WindowMode.Research), ct))
            {
                decomposeStep.Content += token;
                decomposeTokens++;
            }
            _logger.LogInformation(
                "Research step {StepNumber} '{Title}' streaming end: {Tokens} tokens, {Chars} chars in {ElapsedMs}ms",
                decomposeStep.StepNumber, decomposeStep.Title, decomposeTokens, decomposeStep.Content.Length, Environment.TickCount64 - decomposeStartTicks);

            decomposeStep.IsStreaming = false;
            if (decomposeTokens == 0 || decomposeStep.Content.Length < 8)
            {
                _logger.LogWarning(
                    "Research step {StepNumber} '{Title}' ended with suspiciously little content ({Tokens} tokens, {Chars} chars)",
                    decomposeStep.StepNumber, decomposeStep.Title, decomposeTokens, decomposeStep.Content.Length);
                decomposeStep.ErrorMessage = _localizationService["Research_Error_EmptyResponse"];
                decomposeStep.Status = ResearchStatus.Failed;
                decomposeStep.CompletedAt = DateTime.Now;
                throw new InvalidOperationException("Decompose step returned an empty response.");
            }
            decomposeStep.Status = ResearchStatus.Completed;
            decomposeStep.CompletedAt = DateTime.Now;

            var subQuestions = ParseSubQuestions(decomposeStep.Content);

            // Phase 2: Research each sub-question in parallel with staggered start
            var researchSystemPrompt = $"""
                ## Identity

                You are Pia, a research assistant. Provide well-structured answers to research questions using the knowledge available to you.
                The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

                ## Language

                {BuildLanguageInstruction()}

                ## Answer Length

                {BuildLengthInstruction(answerLength)}
                """;

            // Pre-create all step rows on the calling (UI) thread so ordering is stable
            var researchSteps = new List<ResearchStep>(subQuestions.Count);
            foreach (var sq in subQuestions)
            {
                var step = new ResearchStep(stepNumber++, _localizationService.Format("Research_Step_Researching", sq))
                {
                    Status = ResearchStatus.Pending
                };
                session.Steps.Add(step);
                researchSteps.Add(step);
            }

            // First question starts immediately; each subsequent one is delayed
            // by an additional random 1-3s on top of the previous question's delay.
            var tasks = new List<Task>(subQuestions.Count);
            var cumulativeDelayMs = 0;
            for (var i = 0; i < subQuestions.Count; i++)
            {
                if (i > 0)
                    cumulativeDelayMs += Random.Shared.Next(1000, 3001);

                var index = i;
                var startDelayMs = cumulativeDelayMs;
                tasks.Add(RunSubQuestionAsync(
                    researchSteps[index],
                    subQuestions[index],
                    index,
                    subQuestions,
                    researchSystemPrompt,
                    provider,
                    startDelayMs,
                    ct));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Per-task failures are recorded inside RunSubQuestionAsync.
                // OperationCanceledException bubbles to outer catch.
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
            }

            // Phase 3: Synthesize
            ct.ThrowIfCancellationRequested();

            var synthesizeStep = new ResearchStep(stepNumber, _localizationService["Research_Step_Synthesize"])
            {
                IsExpanded = true
            };
            session.Steps.Add(synthesizeStep);
            synthesizeStep.Status = ResearchStatus.InProgress;
            synthesizeStep.StartedAt = DateTime.Now;
            synthesizeStep.IsStreaming = true;

            var synthesizeHistory = new List<ChatMessage>
            {
                new(ChatRole.System, researchSystemPrompt),
                new(ChatRole.User, session.Query),
                new(ChatRole.Assistant, decomposeStep.Content)
            };

            for (var i = 0; i < subQuestions.Count; i++)
            {
                var step = researchSteps[i];
                if (step.Status != ResearchStatus.Completed || string.IsNullOrWhiteSpace(step.Content))
                    continue;

                synthesizeHistory.Add(new ChatMessage(ChatRole.User, $"Sub-question {i + 1}: {subQuestions[i]}"));
                synthesizeHistory.Add(new ChatMessage(ChatRole.Assistant, step.Content));
            }

            synthesizeHistory.Add(new ChatMessage(ChatRole.User,
                "Now synthesize all the research findings above into a well-structured answer to the original question. Use Markdown formatting (headings such as ##, bullet points, bold key terms, and code blocks) for clarity. Organize the information logically. Include key findings, conclusions, and any important caveats. Respect the answer length guidance from the system prompt. Do not wrap your reply in a fenced code block."));

            var synthStartTicks = Environment.TickCount64;
            var synthTokens = 0;
            _logger.LogInformation("Research step {StepNumber} '{Title}' streaming start", synthesizeStep.StepNumber, synthesizeStep.Title);
            try
            {
                await foreach (var token in _aiClientService.StreamChatCompletionAsync(synthesizeHistory, provider, nameof(WindowMode.Research), ct))
                {
                    synthesizeStep.Content += token;
                    session.SynthesizedResult += token;
                    synthTokens++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Synthesis streaming failed after {Tokens} tokens, {Chars} chars", synthTokens, synthesizeStep.Content.Length);
                synthesizeStep.IsStreaming = false;
                synthesizeStep.ErrorMessage = ex.Message;
                synthesizeStep.Status = ResearchStatus.Failed;
                synthesizeStep.CompletedAt = DateTime.Now;
                throw;
            }
            _logger.LogInformation(
                "Research step {StepNumber} '{Title}' streaming end: {Tokens} tokens, {Chars} chars in {ElapsedMs}ms",
                synthesizeStep.StepNumber, synthesizeStep.Title, synthTokens, synthesizeStep.Content.Length, Environment.TickCount64 - synthStartTicks);

            synthesizeStep.IsStreaming = false;
            if (synthTokens == 0 || synthesizeStep.Content.Length < 8)
            {
                _logger.LogWarning(
                    "Research step {StepNumber} '{Title}' ended with suspiciously little content ({Tokens} tokens, {Chars} chars)",
                    synthesizeStep.StepNumber, synthesizeStep.Title, synthTokens, synthesizeStep.Content.Length);
                synthesizeStep.ErrorMessage = _localizationService["Research_Error_EmptyResponse"];
                synthesizeStep.Status = ResearchStatus.Failed;
                synthesizeStep.CompletedAt = DateTime.Now;
                throw new InvalidOperationException("Synthesis step returned an empty response.");
            }
            synthesizeStep.Status = ResearchStatus.Completed;
            synthesizeStep.CompletedAt = DateTime.Now;

            session.Status = ResearchStatus.Completed;
            session.CompletedAt = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            MarkCurrentStepsCancelled(session);
            session.Status = ResearchStatus.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Research failed for session {SessionId}", session.Id);
            MarkCurrentStepsFailed(session);
            session.Status = ResearchStatus.Failed;
            throw;
        }
    }

    private async Task RunSubQuestionAsync(
        ResearchStep step,
        string subQuestion,
        int index,
        IReadOnlyList<string> allSubQuestions,
        string systemPrompt,
        AiProvider provider,
        int startDelayMs,
        CancellationToken ct)
    {
        try
        {
            if (startDelayMs > 0)
                await Task.Delay(startDelayMs, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            step.Status = ResearchStatus.InProgress;
            step.StartedAt = DateTime.Now;
            step.IsStreaming = true;

            var awareness = new StringBuilder();
            awareness.AppendLine($"You are answering sub-question {index + 1} of {allSubQuestions.Count} for the user's research request.");
            if (allSubQuestions.Count > 1)
            {
                awareness.AppendLine("The other sub-questions are being answered separately in parallel:");
                for (var i = 0; i < allSubQuestions.Count; i++)
                {
                    if (i == index) continue;
                    awareness.AppendLine($"- {allSubQuestions[i]}");
                }
                awareness.AppendLine("Focus tightly on YOUR sub-question. Do not duplicate ground covered by the others; trust that they will be answered.");
            }

            var history = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt + "\n\n## Awareness\n\n" + awareness),
                new(ChatRole.User,
                    $"Research and provide an answer to this sub-question. Use Markdown formatting (headings, lists, bold, code blocks) for clarity:\n\n{subQuestion}")
            };

            var startTicks = Environment.TickCount64;
            var tokenCount = 0;
            _logger.LogInformation("Research step {StepNumber} '{Title}' streaming start", step.StepNumber, step.Title);
            await foreach (var token in _aiClientService.StreamChatCompletionAsync(history, provider, nameof(WindowMode.Research), ct).ConfigureAwait(false))
            {
                step.Content += token;
                tokenCount++;
            }
            _logger.LogInformation(
                "Research step {StepNumber} '{Title}' streaming end: {Tokens} tokens, {Chars} chars in {ElapsedMs}ms",
                step.StepNumber, step.Title, tokenCount, step.Content.Length, Environment.TickCount64 - startTicks);

            step.IsStreaming = false;
            if (tokenCount == 0 || step.Content.Length < 8)
            {
                _logger.LogWarning(
                    "Research step {StepNumber} '{Title}' ended with suspiciously little content ({Tokens} tokens, {Chars} chars)",
                    step.StepNumber, step.Title, tokenCount, step.Content.Length);
                step.ErrorMessage = _localizationService["Research_Error_EmptyResponse"];
                step.Status = ResearchStatus.Failed;
                step.CompletedAt = DateTime.Now;
                return;
            }
            step.Status = ResearchStatus.Completed;
            step.CompletedAt = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            step.IsStreaming = false;
            step.Status = ResearchStatus.Cancelled;
            step.CompletedAt = DateTime.Now;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sub-question research failed: {SubQuestion}", subQuestion);
            step.IsStreaming = false;
            step.ErrorMessage = ex.Message;
            step.Status = ResearchStatus.Failed;
            step.CompletedAt = DateTime.Now;
            // Do not rethrow: failure of one sub-question must not abort siblings.
        }
    }

    private static List<string> ParseSubQuestions(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var questions = new List<string>();

        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"^\d+[\.\)]\s*(.+)");
            if (match.Success)
            {
                questions.Add(match.Groups[1].Value.Trim());
            }
        }

        if (questions.Count == 0)
        {
            questions.Add(content.Trim());
        }

        return questions;
    }

    private static void MarkCurrentStepsCancelled(ResearchSession session)
    {
        foreach (var step in session.Steps)
        {
            if (step.Status == ResearchStatus.InProgress || step.Status == ResearchStatus.Pending)
            {
                step.IsStreaming = false;
                step.Status = ResearchStatus.Cancelled;
                step.CompletedAt = DateTime.Now;
            }
        }
    }

    private static void MarkCurrentStepsFailed(ResearchSession session)
    {
        foreach (var step in session.Steps)
        {
            if (step.Status == ResearchStatus.InProgress || step.Status == ResearchStatus.Pending)
            {
                step.IsStreaming = false;
                step.Status = ResearchStatus.Failed;
                step.CompletedAt = DateTime.Now;
            }
        }
    }
}
