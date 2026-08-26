using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The provider editor deliberately keeps the saved key out of its password box, so a "Refresh models" on an
/// existing provider has no key to send unless the service reaches for the stored one.
/// </summary>
public sealed class ProviderModelListKeyTests : IDisposable
{
    private readonly string _dir;
    private readonly DpapiHelper _dpapi = new(NullLogger<DpapiHelper>.Instance);

    public ProviderModelListKeyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaFetchKey_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RedirectedProviderService(
        string directory,
        ILogger<ProviderService> logger,
        IAiClientService aiClient,
        DpapiHelper dpapi,
        ISettingsService settings,
        IAuthService auth,
        SyncDeleteTrackerService deleteTracker)
        : ProviderService(logger, aiClient, dpapi, settings, auth, deleteTracker)
    {
        protected override string DirectoryPath { get; } = directory;
    }

    private ProviderService ServiceOver(params AiProvider[] stored)
    {
        File.WriteAllText(
            Path.Combine(_dir, "providers.json"),
            JsonSerializer.Serialize(stored, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        return new RedirectedProviderService(
            _dir,
            NullLogger<ProviderService>.Instance,
            Substitute.For<IAiClientService>(),
            _dpapi,
            settings,
            Substitute.For<IAuthService>(),
            new SyncDeleteTrackerService(_dir, NullLogger<SyncDeleteTrackerService>.Instance));
    }

    private AiProvider Stored(Guid id, string? plainKey) => new()
    {
        Id = id,
        Name = "Mistral",
        ProviderType = AiProviderType.Mistral,
        Endpoint = "https://api.mistral.ai/v1",
        EncryptedApiKey = plainKey is null ? string.Empty : _dpapi.Encrypt(plainKey),
    };

    [Fact]
    public async Task AnUntouchedKeyField_SendsTheStoredKey()
    {
        var id = Guid.NewGuid();
        var service = ServiceOver(Stored(id, "sk-stored"));

        Assert.Equal("sk-stored", await service.ResolveFetchKeyAsync(typedKey: null, id));
    }

    [Fact]
    public async Task ATypedKey_WinsOverTheStoredOne()
    {
        var id = Guid.NewGuid();
        var service = ServiceOver(Stored(id, "sk-stored"));

        Assert.Equal("sk-rotated", await service.ResolveFetchKeyAsync("sk-rotated", id));
    }

    [Fact]
    public async Task AProviderThatWasNeverSaved_ResolvesToNoKey()
    {
        var service = ServiceOver(Stored(Guid.NewGuid(), "sk-stored"));

        Assert.Null(await service.ResolveFetchKeyAsync(typedKey: null, Guid.NewGuid()));
    }

    [Fact]
    public async Task AStoredProviderWithNoKey_ResolvesToNoKey()
    {
        var id = Guid.NewGuid();
        var service = ServiceOver(Stored(id, plainKey: null));

        Assert.Null(await service.ResolveFetchKeyAsync(typedKey: null, id));
    }
}
