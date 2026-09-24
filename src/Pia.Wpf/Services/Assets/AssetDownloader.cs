using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Assets;

/// <summary>Fetches a runtime asset from the configured mirror, or upstream when none is configured.</summary>
public sealed class AssetDownloader : IAssetDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssetDownloader> _logger;
    private readonly AssetMirrorOptions _options;

    // Only a deployment that configures no mirror reaches upstream, and then it is the sole source, so
    // the deadline is patient: the alternative to waiting is the feature simply not working.
    private static readonly TimeSpan UpstreamHeadersTimeout = TimeSpan.FromSeconds(60);

    public AssetDownloader(
        IHttpClientFactory httpClientFactory,
        IOptions<AssetMirrorOptions> options,
        ILogger<AssetDownloader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<long> DownloadAsync(
        RuntimeAsset asset,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mirrorUrl = MirrorUrlFor(asset.MirrorKey);
        var url = mirrorUrl ?? asset.UpstreamUrl;
        var headersTimeout = mirrorUrl is null
            ? UpstreamHeadersTimeout
            : HeadersTimeout(_options.MirrorTimeoutSeconds);

        var bytes = await DownloadFromAsync(url, destinationPath, headersTimeout, progress, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Fetched {Key} from {Url}", asset.MirrorKey, SafeUrl.Format(url));
        return bytes;
    }

    private static TimeSpan HeadersTimeout(int seconds) => TimeSpan.FromSeconds(Math.Max(1, seconds));

    /// <summary>Null only when no mirror is configured — a key that cannot form a URL under one throws
    /// rather than sending the download to a host the deployment did not ask for.</summary>
    internal string? MirrorUrlFor(string mirrorKey)
    {
        var root = _options.MirrorBaseUrl?.Trim();
        if (string.IsNullOrEmpty(root)) return null;
        if (!root.EndsWith('/')) root += "/";
        return new Uri(new Uri(root, UriKind.Absolute), mirrorKey).ToString();
    }

    // A deadline that fires cancels the linked token, so without this the caller sees the same exception
    // it gets when the user clicks Cancel — and the callers that filter on "did the user cancel?" read a
    // dead host as the user's own click and unwind quietly.
    private static async Task<HttpResponseMessage> GetHeadersAsync(
        HttpClient http, string url, TimeSpan headersTimeout, CancellationToken deadline, CancellationToken caller)
    {
        try
        {
            return await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No response headers from {SafeUrl.Format(url)} within {headersTimeout.TotalSeconds:0}s.");
        }
    }

    private async Task<long> DownloadFromAsync(
        string url,
        string destinationPath,
        TimeSpan headersTimeout,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var http = _httpClientFactory.CreateClient();

        // HttpClient.Timeout is NOT a headers deadline — it keeps running under ResponseHeadersRead and
        // aborts the body stream, so the factory's 100 s default would cap every transfer here at 100 s.
        // The 1.8 GB Whisper Medium bundle is the case that makes that a hard failure, not a slow path.
        http.Timeout = Timeout.InfiniteTimeSpan;

        // What is bounded instead is reaching the first response byte. A host that cannot answer in that
        // window is down, and no size of transfer makes waiting longer for headers useful.
        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersCts.CancelAfter(headersTimeout);

        using var response = await GetHeadersAsync(http, url, headersTimeout, headersCts.Token, cancellationToken)
            .ConfigureAwait(false);

        // Disarm before the body: HttpClient ties the response stream's life to the token the request was
        // made with, so a live timer here would abort a legitimate multi-minute transfer at the deadline.
        headersCts.CancelAfter(Timeout.InfiniteTimeSpan);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0L;
        // Without a length the percentage branch below never fires, so a lazy-show progress dialog is
        // never created and the download runs invisibly. One indeterminate report opens it.
        if (totalBytes == 0)
            progress?.Report(new ModelDownloadProgress(0, 0, ModelDownloadPhase.Downloading));

        var buffer = new byte[81920];
        var bytesRead = 0L;

        await using (var destination = File.Create(destinationPath))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesRead += read;
                if (totalBytes > 0)
                    progress?.Report(new ModelDownloadProgress((int)(bytesRead * 100 / totalBytes), totalBytes));
            }
        }

        // A mirror that answers 200 with a truncated body would otherwise poison the cache: the app's
        // presence checks are "the file is non-empty" and "the directory holds an .onnx", so nothing
        // downstream would ever re-fetch it.
        if (totalBytes > 0 && bytesRead != totalBytes)
        {
            TryDelete(destinationPath);
            throw new IOException(
                $"Truncated download from {SafeUrl.Format(url)}: got {bytesRead} bytes, server said {totalBytes}.");
        }

        return bytesRead;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
