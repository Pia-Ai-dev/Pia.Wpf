using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchService : IResearchService
{
    private readonly IAiClientService _aiClientService;
    private readonly ILogger<ResearchService> _logger;

    public ResearchService(IAiClientService aiClientService, ILogger<ResearchService> logger)
    {
        _aiClientService = aiClientService;
        _logger = logger;
    }

    public async Task ExecuteResearchAsync(ResearchSession session, AiProvider provider, CancellationToken ct)
    {
        var conversationHistory = new List<ChatMessage>();
        var stepNumber = 1;

        session.Status = ResearchStatus.InProgress;

        try
        {
            // Phase 1: Decompose
            var decomposeStep = new ResearchStep(stepNumber++, "Analyzing and decomposing research question");
            session.Steps.Add(decomposeStep);
            decomposeStep.Status = ResearchStatus.InProgress;
            decomposeStep.StartedAt = DateTime.Now;
            decomposeStep.IsStreaming = true;

            conversationHistory.Add(new ChatMessage(ChatRole.System,
                "You are a research assistant. When given a research question, break it down into 3-5 specific sub-questions that need to be answered to fully address the main question. Output ONLY a numbered list (1. 2. 3. etc.) with one sub-question per line. Do not include any other text."));
            conversationHistory.Add(new ChatMessage(ChatRole.User, session.Query));

            await foreach (var token in _aiClientService.StreamChatCompletionAsync(conversationHistory, provider, ct))
            {
                decomposeStep.Content += token;
            }

            decomposeStep.IsStreaming = false;
            decomposeStep.Status = ResearchStatus.Completed;
            decomposeStep.CompletedAt = DateTime.Now;

            conversationHistory.Add(new ChatMessage(ChatRole.Assistant, decomposeStep.Content));

            var subQuestions = ParseSubQuestions(decomposeStep.Content);

            // Phase 2: Research each sub-question
            foreach (var subQuestion in subQuestions)
            {
                ct.ThrowIfCancellationRequested();

                var researchStep = new ResearchStep(stepNumber++, $"Researching: {subQuestion}");
                session.Steps.Add(researchStep);
                researchStep.Status = ResearchStatus.InProgress;
                researchStep.StartedAt = DateTime.Now;
                researchStep.IsStreaming = true;

                conversationHistory.Add(new ChatMessage(ChatRole.User,
                    $"Now research and provide a detailed answer to this sub-question: {subQuestion}"));

                await foreach (var token in _aiClientService.StreamChatCompletionAsync(conversationHistory, provider, ct))
                {
                    researchStep.Content += token;
                }

                researchStep.IsStreaming = false;
                researchStep.Status = ResearchStatus.Completed;
                researchStep.CompletedAt = DateTime.Now;

                conversationHistory.Add(new ChatMessage(ChatRole.Assistant, researchStep.Content));
            }

            // Phase 3: Synthesize
            ct.ThrowIfCancellationRequested();

            var synthesizeStep = new ResearchStep(stepNumber, "Synthesizing final research results");
            session.Steps.Add(synthesizeStep);
            synthesizeStep.Status = ResearchStatus.InProgress;
            synthesizeStep.StartedAt = DateTime.Now;
            synthesizeStep.IsStreaming = true;

            conversationHistory.Add(new ChatMessage(ChatRole.User,
                "Now synthesize all the research findings above into a comprehensive, well-structured answer to the original question. Use clear headings and organize the information logically. Include key findings, conclusions, and any important caveats."));

            await foreach (var token in _aiClientService.StreamChatCompletionAsync(conversationHistory, provider, ct))
            {
                synthesizeStep.Content += token;
                session.SynthesizedResult += token;
            }

            synthesizeStep.IsStreaming = false;
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
            if (step.Status == ResearchStatus.InProgress)
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
            if (step.Status == ResearchStatus.InProgress)
            {
                step.IsStreaming = false;
                step.Status = ResearchStatus.Failed;
                step.CompletedAt = DateTime.Now;
            }
        }
    }
}
