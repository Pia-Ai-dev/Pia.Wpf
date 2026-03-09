using System.Diagnostics;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class TextOptimizationService : ITextOptimizationService
{
    private readonly ITemplateService _templateService;
    private readonly IProviderService _providerService;
    private readonly IHistoryService _historyService;
    private readonly IAiClientService _aiClientService;

    public TextOptimizationService(
        ITemplateService templateService,
        IProviderService providerService,
        IHistoryService historyService,
        IAiClientService aiClientService)
    {
        _templateService = templateService;
        _providerService = providerService;
        _historyService = historyService;
        _aiClientService = aiClientService;
    }

    public async Task<OptimizationSession> OptimizeTextAsync(
        string inputText,
        Guid templateId,
        Guid? providerId = null,
        string targetLanguage = "EN",
        CancellationToken cancellationToken = default)
    {
        var template = await _templateService.GetTemplateAsync(templateId)
            ?? throw new InvalidOperationException($"Template with id {templateId} not found");

        var provider = providerId.HasValue
            ? await _providerService.GetProviderAsync(providerId.Value)
            : await _providerService.GetDefaultProviderAsync();

        if (provider is null)
            throw new InvalidOperationException("No AI provider configured");

        var isVoiceInput = inputText.StartsWith("<voice>") && inputText.EndsWith("</voice>");
        var processedInput = isVoiceInput
            ? inputText[7..^8] // Remove <voice> and </voice> tags
            : inputText;

        var stopwatch = Stopwatch.StartNew();
        string optimizedText;
        if (provider.ProviderType == AiProviderType.PiaCloud)
        {
            // Server builds the prompt — send raw text + template ID
            optimizedText = await _aiClientService.OptimizeViaPiaCloudAsync(
                processedInput, templateId, targetLanguage, isVoiceInput, cancellationToken);
        }
        else
        {
            // Client builds the prompt — existing logic
            var languagePrompt = $"Please answer in {targetLanguage}.";
            var voiceCleanupPrompt = isVoiceInput
                ? "The following input was transcribed from spoken word. Clean it up by removing filler words (um, uh, like, you know, etc.), false starts, repetitions, and other speech artifacts that wouldn't appear in written text. Make the text flow naturally as written prose while preserving the original meaning and intent.\n\n"
                : "";

            var prompt = $"Base prompt: {template.Prompt}\n\n{voiceCleanupPrompt}{languagePrompt}\n\n{processedInput}";
            optimizedText = await _aiClientService.SendRequestAsync(provider, prompt, cancellationToken);
        }

        stopwatch.Stop();

        var session = new OptimizationSession
        {
            OriginalText = inputText,
            OptimizedText = optimizedText,
            TemplateId = template.Id,
            TemplateName = template.Name,
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            WasTranscribed = isVoiceInput,
            TokensUsed = 0,
            ProcessingTimeMs = stopwatch.ElapsedMilliseconds
        };

        await _historyService.AddSessionAsync(session);

        return session;
    }

    public async Task<bool> ValidateInputAsync(string inputText, Guid templateId)
    {
        if (string.IsNullOrWhiteSpace(inputText))
            return false;

        var template = await _templateService.GetTemplateAsync(templateId);
        if (template is null)
            return false;

        var provider = await _providerService.GetDefaultProviderAsync();
        if (provider is null)
            return false;

        return true;
    }

    public async Task<string> GeneratePromptAsync(string styleDescription, Guid? providerId = null)
    {
        var provider = providerId.HasValue
            ? await _providerService.GetProviderAsync(providerId.Value)
            : await _providerService.GetDefaultProviderAsync();

        if (provider is null)
            throw new InvalidOperationException("No AI provider configured");

        var extractionPrompt = $@"Based on the following style description, create a concise prompt (2-4 sentences) that instructs an AI to rewrite any input text to match the described style. The prompt should capture:
1. The tone (formal, casual, professional, friendly, etc.)
2. Sentence structure and complexity
3. Vocabulary level and word choice
4. Any specific formatting or structural patterns

Style description:
{styleDescription}

Provide only the generated prompt, no additional explanation.";

        return await _aiClientService.SendRequestAsync(provider, extractionPrompt);
    }
}
