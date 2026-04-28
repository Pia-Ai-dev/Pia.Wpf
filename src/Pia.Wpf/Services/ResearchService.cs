using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ResearchService : IResearchService
{
    private readonly IAiClientService _aiClientService;
    private readonly IPluginService _pluginService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ResearchService> _logger;

    public ResearchService(
        IAiClientService aiClientService,
        IPluginService pluginService,
        ILocalizationService localizationService,
        ILogger<ResearchService> logger)
    {
        _aiClientService = aiClientService;
        _pluginService = pluginService;
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

    public async Task ExecuteResearchAsync(ResearchSession session, AiProvider provider, CancellationToken ct)
    {
        var conversationHistory = new List<ChatMessage>();
        var stepNumber = 1;
        var tools = _pluginService.GetAllTools();
        var pluginPrompts = _pluginService.GetCombinedSystemPromptAdditions();

        session.Status = ResearchStatus.InProgress;

        try
        {
            // Phase 1: Decompose
            var decomposeStep = new ResearchStep(stepNumber++, "Analyzing and decomposing research question");
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

            conversationHistory.Add(new ChatMessage(ChatRole.System, decomposePrompt));
            conversationHistory.Add(new ChatMessage(ChatRole.User, session.Query));

            await foreach (var token in _aiClientService.StreamChatCompletionAsync(conversationHistory, provider, nameof(WindowMode.Research), ct))
            {
                decomposeStep.Content += token;
            }

            decomposeStep.IsStreaming = false;
            decomposeStep.Status = ResearchStatus.Completed;
            decomposeStep.CompletedAt = DateTime.Now;

            conversationHistory.Add(new ChatMessage(ChatRole.Assistant, decomposeStep.Content));

            var subQuestions = ParseSubQuestions(decomposeStep.Content);

            // Phase 2: Research each sub-question (with tools if available)
            var researchSystemPrompt = $"""
                ## Identity

                You are Pia, a research assistant. Provide detailed, well-structured answers to research questions using the knowledge available to you.
                The current date and time is {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).

                ## Language

                {BuildLanguageInstruction()}
                """;
            if (tools.Count > 0 && !string.IsNullOrEmpty(pluginPrompts))
            {
                researchSystemPrompt += "\n\n" + pluginPrompts;
            }
            conversationHistory[0] = new ChatMessage(ChatRole.System, researchSystemPrompt);

            foreach (var subQuestion in subQuestions)
            {
                ct.ThrowIfCancellationRequested();

                var researchStep = new ResearchStep(stepNumber++, $"Researching: {subQuestion}");
                session.Steps.Add(researchStep);
                researchStep.Status = ResearchStatus.InProgress;
                researchStep.StartedAt = DateTime.Now;
                researchStep.IsStreaming = true;

                conversationHistory.Add(new ChatMessage(ChatRole.User,
                    $"Now research and provide a detailed answer to this sub-question. Use Markdown formatting (headings, lists, bold, code blocks) for clarity: {subQuestion}"));

                if (tools.Count > 0)
                {
                    await foreach (var token in _aiClientService.GetChatCompletionWithToolsAsync(
                        conversationHistory, provider, tools,
                        toolCall => HandleResearchToolCallAsync(toolCall, ct),
                        nameof(WindowMode.Research),
                        ct))
                    {
                        researchStep.Content += token;
                    }
                }
                else
                {
                    await foreach (var token in _aiClientService.StreamChatCompletionAsync(conversationHistory, provider, nameof(WindowMode.Research), ct))
                    {
                        researchStep.Content += token;
                    }
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
                "Now synthesize all the research findings above into a comprehensive, well-structured answer to the original question. Format your response in Markdown with clear headings (##), bullet points, bold key terms, and code blocks where appropriate. Organize the information logically. Include key findings, conclusions, and any important caveats."));

            await foreach (var token in _aiClientService.StreamChatCompletionAsync(conversationHistory, provider, nameof(WindowMode.Research), ct))
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

    private async Task<object?> HandleResearchToolCallAsync(FunctionCallContent toolCall, CancellationToken ct)
    {
        _logger.LogInformation("Research tool call: {Tool}", toolCall.Name);

        var result = await _pluginService.RouteToolCallAsync(toolCall, ct);
        if (result is null)
            return "Unknown tool.";

        var (directResult, pendingAction) = result.Value;
        if (directResult is not null)
            return directResult;

        // In research mode, auto-execute without user confirmation
        if (pendingAction is not null)
            return await pendingAction.Execute();

        return "Tool call handled.";
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
