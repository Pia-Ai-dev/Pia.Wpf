using System.IO;
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
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Services;

// A pull page holding ciphertext this client cannot read must be refused whole, leaving the cursor
// untouched. Applying it row-by-row wrote blank entities over real data — the server blanks the
// plaintext columns of an E2EE row — and the advancing cursor then made that permanent.
public class SyncPullCiphertextRefusalTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IE2EEService _e2ee = Substitute.For<IE2EEService>();

    private SyncClientService CreateSut(bool e2eeReady)
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        _e2ee.IsReady().Returns(e2eeReady);
        _templateService.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        _memoryService.GetAllObjectsAsync().Returns(Array.Empty<MemoryObject>());

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            new SyncMapper(dpapi, _e2ee), _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance),
            e2ee: _e2ee);
    }

    // The wire shape of an E2EE provider: ciphertext, and the plaintext columns the server wiped.
    private static string PullBodyWithEncryptedProvider()
    {
        var response = new SyncPullResponse { ServerTimestamp = DateTime.UtcNow };
        response.Providers.Upserted.Add(new SyncProvider
        {
            Id = Guid.NewGuid(),
            Name = null,
            ProviderType = 0,
            EncryptedPayload = "ZmFrZQ==",
            WrappedDek = "ZmFrZQ==",
            UpdatedAt = DateTime.UtcNow,
        });
        return JsonSerializer.Serialize(response);
    }

    private static async Task<(bool NotModified, bool PullSucceeded, int Pulled, int DecryptionErrors, DateTime? ServerTimestamp, bool HasMore)>
        InvokePullPageAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullPageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<(bool, bool, int, int, DateTime?, bool)>)method.Invoke(
            sut, [client, "http://test", settings, DateTime.MinValue, true])!;
    }

    [Fact]
    public async Task PullPage_withCiphertextAndE2EEInactive_failsAndAppliesNothing()
    {
        var sut = CreateSut(e2eeReady: false);
        using var client = new HttpClient(new StubHandler(PullBodyWithEncryptedProvider()));

        var result = await InvokePullPageAsync(sut, client, new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            SyncUserId = "user-1",
        });

        // PullSucceeded=false is what keeps LastSyncTimestamp where it is (see PullChangesAsync).
        Assert.False(result.PullSucceeded);
        Assert.Null(result.ServerTimestamp);
        await _providerService.DidNotReceive().AddProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
        await _providerService.DidNotReceive().UpdateProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
    }

    // Same page, same client state, but no user id to key the AAD with — equally unreadable.
    [Fact]
    public async Task PullPage_withCiphertextAndNoUserId_failsAndAppliesNothing()
    {
        var sut = CreateSut(e2eeReady: true);
        using var client = new HttpClient(new StubHandler(PullBodyWithEncryptedProvider()));

        var result = await InvokePullPageAsync(sut, client, new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            SyncUserId = null,
        });

        Assert.False(result.PullSucceeded);
        await _providerService.DidNotReceive().AddProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
    }

    // The refusal must be specific to unreadable ciphertext: a plaintext page still applies.
    [Fact]
    public async Task PullPage_plaintextRows_stillApply()
    {
        var sut = CreateSut(e2eeReady: false);
        var response = new SyncPullResponse { ServerTimestamp = DateTime.UtcNow };
        response.Providers.Upserted.Add(new SyncProvider
        {
            Id = Guid.NewGuid(),
            Name = "plain",
            ProviderType = (int)AiProviderType.OpenAICompatible,
            Endpoint = "https://example.invalid/v1",
            UpdatedAt = DateTime.UtcNow,
        });
        using var client = new HttpClient(new StubHandler(JsonSerializer.Serialize(response)));

        var result = await InvokePullPageAsync(sut, client, new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            SyncUserId = "user-1",
        });

        Assert.True(result.PullSucceeded);
        await _providerService.Received(1).AddProviderAsync(
            Arg.Is<AiProvider>(p => p.Name == "plain"), Arg.Any<string?>());
    }

    // The account is flagged E2EE before any row is encrypted, so until a row is migrated the server
    // emits it with no ciphertext AND no plaintext. There is nothing here for the ciphertext scan to
    // catch, so it needs its own guard: leave the local copy alone rather than blank it.
    [Fact]
    public async Task PullPage_unmigratedShell_isDroppedNotApplied()
    {
        var sut = CreateSut(e2eeReady: true);
        var response = new SyncPullResponse { ServerTimestamp = DateTime.UtcNow };
        response.Providers.Upserted.Add(new SyncProvider
        {
            Id = Guid.NewGuid(),
            Name = null,
            ProviderType = 0,
            EncryptedPayload = null, // not migrated yet
            WrappedDek = null,
            UpdatedAt = DateTime.UtcNow,
        });
        using var client = new HttpClient(new StubHandler(JsonSerializer.Serialize(response)));

        var result = await InvokePullPageAsync(sut, client, new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            SyncUserId = "user-1",
        });

        // The page itself is fine — only the shell row is dropped, so the cursor still advances.
        Assert.True(result.PullSucceeded);
        await _providerService.DidNotReceive().AddProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
        await _providerService.DidNotReceive().UpdateProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
