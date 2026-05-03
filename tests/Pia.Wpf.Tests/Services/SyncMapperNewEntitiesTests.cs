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
/// Mapper round-trip coverage for the two new sync entities (ScheduledJob, ResearchHistoryEntry),
/// in both plaintext (E2EE off) and encrypted (E2EE on) modes.
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
        AnswerLength = ResearchAnswerLength.Detailed,
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

    private static ResearchHistoryEntry SampleEntry() => new()
    {
        Id = Guid.NewGuid(),
        Query = "Why does the sky look blue?",
        SynthesizedResult = "Rayleigh scattering — short wavelengths scatter more strongly...",
        StepsJson = "[{\"StepNumber\":1,\"Title\":\"Search\",\"Content\":\"...\",\"Status\":\"Completed\"}]",
        ProviderId = Guid.NewGuid(),
        ProviderName = "OpenAI",
        Status = "Completed",
        StepCount = 4,
        CreatedAt = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc),
        CompletedAt = new DateTime(2026, 5, 1, 10, 30, 0, DateTimeKind.Utc),
        ScheduledJobId = Guid.NewGuid(),
        Embedding = new byte[] { 1, 2, 3, 4 } // not synced
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
        Assert.Equal(original.AnswerLength, back.AnswerLength);
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
        Assert.Equal(original.AnswerLength, back.AnswerLength);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.Recurrence, back.Recurrence);
        Assert.Equal(original.TimeOfDay, back.TimeOfDay);
        Assert.Equal(original.DayOfWeek, back.DayOfWeek);
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.OwnerDeviceId, back.OwnerDeviceId);
    }

    [Fact]
    public void ResearchSession_RoundTrips_Plaintext()
    {
        var mapper = PlainMapper();
        var original = SampleEntry();

        var sync = mapper.ToSyncResearchSession(original);

        Assert.Null(sync.EncryptedPayload);
        Assert.Equal(original.Query, sync.Query);
        Assert.Equal(original.SynthesizedResult, sync.SynthesizedResult);

        var back = mapper.FromSyncResearchSession(sync);

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Query, back.Query);
        Assert.Equal(original.SynthesizedResult, back.SynthesizedResult);
        Assert.Equal(original.StepsJson, back.StepsJson);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.ProviderName, back.ProviderName);
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.StepCount, back.StepCount);
        Assert.Equal(original.ScheduledJobId, back.ScheduledJobId);
        Assert.Equal(original.CreatedAt, back.CreatedAt);
        Assert.Equal(original.UpdatedAt, back.UpdatedAt);
        Assert.Equal(original.CompletedAt, back.CompletedAt);
        // Embedding is intentionally never synced.
        Assert.Null(back.Embedding);
    }

    [Fact]
    public void ResearchSession_RoundTrips_Encrypted()
    {
        var (mapper, _) = E2EEMapper();
        var original = SampleEntry();

        var sync = mapper.ToSyncResearchSession(original, UserId);

        // Content fields go into the encrypted blob.
        Assert.NotNull(sync.EncryptedPayload);
        Assert.NotNull(sync.WrappedDek);
        Assert.Null(sync.Query);
        Assert.Null(sync.SynthesizedResult);
        Assert.Null(sync.ProviderName);
        Assert.Null(sync.Status);
        Assert.Null(sync.ProviderId);
        Assert.Null(sync.ScheduledJobId);
        // Plaintext metadata stays.
        Assert.Equal(original.Id, sync.Id);
        Assert.Equal(original.CreatedAt, sync.CreatedAt);
        Assert.Equal(original.UpdatedAt, sync.UpdatedAt);
        Assert.Equal(original.CompletedAt, sync.CompletedAt);

        var back = mapper.FromSyncResearchSession(sync, UserId);

        Assert.Equal(original.Query, back.Query);
        Assert.Equal(original.SynthesizedResult, back.SynthesizedResult);
        Assert.Equal(original.StepsJson, back.StepsJson);
        Assert.Equal(original.ProviderId, back.ProviderId);
        Assert.Equal(original.ProviderName, back.ProviderName);
        Assert.Equal(original.Status, back.Status);
        Assert.Equal(original.StepCount, back.StepCount);
        Assert.Equal(original.ScheduledJobId, back.ScheduledJobId);
        Assert.Null(back.Embedding);
    }
}
