using System.ClientModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Services.Providers;

namespace Pia.Services;

public class AiClientService : IAiClientService
{
    private const string NoThinkSystemPrompt =
        "You produce only the requested output. Do not reason, think, or explain. " +
        "Do not emit <think> tags. Respond directly with the final text.";

    private static readonly Regex LeadingThinkBlockRegex = new(
        @"^\s*<think\b[^>]*>[\s\S]*?</think>\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAuthService _authService;
    private readonly DpapiHelper _dpapiHelper;
    private readonly AiProviderHandlerResolver _handlers;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiClientService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IProviderRequestThrottle _throttle;

    /// <param name="throttle">T1-2, the per-provider request bound. REQUIRED, not defaulted: this type is
    /// transient and hand-constructed in five test sites, and a nullable "no throttle" would make forgetting
    /// the registration a silent loss of the one thing §18.4 requires before the run pool may widen. The
    /// compiler asking the question at every construction site is the point.</param>
    public AiClientService(
        DpapiHelper dpapiHelper,
        IHttpClientFactory httpClientFactory,
        ISettingsService settingsService,
        AiProviderHandlerResolver handlers,
        IAuthService authService,
        ILogger<AiClientService> logger,
        IProviderRequestThrottle throttle)
    {
        _dpapiHelper = dpapiHelper;
        _httpClientFactory = httpClientFactory;
        _settingsService = settingsService;
        _handlers = handlers;
        _authService = authService;
        _logger = logger;
        _throttle = throttle;
    }

    /// <summary>The per-method timeoutCts owns the request bound; HttpClient's 100s default would fire first and surface as a bare cancellation.</summary>
    private HttpClient CreateAiHttpClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    /// <summary>
    /// T1-2: take this provider's request permit, waited on the SAME linked token as the request itself.
    /// <para>
    /// Consequence, named rather than left to fall out of the token plumbing: queue time is charged against the
    /// request timeout, so a narrow throttle can time a request out that was never sent. The exception TYPE
    /// stays <see cref="LlmTimeoutException"/> — every catch, retry and UI path downstream must keep behaving
    /// as it does — but the MESSAGE and the log line say the timeout elapsed in the queue. A support log that
    /// said "the provider timed out" for a request this device never made would cost whoever reads it a wrong
    /// conclusion before they found the real one, which is exactly the failure the run-panel's pause-reason
    /// constants exist to prevent.
    /// </para>
    /// </summary>
    private async Task<IDisposable> AcquireProviderPermitAsync(
        AiProvider provider, TimeSpan timeout,
        CancellationTokenSource timeoutCts, CancellationToken callerToken, CancellationToken linkedToken)
    {
        try
        {
            return await _throttle.AcquireAsync(provider, linkedToken);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Provider {ProviderName}: the {Seconds}s timeout elapsed while QUEUED for a per-provider request permit — nothing was sent",
                provider.Name, timeout.TotalSeconds);
            throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds,
                $"The request to provider '{provider.Name}' was never sent: the {timeout.TotalSeconds}s timeout "
                + "elapsed while it was queued behind other requests to the same provider "
                + "(MaxParallelRequestsPerProvider).");
        }
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

        var httpClient = CreateAiHttpClient();

        var requestBody = new { styleDescription };

        var json = System.Text.Json.JsonSerializer.Serialize(requestBody);

