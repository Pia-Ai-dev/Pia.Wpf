using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class LoggingAiClientService : IAiClientService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IAiClientService _inner;
    private readonly IPromptLogService _logService;

    public LoggingAiClientService(IAiClientService inner, IPromptLogService logService)
    {
        _inner = inner;
        _logService = logService;
    }

    public async Task<string> SendRequestAsync(AiProvider provider, string prompt, CancellationToken cancellationToken = default)
    {
        await _logService.LogAsync(
            "SendRequest",
            provider.Endpoint ?? "direct",
            provider.Name,
            prompt);

        return await _inner.SendRequestAsync(provider, prompt, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _logService.LogAsync(
            "ChatStream",
            provider.Endpoint ?? "direct",
            provider.Name,
            FormatMessages(messages));

        await foreach (var token in _inner.StreamChatCompletionAsync(messages, provider, cancellationToken))
            yield return token;
    }

    public async Task<ChatResponse> GetChatResponseAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        await _logService.LogAsync(
            "ChatResponse",
            provider.Endpoint ?? "direct",
            provider.Name,
            FormatMessagesWithTools(messages, tools));

        return await _inner.GetChatResponseAsync(messages, provider, tools, cancellationToken);
    }

    public async IAsyncEnumerable<string> GetChatCompletionWithToolsAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        Func<FunctionCallContent, Task<object?>>? toolHandler = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _logService.LogAsync(
            "ChatWithTools",
            provider.Endpoint ?? "direct",
            provider.Name,
            FormatMessagesWithTools(messages, tools));

        await foreach (var token in _inner.GetChatCompletionWithToolsAsync(messages, provider, tools, toolHandler, cancellationToken))
            yield return token;
    }

    public async Task<string> OptimizeViaPiaCloudAsync(
        string text, Guid templateId, string language, bool isVoiceInput,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            text,
            templateId,
            language,
            isVoiceInput
        }, JsonOptions);

        await _logService.LogAsync(
            "Optimize",
            "/api/ai/optimize",
            "Pia Cloud",
            payload);

        return await _inner.OptimizeViaPiaCloudAsync(text, templateId, language, isVoiceInput, cancellationToken);
    }

    public async Task<string> GeneratePromptViaPiaCloudAsync(
        string styleDescription, CancellationToken cancellationToken = default)
    {
        await _logService.LogAsync(
            "GeneratePrompt",
            "/api/ai/generate-prompt",
            "Pia Cloud",
            styleDescription);

        return await _inner.GeneratePromptViaPiaCloudAsync(styleDescription, cancellationToken);
    }

    public Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestToolCallingAsync(provider, cancellationToken);

    public Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestStreamingAsync(provider, cancellationToken);

    public Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default)
        => _inner.TestPiaCloudConnectionAsync(cancellationToken);

    private static string FormatMessages(IList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.AppendLine($"[{msg.Role}]");
            sb.AppendLine(msg.Text ?? "(non-text content)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatMessagesWithTools(IList<ChatMessage> messages, IList<AITool>? tools)
    {
        var sb = new StringBuilder();

        if (tools is { Count: > 0 })
        {
            sb.AppendLine("Tools:");
            foreach (var tool in tools)
            {
                if (tool is AIFunction func)
                    sb.AppendLine($"  - {func.Name}: {func.Description}");
                else
                    sb.AppendLine($"  - {tool.GetType().Name}");
            }
            sb.AppendLine();
        }

        sb.Append(FormatMessages(messages));
        return sb.ToString();
    }
}
