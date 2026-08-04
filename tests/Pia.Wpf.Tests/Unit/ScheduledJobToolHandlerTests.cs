using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class ScheduledJobToolHandlerTests
{
    private static ScheduledJobToolHandler CreateHandler(
        FakeJobService? jobs = null,
        FakeProviderService? providers = null)
    {
        return new ScheduledJobToolHandler(
            jobs ?? new FakeJobService(),
            providers ?? new FakeProviderService(),
            new FakeLocalizationService(),
            NullLogger<ScheduledJobToolHandler>.Instance);
    }

    private static FunctionCallContent MakeCall(string toolName, IDictionary<string, object?> args)
        => new("call-1", toolName, args);

    [Fact]
    public async Task CreateScheduledResearch_WithValidArgs_ReturnsPendingActionAndExecutes()
    {
        var jobs = new FakeJobService();
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["name"] = "Tesla briefing",
            ["query"] = "latest tesla news",
            ["recurrence"] = "Weekly",
            ["timeOfDay"] = "08:00",
            ["dayOfWeek"] = "Monday",
            ["grantedTools"] = "create_object, create_todo"
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.NotNull(pending);
        Assert.Equal("create_scheduled_research", pending!.ToolName);
        Assert.Null(pending.TargetJobId);
        Assert.Contains("Tesla briefing", pending.Details ?? string.Empty);

        // Now actually execute it - should call CreateAsync.
        var execResult = await handler.ExecutePendingActionAsync(pending);

        Assert.NotNull(execResult);
        Assert.Single(jobs.Created);
        var created = jobs.Created[0];
        Assert.Equal("Tesla briefing", created.Name);
        Assert.Equal("latest tesla news", created.Query);
        Assert.Equal(RecurrenceType.Weekly, created.Recurrence);
        Assert.Equal(new TimeOnly(8, 0), created.TimeOfDay);
        Assert.Equal(DayOfWeek.Monday, created.DayOfWeek);
        Assert.Equal(new[] { "create_object", "create_todo" }, created.GrantedTools);
        Assert.Equal(ScheduledJobKind.Research, created.Kind); // default when kind omitted
    }

    [Fact]
    public async Task CreateScheduledResearch_WithAgentKind_CreatesAgentTaskJob()
    {
        var jobs = new FakeJobService();
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["name"] = "Nightly cleanup",
            ["query"] = "tidy my notes folder",
            ["recurrence"] = "Daily",
            ["timeOfDay"] = "02:00",
            ["kind"] = "agent"
        };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args), TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        await handler.ExecutePendingActionAsync(pending!);

        Assert.Single(jobs.Created);
        Assert.Equal(ScheduledJobKind.AgentTask, jobs.Created[0].Kind);
    }

    [Fact]
    public async Task CreateScheduledResearch_StripsDestructiveExternalGrants_AndTellsTheModel()
    {
        // B2 at grant CREATION: a destructive external (MCP) tool name can never become a standing grant on
        // a scheduled job, so the escalation cannot be created in the first place. The card the user
        // approves shows only the grants that will actually be used.
        var jobs = new FakeJobService();
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["name"] = "Nightly tidy",
            ["query"] = "tidy things",
            ["recurrence"] = "Daily",
            ["timeOfDay"] = "02:00",
            ["grantedTools"] = "write_file, purge_records, delete_issue, create_todo"
        };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.NotNull(pending);
        Assert.Contains("write_file", pending!.Details ?? string.Empty);
        Assert.DoesNotContain("purge_records", pending.Details ?? string.Empty);
        Assert.DoesNotContain("delete_issue", pending.Details ?? string.Empty);

        var execResult = Assert.IsType<string>(await handler.ExecutePendingActionAsync(pending));
        Assert.Contains("purge_records", execResult);   // the model is told what was refused
        Assert.Contains("delete_issue", execResult);

        Assert.Equal(new[] { "write_file", "create_todo" }, jobs.Created[0].GrantedTools);
    }

    [Fact]
    public async Task CreateScheduledResearch_KeepsBuiltInDeleteGrants()
    {
        // Only PRESUMED-EXTERNAL destructive names are stripped: our own delete tools stay grantable, so an
        // explicitly requested "delete my old exports nightly" job still works.
        var jobs = new FakeJobService();
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["name"] = "Nightly cleanup",
            ["query"] = "delete stale exports",
            ["recurrence"] = "Daily",
            ["timeOfDay"] = "02:00",
            ["grantedTools"] = "delete_file, delete_todo, forget"
        };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args), TestContext.Current.CancellationToken);
        var execResult = Assert.IsType<string>(await handler.ExecutePendingActionAsync(pending!));

        Assert.Equal(new[] { "delete_file", "delete_todo", "forget" }, jobs.Created[0].GrantedTools);
        Assert.DoesNotContain("refused", execResult);
    }

    [Fact]
    public async Task CreateScheduledResearch_AgentKindWithoutGrants_SurfacesTheEffectiveDefaultOnTheCard()
    {
        // A1/B2: an agent job with no explicit grant silently receives the launcher's default write access
        // at fire time. The approval card must say so instead of omitting the line — and the default it
        // renders is the single source of truth, so it can never drift from what the launcher applies.
        var jobs = new FakeJobService();
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["name"] = "Nightly agent",
            ["query"] = "carry out the work",
            ["recurrence"] = "Daily",
            ["timeOfDay"] = "02:00",
            ["kind"] = "agent"
        };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.NotNull(pending);
        Assert.Contains("Tool_ScheduledResearch_Detail_GrantedTools", pending!.Details ?? string.Empty);
        foreach (var tool in HeadlessRunRequest.DefaultGrantedWrites)
            Assert.Contains(tool, pending.Details ?? string.Empty);
        Assert.DoesNotContain("delete_file", pending.Details ?? string.Empty);

        // The job row itself still stores no explicit grants — the launcher applies its default.
        await handler.ExecutePendingActionAsync(pending);
        Assert.Empty(jobs.Created[0].GrantedTools);
    }

    [Fact]
    public async Task CreateScheduledResearch_ResearchKindWithoutGrants_ShowsNoGrantLine()
    {
        // A research job with no grants genuinely is read-only at fire time, so it must NOT advertise the
        // agent default.
        var jobs = new FakeJobService();
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["name"] = "Morning briefing",
            ["query"] = "news",
            ["recurrence"] = "Daily",
            ["timeOfDay"] = "08:00"
        };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.NotNull(pending);
        Assert.DoesNotContain("Tool_ScheduledResearch_Detail_GrantedTools", pending!.Details ?? string.Empty);
    }

    [Fact]
    public async Task UpdateScheduledResearch_StripsDestructiveExternalGrants()
    {
        var jobs = new FakeJobService();
        var existing = new ScheduledJob
        {
            Name = "Job",
            Query = "q",
            Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(8, 0),
            NextFireAt = DateTime.Now.AddHours(1)
        };
        jobs.SeedActive(existing);
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["id"] = existing.Id.ToString(),
            ["grantedTools"] = "create_todo, remove_page"
        };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("update_scheduled_research", args), TestContext.Current.CancellationToken);
        var execResult = Assert.IsType<string>(await handler.ExecutePendingActionAsync(pending!));

        Assert.Equal(new[] { "create_todo" }, jobs.LastUpdatedGrants);
        Assert.Contains("remove_page", execResult);
    }

    [Fact]
    public async Task UpdateScheduledResearch_WithoutGrantedToolsArg_LeavesGrantsUnchanged()
    {
        // The null-vs-empty contract survives the rejected-grant plumbing: no grantedTools argument must
        // still mean "leave the existing grants alone", not "clear them".
        var jobs = new FakeJobService();
        var existing = new ScheduledJob
        {
            Name = "Job",
            Query = "q",
            Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(8, 0),
            NextFireAt = DateTime.Now.AddHours(1)
        };
        jobs.SeedActive(existing);
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?> { ["id"] = existing.Id.ToString(), ["name"] = "Renamed" };

        var (_, pending) = await handler.HandleToolCallAsync(MakeCall("update_scheduled_research", args), TestContext.Current.CancellationToken);
        await handler.ExecutePendingActionAsync(pending!);

        Assert.Null(jobs.LastUpdatedGrants);
    }

    [Fact]
    public async Task UpdateScheduledResearch_WithInvalidGuid_ReturnsErrorResultNotPendingAction()
    {
        var handler = CreateHandler();

        var args = new Dictionary<string, object?>
        {
            ["id"] = "not-a-guid"
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("update_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.NotNull(result);
        var resultStr = Assert.IsType<string>(result);
        Assert.Contains("Invalid", resultStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteScheduledResearch_WithNotFoundId_ReturnsErrorResult()
    {
        var jobs = new FakeJobService(); // empty - any GetAsync returns null
        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString()
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("delete_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.NotNull(result);
        var resultStr = Assert.IsType<string>(result);
        Assert.Contains("not found", resultStr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueryScheduledResearch_WithActiveFilter_RendersIdsAndNames()
    {
        var jobs = new FakeJobService();
        var job = new ScheduledJob
        {
            Name = "Tesla briefing",
            Query = "tesla news",
            Recurrence = RecurrenceType.Daily,
            TimeOfDay = new TimeOnly(8, 0),
            NextFireAt = DateTime.Now.AddHours(1)
        };
        jobs.SeedActive(job);

        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["filter"] = "active"
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("query_scheduled_research", args), TestContext.Current.CancellationToken);

        Assert.Null(pending);
        Assert.NotNull(result);
        var rendered = Assert.IsType<string>(result);
        Assert.Contains(job.Id.ToString(), rendered);
        Assert.Contains("Tesla briefing", rendered);
        Assert.Contains("Daily", rendered);
    }

    // === Fakes ===

    private sealed class FakeJobService : IScheduledJobService
    {
        public List<ScheduledJob> Created { get; } = new();
        public List<Guid> Deleted { get; } = new();
        public List<Guid> Updated { get; } = new();

        /// <summary>Grant list handed to the last UpdateAsync — null means "leave existing grants alone".</summary>
        public IReadOnlyCollection<string>? LastUpdatedGrants { get; private set; }

        private readonly List<ScheduledJob> _all = new();
        private readonly List<ScheduledJob> _active = new();

        public void SeedActive(ScheduledJob job)
        {
            _active.Add(job);
            _all.Add(job);
        }

        public Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence,
            TimeOnly timeOfDay, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
            DateTime? specificDate = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            ScheduledJobKind kind = ScheduledJobKind.Research, bool quietOnSuccess = false)
        {
            var job = new ScheduledJob
            {
                Name = name,
                Query = query,
                Kind = kind,
                Recurrence = recurrence,
                TimeOfDay = timeOfDay,
                DayOfWeek = dayOfWeek,
                DayOfMonth = dayOfMonth,
                Month = month,
                SpecificDate = specificDate,
                GrantedTools = grantedTools?.ToList() ?? [],
                ProviderId = providerId,
                NextFireAt = DateTime.Now.AddHours(1)
            };
            Created.Add(job);
            return Task.FromResult(job);
        }

        public Task<IReadOnlyList<ScheduledJob>> GetAllAsync()
            => Task.FromResult<IReadOnlyList<ScheduledJob>>(_all.AsReadOnly());

        public Task<IReadOnlyList<ScheduledJob>> GetActiveAsync()
            => Task.FromResult<IReadOnlyList<ScheduledJob>>(_active.AsReadOnly());

        public Task<ScheduledJob?> GetAsync(Guid id)
            => Task.FromResult(_all.FirstOrDefault(j => j.Id == id));

        public Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) => throw new NotImplementedException();
        public Task UpsertFromSyncAsync(ScheduledJob job) => throw new NotImplementedException();

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            DateTime? specificDate = null, ScheduledJobKind? kind = null, bool? quietOnSuccess = null)
        {
            Updated.Add(id);
            LastUpdatedGrants = grantedTools;
            return Task.CompletedTask;
        }

        public Task<bool> IsOwnedByThisDeviceAsync(Guid id) => Task.FromResult(true);

        public Task DeleteAsync(Guid id)
        {
            Deleted.Add(id);
            return Task.CompletedTask;
        }

        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task MarkRunCompleteAsync(Guid id, Guid resultEntryId) => throw new NotImplementedException();
        public Task MarkRunFailedAsync(Guid id, string reason) => throw new NotImplementedException();
        public Task AdvanceMissedRunAsync(Guid id) => throw new NotImplementedException();
        public Task MarkOccurrenceDispatchedAsync(Guid id) => throw new NotImplementedException();

        // The tool handler never books a firing outcome — it authors and lists jobs. Unimplemented like every
        // other execution-state write here, so a handler that started making one would fail loudly.
        public Task MarkFiringOutcomeAsync(Guid id, DateTime firedAt, Guid? resultEntryId, bool succeeded)
            => throw new NotImplementedException();
    }

    private sealed class FakeProviderService : IProviderService
    {
        public List<AiProvider> Providers { get; } = new();

#pragma warning disable CS0067
        public event EventHandler? ProvidersChanged;
#pragma warning restore CS0067

        public Task<IReadOnlyList<AiProvider>> GetProvidersAsync()
            => Task.FromResult<IReadOnlyList<AiProvider>>(Providers.AsReadOnly());

        public Task<AiProvider?> GetProviderAsync(Guid id)
            => Task.FromResult(Providers.FirstOrDefault(p => p.Id == id));

        public Task<AiProvider?> GetDefaultProviderAsync() => Task.FromResult<AiProvider?>(null);
        public Task<AiProvider?> GetDefaultProviderForModeAsync(WindowMode mode) => Task.FromResult<AiProvider?>(null);
        public Task<AiProvider> AddProviderAsync(AiProvider provider, string? apiKey) => throw new NotImplementedException();
        public Task UpdateProviderAsync(AiProvider provider, string? newApiKey = null) => throw new NotImplementedException();
        public Task DeleteProviderAsync(Guid id) => throw new NotImplementedException();
        public string? GetDecryptedApiKey(AiProvider provider) => null;
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider) => throw new NotImplementedException();
        public Task<TestConnectionResult> TestConnectionAsync(AiProvider provider, string? plainApiKey) => throw new NotImplementedException();
        public Task EnsureBuiltInProviderAsync() => Task.CompletedTask;
        public Task<List<string>> FetchModelsAsync(string endpoint, string? apiKey, AiProviderType providerType) => throw new NotImplementedException();
        public Task<bool> IsProviderActiveAsync(AiProvider provider) => Task.FromResult(true);
        public Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged) => Task.CompletedTask;
        public Task RepairModeDefaultsAsync() => Task.CompletedTask;
        public Task ConsolidateLocalDuplicatesAsync() => Task.CompletedTask;
    }

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public TargetLanguage CurrentLanguage => TargetLanguage.EN;

#pragma warning disable CS0067
        public event EventHandler<TargetLanguage>? LanguageChanged;
#pragma warning restore CS0067

        public void SetLanguage(TargetLanguage language) { }

        public string this[string key] => key;

        public string Format(string key, params object[] args) => key;
    }
}
