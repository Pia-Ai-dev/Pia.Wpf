using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class TokenizingAiClientService : IAiClientService
{
    private static readonly string[] WriteOperations =
    [
        "create_object", "update_object", "append_to_list", "delete_object",
        "create_reminder", "update_reminder", "delete_reminder",
        "create_todo", "update_todo", "complete_todo", "delete_todo"
    ];

    private readonly IAiClientService _inner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;
    private bool? _enabled;

    public TokenizingAiClientService(
        IAiClientService inner,
        IServiceProvider serviceProvider,
        ISettingsService settingsService)
    {
        _inner = inner;
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
    }

    private ITokenMapService? TryGetTokenMapService() =>
        _serviceProvider.GetService<ITokenMapService>();

    private bool _initialized;

    private async Task<bool> IsEnabledAsync()
    {
        if (_enabled.HasValue) return _enabled.Value;
        var tokenMapService = TryGetTokenMapService();
        if (tokenMapService is null)
        {
            _enabled = false;
            return false;
        }
        var settings = await _settingsService.GetSettingsAsync();
        _enabled = settings.Privacy.TokenizationEnabled;

        if (_enabled.Value && !_initialized)
        {
            _initialized = true;
            await tokenMapService.InitializeAsync();
        }

        return _enabled.Value;
    }

    public async Task<string> SendRequestAsync(AiProvider provider, string prompt, CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
            return await _inner.SendRequestAsync(provider, prompt, cancellationToken);

        var tokenizedPrompt = TryGetTokenMapService()!.TokenizeStructuredResult(prompt);
        var result = await _inner.SendRequestAsync(provider, tokenizedPrompt, cancellationToken);
        return TryGetTokenMapService()!.Detokenize(result);
    }

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
        {
            await foreach (var token in _inner.StreamChatCompletionAsync(messages, provider, cancellationToken))
                yield return token;
            yield break;
        }

        var tokenizedMessages = TokenizeMessages(messages);
        var tokenBuffer = new StringBuilder();
        var isBuffering = false;

        await foreach (var token in _inner.StreamChatCompletionAsync(tokenizedMessages, provider, cancellationToken))
        {
            var detokenized = BufferedDetokenize(token, tokenBuffer, ref isBuffering);
            if (detokenized.Length > 0)
                yield return detokenized;
        }

        // Flush any remaining buffer
        if (tokenBuffer.Length > 0)
            yield return TryGetTokenMapService()!.Detokenize(tokenBuffer.ToString());
    }

    public async Task<ChatResponse> GetChatResponseAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
            return await _inner.GetChatResponseAsync(messages, provider, tools, cancellationToken);

        var tokenizedMessages = TokenizeMessages(messages);
        var response = await _inner.GetChatResponseAsync(tokenizedMessages, provider, tools, cancellationToken);

        // Detokenize text in response messages
        foreach (var msg in response.Messages)
        {
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrEmpty(msg.Text))
            {
                var detokenized = TryGetTokenMapService()!.Detokenize(msg.Text);
                if (detokenized != msg.Text)
                {
                    msg.Contents.Clear();
                    msg.Contents.Add(new TextContent(detokenized));
                }
            }
        }

        return response;
    }

    public async IAsyncEnumerable<string> GetChatCompletionWithToolsAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        Func<FunctionCallContent, Task<object?>>? toolHandler = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
        {
            await foreach (var token in _inner.GetChatCompletionWithToolsAsync(messages, provider, tools, toolHandler, cancellationToken))
                yield return token;
            yield break;
        }

        var tokenizedMessages = TokenizeMessages(messages);
        var wrappedHandler = toolHandler is not null ? WrapToolHandler(toolHandler) : null;
        var tokenBuffer = new StringBuilder();
        var isBuffering = false;

        await foreach (var token in _inner.GetChatCompletionWithToolsAsync(
            tokenizedMessages, provider, tools, wrappedHandler, cancellationToken))
        {
            var detokenized = BufferedDetokenize(token, tokenBuffer, ref isBuffering);
            if (detokenized.Length > 0)
                yield return detokenized;
        }

        // Flush any remaining buffer
        if (tokenBuffer.Length > 0)
            yield return TryGetTokenMapService()!.Detokenize(tokenBuffer.ToString());
    }

    public async Task<string> OptimizeViaPiaCloudAsync(
        string text, Guid templateId, string language, bool isVoiceInput,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
            return await _inner.OptimizeViaPiaCloudAsync(text, templateId, language, isVoiceInput, cancellationToken);

        var tokenizedText = TryGetTokenMapService()!.TokenizeStructuredResult(text);
        var result = await _inner.OptimizeViaPiaCloudAsync(tokenizedText, templateId, language, isVoiceInput, cancellationToken);
        return TryGetTokenMapService()!.Detokenize(result);
    }

    public Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestToolCallingAsync(provider, cancellationToken);

    public Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestStreamingAsync(provider, cancellationToken);

    public Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default)
        => _inner.TestPiaCloudConnectionAsync(cancellationToken);

    private IList<ChatMessage> TokenizeMessages(IList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.User && !string.IsNullOrEmpty(msg.Text))
            {
                result.Add(new ChatMessage(ChatRole.User, TryGetTokenMapService()!.TokenizeStructuredResult(msg.Text)));
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    private Func<FunctionCallContent, Task<object?>> WrapToolHandler(Func<FunctionCallContent, Task<object?>> handler)
    {
        return async toolCall =>
        {
            // Detokenize string arguments on write operations
            if (IsWriteOperation(toolCall.Name))
                DetokenizeToolCallArguments(toolCall);

            var result = await handler(toolCall);

            // Tokenize string results so the AI sees tokens, not real values
            if (result is string resultStr)
                return TryGetTokenMapService()!.TokenizeStructuredResult(resultStr);

            return result;
        };
    }

    private void DetokenizeToolCallArguments(FunctionCallContent toolCall)
    {
        if (toolCall.Arguments is null) return;

        var keys = toolCall.Arguments.Keys.ToList();
        foreach (var key in keys)
        {
            var value = toolCall.Arguments[key];
            string? strValue = value switch
            {
                string s => s,
                System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
                _ => null
            };

            if (strValue is not null)
            {
                toolCall.Arguments[key] = TryGetTokenMapService()!.Detokenize(strValue);
            }
        }
    }

    private string BufferedDetokenize(string token, StringBuilder tokenBuffer, ref bool isBuffering)
    {
        var result = new StringBuilder();
        foreach (var ch in token)
        {
            if (ch == '[' && !isBuffering)
            {
                isBuffering = true;
                tokenBuffer.Clear();
                tokenBuffer.Append(ch);
            }
            else if (isBuffering)
            {
                tokenBuffer.Append(ch);
                if (ch == ']')
                {
                    var candidate = tokenBuffer.ToString();
                    result.Append(TryGetTokenMapService()!.Detokenize(candidate));
                    tokenBuffer.Clear();
                    isBuffering = false;
                }
                else if (tokenBuffer.Length > 30)
                {
                    result.Append(tokenBuffer);
                    tokenBuffer.Clear();
                    isBuffering = false;
                }
            }
            else
            {
                result.Append(ch);
            }
        }

        return result.ToString();
    }

    private static bool IsWriteOperation(string toolName) =>
        WriteOperations.Contains(toolName);
}
