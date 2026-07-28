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
/// Mapper round-trip coverage for the ScheduledJob sync entity, in both plaintext (E2EE off)
/// and encrypted (E2EE on) modes.
/// </summary>
public class SyncMapperNewEntitiesTests
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
        // Generate UMK so HasUmk()==true; combined with IsE2EEEnabled this makes IsReady()==true.
        e2ee.GenerateAndStoreUmkAsync().GetAwaiter().GetResult();

        return (new SyncMapper(dpapi, e2ee), e2ee);
    }

    private static ScheduledJob SampleJob() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Tesla daily briefing",
        Query = "Latest Tesla news in plain English",
        Kind = ScheduledJobKind.Research,
        GrantedTools = ["create_memory", "create_todo"],
        ProviderId = Guid.NewGuid(),
        Recurrence = RecurrenceType.Weekly,
        TimeOfDay = new TimeOnly(8, 30),
        DayOfWeek = DayOfWeek.Tuesday,
        DayOfMonth = null,
        Month = null,
        SpecificDate = null,
        Status = ScheduledJobStatus.Active,
        CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
        OwnerDeviceId = Guid.NewGuid()
    };

    [Fact]
    public void ScheduledJob_RoundTrips_Plaintext()
    {
        var mapper = PlainMapper();
        var original = SampleJob();

        var sync = mapper.ToSyncScheduledJob(original);

        // No E2EE => content is plaintext, blob fields null.
        Assert.Null(sync.EncryptedPayload);
        Assert.Null(sync.WrappedDek);
        Assert.Equal(original.Name, sync.Name);
        Assert.Equal(original.Query, sync.Query);

        var back = mapper.FromSyncScheduledJob(sync);

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Query, back.Query);
        Assert.Equal(original.Kind, back.Kind);
        Assert.Equal(original.GrantedTools, back.GrantedTools);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.Recurrence, back.Recurrence);
        Assert.Equal(original.TimeOfDay, back.TimeOfDay);
        Assert.Equal(original.DayOfWeek, back.DayOfWeek);
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.OwnerDeviceId, back.OwnerDeviceId);
        Assert.Equal(original.CreatedAt, back.CreatedAt);
        Assert.Equal(original.UpdatedAt, back.UpdatedAt);
    }

    [Fact]
    public void ScheduledJob_RoundTrips_Encrypted()
    {
        var (mapper, _) = E2EEMapper();
        var original = SampleJob();

        var sync = mapper.ToSyncScheduledJob(original, UserId);

        // Under E2EE the content fields are nulled and the encrypted blob carries the data.
        Assert.NotNull(sync.EncryptedPayload);
        Assert.NotNull(sync.WrappedDek);
        Assert.Null(sync.Name);
        Assert.Null(sync.Query);
        // Plaintext-always fields stay populated.
        Assert.Equal(original.Id, sync.Id);
        Assert.Equal(original.OwnerDeviceId, sync.OwnerDeviceId);
        Assert.Equal(original.CreatedAt, sync.CreatedAt);

        var back = mapper.FromSyncScheduledJob(sync, UserId);

        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Query, back.Query);
        Assert.Equal(original.Kind, back.Kind);
        Assert.Equal(original.GrantedTools, back.GrantedTools);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.Recurrence, back.Recurrence);
        Assert.Equal(original.TimeOfDay, back.TimeOfDay);
        Assert.Equal(original.DayOfWeek, back.DayOfWeek);
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.OwnerDeviceId, back.OwnerDeviceId);
    }

    // W3a: Status crosses the wire as an int (SyncMapper.cs Status = (int)job.Status) and is cast back
    // with no Enum.IsDefined validation, so the newly appended ordinal 3 must survive a round trip in
    // both plaintext and encrypted modes. If someone reorders ScheduledJobStatus, these go red.
    [Fact]
    public void ScheduledJob_CompletedStatus_RoundTrips_Plaintext()
    {
        var mapper = PlainMapper();
        var original = SampleJob();
        original.Recurrence = RecurrenceType.Once;
        original.SpecificDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        original.Status = ScheduledJobStatus.Completed;

        var sync = mapper.ToSyncScheduledJob(original);

        // Pin the ordinal itself, not just the enum member: an older peer sees this int.
        Assert.Equal(3, sync.Status);

        var back = mapper.FromSyncScheduledJob(sync);
        Assert.Equal(ScheduledJobStatus.Completed, back.Status);
        Assert.Equal(RecurrenceType.Once, back.Recurrence);
    }

    [Fact]
    public void ScheduledJob_CompletedStatus_RoundTrips_Encrypted()
    {
        var (mapper, _) = E2EEMapper();
        var original = SampleJob();
        original.Recurrence = RecurrenceType.Once;
        original.SpecificDate = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        original.Status = ScheduledJobStatus.Completed;

        var sync = mapper.ToSyncScheduledJob(original, UserId);

        // Under E2EE Status travels inside the encrypted payload, so the plaintext field is nulled.
        Assert.Null(sync.Status);

        var back = mapper.FromSyncScheduledJob(sync, UserId);
        Assert.Equal(ScheduledJobStatus.Completed, back.Status);
        Assert.Equal(RecurrenceType.Once, back.Recurrence);
    }
}
