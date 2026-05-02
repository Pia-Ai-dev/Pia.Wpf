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
            ["answerLength"] = "Detailed"
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("create_scheduled_research", args));

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
        Assert.Equal(ResearchAnswerLength.Detailed, created.AnswerLength);
    }

    [Fact]
    public async Task UpdateScheduledResearch_WithInvalidGuid_ReturnsErrorResultNotPendingAction()
    {
        var handler = CreateHandler();

        var args = new Dictionary<string, object?>
        {
            ["id"] = "not-a-guid"
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("update_scheduled_research", args));

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

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("delete_scheduled_research", args));

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
            NextFireAt = DateTime.Now.AddHours(1),
            AnswerLength = ResearchAnswerLength.Balanced
        };
        jobs.SeedActive(job);

        var handler = CreateHandler(jobs);

        var args = new Dictionary<string, object?>
        {
            ["filter"] = "active"
        };

        var (result, pending) = await handler.HandleToolCallAsync(MakeCall("query_scheduled_research", args));

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

        private readonly List<ScheduledJob> _all = new();
        private readonly List<ScheduledJob> _active = new();

        public void SeedActive(ScheduledJob job)
        {
            _active.Add(job);
            _all.Add(job);
        }

        public Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence,
            TimeOnly timeOfDay, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
            DateTime? specificDate = null, ResearchAnswerLength answerLength = ResearchAnswerLength.Balanced,
            Guid? providerId = null)
        {
            var job = new ScheduledJob
            {
                Name = name,
                Query = query,
                Recurrence = recurrence,
                TimeOfDay = timeOfDay,
                DayOfWeek = dayOfWeek,
                DayOfMonth = dayOfMonth,
                Month = month,
                SpecificDate = specificDate,
                AnswerLength = answerLength,
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

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, ResearchAnswerLength? answerLength = null,
            Guid? providerId = null)
        {
            Updated.Add(id);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id)
        {
            Deleted.Add(id);
            return Task.CompletedTask;
        }

        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task MarkRunCompleteAsync(Guid id, Guid resultEntryId) => throw new NotImplementedException();
        public Task MarkRunFailedAsync(Guid id, string reason) => throw new NotImplementedException();
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
