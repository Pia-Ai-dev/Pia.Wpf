using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Infrastructure;

public class RateLimitRetryHandler : DelegatingHandler
{
    private readonly ILogger<RateLimitRetryHandler> _logger;
    private static readonly ConcurrentDictionary<string, DateTime> _lastRequestTime = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _laneLocks = new();

    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    // A pacing net against a burst, not a rate-limit remedy: it is paid on every request to an endpoint that
    // has never answered 429, and an agent run pays it once per LLM round.
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(100);

    public RateLimitRetryHandler(ILogger<RateLimitRetryHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var lane = LaneOf(request.RequestUri);

        // Proactive throttle: enforce a minimum gap between requests to the same endpoint group
        await ThrottleAsync(lane, cancellationToken);

        // Buffer request content for potential retries
        var (contentBytes, contentType) = await BufferRequestAsync(request);

        HttpResponseMessage response = null!;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
                await ThrottleAsync(lane, cancellationToken);

            var clone = attempt == 0 ? request : CloneRequest(request, contentBytes, contentType);

            response = await base.SendAsync(clone, cancellationToken);
            _lastRequestTime[lane] = DateTime.UtcNow;

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            LogRateLimitHeaders(response, lane);

            if (attempt == MaxRetries)
                break;

            var delay = GetRetryDelay(response, attempt);
            if (delay == null)
            {
                _logger.LogWarning("Retry-After exceeds {MaxSeconds}s, not retrying {Lane}",
                    MaxRetryAfter.TotalSeconds, LaneLabel(lane));
                break;
            }

            _logger.LogInformation("Rate limited by {Lane}, retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                LaneLabel(lane), (int)delay.Value.TotalMilliseconds, attempt + 1, MaxRetries);

            response.Dispose();
            await Task.Delay(delay.Value, cancellationToken);
        }

        return response;
    }

    /// <summary>Host plus up to two path segments. Per HOST alone, one FIFO covered every caller, so a
    /// burst of background sync PUTs queued ahead of the interactive request behind it — measured at 68
    /// requests deep, which is 6.8s of pacing before the user's turn was even sent.</summary>
    private static string LaneOf(Uri? uri)
    {
        if (uri is null) return "unknown";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? uri.Host
            : $"{uri.Host}/{string.Join('/', segments.Take(2))}";
    }

    // The lane KEYS the throttle dictionary; this is only what reaches the log. The host is a configured
    // server, which the export treats as key material, so it travels as a stable code instead.
    private static string LaneLabel(string lane)
    {
        var slash = lane.IndexOf('/');
        var host = slash < 0 ? lane : lane[..slash];
        return $"host-{LogRedactor.HostCode(host)}{(slash < 0 ? string.Empty : lane[slash..])}";
    }

    private async Task ThrottleAsync(string lane, CancellationToken cancellationToken)
    {
        var laneLock = _laneLocks.GetOrAdd(lane, _ => new SemaphoreSlim(1, 1));
        await laneLock.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequestTime.TryGetValue(lane, out var lastTime))
            {
                var elapsed = DateTime.UtcNow - lastTime;
                if (elapsed < MinRequestInterval)
                {
                    var wait = MinRequestInterval - elapsed;
                    _logger.LogDebug("Throttling request to {Lane} for {WaitMs}ms", LaneLabel(lane), (int)wait.TotalMilliseconds);
                    await Task.Delay(wait, cancellationToken);
                }
            }
            _lastRequestTime[lane] = DateTime.UtcNow;
        }
        finally
        {
            laneLock.Release();
        }
    }

    private TimeSpan? GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
        if (retryAfter.HasValue)
        {
            return retryAfter.Value <= MaxRetryAfter ? retryAfter : null;
        }

        var isCloudflare = IsCloudflareResponse(response);

        // Cloudflare DDoS 429s need longer cooldowns: 5s, 10s, 20s
        // Normal API rate limits: 1s, 2s, 4s
        var baseSeconds = isCloudflare ? 5.0 * Math.Pow(2, attempt) : Math.Pow(2, attempt);
        var baseDelay = TimeSpan.FromSeconds(baseSeconds);

        // Apply ±25% jitter
        var jitter = baseDelay * (0.75 + Random.Shared.NextDouble() * 0.5);
        return jitter <= MaxRetryAfter ? jitter : null;
    }

    private static bool IsCloudflareResponse(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues("Server", out var values)
            && values.Any(v => v.Contains("cloudflare", StringComparison.OrdinalIgnoreCase));
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header == null) return null;
        if (header.Delta.HasValue) return header.Delta.Value;
        if (header.Date.HasValue)
        {
            var delay = header.Date.Value - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(100);
        }
        return null;
    }

    private void LogRateLimitHeaders(HttpResponseMessage response, string lane)
    {
        var retryAfter = response.Headers.RetryAfter?.ToString() ?? "-";
        var limit = GetHeaderValue(response, "x-ratelimit-limit-requests");
        var remaining = GetHeaderValue(response, "x-ratelimit-remaining-requests");
        var reset = GetHeaderValue(response, "x-ratelimit-reset-requests");
        var server = GetHeaderValue(response, "Server");
        var cfRay = GetHeaderValue(response, "CF-RAY");

        _logger.LogWarning(
            "Rate limited (429) by {Lane}: Retry-After={RetryAfter}, " +
            "Limit={Limit}, Remaining={Remaining}, Reset={Reset}, " +
            "Server={Server}, CF-RAY={CfRay}",
            LaneLabel(lane), retryAfter, limit, remaining, reset, server, cfRay);
    }

    private static string GetHeaderValue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : "-";
    }

    private static async Task<(byte[] Content, string? ContentType)> BufferRequestAsync(
        HttpRequestMessage request)
    {
        if (request.Content is null)
            return ([], null);
        var bytes = await request.Content.ReadAsByteArrayAsync();
        var contentType = request.Content.Headers.ContentType?.ToString();
        return (bytes, contentType);
    }

    private static HttpRequestMessage CloneRequest(
        HttpRequestMessage original, byte[] content, string? contentType)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (content.Length > 0)
        {
            clone.Content = new ByteArrayContent(content);
            if (contentType is not null)
                clone.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        return clone;
    }
}
