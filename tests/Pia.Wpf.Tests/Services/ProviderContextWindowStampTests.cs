using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Every provider read fills in an assumed context window. Without it <c>AgentContextBudget.From</c> reads
/// null, compaction never runs for anyone, and a chat past the real window fails at the provider instead.
/// </summary>
public sealed class ProviderContextWindowStampTests : IDisposable
{
    private readonly string _dir;

    public ProviderContextWindowStampTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaWindow_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TempPath.Remove(_dir);
    }

    /// <summary>Redirects the store off the real profile — the base exposes DirectoryPath for exactly this.</summary>
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
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            settings,
            Substitute.For<IAuthService>(),
            new SyncDeleteTrackerService(_dir, NullLogger<SyncDeleteTrackerService>.Instance));
    }

    private static AiProvider Stored(string? modelName, int? window) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Configured",
        Endpoint = "https://example.invalid/v1",
        ModelName = modelName,
        MaxContextWindowTokens = window,
    };

    [Fact]
    public async Task AProviderWithNoWindow_ReadsBackWithTheAssumedDefault()
    {
        var service = ServiceOver(Stored("gpt-4o", window: null));

        var provider = Assert.Single(await service.GetProvidersAsync());

        Assert.Equal(ContextWindowDefaults.Fallback, provider.MaxContextWindowTokens);

        // The point of the stamp: compaction now has a budget to work against.
        var budget = AgentContextBudget.From(provider);
        Assert.NotNull(budget);
        Assert.Equal(ContextWindowDefaults.Fallback, budget!.Value.WindowTokens);
    }

    [Fact]
    public async Task AKnownModel_ReadsBackWithItsOwnWindow_NotTheFallback()
    {
        var service = ServiceOver(Stored("anthropic/claude-opus-5", window: null));

        var provider = Assert.Single(await service.GetProvidersAsync());

        Assert.Equal(1_000_000, provider.MaxContextWindowTokens);
    }

    /// <summary>A value the user typed is theirs — the stamp must never overwrite it.</summary>
    [Fact]
    public async Task AConfiguredWindow_SurvivesTheStamp()
    {
        var service = ServiceOver(Stored("anthropic/claude-opus-5", window: 32_000));

        var provider = Assert.Single(await service.GetProvidersAsync());

        Assert.Equal(32_000, provider.MaxContextWindowTokens);
    }

    /// <summary>Stamped into the loaded object, not written back: the editor shows it, but a window nobody
    /// edited stays out of providers.json.</summary>
    [Fact]
    public async Task TheStampIsNotPersisted()
    {
        var service = ServiceOver(Stored("gpt-4o", window: null));
        await service.GetProvidersAsync();

        var onDisk = File.ReadAllText(Path.Combine(_dir, "providers.json"));

        Assert.DoesNotContain("128000", onDisk);
    }
}
