using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The two device-local run pins on a routine: they persist, they honour the clear sentinels, an unrelated
/// edit leaves them alone, and a sync pull cannot reset them.
/// </summary>
public sealed class ScheduledJobPersonaPinTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly ScheduledJobService _jobs;

    public ScheduledJobPersonaPinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaPin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var deleteTracker = new SyncDeleteTrackerService(_dir, NullLogger<SyncDeleteTrackerService>.Instance);
        _jobs = new ScheduledJobService(_ctx, new RecurrenceCalculator(), settings, deleteTracker,
            NullLogger<ScheduledJobService>.Instance);
    }

    private async Task<ScheduledJob> NewJobAsync() =>
        await _jobs.CreateAsync("Digest", "what changed", RecurrenceType.Daily, new TimeOnly(9, 0));

    [Fact]
    public async Task ANewJob_HasNeitherPin()
    {
        var job = await NewJobAsync();

        Assert.Null(job.PersonaId);
        Assert.Null(job.ReasoningEffort);
        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.Null(reloaded!.PersonaId);
        Assert.Null(reloaded.ReasoningEffort);
    }

    [Fact]
    public async Task AJobCanBeCreatedWithBothPins()
    {
        var personaId = Guid.NewGuid();

        var job = await _jobs.CreateAsync("Digest", "what changed", RecurrenceType.Daily, new TimeOnly(9, 0),
            personaId: personaId, reasoningEffort: ReasoningEffort.High);

        Assert.Equal(personaId, job.PersonaId);                 // the returned object
        Assert.Equal(ReasoningEffort.High, job.ReasoningEffort);
        var reloaded = await _jobs.GetAsync(job.Id);            // and the row it wrote
        Assert.Equal(personaId, reloaded!.PersonaId);
        Assert.Equal(ReasoningEffort.High, reloaded.ReasoningEffort);
    }

    /// <summary>The editor's default row sends <c>Guid.Empty</c>, which on CREATE has no earlier value to clear
    /// and must not become a pin that resolves to nothing.</summary>
    [Fact]
    public async Task CreatingWithGuidEmpty_StoresNoPersonaPin()
    {
        var job = await _jobs.CreateAsync("Digest", "what changed", RecurrenceType.Daily, new TimeOnly(9, 0),
            personaId: Guid.Empty);

        Assert.Null(job.PersonaId);
        Assert.Null((await _jobs.GetAsync(job.Id))!.PersonaId);
    }

    [Fact]
    public async Task BothPins_RoundTripThroughTheDatabase()
    {
        var job = await NewJobAsync();
        var personaId = Guid.NewGuid();

        await _jobs.UpdateAsync(job.Id, personaId: personaId, reasoningEffort: ReasoningEffort.Low);

        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.Equal(personaId, reloaded!.PersonaId);
        Assert.Equal(ReasoningEffort.Low, reloaded.ReasoningEffort);
    }

    /// <summary><c>None</c> means "no reasoning" and must survive as a value distinct from "no pin".</summary>
    [Fact]
    public async Task EffortNone_RoundTrips_AndIsNotNull()
    {
        var job = await NewJobAsync();

        await _jobs.UpdateAsync(job.Id, reasoningEffort: ReasoningEffort.None);

        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.NotNull(reloaded!.ReasoningEffort);
        Assert.Equal(ReasoningEffort.None, reloaded.ReasoningEffort);
    }

    [Fact]
    public async Task AnUnrelatedEdit_ClearsNeitherPin()
    {
        var job = await NewJobAsync();
        var personaId = Guid.NewGuid();
        await _jobs.UpdateAsync(job.Id, personaId: personaId, reasoningEffort: ReasoningEffort.XHigh);

        await _jobs.UpdateAsync(job.Id, name: "Renamed");

        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.Equal("Renamed", reloaded!.Name);
        Assert.Equal(personaId, reloaded.PersonaId);
        Assert.Equal(ReasoningEffort.XHigh, reloaded.ReasoningEffort);
    }

    [Fact]
    public async Task GuidEmpty_ClearsThePersonaPin()
    {
        var job = await NewJobAsync();
        await _jobs.UpdateAsync(job.Id, personaId: Guid.NewGuid());

        await _jobs.UpdateAsync(job.Id, personaId: Guid.Empty);

        Assert.Null((await _jobs.GetAsync(job.Id))!.PersonaId);
    }

    [Fact]
    public async Task ClearReasoningEffort_ClearsTheEffortPin()
    {
        var job = await NewJobAsync();
        await _jobs.UpdateAsync(job.Id, reasoningEffort: ReasoningEffort.Medium);

        await _jobs.UpdateAsync(job.Id, clearReasoningEffort: true);

        Assert.Null((await _jobs.GetAsync(job.Id))!.ReasoningEffort);
    }

    /// <summary>The same sentinel on the PROVIDER pin. Before it existed, choosing the editor's "Default
    /// provider" row reported success and left the routine running on the provider just removed.</summary>
    [Fact]
    public async Task GuidEmpty_ClearsTheProviderPin()
    {
        var job = await _jobs.CreateAsync("Digest", "what changed", RecurrenceType.Daily, new TimeOnly(9, 0),
            providerId: Guid.NewGuid());
        Assert.NotNull((await _jobs.GetAsync(job.Id))!.ProviderId);

        await _jobs.UpdateAsync(job.Id, providerId: Guid.Empty);

        Assert.Null((await _jobs.GetAsync(job.Id))!.ProviderId);
    }

    /// <summary>Neither pin is on the wire, and <c>UpsertFromSyncAsync</c> writes only the synced config
    /// columns — so promoting either field to the wire has to delete this test.</summary>
    [Fact]
    public async Task ASyncPull_CannotResetEitherPin()
    {
        var job = await NewJobAsync();
        var personaId = Guid.NewGuid();
        await _jobs.UpdateAsync(job.Id, personaId: personaId, reasoningEffort: ReasoningEffort.Minimal);

        // What a pull hands over: the same job id, config fields only — both pins left at their default.
        await _jobs.UpsertFromSyncAsync(new ScheduledJob
        {
            Id = job.Id,
            Name = "Renamed by a peer",
            Query = "what changed",
            Kind = ScheduledJobKind.AgentTask,
            Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(9, 0),
            NextFireAt = DateTime.Now.AddDays(1),
            Status = ScheduledJobStatus.Active,
            UpdatedAt = DateTime.Now.AddMinutes(5),
        });

        var reloaded = await _jobs.GetAsync(job.Id);
        Assert.Equal("Renamed by a peer", reloaded!.Name); // the pull really did land
        Assert.Equal(personaId, reloaded.PersonaId);       // and it did not touch these
        Assert.Equal(ReasoningEffort.Minimal, reloaded.ReasoningEffort);
    }

    /// <summary>A job imported from a peer starts unpinned on this device — the import arm goes through
    /// <c>InsertAsync</c>, so the columns are written from the incoming object's defaults.</summary>
    [Fact]
    public async Task AJobImportedFromSync_StartsUnpinned()
    {
        var id = Guid.NewGuid();

        await _jobs.UpsertFromSyncAsync(new ScheduledJob
        {
            Id = id,
            Name = "From a peer",
            Query = "what changed",
            Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(9, 0),
            NextFireAt = DateTime.Now.AddDays(1),
            Status = ScheduledJobStatus.Active,
            UpdatedAt = DateTime.Now,
        });

        var imported = await _jobs.GetAsync(id);
        Assert.NotNull(imported);
        Assert.Null(imported!.PersonaId);
        Assert.Null(imported.ReasoningEffort);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        GC.SuppressFinalize(this);
    }
}
