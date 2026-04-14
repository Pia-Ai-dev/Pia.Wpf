using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Pia.Infrastructure;

public class RateLimitRetryHandler : DelegatingHandler
{
    private readonly ILogger<RateLimitRetryHandler> _logger;
    private static readonly ConcurrentDictionary<string, DateTime> _lastRequestTime = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new();

    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(500);

    public RateLimitRetryHandler(ILogger<RateLimitRetryHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "unknown";

        // Proactive throttle: enforce minimum gap between requests to same host
        await ThrottleAsync(host, cancellationToken);

        // Buffer request content for potential retries
        var (contentBytes, contentType) = await BufferRequestAsync(request);

        HttpResponseMessage response = null!;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
                await ThrottleAsync(host, cancellationToken);

            var clone = attempt == 0 ? request : CloneRequest(request, contentBytes, contentType);

            response = await base.SendAsync(clone, cancellationToken);
            _lastRequestTime[host] = DateTime.UtcNow;

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
                return response;

            LogRateLimitHeaders(response, host);

            if (attempt == MaxRetries)
                break;

            var delay = GetRetryDelay(response, attempt);
            if (delay == null)
            {
                _logger.LogWarning("Retry-After exceeds {MaxSeconds}s, not retrying {Host}",
                    MaxRetryAfter.TotalSeconds, host);
                break;
            }

            _logger.LogInformation("Rate limited by {Host}, retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                host, (int)delay.Value.TotalMilliseconds, attempt + 1, MaxRetries);

            response.Dispose();
            await Task.Delay(delay.Value, cancellationToken);
        }

        return response;
    }

    private async Task ThrottleAsync(string host, CancellationToken cancellationToken)
    {
        var hostLock = _hostLocks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequestTime.TryGetValue(host, out var lastTime))
            {
                var elapsed = DateTime.UtcNow - lastTime;
                if (elapsed < MinRequestInterval)
                {
                    var wait = MinRequestInterval - elapsed;
                    _logger.LogDebug("Throttling request to {Host} for {WaitMs}ms", host, (int)wait.TotalMilliseconds);
                    await Task.Delay(wait, cancellationToken);
                }
            }
            _lastRequestTime[host] = DateTime.UtcNow;
        }
        finally
        {
            hostLock.Release();
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

    private void LogRateLimitHeaders(HttpResponseMessage response, string host)
    {
        var retryAfter = response.Headers.RetryAfter?.ToString() ?? "-";
        var limit = GetHeaderValue(response, "x-ratelimit-limit-requests");
        var remaining = GetHeaderValue(response, "x-ratelimit-remaining-requests");
        var reset = GetHeaderValue(response, "x-ratelimit-reset-requests");
        var server = GetHeaderValue(response, "Server");
        var cfRay = GetHeaderValue(response, "CF-RAY");

        _logger.LogWarning(
            "Rate limited (429) by {Host}: Retry-After={RetryAfter}, " +
            "Limit={Limit}, Remaining={Remaining}, Reset={Reset}, " +
            "Server={Server}, CF-RAY={CfRay}",
            host, retryAfter, limit, remaining, reset, server, cfRay);
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
