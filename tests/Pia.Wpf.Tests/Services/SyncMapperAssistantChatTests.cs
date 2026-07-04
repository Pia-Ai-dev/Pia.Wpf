namespace Pia.Tests.Services;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

/// <summary>
/// Round-trip coverage for SyncAssistantChat through the SyncMapper, in both
/// plaintext mode (no E2EE) and end-to-end-encrypted mode (E2EE active).
/// </summary>
public class SyncMapperAssistantChatTests
{
    private const string UserId = "user-123";

    private static SyncMapper PlainMapper()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        return new SyncMapper(dpapi);
    }

    private static SyncMapper E2EEMapper()
    {
        var crypto = new CryptoService();
        var deviceKeys = Substitute.For<IDeviceKeyService>();
        deviceKeys.GetDeviceId().Returns("dev-test");

        var dpapi = Substitute.ForPartsOf<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(c => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(c.Arg<string>())));
        dpapi.Decrypt(Arg.Any<string>())
            .Returns(c => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(c.Arg<string>())));

        var appSettings = new AppSettings { IsE2EEEnabled = true };
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings);

        var e2ee = new E2EEService(crypto, deviceKeys, dpapi, settings, NullLogger<E2EEService>.Instance);
        e2ee.GenerateAndStoreUmkAsync().GetAwaiter().GetResult();

        return new SyncMapper(dpapi, e2ee);
    }

    private static SyncAssistantChat SampleChat()
    {
        var createdAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc);
        return new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            Title = "How do I unit test ICommand bindings?",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            LastAccessedAt = updatedAt,
            WindowMode = "Assistant",
            ProviderId = Guid.NewGuid(),
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = "How do I unit test ICommand bindings?",
                    Timestamp = createdAt,
                },
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "assistant",
                    Content = "Use CommunityToolkit.Mvvm with [RelayCommand]...",
                    ThinkingContent = "(internal monologue)",
                    Timestamp = updatedAt,
                    Tokens = 142,
                    ModelName = "gpt-5",
                    Persona = new SyncMessagePersona
                    {
                        Id = Guid.Parse("0000000A-0000-0000-0000-000000000004"),
                        Name = "Marketing Writer",
                        Emoji = "✍️",
                    },
                },
            ],
        };
    }

    [Fact]
    public void AssistantChat_RoundTrips_Plaintext()
    {
        var mapper = PlainMapper();
        var original = SampleChat();

        var wire = mapper.ToSyncAssistantChat(original, UserId);

        // No E2EE — plaintext fields populated, encryption fields null.
        Assert.Null(wire.EncryptedPayload);
        Assert.Null(wire.WrappedDek);
        Assert.Equal(original.Title, wire.Title);
        Assert.Equal(original.ProviderId, wire.ProviderId);
        Assert.Equal(original.Messages.Count, wire.Messages.Count);

        var back = mapper.FromSyncAssistantChat(wire, UserId);
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Title, back.Title);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.Messages.Count, back.Messages.Count);
        Assert.Equal(original.Messages[0].Content, back.Messages[0].Content);
        Assert.Equal(original.Messages[1].ThinkingContent, back.Messages[1].ThinkingContent);
        Assert.Equal(original.CreatedAt, back.CreatedAt);
        Assert.Equal(original.UpdatedAt, back.UpdatedAt);
        Assert.Equal(original.WindowMode, back.WindowMode);
    }

    [Fact]
    public void AssistantChat_RoundTrips_E2EE()
    {
        var mapper = E2EEMapper();
        var original = SampleChat();

        var wire = mapper.ToSyncAssistantChat(original, UserId);

        // E2EE on — content moves into ciphertext, plaintext fields cleared.
        Assert.NotNull(wire.EncryptedPayload);
        Assert.NotNull(wire.WrappedDek);
        Assert.Null(wire.Title);
        Assert.Null(wire.ProviderId);
        Assert.Empty(wire.Messages);

        // Server-needed fields stay plaintext at the top level.
        Assert.Equal(original.Id, wire.Id);
        Assert.Equal(original.SchemaVersion, wire.SchemaVersion);
        Assert.Equal(original.CreatedAt, wire.CreatedAt);
        Assert.Equal(original.UpdatedAt, wire.UpdatedAt);
        // LastAccessedAt is deliberately day-truncated on the wire (read-tracking privacy).
        Assert.Equal(original.LastAccessedAt.Date, wire.LastAccessedAt);
        Assert.Equal(original.WindowMode, wire.WindowMode);

        var back = mapper.FromSyncAssistantChat(wire, UserId);

        // After decrypt, plaintext fields restored and encryption fields nulled out
        // so the local store doesn't persist ciphertext.
        Assert.Null(back.EncryptedPayload);
        Assert.Null(back.WrappedDek);
        Assert.Equal(original.Title, back.Title);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.Messages.Count, back.Messages.Count);
        Assert.Equal(original.Messages[0].Content, back.Messages[0].Content);
        Assert.Equal(original.Messages[1].ModelName, back.Messages[1].ModelName);
        Assert.Equal(original.Messages[1].Tokens, back.Messages[1].Tokens);
        Assert.Equal(original.Messages[1].Persona!.Name, back.Messages[1].Persona!.Name);
        Assert.Equal(original.Messages[1].Persona!.Id, back.Messages[1].Persona!.Id);
        Assert.Equal(original.Messages[1].Persona!.Emoji, back.Messages[1].Persona!.Emoji);
    }

    [Fact]
    public void AssistantChat_E2EE_ExtensionDataRidesInsideCiphertext()
    {
        var mapper = E2EEMapper();
        var original = SampleChat();
        original.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["serverFutureField"] = System.Text.Json.JsonSerializer.SerializeToElement("future-value"),
        };

        var wire = mapper.ToSyncAssistantChat(original, UserId);

        // Unknown/forward-compat fields must not bypass E2EE via the plaintext wire.
        Assert.Null(wire.ExtensionData);
        Assert.NotNull(wire.EncryptedPayload);

        var back = mapper.FromSyncAssistantChat(wire, UserId);

        // ...but they must survive the round-trip through the ciphertext.
        Assert.NotNull(back.ExtensionData);
        Assert.Equal("future-value", back.ExtensionData!["serverFutureField"].GetString());
    }

    [Fact]
    public void AssistantChat_E2EE_PlaintextWireExtensionKeys_AreDropped()
    {
        var mapper = E2EEMapper();
        var wire = mapper.ToSyncAssistantChat(SampleChat(), UserId);

        // Simulate a server echoing plaintext extension keys onto an encrypted chat.
        wire.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["injectedByServer"] = System.Text.Json.JsonSerializer.SerializeToElement("plaintext"),
        };

        var back = mapper.FromSyncAssistantChat(wire, UserId);

        // Plaintext keys must not enter the local store (they would echo back out
        // on the next push, permanently bypassing E2EE).
        Assert.True(back.ExtensionData is null || !back.ExtensionData.ContainsKey("injectedByServer"));
    }

    [Fact]
    public void AssistantChat_Plaintext_ExtensionDataStillRoundTrips()
    {
        var mapper = PlainMapper();
        var original = SampleChat();
        original.ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["serverFutureField"] = System.Text.Json.JsonSerializer.SerializeToElement(42),
        };

        var wire = mapper.ToSyncAssistantChat(original, UserId);
        Assert.NotNull(wire.ExtensionData);

        var back = mapper.FromSyncAssistantChat(wire, UserId);
        Assert.Equal(42, back.ExtensionData!["serverFutureField"].GetInt32());
    }

    [Fact]
    public void FromSync_Throws_WhenCiphertextArrives_ButE2EEInactive()
    {
        // Wire has ciphertext but this client has no UMK / E2EE off. Silently
        // dropping the encrypted payload would write an empty chat to local store
        // (no title, no messages), so the mapper throws and the sync service skips.
        var mapper = PlainMapper();
        var wire = new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            WindowMode = "Assistant",
            EncryptedPayload = "ciphertext-blob",
            WrappedDek = "wrapped-dek-blob",
        };

        Assert.Throws<InvalidOperationException>(
            () => mapper.FromSyncAssistantChat(wire, UserId));
    }
}