        try
        {
            using var response = await SendPiaCloudRequestAsync(
                httpClient, $"{serverUrl}/api/ai/generate-prompt", json, mode,
                // §7: the prompt-generation route has no connector plane and never reads X-Pia-Persona.
                managedPersonaId: null, linkedCts.Token);

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

    public async IAsyncEnumerable<ChatStreamItem> GetChatCompletionWithToolsAsync(
            IList<Microsoft.Extensions.AI.ChatMessage> messages,
            AiProvider provider,
            IList<AITool>? tools = null,
            ToolCallHandler? toolHandler = null,
            string? mode = null,
            Guid? managedPersonaId = null,
            string? personaModelType = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            AgentContextBudget? contextBudget = null)
    {
        _logger.LogInformation("Starting tool-aware chat completion, provider={ProviderName}, toolCount={ToolCount}",
            provider.Name, tools?.Count ?? 0);

        long aggregatedInput = 0;
        long aggregatedOutput = 0;
        bool hasUsage = false;
        bool protectedRoute = false;

        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var providerHandler = _handlers.Get(provider.ProviderType);
        var httpClient = CreateAiHttpClient();
        var chatClient = await providerHandler.CreateChatClientAsync(
            provider, apiKey, httpClient, mode, managedPersonaId, personaModelType, linkedCts.Token);

        var useTools = provider.SupportsToolCalling && tools is { Count: > 0 };
        var options = providerHandler.CreateChatOptions(provider, hasTools: useTools);
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

        // Clamped here as well, not only in the settings UI, so a hand-edited settings file can neither
        // starve a step (0 rounds = the model may never call a tool) nor run an unbounded loop.
        var settings = await _settingsService.GetSettingsAsync();
        var maxToolRounds = Math.Clamp(
            settings.MaxToolRoundsPerStep, RunProfile.MinToolRounds, RunProfile.MaxToolRoundsCap);
        var workingMessages = new List<Microsoft.Extensions.AI.ChatMessage>(messages);

        for (var round = 0; round < maxToolRounds; round++)
        {
            _logger.LogDebug("Tool round {Round}/{MaxRounds} starting, path={Path}",
                round + 1, maxToolRounds, provider.SupportsStreaming ? "streaming" : "non-streaming");

            // The server decides the guardrail route (and emits the protected marker) per request, so it is
            // re-evaluated every tool round. Reset the flag each round so the badge reflects the round that
            // produced the FINAL answer — not a transient intermediate round (e.g. a classifier ERROR that
            // fail-closed to the protected model but recovered to the normal model on the next round). A
            // genuine HIT keeps marking every round because the offending content stays in workingMessages.
            protectedRoute = false;

            // Bound the IN-STEP tool loop. workingMessages is the ONLY list in Pia that ever holds
            // FunctionCallContent / FunctionResultContent messages (appended below, after a round that
            // produced tool calls), so this is the only place tool-result eviction can do any work —
            // and the only overflow path the executors' own compaction cannot see, because the growth
            // happens after they hand the request over. Nothing here is persisted: workingMessages is
            // discarded when the loop ends.
            //
            // Placed BEFORE the streaming / non-streaming branch so one insertion covers both provider
            // paths (they read the same workingMessages, including the tool-disabled retry).
            // round > 0 because round 0's list is what the executor already compacted.
            if (round > 0 && contextBudget is { } budget)
            {
                workingMessages = await AgentContextCompactor
                    .CompactAsync(workingMessages, budget, _logger, linkedCts.Token)
                    .ConfigureAwait(false);
            }

            ChatResponse response;

            if (provider.SupportsStreaming)
            {
                // Streaming path: yield text tokens as they arrive.
                // Tool-not-supported errors (HTTP 400/404) occur on the first MoveNextAsync,
                // so we handle retry in a try-catch there, then yield in try-finally.
                var updates = new List<ChatResponseUpdate>();
                IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
                var hasFirst = false;

                // T1-2: PER ROUND, and released the moment this round's stream is exhausted — see the permit
                // block's own comment below for why it must not span the tool dispatch further down.
                var permit = await AcquireProviderPermitAsync(
                    provider, timeout, timeoutCts, cancellationToken, linkedCts.Token);
                try
                {
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
                        // Diagnosis only, and FIRST so the real cause outranks the line below: the filter above
                        // says true for essentially any 400, so a context overflow arrives here dressed as a
                        // tool-support problem. The retry itself is unchanged either way.
                        LogContextLengthRejection(ex, provider, round, workingMessages.Count, contextBudget);
                        _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled during streaming, retrying without tools", provider.Name);
                        options = providerHandler.CreateChatOptions(provider, hasTools: false);
                        useTools = false;
                        if (enumerator != null) await enumerator.DisposeAsync();

                        try
                        {
                            // Deliberately under the SAME permit: the tool-disabled retry is the same logical
                            // round-trip re-attempted, so re-queueing it behind other requests would let a
                            // provider that rejects tools cost this round two waits instead of one.
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
                                var current = enumerator!.Current;
                                updates.Add(current);
                                if (!string.IsNullOrEmpty(current.Text))
                                {
                                    yield return new TextDelta(current.Text);
                                }

                                // The `reasoning` scalar (OpenRouter) only rides text-less chunks, so
                                // skip the raw-representation round-trip whenever this chunk already
                                // carries visible text — avoids serializing every content delta.
                                foreach (var reasoning in ExtractReasoning(
                                    current.Contents, current.RawRepresentation,
                                    attemptRawExtraction: string.IsNullOrEmpty(current.Text)))
                                {
                                    yield return reasoning;
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
                }
                finally
                {
                    // The permit is held for the WHOLE stream, because that is how long the HTTP request is
                    // open — that, and not the wall-clock of the method, is what a provider rate-limits on.
                    // The yields above sit inside a try/FINALLY (never a try/catch), which is what keeps this
                    // legal in an async iterator.
                    permit.Dispose();
                }

                response = updates.ToChatResponse();
                if (updates.Any(u => u.AdditionalProperties is { } ap && ap.ContainsKey(GuardrailMarker.AdditionalPropertyKey)))
                    protectedRoute = true;
                _logger.LogDebug("Round {Round} streaming done: {MsgCount} messages, textLength={TextLen}, finishReason={FinishReason}",
                    round + 1, response.Messages.Count, response.Text?.Length ?? 0, response.FinishReason);
            }
            else
            {
                // Non-streaming path: fetch entire response at once.
                // T1-2: the permit brackets the round-trip ONLY, and is released before the yields below —
                // whatever the consumer does with the text is not this provider's concurrency.
                using (await AcquireProviderPermitAsync(
                    provider, timeout, timeoutCts, cancellationToken, linkedCts.Token))
                {
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
                        // Same ordering as the streaming path above, for the same reason: name the real cause
                        // before the tool-support line. Diagnosis only; the retry below is untouched.
                        LogContextLengthRejection(ex, provider, round, workingMessages.Count, contextBudget);
                        _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled, retrying without tools", provider.Name);
                        options = providerHandler.CreateChatOptions(provider, hasTools: false);
                        useTools = false;
                        try
                        {
                            // Same permit as the first attempt — see the streaming twin for why.
                            response = await chatClient.GetResponseAsync(workingMessages, options, linkedCts.Token);
                        }
                        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogWarning("GetChatCompletionWithToolsAsync: provider {ProviderName} timed out on tool-disabled retry after {Seconds}s", provider.Name, timeout.TotalSeconds);
                            throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
                        }
                    }
                }

                var text = response.Text;
                _logger.LogDebug("Round {Round} non-streaming done: {MsgCount} messages, textLength={TextLen}",
                    round + 1, response.Messages.Count, text?.Length ?? 0);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new TextDelta(text);
                }

                var nonStreamingContents = response.Messages.SelectMany(m => m.Contents);
                foreach (var reasoning in ExtractReasoning(
                    nonStreamingContents, response.RawRepresentation, attemptRawExtraction: true))
                {
                    yield return reasoning;
                }
            }

            if (response.AdditionalProperties is { } respProps && respProps.ContainsKey(GuardrailMarker.AdditionalPropertyKey))
                protectedRoute = true;

            if (response.Usage is { } roundUsage)
            {
                if (roundUsage.InputTokenCount is long input) { aggregatedInput += input; hasUsage = true; }
                if (roundUsage.OutputTokenCount is long output) { aggregatedOutput += output; hasUsage = true; }
                _logger.LogDebug("Round {Round} token usage: input={Input}, output={Output}, cached={Cached}",
                    round + 1, roundUsage.InputTokenCount, roundUsage.OutputTokenCount, roundUsage.CachedInputTokenCount);
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

                // Process tool calls.
                //
                // T1-2, and this is the property the per-ROUND permit exists for: NO provider permit is held
                // here. This loop can block for as long as a human takes to answer an approval card (the
                // interactive gate awaits the card inside the handler) or for as long as a tool takes to run,
                // and a permit held across it would mean one open dialog stops every background run on that
                // provider. The next round re-queues from the top of the loop, which is correct: it is a new
                // round-trip.
                foreach (var toolCall in toolCalls)
                {
                    // No CallId on this line. It is copied verbatim out of provider JSON and nothing in this
                    // process validates it — the premise AgentTimelineScope.SanitizeCallId is built on — so a
                    // provider or proxy that echoes model text into it would put free text in a release support
                    // log through a plain LogDebug (level is runtime-configurable, so it is not a gate). The id
                    // is still available in DEBUG on the SensitiveDebug line a few lines up, which logs it
                    // alongside the args for the same call.
                    _logger.LogDebug("Invoking tool handler for {ToolName}", toolCall.Name);
                    // The ONE construction site of a ToolDispatchContext. `round + 1` so the number a gate
                    // persists is the same one every log line in this loop prints — `round` is 0-based only as
                    // a `for` counter, and an audit row that said "round 0" while the log said "round 1/10"
                    // would cost whoever correlates them a wrong conclusion before an off-by-one.
                    var result = await toolHandler(toolCall, new ToolDispatchContext(round + 1));
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
            yield return BuildFinishedItem(provider, hasUsage, aggregatedInput, aggregatedOutput, protectedRoute);
            yield break;
        }

        _logger.LogWarning("Tool loop exhausted max rounds ({MaxRounds}) without final response", maxToolRounds);
        yield return BuildFinishedItem(provider, hasUsage, aggregatedInput, aggregatedOutput, protectedRoute);
    }

    public async Task<ChatResponse> GetChatResponseAsync(
            IList<Microsoft.Extensions.AI.ChatMessage> messages,
            AiProvider provider,
            IList<AITool>? tools = null,
            string? mode = null,
            Guid? managedPersonaId = null,
            CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            var handler = _handlers.Get(provider.ProviderType);
            var httpClient = CreateAiHttpClient();
            var chatClient = await handler.CreateChatClientAsync(
                provider, apiKey, httpClient, mode, managedPersonaId, personaModelType: null, linkedCts.Token);

            var useTools = provider.SupportsToolCalling && tools is { Count: > 0 };
            var options = handler.CreateChatOptions(provider, hasTools: useTools);
            if (useTools)
            {
                options.Tools = [.. tools!];
            }

            // T1-2: one round-trip, one permit — this method is a single-shot request (AgentPlanner's plan and
            // AgentVerifier's critic turns), so the whole method body IS the round-trip.
            using (await AcquireProviderPermitAsync(
                provider, timeout, timeoutCts, cancellationToken, linkedCts.Token))
            {
                try
                {
                    return await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
                }
                catch (Exception ex) when (useTools && IsToolNotSupportedError(ex))
                {
                    _logger.LogWarning(ex, "Provider {ProviderName} returned an error with tools enabled, retrying without tools", provider.Name);
                    options = handler.CreateChatOptions(provider, hasTools: false);
                    return await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
                }
            }
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            _logger.LogWarning("GetChatResponseAsync: provider {ProviderName} timed out after {Seconds}s", provider.Name, timeout.TotalSeconds);
            throw new LlmTimeoutException(provider.Name, timeout.TotalSeconds);
        }
    }

    public async Task<AiCompletionResult> OptimizeViaPiaCloudAsync(
            string text,
            Guid templateId,
            string language,
            bool isVoiceInput,
            string? mode = null,
            string? customPrompt = null,
            string? customTemplateName = null,
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

        var httpClient = CreateAiHttpClient();

        var requestBody = new
        {
            text,
            templateId = templateId.ToString(),
            language,
            isVoiceInput,
            customPrompt,
            customTemplateName
        };

        var json = System.Text.Json.JsonSerializer.Serialize(requestBody);

        try
        {
            using var response = await SendPiaCloudRequestAsync(
                httpClient, $"{serverUrl}/api/ai/optimize", json, mode,
                // §7: /api/ai/optimize has no connector plane and never reads X-Pia-Persona.
                managedPersonaId: null, linkedCts.Token);

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

            var handler = _handlers.Get(provider.ProviderType);
            var httpClient = CreateAiHttpClient();
            var chatClient = await handler.CreateChatClientAsync(provider, apiKey, httpClient, mode: null, managedPersonaId: null, personaModelType: null, linkedCts.Token);

            var messages = new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, NoThinkSystemPrompt),
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, prompt),
            };

