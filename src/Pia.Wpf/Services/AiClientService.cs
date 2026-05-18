using System.ClientModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AiClientService : IAiClientService
{
    private const string NoThinkSystemPrompt =
        "You produce only the requested output. Do not reason, think, or explain. " +
        "Do not emit <think> tags. Respond directly with the final text.";

    private static readonly Regex LeadingThinkBlockRegex = new(
        @"^\s*<think\b[^>]*>[\s\S]*?</think>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DpapiHelper _dpapiHelper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AiClientService> _logger;

    public AiClientService(
        DpapiHelper dpapiHelper,
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService,
        ILogger<AiClientService> logger)
    {
        _dpapiHelper = dpapiHelper;
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<AiCompletionResult> SendRequestAsync(
        AiProvider provider,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            _logger.LogInformation("SendRequestAsync: type={Type}", provider.ProviderType);
            _logger.SensitiveDebug("SendRequestAsync: provider name={Name}", provider.Name);
            IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

            var messages = new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, NoThinkSystemPrompt),
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, prompt ),
            };

            var options = CreateModeOptions(mode: null, hasTools: false);

            var response = await chatClient.GetResponseAsync(
                messages,
                options,
                cancellationToken: linkedCts.Token
            );

            var text = response.Text ?? string.Empty;
            _logger.LogDebug("SendRequestAsync: received response, length={Length}, finishReason={FinishReason}",
                text.Length, response.FinishReason);

            if (response.FinishReason == Microsoft.Extensions.AI.ChatFinishReason.Length)
                throw new LlmTruncatedException(provider.Name, text.Length);

            var tokensUsed = 0;
            if (response.Usage is { } usage)
            {
                if (usage.InputTokenCount is long input) tokensUsed += (int)input;
                if (usage.OutputTokenCount is long output) tokensUsed += (int)output;
            }

            return new AiCompletionResult(text, tokensUsed);
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            _logger.LogWarning("SendRequestAsync: provider {ProviderName} timed out after {Seconds}s", provider.Name, timeout.TotalSeconds);
            throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendRequestAsync: provider {ProviderName} threw an exception", provider.Name);
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<Microsoft.Extensions.AI.ChatMessage> messages,
        AiProvider provider,
        string? mode = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var chatClient = await CreateChatClientAsync(provider, apiKey, mode);
        var options = CreateModeOptions(mode, hasTools: false);

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            try
            {
                var stream = chatClient.GetStreamingResponseAsync(messages, options, cancellationToken: linkedCts.Token);
                enumerator = stream.GetAsyncEnumerator(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("StreamChatCompletionAsync: provider {ProviderName} timed out after {Seconds}s", provider.Name, timeout.TotalSeconds);
                throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
            }

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("StreamChatCompletionAsync: provider {ProviderName} timed out mid-stream after {Seconds}s", provider.Name, timeout.TotalSeconds);
                    throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                }

                if (!hasNext) break;

                var update = enumerator.Current;
                if (!string.IsNullOrEmpty(update.Text))
                {
                    yield return update.Text;
                }
            }
        }
        finally
        {
            if (enumerator != null) await enumerator.DisposeAsync();
        }
    }

    public async Task<ChatResponse> GetChatResponseAsync(
        IList<Microsoft.Extensions.AI.ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            IChatClient chatClient = await CreateChatClientAsync(provider, apiKey, mode);

            var useTools = provider.SupportsToolCalling && tools is { Count: > 0 };
            var options = CreateModeOptions(mode, hasTools: useTools);
            if (useTools)
            {
                options.Tools = [.. tools!];
            }

            try
            {
                return await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
            }
            catch (Exception ex) when (useTools && IsToolNotSupportedError(ex))
            {
                _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled, retrying without tools", provider.Name);
                options = CreateModeOptions(mode, hasTools: false);
                return await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
            }
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            _logger.LogWarning("GetChatResponseAsync: provider {ProviderName} timed out after {Seconds}s", provider.Name, timeout.TotalSeconds);
            throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
        }
    }

    public async IAsyncEnumerable<ChatStreamItem> GetChatCompletionWithToolsAsync(
        IList<Microsoft.Extensions.AI.ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        Func<FunctionCallContent, Task<object?>>? toolHandler = null,
        string? mode = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting tool-aware chat completion, provider={ProviderName}, toolCount={ToolCount}",
            provider.Name, tools?.Count ?? 0);

        long aggregatedInput = 0;
        long aggregatedOutput = 0;
        bool hasUsage = false;

        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IChatClient chatClient = await CreateChatClientAsync(provider, apiKey, mode);

        var useTools = provider.SupportsToolCalling && tools is { Count: > 0 };
        var options = CreateModeOptions(mode, hasTools: useTools);
        if (useTools)
        {
            options.Tools = [.. tools!];
            _logger.LogDebug("Tool schemas being sent: [{ToolNames}]",
                string.Join(", ", tools!.Select(t => t.Name)));
        }
        else
        {
            _logger.LogWarning("Tools NOT included in request: SupportsToolCalling={SupportsToolCalling}, toolCount={ToolCount}",
                provider.SupportsToolCalling, tools?.Count ?? 0);
        }

        const int maxToolRounds = 10;
        var workingMessages = new List<Microsoft.Extensions.AI.ChatMessage>(messages);

        for (var round = 0; round < maxToolRounds; round++)
        {
            _logger.LogDebug("Tool round {Round}/{MaxRounds} starting, path={Path}",
                round + 1, maxToolRounds, provider.SupportsStreaming ? "streaming" : "non-streaming");
            ChatResponse response;

            if (provider.SupportsStreaming)
            {
                // Streaming path: yield text tokens as they arrive.
                // Tool-not-supported errors (HTTP 400/404) occur on the first MoveNextAsync,
                // so we handle retry in a try-catch there, then yield in try-finally.
                var updates = new List<ChatResponseUpdate>();
                IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
                var hasFirst = false;

                try
                {
                    var stream = chatClient.GetStreamingResponseAsync(workingMessages, options, linkedCts.Token);
                    enumerator = stream.GetAsyncEnumerator(linkedCts.Token);
                    hasFirst = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("GetChatCompletionWithToolsAsync: provider {ProviderName} timed out at stream start (round {Round}) after {Seconds}s", provider.Name, round + 1, timeout.TotalSeconds);
                    if (enumerator != null) await enumerator.DisposeAsync();
                    throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                }
                catch (Exception ex) when (useTools && round == 0 && IsToolNotSupportedError(ex))
                {
                    _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled during streaming, retrying without tools", provider.Name);
                    options = CreateModeOptions(mode, hasTools: false);
                    useTools = false;
                    if (enumerator != null) await enumerator.DisposeAsync();

                    try
                    {
                        var retryStream = chatClient.GetStreamingResponseAsync(workingMessages, options, linkedCts.Token);
                        enumerator = retryStream.GetAsyncEnumerator(linkedCts.Token);
                        hasFirst = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("GetChatCompletionWithToolsAsync: provider {ProviderName} timed out on tool-disabled retry after {Seconds}s", provider.Name, timeout.TotalSeconds);
                        if (enumerator != null) await enumerator.DisposeAsync();
                        throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                    }
                }

                // Yield tokens outside try-catch (yield is allowed in try-finally)
                try
                {
                    if (hasFirst)
                    {
                        while (true)
                        {
                            updates.Add(enumerator!.Current);
                            if (!string.IsNullOrEmpty(enumerator.Current.Text))
                            {
                                yield return new TextDelta(enumerator.Current.Text);
                            }

                            bool hasNext;
                            try
                            {
                                hasNext = await enumerator.MoveNextAsync();
                            }
                            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            {
                                _logger.LogWarning("GetChatCompletionWithToolsAsync: provider {ProviderName} timed out mid-stream (round {Round}) after {Seconds}s", provider.Name, round + 1, timeout.TotalSeconds);
                                throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                            }

                            if (!hasNext) break;
                        }
                    }
                }
                finally
                {
                    if (enumerator != null) await enumerator.DisposeAsync();
                }

                response = updates.ToChatResponse();
                _logger.LogDebug("Round {Round} streaming done: {MsgCount} messages, textLength={TextLen}, finishReason={FinishReason}",
                    round + 1, response.Messages.Count, response.Text?.Length ?? 0, response.FinishReason);
            }
            else
            {
                // Non-streaming path: fetch entire response at once
                try
                {
                    response = await chatClient.GetResponseAsync(workingMessages, options, linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("GetChatCompletionWithToolsAsync: provider {ProviderName} timed out (round {Round}) after {Seconds}s", provider.Name, round + 1, timeout.TotalSeconds);
                    throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                }
                catch (Exception ex) when (useTools && round == 0 && IsToolNotSupportedError(ex))
                {
                    _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled, retrying without tools", provider.Name);
                    options = CreateModeOptions(mode, hasTools: false);
                    useTools = false;
                    try
                    {
                        response = await chatClient.GetResponseAsync(workingMessages, options, linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("GetChatCompletionWithToolsAsync: provider {ProviderName} timed out on tool-disabled retry after {Seconds}s", provider.Name, timeout.TotalSeconds);
                        throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                    }
                }

                var text = response.Text;
                _logger.LogDebug("Round {Round} non-streaming done: {MsgCount} messages, textLength={TextLen}",
                    round + 1, response.Messages.Count, text?.Length ?? 0);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new TextDelta(text);
                }
            }

            if (response.Usage is { } roundUsage)
            {
                if (roundUsage.InputTokenCount is long input) { aggregatedInput += input; hasUsage = true; }
                if (roundUsage.OutputTokenCount is long output) { aggregatedOutput += output; hasUsage = true; }
            }

            // Detect truncation by output token cap. Surfaced to the UI as a friendly hint
            // (otherwise an incomplete tool-call argument JSON would just fail silently).
            if (response.FinishReason == Microsoft.Extensions.AI.ChatFinishReason.Length)
            {
                var partial = response.Text?.Length ?? 0;
                _logger.LogWarning("Round {Round}: response truncated by token cap (finish_reason=length, partialChars={PartialChars})",
                    round + 1, partial);
                throw new LlmTruncatedException(provider.Name, partial);
            }

            // Check if there are tool calls in the response
            var contentTypes = response.Messages
                .SelectMany(m => m.Contents)
                .Select(c => c.GetType().Name)
                .Distinct();
            _logger.LogDebug("Round {Round} response content types: [{ContentTypes}]",
                round + 1, string.Join(", ", contentTypes));

            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (toolCalls.Count > 0 && toolHandler is not null)
            {
                _logger.LogInformation("Round {Round}: {ToolCallCount} tool call(s) detected: {ToolNames}",
                    round + 1, toolCalls.Count, string.Join(", ", toolCalls.Select(t => t.Name)));

                foreach (var tc in toolCalls)
                {
                    _logger.SensitiveDebug("Tool call {ToolName} (callId={CallId}) args: {Args}",
                        tc.Name,
                        tc.CallId,
                        tc.Arguments is not null
                            ? Truncate(JsonSerializer.Serialize(tc.Arguments), 500)
                            : "<null>");
                }

                // Add assistant messages with tool calls to working messages
                foreach (var msg in response.Messages)
                {
                    workingMessages.Add(msg);
                }

                // Process tool calls
                foreach (var toolCall in toolCalls)
                {
                    _logger.LogDebug("Invoking tool handler for {ToolName} (callId={CallId})", toolCall.Name, toolCall.CallId);
                    var result = await toolHandler(toolCall);
                    var resultPreview = result?.ToString() ?? "<null>";
                    _logger.SensitiveDebug("Tool {ToolName} handler result ({Length} chars): {Preview}",
                        toolCall.Name, resultPreview.Length, Truncate(resultPreview, 500));
                    var resultMessage = new Microsoft.Extensions.AI.ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(toolCall.CallId, result)]);
                    workingMessages.Add(resultMessage);
                }

                _logger.LogDebug("Round {Round} complete, continuing with {MessageCount} working messages",
                    round + 1, workingMessages.Count);
                // Continue the loop to get the AI's response after tool execution
                continue;
            }

            _logger.LogDebug("Round {Round}: no tool calls, completing", round + 1);
            yield return BuildFinishedItem(provider, hasUsage, aggregatedInput, aggregatedOutput);
            yield break;
        }

        _logger.LogWarning("Tool loop exhausted max rounds ({MaxRounds}) without final response", maxToolRounds);
        yield return BuildFinishedItem(provider, hasUsage, aggregatedInput, aggregatedOutput);
    }

    private ChatStreamItem BuildFinishedItem(AiProvider provider, bool hasUsage, long aggregatedInput, long aggregatedOutput)
    {
        UsageDetails? usage = null;
        if (hasUsage)
        {
            usage = new UsageDetails
            {
                InputTokenCount = aggregatedInput,
                OutputTokenCount = aggregatedOutput,
                TotalTokenCount = aggregatedInput + aggregatedOutput,
            };
        }
        else
        {
            _logger.LogDebug("Stream finished without usage details, providerType={ProviderType}", provider.ProviderType);
        }

        var modelLabel = !string.IsNullOrWhiteSpace(provider.ModelName)
            ? provider.ModelName
            : provider.Name;
        return new Finished(usage, modelLabel);
    }

    public async Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

        var dummyTool = AIFunctionFactory.Create(() => "ok", "ping", "A test tool");
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, NoThinkSystemPrompt),
            new(ChatRole.User, "Say hello.")
        };
        var options = CreateModeOptions(mode: null, hasTools: true);
        options.Tools = [dummyTool];

        try
        {
            // If the request succeeds with tools in the schema, the provider supports tool calling.
            // Models that truly don't support tools will reject with 400/404.
            // We don't force tool use (RequireAny) — many providers silently ignore it.
            await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
            return true;
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Tool calling test timed out after {timeout.TotalSeconds} seconds");
        }
        catch (Exception ex) when (IsToolNotSupportedError(ex))
        {
            return false;
        }
    }

    public async Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, NoThinkSystemPrompt),
            new(ChatRole.User, "Say hello.")
        };
        var options = CreateModeOptions(mode: null, hasTools: false);

        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, linkedCts.Token))
            {
                return true;
            }

            return true;
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Streaming test timed out after {timeout.TotalSeconds} seconds");
        }
        catch
        {
            return false;
        }
    }

    public async Task<AiCompletionResult> OptimizeViaPiaCloudAsync(
        string text,
        Guid templateId,
        string language,
        bool isVoiceInput,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        var timeout = TimeSpan.FromSeconds(300);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var httpClient = _httpClientFactory.CreateClient();

        var requestBody = new
        {
            text,
            templateId = templateId.ToString(),
            language,
            isVoiceInput
        };

        var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // Add JWT token if available
        if (!string.IsNullOrEmpty(settings.EncryptedAccessToken))
        {
            try
            {
                var token = _dpapiHelper.Decrypt(settings.EncryptedAccessToken);
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            catch
            {
                // If decryption fails, proceed without auth
            }
        }

        if (!string.IsNullOrEmpty(mode))
            httpClient.DefaultRequestHeaders.Add("X-Pia-Mode", mode);

        try
        {
            var response = await httpClient.PostAsync(
                $"{serverUrl}/api/ai/optimize", content, linkedCts.Token);

            var responseJson = await response.Content.ReadAsStringAsync(linkedCts.Token);
            _logger.LogDebug("PiaCloud optimize: response body length={Length}", responseJson.Length);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var friendlyMessage = "Token limit reached.";
                try
                {
                    using var errDoc = System.Text.Json.JsonDocument.Parse(responseJson);
                    var root = errDoc.RootElement;
                    if (root.TryGetProperty("resetsAt", out var resetsAtProp))
                    {
                        var resetsAt = resetsAtProp.GetDateTime();
                        var remaining = resetsAt - DateTime.UtcNow;
                        if (remaining.TotalMinutes > 60)
                            friendlyMessage = $"Token limit reached. Resets in {remaining.Hours}h {remaining.Minutes}m.";
                        else if (remaining.TotalMinutes > 1)
                            friendlyMessage = $"Token limit reached. Resets in {(int)remaining.TotalMinutes} minutes.";
                        else
                            friendlyMessage = "Token limit reached. Resets shortly.";
                    }
                }
                catch { }
                throw new InvalidOperationException(friendlyMessage);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PiaCloud optimize returned {StatusCode}", (int)response.StatusCode);
                _logger.SensitiveDebug("PiaCloud optimize body: {Body}", responseJson);
                throw new HttpRequestException(
                    $"PiaCloud optimization failed ({(int)response.StatusCode}): {responseJson}");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
            var responseRoot = doc.RootElement;
            var optimizedText = responseRoot.GetProperty("optimizedText").GetString()
                ?? throw new InvalidOperationException("Server returned empty optimized text");

            var tokensUsed = 0;
            if (responseRoot.TryGetProperty("inputTokens", out var inputEl) && inputEl.TryGetInt32(out var inputTokens))
                tokensUsed += inputTokens;
            if (responseRoot.TryGetProperty("outputTokens", out var outputEl) && outputEl.TryGetInt32(out var outputTokens))
                tokensUsed += outputTokens;

            _logger.LogDebug("PiaCloud optimize: extracted text length={Length}, tokens={Tokens}",
                optimizedText.Length, tokensUsed);
            return new AiCompletionResult(optimizedText, tokensUsed);
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new LlmTimeoutException("Pia Cloud", timeout.TotalSeconds);
        }
    }

    private static bool IsToolNotSupportedError(Exception ex)
    {
        if (ex is ClientResultException clientEx)
        {
            return clientEx.Status is 404 or 400;
        }

        if (ex is HttpRequestException httpEx)
        {
            return httpEx.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest;
        }

        return false;
    }

    private async Task<IChatClient> CreateChatClientAsync(AiProvider provider, string apiKey, string? mode = null)
    {
        var httpClient = _httpClientFactory.CreateClient();

        if (provider.ProviderType == AiProviderType.PiaCloud)
        {
            var client = await CreatePiaCloudChatClientAsync(httpClient, mode);
            _logger.LogDebug("Created PiaCloud chat client");
            return client;
        }

        if (provider.ProviderType == AiProviderType.OpenRouter || IsOpenRouterEndpoint(provider.Endpoint))
        {
            httpClient.DefaultRequestHeaders.Add("X-Title", "Pia");
            httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Pia-Ai-dev/Pia.Wpf");
        }

        return provider.ProviderType switch
        {
            AiProviderType.OpenAI or AiProviderType.OpenRouter or AiProviderType.OpenAICompatible or AiProviderType.Ollama or AiProviderType.Mistral =>
                new ChatClient(
                    model: provider.ModelName ?? "gpt-3.5-turbo",
                    credential: new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey),
                    options: new OpenAI.OpenAIClientOptions
                    {
                        Endpoint = new Uri(provider.Endpoint),
                        Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
                    }
                ).AsIChatClient(),

            AiProviderType.AzureOpenAI =>
                new AzureOpenAIClient(
                    new Uri(provider.Endpoint),
                    new ApiKeyCredential(apiKey),
                    new Azure.AI.OpenAI.AzureOpenAIClientOptions
                    {
                        Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
                    }
                ).GetChatClient(provider.AzureDeploymentName ?? provider.ModelName ?? "gpt-35-turbo")
                .AsIChatClient(),

            _ => throw new NotSupportedException($"Provider type {provider.ProviderType} is not supported")
        };
    }

    private static bool IsOpenRouterEndpoint(string endpoint) =>
        endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    private async Task<IChatClient> CreatePiaCloudChatClientAsync(HttpClient httpClient, string? mode = null)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        string? accessToken = null;
        if (!string.IsNullOrEmpty(settings.EncryptedAccessToken))
        {
            try
            {
                accessToken = _dpapiHelper.Decrypt(settings.EncryptedAccessToken);
            }
            catch
            {
                // If decryption fails, proceed without auth
            }
        }

        _logger.LogInformation("PiaCloud: creating PiaCloudChatClient with endpoint={ServerUrl}/api/ai/chat",
            SafeUrl.Format(serverUrl));

        return new PiaCloudChatClient(httpClient, serverUrl, accessToken, _logger, mode);
    }

    public async Task<string> GeneratePromptViaPiaCloudAsync(
        string styleDescription,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        var timeout = TimeSpan.FromSeconds(300);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var httpClient = _httpClientFactory.CreateClient();

        var requestBody = new { styleDescription };

        var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(settings.EncryptedAccessToken))
        {
            try
            {
                var token = _dpapiHelper.Decrypt(settings.EncryptedAccessToken);
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            catch
            {
                // If decryption fails, proceed without auth
            }
        }

        if (!string.IsNullOrEmpty(mode))
            httpClient.DefaultRequestHeaders.Add("X-Pia-Mode", mode);

        try
        {
            var response = await httpClient.PostAsync(
                $"{serverUrl}/api/ai/generate-prompt", content, linkedCts.Token);

            var responseJson = await response.Content.ReadAsStringAsync(linkedCts.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var friendlyMessage = "Token limit reached.";
                try
                {
                    using var errDoc = System.Text.Json.JsonDocument.Parse(responseJson);
                    var root = errDoc.RootElement;
                    if (root.TryGetProperty("resetsAt", out var resetsAtProp))
                    {
                        var resetsAt = resetsAtProp.GetDateTime();
                        var remaining = resetsAt - DateTime.UtcNow;
                        if (remaining.TotalMinutes > 60)
                            friendlyMessage = $"Token limit reached. Resets in {remaining.Hours}h {remaining.Minutes}m.";
                        else if (remaining.TotalMinutes > 1)
                            friendlyMessage = $"Token limit reached. Resets in {(int)remaining.TotalMinutes} minutes.";
                        else
                            friendlyMessage = "Token limit reached. Resets shortly.";
                    }
                }
                catch { }
                throw new InvalidOperationException(friendlyMessage);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PiaCloud generate-prompt returned {StatusCode}", (int)response.StatusCode);
                _logger.SensitiveDebug("PiaCloud generate-prompt body: {Body}", responseJson);
                throw new HttpRequestException(
                    $"PiaCloud prompt generation failed ({(int)response.StatusCode}): {responseJson}");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
            return doc.RootElement.GetProperty("prompt").GetString()
                ?? throw new InvalidOperationException("Server returned empty prompt");
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new LlmTimeoutException("Pia Cloud", timeout.TotalSeconds);
        }
    }

    public async Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        var statusUrl = $"{serverUrl}/api/ai/status";
        _logger.LogInformation("PiaCloud connection test: GET {Url}", SafeUrl.Format(statusUrl));

        var timeout = TimeSpan.FromSeconds(15);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            var httpClient = _httpClientFactory.CreateClient();

            // Add JWT token if available
            if (!string.IsNullOrEmpty(settings.EncryptedAccessToken))
            {
                try
                {
                    var token = _dpapiHelper.Decrypt(settings.EncryptedAccessToken);
                    httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
                catch
                {
                    // If decryption fails, proceed without auth (will yield 401 below)
                }
            }

            var response = await httpClient.GetAsync(statusUrl, linkedCts.Token);

            _logger.LogInformation("PiaCloud connection test: {StatusCode}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new LlmTimeoutException("Pia Cloud", timeout.TotalSeconds);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length > max ? value[..max] + "..." : value;

    // Sets reasoning_effort at the API layer per WindowMode. The OpenAI SDK's
    // RawRepresentationFactory returns a seed ChatCompletionOptions; M.E.AI overlays its mapped
    // fields on top, so ReasoningEffortLevel is preserved end-to-end. Honored by OpenAI o-series
    // and Ollama 0.10+; ignored by backends that don't recognize the field. Any request that
    // carries tools forces None — tool-using turns must not burn reasoning tokens.
    private static Microsoft.Extensions.AI.ChatOptions CreateModeOptions(string? mode, bool hasTools)
        => new()
        {
            RawRepresentationFactory = _ =>
            {
#pragma warning disable OPENAI001 // ReasoningEffortLevel is marked [Experimental] in OpenAI SDK 2.10.
                return new ChatCompletionOptions
                {
                    ReasoningEffortLevel = ResolveEffort(mode, hasTools),
                };
#pragma warning restore OPENAI001
            },
        };

#pragma warning disable OPENAI001 // ReasoningEffortLevel is marked [Experimental] in OpenAI SDK 2.10.
    private static ChatReasoningEffortLevel ResolveEffort(string? mode, bool hasTools)
    {
        if (hasTools) return ChatReasoningEffortLevel.None;
        return mode switch
        {
            nameof(WindowMode.Optimize) => ChatReasoningEffortLevel.None,
            nameof(WindowMode.Assistant) => ChatReasoningEffortLevel.None,
            nameof(WindowMode.Research) => ChatReasoningEffortLevel.Medium,
            _ => ChatReasoningEffortLevel.None,
        };
    }
#pragma warning restore OPENAI001
}
