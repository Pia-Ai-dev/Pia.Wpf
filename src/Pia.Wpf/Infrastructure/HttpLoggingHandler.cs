using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Infrastructure;

public class HttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<HttpLoggingHandler> _logger;

    public HttpLoggingHandler(ILogger<HttpLoggingHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method;
        var url = SafeUrl.Format(request.RequestUri);

        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            // A cancelled request — the user stopped the turn, cleared the chat, or the
            // app is shutting down — is normal, not a transport failure. Mid-stream
            // cancellation often surfaces as an IOException/SocketException ("operation
            // aborted") rather than OperationCanceledException; either way, log it gently
            // (no error level, no alarming stack) so it doesn't read as a fault in the
            // support log. Rethrow unchanged so the caller's cancellation handling runs.
            _logger.LogDebug("HTTP {Method} {Url} cancelled after {ElapsedMs}ms",
                method, url, stopwatch.ElapsedMilliseconds);
            throw;
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
}
