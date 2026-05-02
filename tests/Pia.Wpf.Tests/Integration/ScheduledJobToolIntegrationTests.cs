using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Xunit;

namespace Pia.Tests.Integration;

/// <summary>
/// End-to-end integration test for the scheduled-research pipeline:
/// create a <see cref="ScheduledJob"/> via the tool handler -> background service finds
/// it due -> stubbed research runs -> entry persisted -> search_research_history finds it.
/// </summary>
/// <remarks>
/// <para>
/// This test exercises the real <see cref="SqliteContext"/> against the user's
/// <c>%LOCALAPPDATA%\Pia\history.db</c>. That is a known plan-accepted tradeoff: the
/// project does not yet have a per-test in-memory SQLite harness, and the same convention
/// is used by other integration tests in this folder. Cleanup deletes only TEST_E2E_-prefixed
/// scheduled jobs and the research sessions linked to them, so dev-local data is not affected.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class ScheduledJobToolIntegrationTests : IDisposable
{
    private readonly SqliteContext _ctx = new();

    [Fact]
    public async Task EndToEnd_CreateJob_RunDueJob_SearchFinds()
    {
        // Arrange the dependency graph manually for an integration test.
        var calc = new RecurrenceCalculator();
        var jobs = new ScheduledJobService(_ctx, calc, NullLogger<ScheduledJobService>.Instance);

        var research = new StubResearchService("Test result for Tesla");
        var embedding = new StubEmbedding();
        var history = new ResearchHistoryService(_ctx, embedding, NullLogger<ResearchHistoryService>.Instance);
        var providers = new StubProviderResolver(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "Stub",
            Endpoint = "https://example",
            TimeoutSeconds = 60
        });
        var notifications = new SilentNotificationSurface();

        // The BG service takes IServiceScopeFactory after Task 11 fix.
        var sp = new IntegrationServiceProvider()
            .Add<IResearchService>(research);
        var scopeFactory = new IntegrationScopeFactory(sp);

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, history, providers, notifications,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        // Create a job via the tool handler so the path exercises the actual JSON arg parsing.
        var providerSvc = new StubProviderService();
        var l10n = new StubLocalization();
        var toolHandler = new ScheduledJobToolHandler(
            jobs, providerSvc, l10n, NullLogger<ScheduledJobToolHandler>.Instance);

        var createCall = new FunctionCallContent("call1", "create_scheduled_research",
            new Dictionary<string, object?>
            {
                ["name"] = "TEST_E2E_Tesla",
                ["query"] = "Tesla stock pricing news",
                ["recurrence"] = "Daily",
                ["timeOfDay"] = "08:00"
            });

        var (_, pending) = await toolHandler.HandleToolCallAsync(createCall);
        Assert.NotNull(pending);
        await pending!.Execute();

        // Force the row "due" so the BG service picks it up immediately.
        var conn = _ctx.GetConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE ScheduledJobs SET NextFireAt = @t WHERE Name = 'TEST_E2E_Tesla'";
            cmd.Parameters.AddWithValue("@t", DateTime.Now.AddSeconds(-1).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        // Run the background service tick once.
        await bg.ExecuteOnceAsync(CancellationToken.None);

        // Assert: the job ran and a history entry was persisted with ScheduledJobId set.
        var allEntries = await history.SearchEntriesAsync(
            searchText: "Tesla stock pricing", fromDate: null, toDate: null, offset: 0, limit: 10);
        Assert.Contains(allEntries,
            e => e.ScheduledJobId.HasValue && e.SynthesizedResult == "Test result for Tesla");

        // Assert: search_research_history finds it via the history tool handler.
        var historyHandler = new ResearchHistoryToolHandler(
            history, embedding, NullLogger<ResearchHistoryToolHandler>.Instance);
        var searchCall = new FunctionCallContent("call2", "search_research_history",
            new Dictionary<string, object?> { ["query"] = "Tesla stock pricing" });
        var (searchResult, _) = await historyHandler.HandleToolCallAsync(searchCall);

        Assert.NotNull(searchResult);
        var text = searchResult!.ToString()!;
        Assert.Contains("Tesla", text);
        Assert.Contains("(scheduled)", text);
    }

    public void Dispose()
    {
        try
        {
            var conn = _ctx.GetConnection();

            // Order matters: delete ResearchSessions BEFORE ScheduledJobs so the subselect
            // can still resolve the test job IDs. Two separate executions because SQLite
            // (Microsoft.Data.Sqlite) does not run multi-statement command text reliably.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "DELETE FROM ResearchSessions WHERE ScheduledJobId IN " +
                    "(SELECT Id FROM ScheduledJobs WHERE Name LIKE 'TEST_E2E_%')";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM ScheduledJobs WHERE Name LIKE 'TEST_E2E_%'";
                cmd.ExecuteNonQuery();
            }
        }
        finally
        {
            _ctx.Dispose();
        }
    }

    // ---- Stubs ---------------------------------------------------------------

    private sealed class StubResearchService : IResearchService
    {
        private readonly string _result;
        public StubResearchService(string result) => _result = result;

        public Task ExecuteResearchAsync(ResearchSession session, AiProvider provider,
            ResearchAnswerLength answerLength, CancellationToken ct)
        {
            session.SynthesizedResult = _result;
            session.Status = ResearchStatus.Completed;
            session.CompletedAt = DateTime.Now;
            return Task.CompletedTask;
        }
    }

    private sealed class StubEmbedding : IEmbeddingService
    {
        public bool IsModelAvailable => false;

        public Task<bool> DownloadModelAsync(IProgress<float>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> EnsureAvailableAsync(IProgress<float>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<float[]> GenerateEmbeddingAsync(string text,
            CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<float>());

        public byte[] FloatsToBytes(float[] embedding) => Array.Empty<byte>();
        public float[] BytesToFloats(byte[] bytes) => Array.Empty<float>();
    }

    private sealed class StubProviderResolver : IScheduledResearchProviderResolver
    {
        private readonly AiProvider? _provider;
        public StubProviderResolver(AiProvider? provider) => _provider = provider;

        public Task<AiProvider?> ResolveAsync(Guid? pinnedProviderId) => Task.FromResult(_provider);
    }

    private sealed class StubProviderService : IProviderService
    {
#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler? ProvidersChanged;
#pragma warning restore CS0067

        public Task<IReadOnlyList<AiProvider>> GetProvidersAsync()
            => Task.FromResult<IReadOnlyList<AiProvider>>(new List<AiProvider>().AsReadOnly());

        public Task<AiProvider?> GetProviderAsync(Guid id) => Task.FromResult<AiProvider?>(null);
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

    private sealed class StubLocalization : ILocalizationService
    {
        public TargetLanguage CurrentLanguage => TargetLanguage.EN;

#pragma warning disable CS0067 // Event is never used in tests.
        public event EventHandler<TargetLanguage>? LanguageChanged;
#pragma warning restore CS0067

        public void SetLanguage(TargetLanguage language) { }
        public string this[string key] => key;
        public string Format(string key, params object[] args) => key;
    }

    private sealed class SilentNotificationSurface : IScheduledJobNotificationSurface
    {
        public void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry) { }
        public void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason) { }

        public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
            => Task.FromResult<bool?>(false);
    }

    private sealed class IntegrationServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();

        public IntegrationServiceProvider Add<T>(T instance) where T : notnull
        {
            _services[typeof(T)] = instance;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var svc) ? svc : null;
    }

    private sealed class IntegrationScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _sp;
        public IntegrationScopeFactory(IServiceProvider sp) => _sp = sp;
        public IServiceScope CreateScope() => new IntegrationScope(_sp);

        private sealed class IntegrationScope : IServiceScope
        {
            public IntegrationScope(IServiceProvider sp) => ServiceProvider = sp;
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }
    }
}
