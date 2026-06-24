using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        string? mode = null,
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
        var languagePrompt = $"Target language: {targetLanguage}";
        var voiceCleanupPrompt = isVoiceInput
            ? "\nThe following input was transcribed from spoken word. Clean it up by removing filler words (um, uh, you know, etc.), false starts, repetitions, and other speech artifacts that wouldn't appear in written text. Make the text flow naturally as written prose while preserving the original meaning and intent.\n"
            : "";

        var outputInstruction = "ONLY the transformed text. No introductions, explanations, labels — just the final text itself without any md, xml, or other formattings.";
        var prompt = $"Base prompt: {template.Prompt}\nOutput: {outputInstruction}{voiceCleanupPrompt}\n{languagePrompt}\nInput: {processedInput}";
        AiCompletionResult completion;
        if (provider.ProviderType == AiProviderType.PiaCloud)
        {
            // For built-in templates the server already has the prompt body; for custom
            // templates we have to send it inline since the server's registry won't know it.
            var customPrompt = template.IsBuiltIn ? null : template.Prompt;
            var customName = template.IsBuiltIn ? null : template.Name;
            completion = await _aiClientService.OptimizeViaPiaCloudAsync(
                processedInput, templateId, targetLanguage, isVoiceInput, mode,
                customPrompt, customName, cancellationToken);
        }
        else
        {
            completion = await _aiClientService.SendRequestAsync(provider, prompt, cancellationToken);
        }

        stopwatch.Stop();

        var session = new OptimizationSession
        {
            OriginalText = inputText,
            OptimizedText = completion.Text,
            TemplateId = template.Id,
            TemplateName = template.Name,
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            WasTranscribed = isVoiceInput,
            TokensUsed = completion.TokensUsed,
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
            : await _providerService.GetDefaultProviderForModeAsync(WindowMode.Optimize);

        if (provider is null)
            throw new InvalidOperationException("No AI provider configured");

        if (provider.ProviderType == AiProviderType.PiaCloud)
        {
            return await _aiClientService.GeneratePromptViaPiaCloudAsync(styleDescription);
        }

        var extractionPrompt = $@"Based on the following style description, create a concise prompt (2-4 sentences) that instructs an AI to rewrite any input text to match the described style. The prompt should capture:
1. The tone (formal, casual, professional, friendly, etc.)
2. Sentence structure and complexity
3. Vocabulary level and word choice
4. Any specific formatting or structural patterns

Style description:
{styleDescription}

Provide only the generated prompt, no additional explanation.";

        var completion = await _aiClientService.SendRequestAsync(provider, extractionPrompt);
        return completion.Text;
    }

    public async Task<PersonaDraft> GeneratePersonaDraftAsync(string description, Guid? providerId = null)
    {
        var provider = providerId.HasValue
            ? await _providerService.GetProviderAsync(providerId.Value)
            : await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);

        if (provider is null)
            throw new InvalidOperationException("No AI provider configured");

        var draftPrompt = $@"You are designing an AI assistant persona from a short description. Return ONLY a JSON object (no prose, no code fences) with exactly these keys:
- ""name"": a short display name (max 40 characters)
- ""tagline"": a one-line summary (max 120 characters)
- ""systemPrompt"": a 2-5 sentence identity/voice instruction written in the second person (""You are…"") that fully defines how the assistant should speak and behave
- ""guardrails"": one or two sentences of constraints the assistant must respect (e.g. topics to avoid, disclaimers); use an empty string if none apply
- ""outputFormat"": 3-6 short bullet points (each on its own line, starting with ""- "") describing how this persona should format its replies — length, prose vs. structure, when to use headings, lists, tables, or code; tailor it to the persona; use an empty string if nothing special applies
- ""archetype"": exactly one of ""assistant"", ""analyst"", ""creative"", ""visionary"", ""explainer"", ""custom""
- ""emoji"": a single emoji that represents the persona
- ""accentColor"": a hex colour like ""#7C4DFF""
- ""expertise"": an array of up to 6 short domain tags

Description:
{description}";

        // Route through the same streaming chat path that Assistant conversations use. Pia Cloud's
        // /api/ai/chat only returns the expected shape on the streaming path (its non-streaming
        // response shape is unsupported here), and this is the proven path for every provider. The
        // system message keeps reasoning models from wrapping the JSON in think/commentary, which
        // would defeat the extraction in ParsePersonaDraft.
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System,
                "You produce only the requested output. Do not reason, think, or explain."),
            new(Microsoft.Extensions.AI.ChatRole.User, draftPrompt),
        };

        var buffer = new StringBuilder();
        await foreach (var item in _aiClientService.GetChatCompletionWithToolsAsync(
            messages, provider, tools: null, toolHandler: null, mode: nameof(WindowMode.Assistant)))
        {
            if (item is TextDelta delta)
                buffer.Append(delta.Text);
        }

        return ParsePersonaDraft(buffer.ToString());
    }

    private static PersonaDraft ParsePersonaDraft(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
            return new PersonaDraft(null, null, raw.Trim(), null, null, null, null, null, null);

        try
        {
            var dto = JsonSerializer.Deserialize<PersonaDraftDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (dto is null)
                return new PersonaDraft(null, null, raw.Trim(), null, null, null, null, null, null);

            return new PersonaDraft(
                Clean(dto.Name),
                Clean(dto.Tagline),
                Clean(dto.SystemPrompt),
                Clean(dto.Guardrails),
                Clean(dto.OutputFormat),
                Clean(dto.Archetype),
                Clean(dto.Emoji),
                Clean(dto.AccentColor),
                dto.Expertise?.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList());
        }
        catch (JsonException)
        {
            // Model didn't return valid JSON — fall back to using the raw text as the system prompt.
            return new PersonaDraft(null, null, raw.Trim(), null, null, null, null, null, null);
        }

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // Extracts the first {...} object from a model response, tolerating code fences / surrounding prose.
    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }

    private sealed class PersonaDraftDto
    {
        public string? Name { get; set; }
        public string? Tagline { get; set; }
        public string? SystemPrompt { get; set; }
        public string? Guardrails { get; set; }
        public string? OutputFormat { get; set; }
        public string? Archetype { get; set; }
        public string? Emoji { get; set; }
        public string? AccentColor { get; set; }
        public List<string>? Expertise { get; set; }
    }
}
