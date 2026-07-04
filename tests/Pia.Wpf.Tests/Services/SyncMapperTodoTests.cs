namespace Pia.Tests.Services;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Xunit;

/// <summary>
/// Mapper coverage for the Todo sync entity, focused on ColumnId privacy: under E2EE the
/// board membership must ride inside the encrypted payload only, never as plaintext.
/// </summary>
public class SyncMapperTodoTests
{
    private const string UserId = "user-123";

    private static SyncMapper PlainMapper()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        return new SyncMapper(dpapi);
    }

    private static (SyncMapper Mapper, E2EEService E2EE) E2EEMapper()
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

        return (new SyncMapper(dpapi, e2ee), e2ee);
    }

    private static TodoItem SampleTodo() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Review sync privacy doc",
        Notes = "Check every table row",
        Priority = TodoPriority.High,
        Status = TodoStatus.Pending,
        DueDate = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc),
        SortOrder = 3,
        ColumnId = Guid.NewGuid()
    };

    [Fact]
    public void Todo_Plaintext_CarriesColumnIdOnTheWire()
    {
        var mapper = PlainMapper();
        var original = SampleTodo();

        var sync = mapper.ToSyncTodo(original);

        Assert.Null(sync.EncryptedPayload);
        Assert.Null(sync.WrappedDek);
        Assert.Equal(original.ColumnId, sync.ColumnId);

        var back = mapper.FromSyncTodo(sync);

        Assert.Equal(original.Title, back.Title);
        Assert.Equal(original.ColumnId, back.ColumnId);
        Assert.Equal(original.SortOrder, back.SortOrder);
    }

    [Fact]
    public void Todo_Encrypted_DoesNotLeakColumnIdInPlaintext()
    {
        var (mapper, _) = E2EEMapper();
        var original = SampleTodo();

        var sync = mapper.ToSyncTodo(original, UserId);

        Assert.NotNull(sync.EncryptedPayload);
        Assert.NotNull(sync.WrappedDek);
        Assert.Null(sync.Title);
        // Board structure must not be visible to the server.
        Assert.Null(sync.ColumnId);

        var back = mapper.FromSyncTodo(sync, UserId);

        Assert.Equal(original.Title, back.Title);
        Assert.Equal(original.Notes, back.Notes);
        Assert.Equal(original.Priority, back.Priority);
        Assert.Equal(original.ColumnId, back.ColumnId);
    }

    [Fact]
    public void Todo_Encrypted_LegacyPayloadWithoutColumnId_FallsBackToPlaintext()
    {
        var (mapper, e2ee) = E2EEMapper();
        var original = SampleTodo();

        // Payloads from clients that predate ColumnId-in-payload lack the field;
        // the pull path must fall back to the plaintext wire value for those rows.
        var legacyPayload = new
        {
            original.Title,
            original.Notes,
            Priority = (int)original.Priority,
            Status = (int)original.Status,
            original.DueDate,
            original.LinkedReminderId,
            original.CompletedAt
        };
        var (encryptedPayload, wrappedDek) = e2ee.EncryptRecord(
            legacyPayload, UserId, "todo", original.Id.ToString());

        var sync = new Pia.Shared.Models.SyncTodo
        {
            Id = original.Id,
            CreatedAt = original.CreatedAt,
            UpdatedAt = original.UpdatedAt,
            SortOrder = original.SortOrder,
            ColumnId = original.ColumnId,
            EncryptedPayload = encryptedPayload,
            WrappedDek = wrappedDek
        };

        var back = mapper.FromSyncTodo(sync, UserId);

        Assert.Equal(original.Title, back.Title);
        Assert.Equal(original.ColumnId, back.ColumnId);
    }
}
