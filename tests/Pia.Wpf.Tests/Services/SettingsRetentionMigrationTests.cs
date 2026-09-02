using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>The chat-history flag is gone from <see cref="AppSettings"/>, so these pin the one-shot
/// migration that reads it off the raw document before any save can erase it.</summary>
public sealed class SettingsRetentionMigrationTests : IDisposable
{
    private readonly string _dir;

    public SettingsRetentionMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaRetention_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TempPath.Remove(_dir);

    private sealed class RedirectedSettingsService(string directory, IPolicyService policy)
        : SettingsService(NullLogger<SettingsService>.Instance, policy)
    {
        protected override string DirectoryPath { get; } = directory;
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private void WriteStored(string json) => File.WriteAllText(SettingsPath, json);

    private SettingsService Service() =>
        new RedirectedSettingsService(_dir, Substitute.For<IPolicyService>());

    private JsonNode StoredDocument() => JsonNode.Parse(File.ReadAllText(SettingsPath))!;

    [Fact]
    public async Task HistoryOffAtTheOldDefault_IsRaisedToTheNewDefault()
    {
        // These installs never evicted anything, so the now-unconditional sweep must not cut at 30 days.
        WriteStored("""{ "chatHistoryEnabled": false, "chatHistoryRetentionDays": 30 }""");

        var settings = await Service().GetSettingsAsync();

        Assert.Equal(AppSettings.DefaultChatHistoryRetentionDays, settings.ChatHistoryRetentionDays);
        Assert.Null(StoredDocument()["chatHistoryEnabled"]);
        Assert.Equal(
            AppSettings.DefaultChatHistoryRetentionDays,
            (int)StoredDocument()["chatHistoryRetentionDays"]!);
    }

    [Fact]
    public async Task HistoryOffWithALongerWindow_KeepsIt()
    {
        // Raising only: 365 already outlives the new default, and lowering would delete more than asked.
        WriteStored("""{ "chatHistoryEnabled": false, "chatHistoryRetentionDays": 365 }""");

        var settings = await Service().GetSettingsAsync();

        Assert.Equal(365, settings.ChatHistoryRetentionDays);
        Assert.Null(StoredDocument()["chatHistoryEnabled"]);
    }

    [Fact]
    public async Task HistoryOn_KeepsItsWindowAndDropsTheRetiredKey()
    {
        WriteStored("""{ "chatHistoryEnabled": true, "chatHistoryRetentionDays": 30 }""");

        var settings = await Service().GetSettingsAsync();

        Assert.Equal(30, settings.ChatHistoryRetentionDays);
        Assert.Null(StoredDocument()["chatHistoryEnabled"]);
    }

    [Fact]
    public async Task ADocumentWithoutTheRetiredKey_IsLeftAlone()
    {
        WriteStored("""{ "chatHistoryRetentionDays": 45 }""");

        var settings = await Service().GetSettingsAsync();

        Assert.Equal(45, settings.ChatHistoryRetentionDays);
        Assert.Equal(45, (int)StoredDocument()["chatHistoryRetentionDays"]!);
    }

    [Fact]
    public async Task AFreshInstall_StartsOnTheNewDefault()
    {
        var settings = await Service().GetSettingsAsync();

        Assert.Equal(AppSettings.DefaultChatHistoryRetentionDays, settings.ChatHistoryRetentionDays);
    }

    [Fact]
    public async Task TheMigrationDoesNotRunTwice()
    {
        WriteStored("""{ "chatHistoryEnabled": false, "chatHistoryRetentionDays": 30 }""");

        var service = Service();
        await service.GetSettingsAsync();

        // A second process would find no flag left, so a stored value below the default must survive.
        WriteStored("""{ "chatHistoryRetentionDays": 30 }""");
        var reloaded = await new RedirectedSettingsService(_dir, Substitute.For<IPolicyService>())
            .GetSettingsAsync();

        Assert.Equal(30, reloaded.ChatHistoryRetentionDays);
    }
}
