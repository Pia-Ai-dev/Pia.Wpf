using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Pia.Infrastructure;

public class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _logger;
    private const int MaxUrlLength = 500;

    public HttpLoggingHandler(ILogger<HttpLoggingHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method;
        var url = TruncateUrl(request.RequestUri?.ToString() ?? "<no-url>");

        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex,
                "HTTP {Method} {Url} failed after {ElapsedMs}ms: {ErrorMessage}",
                method, url, stopwatch.ElapsedMilliseconds, ex.Message);
            throw;
        }

        stopwatch.Stop();
        var elapsed = stopwatch.ElapsedMilliseconds;
        var statusCode = (int)response.StatusCode;
        var responseContentType = response.Content?.Headers.ContentType?.ToString();
        var responseContentLength = response.Content?.Headers.ContentLength;

        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "HTTP {Method} {Url} -> {StatusCode} ({ElapsedMs}ms, ContentType={ResponseContentType}, ContentLength={ResponseContentLength})",
                method, url, statusCode, elapsed, responseContentType ?? "-", responseContentLength);
        }
        else
        {
            _logger.LogWarning(
                "HTTP {Method} {Url} -> {StatusCode} ({ElapsedMs}ms, ContentType={ResponseContentType}, ContentLength={ResponseContentLength})",
                method, url, statusCode, elapsed, responseContentType ?? "-", responseContentLength);
        }

        return response;
    }

    private static string TruncateUrl(string url)
    {
        return url.Length > MaxUrlLength
            ? string.Concat(url.AsSpan(0, MaxUrlLength), "...")
            : url;
    }
}
