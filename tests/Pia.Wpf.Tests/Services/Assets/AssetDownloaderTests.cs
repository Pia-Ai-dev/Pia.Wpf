using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pia.Models;
using Pia.Services.Assets;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Assets;

/// <summary>
/// A configured mirror is the only source a download has. These pin that a mirror failure surfaces
/// instead of being answered from somewhere else.
/// </summary>
public class AssetDownloaderTests : IDisposable
{
    private const string Mirror = "https://storage.example/f/assets/";
    private const string MirrorUrl = Mirror + "models/thing.onnx";
    private static readonly RuntimeAsset Asset = new("models/thing.onnx", "https://upstream.example/thing.onnx");

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "pia-asset-tests", Guid.NewGuid().ToString("N"));

    private string Destination => Path.Combine(_dir, "thing.onnx");

    public void Dispose()
    {
        TempPath.Remove(_dir);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Fetches_from_the_mirror_and_never_touches_upstream()
    {
        var handler = new RoutingHandler { [MirrorUrl] = Ok("mirrored") };
        var downloader = Create(handler, Mirror);

        var bytes = await downloader.DownloadAsync(Asset, Destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8, bytes);
        Assert.Equal("mirrored", File.ReadAllText(Destination));
        Assert.Equal([MirrorUrl], handler.Requested);
    }

    [Fact]
    public async Task A_mirror_404_fails_the_download_instead_of_reaching_upstream()
    {
        var handler = new RoutingHandler
        {
            [MirrorUrl] = Status(HttpStatusCode.NotFound),
            [Asset.UpstreamUrl] = Ok("upstream"),
        };
        var downloader = Create(handler, Mirror);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([MirrorUrl], handler.Requested);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task An_unreachable_mirror_fails_the_download_instead_of_reaching_upstream()
    {
        var handler = new RoutingHandler
        {
            Transport = { [MirrorUrl] = () => new HttpRequestException("no route") },
            [Asset.UpstreamUrl] = Ok("upstream"),
        };
        var downloader = Create(handler, Mirror);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([MirrorUrl], handler.Requested);
    }

    /// <summary>
    /// No failure may latch the mirror off: with nowhere else to download from, a latched process could
    /// not fetch an asset again until it restarts.
    /// </summary>
    [Fact]
    public async Task Keeps_trying_the_mirror_after_it_failed_once()
    {
        var handler = new RoutingHandler
        {
            Transport = { [MirrorUrl] = () => new HttpRequestException("no route") },
        };
        var downloader = Create(handler, Mirror);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken));
        handler.Requested.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal([MirrorUrl], handler.Requested);
    }

    [Fact]
    public async Task A_blank_mirror_goes_straight_upstream()
    {
        var handler = new RoutingHandler { [Asset.UpstreamUrl] = Ok("upstream") };
        var downloader = Create(handler, mirrorBaseUrl: "   ");

        await downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([Asset.UpstreamUrl], handler.Requested);
    }

    [Fact]
    public void A_mirror_base_without_a_trailing_slash_still_resolves_under_it()
    {
        var downloader = Create(new RoutingHandler(), "https://storage.example/f/assets");

        Assert.Equal(MirrorUrl, downloader.MirrorUrlFor("models/thing.onnx"));
    }

    /// <summary>
    /// The deadline cancels a token linked to the caller's, so a silent host reaches callers as the
    /// exception the Cancel button raises — and the ones filtering on "did the user cancel?" would
    /// unwind without ever reporting the host.
    /// </summary>
    [Fact]
    public async Task A_mirror_that_never_answers_reads_as_a_timeout_not_a_cancel()
    {
        var handler = new RoutingHandler { Hangs = { MirrorUrl } };
        var downloader = Create(handler, Mirror, mirrorTimeoutSeconds: 1);

        await Assert.ThrowsAsync<TimeoutException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_caller_gets_no_file()
    {
        var handler = new RoutingHandler
        {
            Transport = { [MirrorUrl] = () => new OperationCanceledException() },
        };
        var downloader = Create(handler, Mirror);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: cts.Token));
        Assert.False(File.Exists(Destination));
    }

    /// <summary>
    /// A truncated body is the failure that survives: every presence check downstream is "the file is
    /// non-empty", so a short file would be cached forever and never re-fetched.
    /// </summary>
    [Fact]
    public async Task A_body_shorter_than_Content_Length_is_a_failure_that_leaves_no_file()
    {
        var handler = new RoutingHandler
        {
            [MirrorUrl] = () =>
            {
                var truncated = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ab", Encoding.UTF8)
                };
                truncated.Content.Headers.ContentLength = 99;
                return truncated;
            },
        };
        var downloader = Create(handler, Mirror);

        await Assert.ThrowsAsync<IOException>(
            () => downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Destination));
    }

    /// <summary>
    /// HttpClient.Timeout keeps running under ResponseHeadersRead and aborts the body stream, so the
    /// factory's 100 s default would cap every transfer — a hard failure on the 1.8 GB bundle, not a
    /// slow path. The deadline this service does enforce covers reaching the first response byte.
    /// </summary>
    [Fact]
    public async Task Lifts_the_client_timeout_that_would_otherwise_cap_the_body_read()
    {
        var handler = new RoutingHandler { [MirrorUrl] = Ok("mirrored") };
        var factory = new SingleHandlerFactory(handler);
        var downloader = new AssetDownloader(factory,
            Options.Create(new AssetMirrorOptions { MirrorBaseUrl = Mirror }),
            NullLogger<AssetDownloader>.Instance);

        await downloader.DownloadAsync(Asset, Destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Timeout.InfiniteTimeSpan, Assert.Single(factory.Created).Timeout);
    }

    private static AssetDownloader Create(
        HttpMessageHandler handler, string? mirrorBaseUrl, int mirrorTimeoutSeconds = 60) =>
        new(new SingleHandlerFactory(handler),
            Options.Create(new AssetMirrorOptions
            {
                MirrorBaseUrl = mirrorBaseUrl,
                MirrorTimeoutSeconds = mirrorTimeoutSeconds,
            }),
            NullLogger<AssetDownloader>.Instance);

    private static Func<HttpResponseMessage> Ok(string body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private static Func<HttpResponseMessage> Status(HttpStatusCode code) => () => new HttpResponseMessage(code);

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<HttpClient> Created { get; } = [];

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            Created.Add(client);
            return client;
        }
    }

    // Responses are built per request, not stored: the downloader disposes what it reads, so a shared
    // instance makes the second call to the same URL throw ObjectDisposedException.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];

        public Dictionary<string, Func<Exception>> Transport { get; } = [];
        public HashSet<string> Hangs { get; } = [];
        public List<string> Requested { get; } = [];

        public Func<HttpResponseMessage> this[string url] { set => _responses[url] = value; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (Hangs.Contains(url))
                return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
                    _ => new HttpResponseMessage(), cancellationToken);
            if (Transport.TryGetValue(url, out var thrower))
                return Task.FromException<HttpResponseMessage>(thrower());
            return Task.FromResult(_responses.TryGetValue(url, out var response)
                ? response()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
