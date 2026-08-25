using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

// A pull row that carries ciphertext must NEVER be mapped through the plaintext branch. The server
// blanks its plaintext columns for E2EE rows, so that branch yields Name="" and ProviderType=0 —
// which is AiProviderType.PiaCloud, a type the UI refuses to delete. The corrupted rows then persist
// because the pull cursor advances past them.
public class SyncMapperCiphertextFallbackTests
{
    private const string UserId = "user-1";
    private static readonly Guid RowId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SyncMapper Make(bool e2eeReady)
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var e2ee = Substitute.For<IE2EEService>();
        e2ee.IsReady().Returns(e2eeReady);
        return new SyncMapper(dpapi, e2ee);
    }

    // What the server sends for an E2EE provider: ciphertext, plus the plaintext columns it wiped.
    private static SyncProvider CiphertextProvider() => new()
    {
        Id = RowId,
        Name = null,
        ProviderType = 0,
        Endpoint = null,
        SupportsToolCalling = false,
        EncryptedPayload = "ZmFrZS1jaXBoZXJ0ZXh0",
        WrappedDek = "ZmFrZS13cmFwcGVkLWRlaw==",
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow,
    };

    private static SyncTemplate CiphertextTemplate() => new()
    {
        Id = RowId,
        Name = null,
        Prompt = null,
        EncryptedPayload = "ZmFrZS1jaXBoZXJ0ZXh0",
        WrappedDek = "ZmFrZS13cmFwcGVkLWRlaw==",
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        ModifiedAt = DateTime.UtcNow,
    };

    [Fact]
    public void FromSyncProvider_e2eeNotReady_doesNotSilentlyProduceBlankPiaCloudRow()
    {
        var mapper = Make(e2eeReady: false);

        var ex = Record.Exception(() => mapper.FromSyncProvider(CiphertextProvider(), UserId));

        Assert.NotNull(ex);
    }

    [Fact]
    public void FromSyncProvider_nullUserId_doesNotSilentlyProduceBlankPiaCloudRow()
    {
        var mapper = Make(e2eeReady: true);

        var ex = Record.Exception(() => mapper.FromSyncProvider(CiphertextProvider(), null));

        Assert.NotNull(ex);
    }

    [Fact]
    public void FromSyncTemplate_e2eeNotReady_doesNotSilentlyProduceBlankRow()
    {
        var mapper = Make(e2eeReady: false);

        var ex = Record.Exception(() => mapper.FromSyncTemplate(CiphertextTemplate(), UserId));

        Assert.NotNull(ex);
    }

    // The message names the entity, so a log line from the field identifies which pull was refused.
    [Fact]
    public void Guard_namesTheEntityItRefused()
    {
        var mapper = Make(e2eeReady: false);

        var ex = Assert.Throws<InvalidOperationException>(
            () => mapper.FromSyncProvider(CiphertextProvider(), UserId));

        Assert.Contains("provider", ex.Message, StringComparison.Ordinal);
    }

    // ToSyncTemplate encrypts a payload whose member is StyleDescription, but SyncTemplate renames
    // that member to ExampleText for the plaintext wire. Decrypting back into SyncTemplate therefore
    // dropped the style description on every E2EE round-trip.
    [Fact]
    public void Template_styleDescription_survivesAnE2EERoundTrip()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapi, new JsonRoundTripE2EE());

        var wire = mapper.ToSyncTemplate(new OptimizationTemplate
        {
            Id = RowId,
            Name = "Clarity",
            Prompt = "p",
            StyleDescription = "terse and formal",
        }, UserId);

        var back = mapper.FromSyncTemplate(wire, UserId);

        Assert.Equal("terse and formal", back.StyleDescription);
    }

    // Encrypt/decrypt reduced to a JSON round-trip: this exercises the payload property NAMES, which
    // is where the bug lives. Real crypto would only obscure it.
    private sealed class JsonRoundTripE2EE : IE2EEService
    {
        public bool IsReady() => true;
        public (string EncryptedPayload, string WrappedDek) EncryptRecord(
            object record, string userId, string entityType, string entityId)
            => (System.Text.Json.JsonSerializer.Serialize(record), "dek");
        public T DecryptRecord<T>(string encryptedPayload, string wrappedDek,
            string userId, string entityType, string entityId)
            => System.Text.Json.JsonSerializer.Deserialize<T>(encryptedPayload)!;

        public Task<byte[]> GenerateAndStoreUmkAsync() => throw new NotSupportedException();
        public byte[]? LoadUmk() => throw new NotSupportedException();
        public Task StoreUmkAsync(byte[] umk) => throw new NotSupportedException();
        public bool HasUmk() => true;
        public (string Ciphertext, string HkdfSalt) WrapUmkForSelf() => throw new NotSupportedException();
        public (string Ciphertext, string HkdfSalt) WrapUmkForDevice(string k, string d) => throw new NotSupportedException();
        public byte[] UnwrapUmkForDevice(string c, string s, string k, string d) => throw new NotSupportedException();
    }

    // A row with no ciphertext is ordinary plaintext sync and must still map normally.
    [Fact]
    public void FromSyncProvider_plaintextRow_stillMaps()
    {
        var mapped = Make(e2eeReady: false).FromSyncProvider(new SyncProvider
        {
            Id = RowId,
            Name = "plain",
            ProviderType = (int)AiProviderType.OpenAICompatible,
            Endpoint = "https://example.invalid/v1",
        });

        Assert.Equal("plain", mapped.Name);
        Assert.Equal(AiProviderType.OpenAICompatible, mapped.ProviderType);
    }
}
