using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Safety net for OpenAI Responses reasoning summaries. We request
/// <c>reasoning.summary = "auto"</c> so the model emits thinking we can surface,
/// but some organizations / models reject that field with HTTP 400. When that
/// happens this handler strips <c>reasoning.summary</c> from the request and
/// retries once, degrading to the prior (summary-free) behavior instead of
/// failing the turn. Retrying without the summary is always safe: if the 400 was
/// unrelated to the summary, the retry fails identically and that response is
/// returned unchanged.
/// </summary>
internal sealed class OpenAiReasoningSummaryFallbackHandler : DelegatingHandler
{
    private readonly ILogger? _logger;

    public OpenAiReasoningSummaryFallbackHandler(ILogger? logger = null) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only JSON requests can carry reasoning.summary. Buffer the body and reset
        // the content so the first send still has a readable stream.
        string? originalBody = null;
        if (request.Content is not null &&
            request.Content.Headers.ContentType?.MediaType == "application/json")
        {
            originalBody = await request.Content.ReadAsStringAsync(cancellationToken);
            request.Content = new StringContent(originalBody, Encoding.UTF8, "application/json");
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.BadRequest || originalBody is null)
            return response;

        var strippedBody = StripReasoningSummary(originalBody);
        if (strippedBody is null)
            return response; // no summary to drop — nothing we can do, surface the 400

        // High-signal: this means the OpenAI org/model rejected reasoning.summary, so no
        // reasoning will surface even though it was requested. Reasoning effort is unaffected.
        _logger?.LogWarning(
            "OpenAI returned 400 with reasoning.summary requested; retrying once without it (no reasoning summary will be shown for this turn)");

        using var retry = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = new StringContent(strippedBody, Encoding.UTF8, "application/json"),
            Version = request.Version,
        };
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);

        var retried = await base.SendAsync(retry, cancellationToken);
        response.Dispose();
        return retried;
    }

    /// <summary>Removes <c>reasoning.summary</c> from an OpenAI Responses request body.
    /// Returns null when there is no summary field to remove (or the body isn't parseable).</summary>
    internal static string? StripReasoningSummary(string body)
    {
        if (string.IsNullOrEmpty(body))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { return null; }

        if (root is not JsonObject obj || obj["reasoning"] is not JsonObject reasoning)
            return null;
        if (reasoning["summary"] is null)
            return null;

        reasoning.Remove("summary");
        return obj.ToJsonString();
    }
}
