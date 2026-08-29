using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services.Assets;

/// <summary>
/// Mirror-first downloader. Everything here exists because the fallback has to be cheap when the
/// mirror is down and invisible when it is up.
/// </summary>
public sealed class AssetDownloader : IAssetDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AssetDownloader> _logger;
    private readonly AssetMirrorOptions _options;

    // The mirror gets a short deadline because falling back is cheap; upstream is the last resort and
    // gets a patient one, since the alternative to waiting is the feature simply not working.
    private static readonly TimeSpan UpstreamHeadersTimeout = TimeSpan.FromSeconds(60);

    // Latched on a TRANSPORT failure only, so the remaining ten assets in a first-run download do not
    // each re-pay a DNS or TLS timeout. An HTTP status answer proves the host is up and reachable, and
    // says nothing about the next key — a 404 there means "not mirrored", not "mirror down".
    private volatile bool _mirrorUnreachable;

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
        var mirrorUrl = TryBuildMirrorUrl(asset.MirrorKey);
        if (mirrorUrl is not null && !_mirrorUnreachable)
        {
            try
            {
                var bytes = await DownloadFromAsync(
                        mirrorUrl, destinationPath, HeadersTimeout(_options.MirrorTimeoutSeconds), progress, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Fetched {Key} from the asset mirror", asset.MirrorKey);
                return bytes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Latch on everything EXCEPT a real HTTP answer: a status code proves the host is up and
                // says nothing about the next key, while a TLS, DNS, socket or timeout failure is a cost
                // every remaining asset of a first run would otherwise re-pay.
                if (ex is not HttpRequestException { StatusCode: not null })
                    _mirrorUnreachable = true;

                _logger.LogWarning(ex, "Asset mirror {Url} failed for {Key}; falling back upstream",
                    SafeUrl.Format(mirrorUrl), asset.MirrorKey);
            }
        }

        // Restarting from zero rather than resuming: the two origins share no ETag, so a Range request
        // against the fallback could splice bytes from two different files. The progress bar rewinds.
        return await DownloadFromAsync(
                asset.UpstreamUrl, destinationPath, UpstreamHeadersTimeout, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TimeSpan HeadersTimeout(int seconds) => TimeSpan.FromSeconds(Math.Max(1, seconds));

    /// <summary>Null when no mirror is configured, or when the key cannot form a URL under it.</summary>
    internal string? TryBuildMirrorUrl(string mirrorKey)
    {
        var root = _options.MirrorBaseUrl?.Trim();
        if (string.IsNullOrEmpty(root)) return null;
        if (!root.EndsWith('/')) root += "/";
        return Uri.TryCreate(new Uri(root, UriKind.Absolute), mirrorKey, out var url) ? url.ToString() : null;
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

        using var response = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersCts.Token)
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
