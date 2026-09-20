using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Services;

// Which local rows the delta push is allowed to put on the wire.
public class SyncPushSelectionTests : IDisposable
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();

    private readonly string _trackerDir = Path.Combine(
        Path.GetTempPath(), "pia-push-selection-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_trackerDir, recursive: true); } catch (IOException) { }
    }

    private SyncClientService CreateSut()
    {
        Directory.CreateDirectory(_trackerDir);
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);

        _authService.GetAccessTokenAsync().Returns("token");
        _templateService.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        _memoryService.GetAllObjectsAsync().Returns(Array.Empty<MemoryObject>());
        _historyService.SearchSessionsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<OptimizationSession>());

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            new SyncMapper(dpapiHelper), _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            new SyncDeleteTrackerService(_trackerDir, NullLogger<SyncDeleteTrackerService>.Instance));
    }

    // LastPushedSettingsHash stays null so the settings-hash gate forces the POST even when the
    // collection under test is the only thing in the body.
    private static AppSettings PushSettings(DateTime? lastSync = null) =>
        new() { LastPushedSettingsHash = null, LastSyncTimestamp = lastSync };

    private async Task<string> PushAndCaptureBodyAsync(SyncClientService sut, AppSettings settings)
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var method = typeof(SyncClientService)
            .GetMethod("PushChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<(int PushedCount, bool PushSucceeded, bool SentChanges)>)method.Invoke(
            sut, [client, "http://test", settings])!;

        Assert.True(result.PushSucceeded);
        Assert.NotNull(handler.LastPushBody);
        return handler.LastPushBody!;
    }

    private static IReadOnlyList<Guid> UpsertedIds(string body, string collection)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty(collection).GetProperty("upserted")
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task Push_OmitsBuiltInTemplates()
    {
        var userTemplateId = Guid.NewGuid();
        var sut = CreateSut();
        _templateService.GetTemplatesAsync().Returns(new[]
        {
            new OptimizationTemplate { Id = Guid.NewGuid(), Name = "Summarize", Prompt = "p", IsBuiltIn = true },
            new OptimizationTemplate { Id = userTemplateId, Name = "Mine", Prompt = "p" }
        });

        var body = await PushAndCaptureBodyAsync(sut, PushSettings());

        Assert.Equal([userTemplateId], UpsertedIds(body, "templates"));
    }

    [Fact]
    public async Task Push_OmitsPiaCloudProviders()
    {
        var ownProviderId = Guid.NewGuid();
        var sut = CreateSut();
        _providerService.GetProvidersAsync().Returns(new[]
        {
            new AiProvider
            {
                Id = ProviderService.PiaCloudProviderId,
                Name = "Pia Cloud",
                ProviderType = AiProviderType.PiaCloud,
                Endpoint = "https://cloud.invalid"
            },
            new AiProvider
            {
                Id = ownProviderId,
                Name = "Mine",
                ProviderType = AiProviderType.OpenAI,
                Endpoint = "https://api.invalid"
            }
        });

        var body = await PushAndCaptureBodyAsync(sut, PushSettings());

        Assert.Equal([ownProviderId], UpsertedIds(body, "providers"));
    }

    [Fact]
    public async Task Push_AsksHistoryOnlyForSessionsCreatedSinceTheLastSync()
    {
        var lastSync = DateTime.UtcNow.AddHours(-3);
        var sessionId = Guid.NewGuid();
        var sut = CreateSut();
        _historyService.SearchSessionsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), lastSync,
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new[]
            {
                new OptimizationSession { Id = sessionId, OriginalText = "before", OptimizedText = "after" }
            });

        var body = await PushAndCaptureBodyAsync(sut, PushSettings(lastSync));

        await _historyService.Received(1).SearchSessionsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), lastSync,
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>());

        using var doc = JsonDocument.Parse(body);
        var added = doc.RootElement.GetProperty("sessions").GetProperty("added")
            .EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([sessionId], added);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastPushBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                LastPushBody = request.Content.Headers.ContentEncoding.Contains("gzip")
                    ? Decompress(bytes)
                    : Encoding.UTF8.GetString(bytes);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new SyncPushResponse { ServerTimestamp = DateTime.UtcNow }),
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private static string Decompress(byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
