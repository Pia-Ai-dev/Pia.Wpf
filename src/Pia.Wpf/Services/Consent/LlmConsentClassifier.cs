using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Services.Consent.Cloud;
using Pia.Services.Consent.Privacy;

namespace Pia.Services.Consent;

/// <summary>
/// LLM-based classifier used as a fallback for the rule-based classifier when its confidence
/// is low. The prompt is single-shot, returns strict JSON, and never receives meeting content
/// other than the candidate consent reply itself.
///
/// Privacy gate: refuses to issue requests when <see cref="_isEuEndpoint"/> is false. In Strict
/// Mode (the only mode in Phase 2), non-EU endpoints are forbidden.
/// </summary>
public sealed class LlmConsentClassifier
{
    private const string SystemPrompt =
        "You classify a user's spoken reply to a recording-consent prompt. "
        + "Return ONLY a JSON object: {\"decision\":\"grant|deny|ambiguous\",\"confidence\":0.0-1.0,\"reason\":\"short\"}. "
        + "No prose, no markdown, no extra keys. If unclear, return ambiguous.";

    private readonly Func<CancellationToken, Task<IChatClient?>> _chatClientFactory;
    private readonly Func<bool> _isEuEndpointGate;
    private readonly IPreCloudPipeline? _preCloudPipeline;
    private readonly Func<(ConsentScope scope, CloudProviderDescriptor provider)>? _scopeProvider;
    private readonly ILogger<LlmConsentClassifier> _logger;

    public LlmConsentClassifier(
        Func<CancellationToken, Task<IChatClient?>> chatClientFactory,
        Func<bool> isEuEndpointGate,
        ILogger<LlmConsentClassifier> logger)
    {
        _chatClientFactory = chatClientFactory;
        _isEuEndpointGate = isEuEndpointGate;
        _logger = logger;
    }

    /// <summary>Convenience constructor for tests with a pre-built client.</summary>
    public LlmConsentClassifier(IChatClient chatClient, bool isEuEndpoint, ILogger<LlmConsentClassifier> logger)
        : this(_ => Task.FromResult<IChatClient?>(chatClient), () => isEuEndpoint, logger) { }

    /// <summary>Phase 4: route the call through <see cref="IPreCloudPipeline"/> for
    /// scope gating + PII pseudonymisation. Supersedes the legacy EU-endpoint gate.</summary>
    public LlmConsentClassifier(
        Func<CancellationToken, Task<IChatClient?>> chatClientFactory,
        IPreCloudPipeline preCloudPipeline,
        Func<(ConsentScope scope, CloudProviderDescriptor provider)> scopeProvider,
        ILogger<LlmConsentClassifier> logger)
    {
        _chatClientFactory = chatClientFactory;
        _preCloudPipeline = preCloudPipeline;
        _scopeProvider = scopeProvider;
        _isEuEndpointGate = () => true;
        _logger = logger;
    }

    public async Task<ConsentClassification> ClassifyAsync(string transcriptText, string promptText, CancellationToken cancellationToken = default)
    {
        if (_preCloudPipeline is null && !_isEuEndpointGate())
        {
            _logger.LogWarning("LlmConsentClassifier refused: non-EU endpoint in Strict Mode");
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);
        }

        try
        {
            var client = await _chatClientFactory(cancellationToken).ConfigureAwait(false);
            if (client is null)
            {
                _logger.LogWarning("LlmConsentClassifier: no chat client available; clamping to ambiguous");
                return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);
            }

            var userMessage =
                $"Consent prompt that was played: \"{promptText}\"\n"
                + $"User's reply: \"{transcriptText}\"\n"
                + "Classify the reply.";

            CloudCallContext? ctx = null;
            if (_preCloudPipeline is not null && _scopeProvider is not null)
            {
                var (scope, provider) = _scopeProvider();
                try
                {
                    ctx = await _preCloudPipeline
                        .PrepareAsync(userMessage, scope, provider, cancellationToken)
                        .ConfigureAwait(false);
                    userMessage = ctx.PseudonymisedPayload;
                }
                catch (CloudCallNotPermittedException)
                {
                    _logger.LogWarning("LlmConsentClassifier: pre-cloud pipeline blocked the call");
                    return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);
                }
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, userMessage),
            };
            var response = await client
                .GetResponseAsync(messages, options: null, cancellationToken)
                .ConfigureAwait(false);

            var raw = response.Messages.FirstOrDefault()?.Text ?? string.Empty;
            if (ctx is not null)
            {
                raw = await _preCloudPipeline!.PostProcessAsync(raw, ctx, cancellationToken).ConfigureAwait(false);
            }
            return Parse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM consent classification failed; clamping to ambiguous");
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);
        }
    }

    private static ConsentClassification Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);

        // Tolerate prose/markdown around the JSON object.
        var open = raw.IndexOf('{');
        var close = raw.LastIndexOf('}');
        if (open < 0 || close <= open) return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);
        var json = raw.Substring(open, close - open + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var decisionStr = root.TryGetProperty("decision", out var d) ? d.GetString() ?? "ambiguous" : "ambiguous";
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? Math.Clamp((float)c.GetDouble(), 0f, 1f)
                : 0.0f;
            var decision = decisionStr.ToLowerInvariant() switch
            {
                "grant" => ConsentDecision.Grant,
                "deny" => ConsentDecision.Deny,
                _ => ConsentDecision.Ambiguous,
            };
            return new ConsentClassification(decision, confidence);
        }
        catch (JsonException)
        {
            return new ConsentClassification(ConsentDecision.Ambiguous, 0.0f);
        }
    }
}