            var options = handler.CreateChatOptions(provider, hasTools: false);

            // T1-2: single-shot, so the permit brackets exactly this call.
            ChatResponse response;
            using (await AcquireProviderPermitAsync(
                provider, timeout, timeoutCts, cancellationToken, linkedCts.Token))
            {
                response = await chatClient.GetResponseAsync(
                    messages,
                    options,
                    cancellationToken: linkedCts.Token
                );
            }

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
                _logger.LogDebug("Token usage: input={Input}, output={Output}, cached={Cached}",
                    usage.InputTokenCount, usage.OutputTokenCount, usage.CachedInputTokenCount);
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

        var handler = _handlers.Get(provider.ProviderType);
        var httpClient = CreateAiHttpClient();
        // No persona here on purpose: this tool-free stream serves SuggestionService, not an assistant
        // chat turn, so there is no selected persona whose resources should be unlocked.
        var chatClient = await handler.CreateChatClientAsync(
            provider, apiKey, httpClient, mode, managedPersonaId: null, personaModelType: null, linkedCts.Token);
        var options = handler.CreateChatOptions(provider, hasTools: false);

        // T1-2: this tool-free stream (SuggestionService) is one round-trip that stays open for the length of
        // the stream, so the permit is taken here and released in the outer finally that disposes the
        // enumerator — the same bracket the tool loop's streaming round uses.
        var permit = await AcquireProviderPermitAsync(
            provider, timeout, timeoutCts, cancellationToken, linkedCts.Token);

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
            permit.Dispose();
        }
    }

    // The three Test* probes below and the two PiaCloud REST helpers above are deliberately NOT throttled
    // (T1-2). The probes are one-shot connectivity checks a person just clicked in Settings or the first-run
    // wizard: making a "Test" button queue behind two background runs would read as a hung dialog, and one
    // extra request is not what a rate limit trips on. The PiaCloud REST helpers
    // (GeneratePromptViaPiaCloudAsync / OptimizeViaPiaCloudAsync) have no AiProvider in scope at all — they
    // call Pia Cloud's own endpoints, so there is no provider key to bound.
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
            var httpClient = CreateAiHttpClient();

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

    public async Task<bool> TestStreamingAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var handler = _handlers.Get(provider.ProviderType);
        var httpClient = CreateAiHttpClient();
        var chatClient = await handler.CreateChatClientAsync(provider, apiKey, httpClient, mode: null, managedPersonaId: null, personaModelType: null, linkedCts.Token);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, NoThinkSystemPrompt),
            new(ChatRole.User, "Say hello.")
        };
        var options = handler.CreateChatOptions(provider, hasTools: false);

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

    public async Task<bool> TestToolCallingAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var handler = _handlers.Get(provider.ProviderType);
        var httpClient = CreateAiHttpClient();
        var chatClient = await handler.CreateChatClientAsync(provider, apiKey, httpClient, mode: null, managedPersonaId: null, personaModelType: null, linkedCts.Token);

        var dummyTool = AIFunctionFactory.Create(() => "ok", "ping", "A test tool");
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, NoThinkSystemPrompt),
            new(ChatRole.User, "Say hello.")
        };
        var options = handler.CreateChatOptions(provider, hasTools: true);
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

    public async Task<bool> TestToolCallEmittedAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        // R10 strengthening: unlike TestToolCallingAsync (schema-accept only), this demands an ACTUAL
        // tool call and inspects the response for a FunctionCallContent. Does NOT mutate the settings-page
        // probe. Non-blocking: 400/404 and other faults surface to the caller → Weak/Unknown.
        var apiKey = _dpapiHelper.Decrypt(provider.EncryptedApiKey ?? string.Empty);
        var timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds is > 0 ? provider.TimeoutSeconds : 300);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        var handler = _handlers.Get(provider.ProviderType);
        var httpClient = CreateAiHttpClient();
        var chatClient = await handler.CreateChatClientAsync(provider, apiKey, httpClient, mode: null, managedPersonaId: null, personaModelType: null, linkedCts.Token);

        var pingTool = AIFunctionFactory.Create(() => "ok", "ping", "A test tool. Call it to confirm tool support.");
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, NoThinkSystemPrompt),
            new(ChatRole.User, "Call the ping tool now. Do not answer in text."),
        };
        var options = handler.CreateChatOptions(provider, hasTools: true);
        options.Tools = [pingTool];
        options.ToolMode = ChatToolMode.RequireAny; // demand a call; providers that ignore it fall through to false

        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, linkedCts.Token);
            return response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Any(fc => string.Equals(fc.Name, "ping", StringComparison.Ordinal));
        }
        catch (TaskCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Capability probe timed out after {timeout.TotalSeconds} seconds");
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

    /// <summary>
    /// Provider phrasings for "this request is larger than the model's context window", matched as
    /// substrings. Substring matching is the only option available: HTTP 400 is the only part of the shape
    /// every provider agrees on, and the machine-readable discriminator lives in the response body, which
    /// neither <see cref="ClientResultException"/> nor <see cref="HttpRequestException"/> surfaces in a typed
    /// form. The list WILL miss providers whose phrasing is not here, and it misses a provider that carries
    /// the body only on an inner exception; every miss degrades to exactly the previous behaviour (the extra
    /// log line is simply not emitted, the retry runs as before) and never to worse.
    /// </summary>
    private static readonly string[] ContextLengthErrorMarkers =
    [
        "context_length_exceeded",           // OpenAI / Azure OpenAI / vLLM error code
        "context length",                    // "This model's maximum context length is 8192 tokens"
        "context window",                    // Anthropic / OpenRouter prose
        "context size",                      // llama.cpp / Ollama: "exceeds the available context size"
        "prompt is too long",                // Anthropic: "prompt is too long: 218898 tokens > 199999 maximum"
        "too many tokens",                   // Mistral: "Too many tokens in prompt"
        "input token count",                 // Gemini: "The input token count (...) exceeds the maximum"
        "reduce the length of the messages", // OpenAI's own remediation hint
    ];

    /// <summary>
    /// True when <paramref name="ex"/> is a provider rejection whose body says the request exceeded the
    /// model's context window.
    /// <para>
    /// WHY: <see cref="IsToolNotSupportedError"/> answers true for essentially ANY 400, so a context overflow
    /// at round 0 was reported as "retrying without tools" — the wrong top-line diagnosis for whoever reads a
    /// support log, on top of one wasted round trip that re-sends the same oversized list. This classifier
    /// exists ONLY to name the real cause in the log. It gates no control flow: the tool-disabled retry runs
    /// exactly as it did before, whatever this answers.
    /// </para>
    /// <para>
    /// Same defensive posture as its sibling: only the two exception types a provider adapter actually throws
    /// are considered, everything else is false. The status code is deliberately NOT re-checked — every call
    /// site sits inside a filter that has already established 400/404 via
    /// <see cref="IsToolNotSupportedError"/>, and a re-check would go blind on a
    /// <see cref="ClientResultException"/> that carries a body but no response (its <c>Status</c> is 0 then,
    /// measured). <c>internal</c> rather than <c>private</c> so the classification can be unit-tested directly
    /// through <c>InternalsVisibleTo</c>; it is the only new logic here.
    /// </para>
    /// </summary>
    internal static bool IsContextLengthError(Exception ex)
    {
        if (ex is not (ClientResultException or HttpRequestException))
        {
            return false;
        }

        var message = ex.Message;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (var marker in ContextLengthErrorMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Emits the ONE metadata-only line that names a context overflow for what it is, so a support log does
    /// not read as a tool-capability problem. Called as the FIRST statement of each tool-not-supported catch
    /// body, i.e. ahead of that body's "retrying without tools" warning — the ordering is the whole point, and
    /// nothing about the retry changes. It follows that the line only appears when <c>useTools</c> and
    /// <c>round == 0</c>, since that is the only condition reaching those catch bodies.
    /// <para>
    /// Privacy: no message content, no goal text, and NOT the provider's raw error string — release logs get
    /// attached to support tickets. Counts, the round index and the configured budget only, and the provider
    /// TYPE rather than the user-named provider.
    /// </para>
    /// </summary>
    private void LogContextLengthRejection(
        Exception ex, AiProvider provider, int round, int messageCount, AgentContextBudget? contextBudget)
    {
        if (!IsContextLengthError(ex))
        {
            return;
        }

        _logger.LogWarning(
            "Context overflow, NOT a tool-support problem: providerType={ProviderType} rejected round {Round}; " +
            "the request carried {MessageCount} message(s), contextBudgetConfigured={BudgetConfigured}, " +
            "windowTokens={WindowTokens}, maxOutputTokens={MaxOutputTokens}. The tool-disabled retry logged " +
            "next re-sends the same request and is expected to fail the same way.",
            provider.ProviderType,
            round + 1,
            messageCount,
            contextBudget is not null,
            contextBudget?.WindowTokens ?? 0,
            contextBudget?.MaxOutputTokens ?? 0);
    }

    private static string Truncate(string value, int max)
            => value.Length > max ? value[..max] + "..." : value;

    /// <summary>
    /// Surfaces model reasoning from a response chunk via every channel a provider might use:
    /// the canonical <see cref="TextReasoningContent"/> that Microsoft.Extensions.AI maps from
    /// <c>reasoning_content</c> (DeepSeek / vLLM / Ollama) and OpenAI Responses reasoning
    /// summaries, plus OpenRouter's non-standard <c>reasoning</c> scalar that the adapter drops
    /// (recovered from the raw representation). The two channels are mutually exclusive per
    /// provider, so this never double-counts.
    /// </summary>
    private static IEnumerable<ChatStreamItem> ExtractReasoning(
            IEnumerable<AIContent> contents, object? rawRepresentation, bool attemptRawExtraction)
    {
        foreach (var content in contents)
        {
            if (content is TextReasoningContent { Text: { Length: > 0 } reasoningText })
                yield return new ReasoningDelta(reasoningText);
        }

        if (attemptRawExtraction)
        {
            var rawReasoning = ReasoningExtractor.FromRawRepresentation(rawRepresentation);
            if (!string.IsNullOrEmpty(rawReasoning))
                yield return new ReasoningDelta(rawReasoning);
        }
    }

    private ChatStreamItem BuildFinishedItem(AiProvider provider, bool hasUsage, long aggregatedInput, long aggregatedOutput, bool protectedRoute)
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
            _logger.LogDebug("Completion total usage: input={Input}, output={Output}, total={Total}",
                aggregatedInput, aggregatedOutput, aggregatedInput + aggregatedOutput);
        }
        else
        {
            _logger.LogDebug("Stream finished without usage details, providerType={ProviderType}", provider.ProviderType);
        }

        var modelLabel = !string.IsNullOrWhiteSpace(provider.ModelName)
            ? provider.ModelName
            : provider.Name;
        return new Finished(usage, modelLabel, protectedRoute);
    }

    /// <summary>
    /// POSTs to a Pia Cloud endpoint with a valid bearer token from the auth service, retrying
    /// once on 401 with a forced token refresh. Caller owns disposing the returned response.
    /// <para>
    /// <paramref name="managedPersonaId"/> exists for a future non-streaming chat route. Both current
    /// callers (/api/ai/generate-prompt and /api/ai/optimize) pass null deliberately: only /api/ai/chat
    /// reads <c>X-Pia-Persona</c>, and the handoff's out-of-scope list forbids sending it on those two.
    /// </para>
    /// </summary>
    private async Task<HttpResponseMessage> SendPiaCloudRequestAsync(
        HttpClient httpClient, string url, string jsonBody, string? mode, Guid? managedPersonaId,
        CancellationToken cancellationToken)
    {
        async Task<(HttpResponseMessage Response, string? Token)> Attempt(bool forceRefresh, string? staleAccessToken = null)
        {
            var token = await _authService.GetAccessTokenAsync(forceRefresh, staleAccessToken);
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(mode))
                request.Headers.Add("X-Pia-Mode", mode);
            if (managedPersonaId is Guid personaId)
                request.Headers.Add("X-Pia-Persona", personaId.ToString());
            return (await httpClient.SendAsync(request, cancellationToken), token);
        }

        var (response, token) = await Attempt(forceRefresh: false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("PiaCloud request unauthorized; refreshing token and retrying once");
            response.Dispose();
            (response, _) = await Attempt(forceRefresh: true, token);
        }
        return response;
    }
}