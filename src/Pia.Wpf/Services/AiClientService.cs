using System.ClientModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AiClientService : IAiClientService
{
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

    public async Task<string> SendRequestAsync(
        AiProvider provider,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(30);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            _logger.LogInformation("SendRequestAsync: provider={Name} type={Type}", provider.Name, provider.ProviderType);
            IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

            var response = await chatClient.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, prompt)],
                cancellationToken: linkedCts.Token
            );

            _logger.LogDebug("SendRequestAsync: received response, length={Length}", response.Text?.Length ?? 0);
            return response.Text ?? string.Empty;
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out after {timeout.TotalSeconds} seconds");
        }
    }

    public async IAsyncEnumerable<string> StreamChatCompletionAsync(
        IList<Microsoft.Extensions.AI.ChatMessage> messages,
        AiProvider provider,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(60);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var chatClient = await CreateChatClientAsync(provider, apiKey);

        IAsyncEnumerable<ChatResponseUpdate> stream;
        try
        {
            stream = chatClient.GetStreamingResponseAsync(messages, cancellationToken: linkedCts.Token);
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out after {timeout.TotalSeconds} seconds");
        }

        await foreach (var update in stream.WithCancellation(linkedCts.Token))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    public async Task<ChatResponse> GetChatResponseAsync(
        IList<Microsoft.Extensions.AI.ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(60);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

            var useTools = provider.SupportsToolCalling && tools is { Count: > 0 };
            var options = new ChatOptions();
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
                options.Tools = null;
                return await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
            }
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out after {timeout.TotalSeconds} seconds");
        }
    }

    public async IAsyncEnumerable<string> GetChatCompletionWithToolsAsync(
        IList<Microsoft.Extensions.AI.ChatMessage> messages,
        AiProvider provider,
        IList<AITool>? tools = null,
        Func<FunctionCallContent, Task<object?>>? toolHandler = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(120);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

        var useTools = provider.SupportsToolCalling && tools is { Count: > 0 };
        var options = new ChatOptions();
        if (useTools)
        {
            options.Tools = [.. tools!];
        }

        const int maxToolRounds = 10;
        var workingMessages = new List<Microsoft.Extensions.AI.ChatMessage>(messages);

        for (var round = 0; round < maxToolRounds; round++)
        {
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
                catch (Exception ex) when (useTools && round == 0 && IsToolNotSupportedError(ex))
                {
                    _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled during streaming, retrying without tools", provider.Name);
                    options.Tools = null;
                    useTools = false;
                    if (enumerator != null) await enumerator.DisposeAsync();

                    var retryStream = chatClient.GetStreamingResponseAsync(workingMessages, options, linkedCts.Token);
                    enumerator = retryStream.GetAsyncEnumerator(linkedCts.Token);
                    hasFirst = await enumerator.MoveNextAsync();
                }

                // Yield tokens outside try-catch (yield is allowed in try-finally)
                try
                {
                    if (hasFirst)
                    {
                        do
                        {
                            updates.Add(enumerator!.Current);
                            if (!string.IsNullOrEmpty(enumerator.Current.Text))
                            {
                                yield return enumerator.Current.Text;
                            }
                        } while (await enumerator.MoveNextAsync());
                    }
                }
                finally
                {
                    if (enumerator != null) await enumerator.DisposeAsync();
                }

                response = updates.ToChatResponse();
            }
            else
            {
                // Non-streaming path: fetch entire response at once
                try
                {
                    response = await chatClient.GetResponseAsync(workingMessages, options, linkedCts.Token);
                }
                catch (Exception ex) when (useTools && round == 0 && IsToolNotSupportedError(ex))
                {
                    _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled, retrying without tools", provider.Name);
                    options.Tools = null;
                    useTools = false;
                    response = await chatClient.GetResponseAsync(workingMessages, options, linkedCts.Token);
                }

                var text = response.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }
            }

            // Check if there are tool calls in the response
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (toolCalls.Count > 0 && toolHandler is not null)
            {
                // Add assistant messages with tool calls to working messages
                foreach (var msg in response.Messages)
                {
                    workingMessages.Add(msg);
                }

                // Process tool calls
                foreach (var toolCall in toolCalls)
                {
                    var result = await toolHandler(toolCall);
                    var resultMessage = new Microsoft.Extensions.AI.ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent(toolCall.CallId, result)]);
                    workingMessages.Add(resultMessage);
                }

                // Continue the loop to get the AI's response after tool execution
                continue;
            }

            yield break;
        }
    }

    public async Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(30);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

        var dummyTool = AIFunctionFactory.Create(() => "ok", "ping", "A test tool");
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.User, "Say hello.")
        };
        var options = new ChatOptions { Tools = [dummyTool] };

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
        var timeout = TimeSpan.FromSeconds(30);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        IChatClient chatClient = await CreateChatClientAsync(provider, apiKey);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.User, "Say hello.")
        };

        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: linkedCts.Token))
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

    public async Task<string> OptimizeViaPiaCloudAsync(
        string text,
        Guid templateId,
        string language,
        bool isVoiceInput,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        var timeout = TimeSpan.FromSeconds(30);
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

        try
        {
            var response = await httpClient.PostAsync(
                $"{serverUrl}/api/ai/optimize", content, linkedCts.Token);

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
                _logger.LogWarning("PiaCloud optimize returned {StatusCode}: {Body}",
                    (int)response.StatusCode, responseJson);
                throw new HttpRequestException(
                    $"PiaCloud optimization failed ({(int)response.StatusCode}): {responseJson}");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
            return doc.RootElement.GetProperty("optimizedText").GetString()
                ?? throw new InvalidOperationException("Server returned empty optimized text");
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"PiaCloud request timed out after {timeout.TotalSeconds} seconds");
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

    private async Task<IChatClient> CreateChatClientAsync(AiProvider provider, string apiKey)
    {
        var httpClient = _httpClientFactory.CreateClient();

        if (provider.ProviderType == AiProviderType.PiaCloud)
        {
            var client = await CreatePiaCloudChatClientAsync(httpClient);
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
            AiProviderType.OpenAI or AiProviderType.OpenRouter or AiProviderType.OpenAICompatible or AiProviderType.Ollama =>
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

    private async Task<IChatClient> CreatePiaCloudChatClientAsync(HttpClient httpClient)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        // Use JWT token as credential if logged in, otherwise use placeholder for unauthenticated access
        var credential = "anonymous";
        if (!string.IsNullOrEmpty(settings.EncryptedAccessToken))
        {
            try
            {
                credential = _dpapiHelper.Decrypt(settings.EncryptedAccessToken);
            }
            catch
            {
                // If decryption fails, fall back to unauthenticated
            }
        }

        var endpoint = new Uri($"{serverUrl}/api/ai");
        _logger.LogInformation("PiaCloud: creating ChatClient with endpoint={Endpoint}, model=pia-cloud", endpoint);

        return new ChatClient(
            model: "pia-cloud",
            credential: new ApiKeyCredential(credential),
            options: new OpenAI.OpenAIClientOptions
            {
                Endpoint = endpoint,
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
            }
        ).AsIChatClient();
    }

    public async Task<string> GeneratePromptViaPiaCloudAsync(
        string styleDescription,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        var timeout = TimeSpan.FromSeconds(30);
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
                _logger.LogWarning("PiaCloud generate-prompt returned {StatusCode}: {Body}",
                    (int)response.StatusCode, responseJson);
                throw new HttpRequestException(
                    $"PiaCloud prompt generation failed ({(int)response.StatusCode}): {responseJson}");
            }

            using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
            return doc.RootElement.GetProperty("prompt").GetString()
                ?? throw new InvalidOperationException("Server returned empty prompt");
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"PiaCloud request timed out after {timeout.TotalSeconds} seconds");
        }
    }

    public async Task TestPiaCloudConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var serverUrl = settings.ServerUrl?.TrimEnd('/');

        if (string.IsNullOrEmpty(serverUrl))
            throw new InvalidOperationException("Pia Cloud server URL is not configured. Set it in Settings > Sync.");

        var statusUrl = $"{serverUrl}/api/ai/status";
        _logger.LogInformation("PiaCloud connection test: GET {Url}", statusUrl);

        var timeout = TimeSpan.FromSeconds(15);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var response = await httpClient.GetAsync(statusUrl, linkedCts.Token);

            _logger.LogInformation("PiaCloud connection test: {StatusCode}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection test timed out after {timeout.TotalSeconds} seconds");
        }
    }
}
