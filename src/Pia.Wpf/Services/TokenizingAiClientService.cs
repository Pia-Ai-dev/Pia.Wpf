using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class TokenizingAiClientService : IAiClientService
{
    private readonly IAiClientService _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<TokenizingAiClientService> _logger;
    private bool? _enabled;
    private IServiceScope? _scope;
    private ITokenMapService? _tokenMapService;

    public TokenizingAiClientService(
        IAiClientService inner,
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        ILogger<TokenizingAiClientService> logger)
    {
        _inner = inner;
        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _settingsService = settingsService;
        _logger = logger;
    }

    private ITokenMapService? TryGetTokenMapService()
    {
        // Prefer the running turn's own map (per-session isolation — prevents
        // cross-chat PII namespace collisions once background turns interleave).
        // Falls back to the lazily-created scope map for all non-turn callers
        // (Optimize, voice one-shots) — unchanged behavior there.
        if (TokenMapAmbient.Current is { } ambient) return ambient;
        if (_tokenMapService is not null) return _tokenMapService;
        _scope = _scopeFactory.CreateScope();
        _tokenMapService = _scope.ServiceProvider.GetService<ITokenMapService>();
        return _tokenMapService;
    }

    private bool _initialized;

    private async Task<bool> IsEnabledAsync()
    {
        if (_enabled.HasValue) return _enabled.Value;
        var tokenMapService = TryGetTokenMapService();
        if (tokenMapService is null)
        {
            _enabled = false;
            TokenizationLatch.Latch(null);
            return false;
        }
        var settings = await _settingsService.GetSettingsAsync();
        _enabled = settings.Privacy.TokenizationEnabled;
        TokenizationLatch.Latch(settings.Privacy);

        if (_enabled.Value && !_initialized)
        {
            _initialized = true;
            await tokenMapService.InitializeAsync();
        }

        return _enabled.Value;
    }

    public async Task<AiCompletionResult> SendRequestAsync(
        AiProvider provider, string prompt, CancellationToken cancellationToken = default, string? mode = null)
    {
        if (!await IsEnabledAsync())
            return await _inner.SendRequestAsync(provider, prompt, cancellationToken, mode);

        var tokenizedPrompt = TryGetTokenMapService()!.TokenizeStructuredResult(prompt);
        var result = await _inner.SendRequestAsync(provider, tokenizedPrompt, cancellationToken, mode);
        var detokenized = TryGetTokenMapService()!.Detokenize(result.Text);
        _logger.LogDebug("Tokenizing.SendRequest: pre-detok length={Pre}, post-detok length={Post}",
            result.Text.Length, detokenized.Length);
        return result with { Text = detokenized };
    }

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        string? mode = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
        {
            await foreach (var token in _inner.StreamChatCompletionAsync(messages, provider, mode, cancellationToken))
                yield return token;
            yield break;
        }

        var tokenizedMessages = TokenizeMessages(messages);
        var tokenBuffer = new StringBuilder();
        var isBuffering = false;

        await foreach (var token in _inner.StreamChatCompletionAsync(tokenizedMessages, provider, mode, cancellationToken))
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
        string? mode = null,
        Guid? managedPersonaId = null,
        string? personaModelType = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
            return await _inner.GetChatResponseAsync(
                messages, provider, tools, mode, managedPersonaId, personaModelType, cancellationToken);

        var tokenizedMessages = TokenizeMessages(messages);
        var response = await _inner.GetChatResponseAsync(
            tokenizedMessages, provider, tools, mode, managedPersonaId, personaModelType, cancellationToken);

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

    public async IAsyncEnumerable<ChatStreamItem> GetChatCompletionWithToolsAsync(
        IList<ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        ToolCallHandler? toolHandler = null,
        string? mode = null,
        Guid? managedPersonaId = null,
        string? personaModelType = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        AgentContextBudget? contextBudget = null)
    {
        // The budget is relayed verbatim on BOTH branches. Note the one-directional skew this decorator
        // introduces: TokenizeMessages rewrites User-role text to PII placeholders BEFORE the inner
        // call, so the byte counts compaction measures downstream are of placeholders rather than of
        // the user's real text. The effect is small and always in the "slightly optimistic" direction.
        _logger.LogDebug("TokenizingAiClientService: relaying GetChatCompletionWithToolsAsync with {ToolCount} tools, tokenization={Enabled}",
            tools?.Count ?? 0, _enabled ?? false);
        if (!await IsEnabledAsync())
        {
            _logger.LogDebug("Tokenization disabled, passing through tool completion");
            await foreach (var item in _inner.GetChatCompletionWithToolsAsync(
                messages, provider, tools, toolHandler, mode, managedPersonaId, personaModelType, cancellationToken, contextBudget))
                yield return item;
            yield break;
        }

        _logger.LogDebug("Tokenization active, wrapping tool handler");
        var tokenizedMessages = TokenizeMessages(messages);
        var wrappedHandler = toolHandler is not null ? WrapToolHandler(toolHandler) : null;
        var tokenBuffer = new StringBuilder();
        var isBuffering = false;
        // Reasoning streams as its own deltas; detokenize it with a SEPARATE buffer so masked
        // PII the model echoes in its reasoning is restored — and, crucially, so ReasoningDelta
        // is forwarded at all (a missing branch here previously dropped all reasoning when
        // tokenization was enabled).
        var reasoningBuffer = new StringBuilder();
        var reasoningIsBuffering = false;

        await foreach (var item in _inner.GetChatCompletionWithToolsAsync(
            tokenizedMessages, provider, tools, wrappedHandler, mode, managedPersonaId, personaModelType, cancellationToken, contextBudget))
        {
            if (item is TextDelta td)
            {
                var detokenized = BufferedDetokenize(td.Text, tokenBuffer, ref isBuffering);
                if (detokenized.Length > 0)
                    yield return new TextDelta(detokenized);
            }
            else if (item is ReasoningDelta rd)
            {
                var detokenized = BufferedDetokenize(rd.Text, reasoningBuffer, ref reasoningIsBuffering);
                if (detokenized.Length > 0)
                    yield return new ReasoningDelta(detokenized);
            }
            else if (item is Finished)
            {
                if (tokenBuffer.Length > 0)
                {
                    yield return new TextDelta(TryGetTokenMapService()!.Detokenize(tokenBuffer.ToString()));
                    tokenBuffer.Clear();
                    isBuffering = false;
                }
                if (reasoningBuffer.Length > 0)
                {
                    yield return new ReasoningDelta(TryGetTokenMapService()!.Detokenize(reasoningBuffer.ToString()));
                    reasoningBuffer.Clear();
                    reasoningIsBuffering = false;
                }
                yield return item;
            }
            else
            {
                // Any other stream item (or future type) passes through untouched.
                yield return item;
            }
        }

        // Safety net flush if Finished was never emitted (e.g. inner faulted before completion)
        if (tokenBuffer.Length > 0)
            yield return new TextDelta(TryGetTokenMapService()!.Detokenize(tokenBuffer.ToString()));
        if (reasoningBuffer.Length > 0)
            yield return new ReasoningDelta(TryGetTokenMapService()!.Detokenize(reasoningBuffer.ToString()));
    }

    public async Task<AiCompletionResult> OptimizeViaPiaCloudAsync(
        string text, Guid templateId, string language, bool isVoiceInput,
        string? mode = null,
        string? customPrompt = null,
        string? customTemplateName = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync())
            return await _inner.OptimizeViaPiaCloudAsync(text, templateId, language, isVoiceInput, mode, customPrompt, customTemplateName, cancellationToken);

        var tokenizedText = TryGetTokenMapService()!.TokenizeStructuredResult(text);
        var result = await _inner.OptimizeViaPiaCloudAsync(tokenizedText, templateId, language, isVoiceInput, mode, customPrompt, customTemplateName, cancellationToken);
        var detokenized = TryGetTokenMapService()!.Detokenize(result.Text);
        _logger.LogDebug("Tokenizing.OptimizeViaPiaCloud: pre-detok length={Pre}, post-detok length={Post}",
            result.Text.Length, detokenized.Length);
        return result with { Text = detokenized };
    }

    public Task<string> GeneratePromptViaPiaCloudAsync(string styleDescription, string? mode = null, CancellationToken cancellationToken = default)
        => _inner.GeneratePromptViaPiaCloudAsync(styleDescription, mode, cancellationToken);

    public Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestToolCallingAsync(provider, cancellationToken);

    public Task<bool> TestToolCallEmittedAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestToolCallEmittedAsync(provider, cancellationToken);

    public Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default)
        => _inner.TestStreamingAsync(provider, cancellationToken);

    public Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default)
        => _inner.TestPiaCloudConnectionAsync(cancellationToken);

    /// <summary>
    /// Rewrites TEXT to placeholders on the way in. ASSISTANT text as well as USER text: an earlier turn's
    /// reply was detokenized on the way OUT by <see cref="BufferedDetokenize"/>, so a User-only pass sent the
    /// real values straight back to the provider on the next step. Nothing but text is touched — a carried
    /// tool result is a FunctionResultContent and was already tokenized where it was produced, so it is
    /// preserved rather than tokenized twice.
    /// </summary>
    private IList<ChatMessage> TokenizeMessages(IList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if ((msg.Role != ChatRole.User && msg.Role != ChatRole.Assistant) || string.IsNullOrEmpty(msg.Text))
            {
                result.Add(msg);
                continue;
            }

            var tokenized = TryGetTokenMapService()!.TokenizeStructuredResult(msg.Text);
            var nonText = msg.Contents.Where(c => c is not TextContent).ToList();
            if (nonText.Count == 0)
            {
                result.Add(new ChatMessage(msg.Role, tokenized));
                continue;
            }

            var rebuilt = new List<AIContent>(nonText.Count + 1) { new TextContent(tokenized) };
            rebuilt.AddRange(nonText);
            result.Add(new ChatMessage(msg.Role, rebuilt));
        }
        return result;
    }

    private ToolCallHandler WrapToolHandler(ToolCallHandler handler)
    {
        // ctx is RELAYED, never re-created and never `default`. This decorator sits between the tool loop and
        // the real gate for every tokenization-enabled user, so passing `default` here would persist Round = 0
        // on exactly those installs — and be invisible to any test that leaves tokenization off.
        return async (toolCall, ctx) =>
        {
            // EVERY tool, not a named list of write verbs. A placeholder the model copies out of a tool result
            // and back into an argument reaches the tool verbatim otherwise, and lands on disk — no file tool
            // was ever on that list, so write_file wrote "[Phone_9]" into a user's report. Detokenize is
            // idempotent and passes unknown tokens through, so a read tool and the pre-route step tools are
            // safe to include.
            //
            // Onto a COPY: the FunctionCallContent the loop handed us is the one it appended to its own message
            // list, so an in-place rewrite would send the real values back to the provider on the next round —
            // and, once a step carries its exchanges forward, for the rest of the run.
            _logger.LogDebug("Detokenizing arguments for {ToolName}", toolCall.Name);
            var result = await handler(DetokenizeToolCallArguments(toolCall), ctx);
            if (result is null)
                return null;

            // Tokenize the result the model will see so real PII never reaches the provider. A string result
            // tokenizes directly; a structured OBJECT result (recall hits, a read_topic body, a raw
            // read_source transcript, a query_* list) is serialized to the SAME wire JSON PiaCloudChatClient
            // would emit, then tokenized — returning that tokenized JSON string is wire-equivalent, since the
            // serializer passes strings through. Without this branch, object results bypassed tokenization
            // entirely (read_source returns never-synthesized primary text — the sharpest PII exposure).
            if (result is string resultStr)
                return TryGetTokenMapService()!.TokenizeStructuredResult(resultStr);

            var resultJson = System.Text.Json.JsonSerializer.Serialize(result);
            return TryGetTokenMapService()!.TokenizeStructuredResult(resultJson);
        };
    }

    private FunctionCallContent DetokenizeToolCallArguments(FunctionCallContent toolCall)
    {
        if (toolCall.Arguments is null) return toolCall;

        var detokenized = new Dictionary<string, object?>(toolCall.Arguments);
        foreach (var key in toolCall.Arguments.Keys)
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
                detokenized[key] = TryGetTokenMapService()!.Detokenize(strValue);
            }
        }

        return new FunctionCallContent(toolCall.CallId, toolCall.Name, detokenized);
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

}
