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

    private static AiProvider NewProvider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Endpoint = "https://example",
        TimeoutSeconds = 60
    };

    [Fact]
    public async Task ExecuteOnceAsync_Success_MarksCompleteWithChatIdAndNotifies()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var chatId = Guid.NewGuid();
        var runner = new FakeRunner { Result = new BackgroundTurnResult(chatId, true, null) };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
        Assert.Single(jobs.Completed);
        Assert.Equal(due.Id, jobs.Completed[0].JobId);
        Assert.Equal(chatId, jobs.Completed[0].EntryId);
        Assert.Equal(1, notifications.SuccessCount);
        Assert.Equal(chatId, notifications.LastSuccessChatId);
        Assert.Equal(0, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_Success_PassesScheduleProvenanceToRunner()
    {
        // The scheduled path must wire Trigger=Schedule, TriggerRef=job.Id, and OwnerDeviceId=
        // job.OwnerDeviceId into the BackgroundTurnRequest handed to the runner.
        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.OwnerDeviceId = Guid.NewGuid();
        jobs.SeedDue(due);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.NotNull(runner.LastRequest);
        Assert.Equal(AgentRunTrigger.Schedule, runner.LastRequest!.Trigger);
        Assert.Equal(due.Id, runner.LastRequest.TriggerRef);
        Assert.Equal(due.OwnerDeviceId, runner.LastRequest.OwnerDeviceId);
    }

    [Fact]
    public async Task ExecuteOnceAsync_RunnerReturnsFailure_MarksFailedAndNotifies()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), false, "test failure") };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal(due.Id, jobs.Failed[0].JobId);
        Assert.Equal("test failure", jobs.Failed[0].Reason);
        Assert.Empty(jobs.Completed);
        Assert.Equal(0, notifications.SuccessCount);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_RunnerThrows_MarksFailedAndNotifies()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var runner = new FakeRunner { ThrowMessage = "boom" };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal("boom", jobs.Failed[0].Reason);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_NoProvider_MarksFailedAndDoesNotRun()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var runner = new FakeRunner();
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(null);
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal("NoProvider", jobs.Failed[0].Reason);
        Assert.Equal(0, runner.RunCount);
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
        var runner = new FakeRunner();
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(0, runner.RunCount);
        Assert.Equal(1, notifications.AskCount);
        // Skip should advance NextFireAt without incrementing failure counter.
        Assert.Single(jobs.Advanced);
        Assert.Equal(late.Id, jobs.Advanced[0]);
        Assert.Empty(jobs.Failed);
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
        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
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
        var runner = new FakeRunner();
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);
        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, notifications.AskCount); // not 2
    }

    private sealed class FakeJobService : IScheduledJobService
    {
        private readonly List<ScheduledJob> _due = new();
        public List<(Guid JobId, Guid EntryId)> Completed { get; } = new();
        public List<(Guid JobId, string Reason)> Failed { get; } = new();
        public List<Guid> Advanced { get; } = new();

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

        public Task AdvanceMissedRunAsync(Guid id)
        {
            Advanced.Add(id);
            return Task.CompletedTask;
        }

        public Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence,
            TimeOnly timeOfDay, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
            DateTime? specificDate = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null) => throw new NotImplementedException();

        public Task<IReadOnlyList<ScheduledJob>> GetAllAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() => throw new NotImplementedException();
        public Task<ScheduledJob?> GetAsync(Guid id) => throw new NotImplementedException();

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null) => throw new NotImplementedException();

        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) => throw new NotImplementedException();
        public Task UpsertFromSyncAsync(ScheduledJob job) => throw new NotImplementedException();
    }

    private sealed class FakeRunner : IBackgroundAssistantTurnRunner
    {
        public int RunCount { get; private set; }
        public BackgroundTurnResult Result { get; set; } = new(Guid.NewGuid(), true, null);
        public string? ThrowMessage { get; set; }
        public BackgroundTurnRequest? LastRequest { get; private set; }

        public Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
        {
            RunCount++;
            LastRequest = request;
            if (ThrowMessage is not null)
                throw new InvalidOperationException(ThrowMessage);
            return Task.FromResult(Result);
        }
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
        public Guid? LastSuccessChatId { get; private set; }
        public bool? AskAnswer { get; set; } = false;
        public int AskCount { get; private set; }
        public TaskCompletionSource<bool?>? PendingAsk { get; set; }

        public void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle)
        {
            SuccessCount++;
            LastSuccessChatId = chatId;
        }

        public void NotifyFailure(ScheduledJob job, string reason) => FailureCount++;

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
