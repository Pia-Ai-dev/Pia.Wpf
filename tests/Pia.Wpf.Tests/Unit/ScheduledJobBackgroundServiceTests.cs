using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class ScheduledJobBackgroundServiceTests
{
    private static ScheduledJob NewDueJob() => new()
    {
        Name = "T",
        Query = "q",
        Recurrence = RecurrenceType.Daily,
        TimeOfDay = TimeOnly.MinValue,
        NextFireAt = DateTime.Now.AddSeconds(-1)
    };

    [Fact]
    public async Task ExecuteOnceAsync_Success_PersistsEntryAndMarksComplete()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var research = new FakeResearchService { SynthesizedResult = "RESULT" };
        var history = new FakeResearchHistoryService();
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IResearchService>(research));
        var providers = new FakeProviderResolver(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "P",
            Endpoint = "https://example",
            TimeoutSeconds = 60
        });
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, history, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(history.Added);
        Assert.Equal(due.Id, history.Added[0].ScheduledJobId);
        Assert.Equal("RESULT", history.Added[0].SynthesizedResult);
        Assert.Single(jobs.Completed);
        Assert.Equal(due.Id, jobs.Completed[0].JobId);
        Assert.Equal(history.Added[0].Id, jobs.Completed[0].EntryId);
        Assert.Equal(1, notifications.SuccessCount);
        Assert.Equal(0, notifications.FailureCount);
        Assert.Equal(1, research.ExecuteCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ResearchThrows_PersistsFailedEntryAndMarksFailed()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var research = new FakeResearchService { ThrowOnExecute = true };
        var history = new FakeResearchHistoryService();
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IResearchService>(research));
        var providers = new FakeProviderResolver(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "P",
            Endpoint = "https://example",
            TimeoutSeconds = 60
        });
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, history, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(history.Added);
        Assert.Equal("Failed", history.Added[0].Status);
        Assert.Equal(due.Id, history.Added[0].ScheduledJobId);
        Assert.Single(jobs.Failed);
        Assert.Equal(due.Id, jobs.Failed[0].JobId);
        Assert.Equal("test failure", jobs.Failed[0].Reason);
        Assert.Equal(0, notifications.SuccessCount);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_NoProvider_PersistsFailedAndDoesNotCallResearch()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var research = new FakeResearchService();
        var history = new FakeResearchHistoryService();
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IResearchService>(research));
        var providers = new FakeProviderResolver(null);
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, history, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal("NoProvider", jobs.Failed[0].Reason);
        Assert.Equal(0, research.ExecuteCount);
        Assert.Single(history.Added);
        Assert.Equal("Failed", history.Added[0].Status);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_AsksUserAndSkipsIfDeclined()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface { AskAnswer = false };
        var research = new FakeResearchService();
        var history = new FakeResearchHistoryService();
        var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://example", TimeoutSeconds = 60 });

        var sp = new FakeServiceProvider().Add<IResearchService>(research);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Empty(history.Added);
        Assert.Equal(1, notifications.AskCount);
        Assert.Single(jobs.Failed); // MarkRunFailedAsync called with "MissedRunSkippedByUser"
    }

    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_RunsIfAccepted()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface { AskAnswer = true };
        var research = new FakeResearchService { SynthesizedResult = "OK" };
        var history = new FakeResearchHistoryService();
        var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://example", TimeoutSeconds = 60 });

        var sp = new FakeServiceProvider().Add<IResearchService>(research);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(history.Added);
        Assert.Single(jobs.Completed);
    }

    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_DedupesPromptOnSecondTickIfUnanswered()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface
        {
            // Simulate "user closed without answering" — return null.
            AskAnswer = null
        };
        var research = new FakeResearchService();
        var history = new FakeResearchHistoryService();
        var providers = new FakeProviderResolver(new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://example", TimeoutSeconds = 60 });

        var sp = new FakeServiceProvider().Add<IResearchService>(research);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), history, providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);
        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, notifications.AskCount); // not 2
    }

    private sealed class FakeJobService : IScheduledJobService
    {
        private readonly List<ScheduledJob> _due = new();
        public List<(Guid JobId, Guid EntryId)> Completed { get; } = new();
        public List<(Guid JobId, string Reason)> Failed { get; } = new();

        public void SeedDue(ScheduledJob job) => _due.Add(job);

        public Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync()
            => Task.FromResult<IReadOnlyList<ScheduledJob>>(_due.AsReadOnly());

        public Task MarkRunCompleteAsync(Guid id, Guid resultEntryId)
        {
            Completed.Add((id, resultEntryId));
            return Task.CompletedTask;
        }

        public Task MarkRunFailedAsync(Guid id, string reason)
        {
            Failed.Add((id, reason));
            return Task.CompletedTask;
        }

        public Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence,
            TimeOnly timeOfDay, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
            DateTime? specificDate = null, ResearchAnswerLength answerLength = ResearchAnswerLength.Balanced,
            Guid? providerId = null) => throw new NotImplementedException();

        public Task<IReadOnlyList<ScheduledJob>> GetAllAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() => throw new NotImplementedException();
        public Task<ScheduledJob?> GetAsync(Guid id) => throw new NotImplementedException();

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, ResearchAnswerLength? answerLength = null,
            Guid? providerId = null) => throw new NotImplementedException();

        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
    }

    private sealed class FakeResearchService : IResearchService
    {
        public string SynthesizedResult { get; set; } = string.Empty;
        public bool ThrowOnExecute { get; set; }
        public int ExecuteCount { get; private set; }

        public Task ExecuteResearchAsync(ResearchSession session, AiProvider provider,
            ResearchAnswerLength answerLength, CancellationToken ct)
        {
            ExecuteCount++;
            if (ThrowOnExecute)
            {
                throw new InvalidOperationException("test failure");
            }

            session.SynthesizedResult = SynthesizedResult;
            session.Status = ResearchStatus.Completed;
            session.CompletedAt = DateTime.Now;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResearchHistoryService : IResearchHistoryService
    {
        public List<ResearchHistoryEntry> Added { get; } = new();

#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler? SessionsChanged;
#pragma warning restore CS0067

        public Task AddEntryAsync(ResearchHistoryEntry entry)
        {
            Added.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ResearchHistoryEntry>> SearchEntriesAsync(string? searchText = null,
            DateTime? fromDate = null, DateTime? toDate = null, int offset = 0, int limit = 50)
            => throw new NotImplementedException();

        public Task<ResearchHistoryEntry?> GetEntryAsync(Guid id) => throw new NotImplementedException();
        public Task DeleteEntryAsync(Guid id) => throw new NotImplementedException();

        public Task<int> GetEntryCountAsync(string? searchText = null, DateTime? fromDate = null,
            DateTime? toDate = null) => throw new NotImplementedException();

        public Task UpdateEmbeddingAsync(Guid id, byte[] embedding) => throw new NotImplementedException();

        public Task<IReadOnlyList<ResearchHistoryEntry>> VectorSearchAsync(float[] queryEmbedding,
            int topK = 10, float threshold = 0.2f) => throw new NotImplementedException();

        public Task<IReadOnlyList<ResearchHistoryEntry>> HybridSearchAsync(string query,
            float[]? queryEmbedding = null, int topK = 10) => throw new NotImplementedException();
    }

    private sealed class FakeProviderResolver : IScheduledResearchProviderResolver
    {
        private readonly AiProvider? _provider;
        public FakeProviderResolver(AiProvider? provider) => _provider = provider;

        public Task<AiProvider?> ResolveAsync(Guid? pinnedProviderId) => Task.FromResult(_provider);
    }

    private sealed class FakeNotificationSurface : IScheduledJobNotificationSurface
    {
        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }
        public bool? AskAnswer { get; set; } = false;
        public int AskCount { get; private set; }
        public TaskCompletionSource<bool?>? PendingAsk { get; set; }

        public void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry) => SuccessCount++;
        public void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason) => FailureCount++;

        public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
        {
            AskCount++;
            if (PendingAsk is not null) return PendingAsk.Task;
            return Task.FromResult(AskAnswer);
        }
    }

    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _sp;
        public FakeScopeFactory(IServiceProvider sp) => _sp = sp;
        public IServiceScope CreateScope() => new FakeScope(_sp);

        private sealed class FakeScope : IServiceScope
        {
            public FakeScope(IServiceProvider sp) => ServiceProvider = sp;
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();

        public FakeServiceProvider Add<T>(T instance) where T : notnull
        {
            _services[typeof(T)] = instance;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var svc) ? svc : null;
    }
}
