using System.IO;
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
/// it due -> stubbed background assistant turn runs -> job marked complete with the chat id ->
/// query_scheduled_research finds it.
/// </summary>
/// <remarks>
/// This test exercises the real <see cref="SqliteContext"/> against the user's
/// <c>%LOCALAPPDATA%\Pia\history.db</c> (a known plan-accepted tradeoff shared by the other
/// integration tests). Cleanup deletes only TEST_E2E_-prefixed scheduled jobs.
/// </remarks>
[Trait("Category", "Integration")]
public class ScheduledJobToolIntegrationTests : IDisposable
{
    private readonly SqliteContext _ctx = new();

    private sealed class IntegrationSettingsService : ISettingsService
    {
#pragma warning disable CS0067
        public event EventHandler<AppSettings>? SettingsChanged;
#pragma warning restore CS0067
        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task SaveDraftAsync(string? draftText) => Task.CompletedTask;
        public Task<string?> GetDraftAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task EndToEnd_CreateJob_RunDueJob_MarksCompleteAndQueryFinds()
    {
        // Arrange the dependency graph manually for an integration test.
        var calc = new RecurrenceCalculator();
        var tmpDir = Path.Combine(Path.GetTempPath(), "PiaIntTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var settings = new IntegrationSettingsService();
        var deleteTracker = new SyncDeleteTrackerService(tmpDir, NullLogger<SyncDeleteTrackerService>.Instance);
        var jobs = new ScheduledJobService(_ctx, calc, settings, deleteTracker, NullLogger<ScheduledJobService>.Instance);

        var chatId = Guid.NewGuid();
        var runner = new StubRunner(chatId);
        var providers = new StubProviderResolver(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "Stub",
            Endpoint = "https://example",
            TimeoutSeconds = 60
        });
        var notifications = new SilentNotificationSurface();

        var sp = new IntegrationServiceProvider()
            .Add<IBackgroundAssistantTurnRunner>(runner);
        var scopeFactory = new IntegrationScopeFactory(sp);

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
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
                ["timeOfDay"] = "08:00",
                ["grantedTools"] = "create_memory"
            });

        var (_, pending) = await toolHandler.HandleToolCallAsync(createCall);
        Assert.NotNull(pending);
        await pending!.Execute();

        // Force the row "due" so the BG service picks it up immediately.
        var conn = _ctx.GetConnection();
        Guid jobId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM ScheduledJobs WHERE Name = 'TEST_E2E_Tesla'";
            jobId = Guid.Parse((string)(await cmd.ExecuteScalarAsync())!);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE ScheduledJobs SET NextFireAt = @t WHERE Id = @id";
            cmd.Parameters.AddWithValue("@t", DateTime.Now.AddSeconds(-1).ToString("O"));
            cmd.Parameters.AddWithValue("@id", jobId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }

        // Run the background service tick once.
        await bg.ExecuteOnceAsync(CancellationToken.None);

        // Assert: the runner ran and the job was marked complete with the produced chat id.
        Assert.Equal(1, runner.RunCount);
        Assert.Equal("Tesla stock pricing news", runner.LastPrompt);
        Assert.Contains("create_memory", runner.LastGrantedTools);

        var reloaded = await jobs.GetAsync(jobId);
        Assert.NotNull(reloaded);
        Assert.Equal(chatId, reloaded!.LastResultEntryId);
        Assert.Equal(new[] { "create_memory" }, reloaded.GrantedTools);
        Assert.Equal(1, notifications.SuccessCount);

        // Assert: query_scheduled_research renders the job.
        var queryCall = new FunctionCallContent("call2", "query_scheduled_research",
            new Dictionary<string, object?> { ["filter"] = "all" });
        var (queryResult, _) = await toolHandler.HandleToolCallAsync(queryCall);

        Assert.NotNull(queryResult);
        var text = queryResult!.ToString()!;
        Assert.Contains("TEST_E2E_Tesla", text);
    }

    public void Dispose()
    {
        try
        {
            var conn = _ctx.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ScheduledJobs WHERE Name LIKE 'TEST_E2E_%'";
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _ctx.Dispose();
        }
    }

    // ---- Stubs ---------------------------------------------------------------

    private sealed class StubRunner : IBackgroundAssistantTurnRunner
    {
        private readonly Guid _chatId;
        public StubRunner(Guid chatId) => _chatId = chatId;

        public int RunCount { get; private set; }
        public string? LastPrompt { get; private set; }
        public IReadOnlyCollection<string> LastGrantedTools { get; private set; } = [];

        public Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
        {
            RunCount++;
            LastPrompt = request.Prompt;
            LastGrantedTools = request.GrantedWriteTools;
            return Task.FromResult(new BackgroundTurnResult(_chatId, true, null));
        }
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
        public Task ReassignProviderIdAsync(Guid oldId, Guid newId, AiProvider merged) => Task.CompletedTask;
        public Task RepairModeDefaultsAsync() => Task.CompletedTask;
        public Task ConsolidateLocalDuplicatesAsync() => Task.CompletedTask;
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
        public int SuccessCount { get; private set; }

        public void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle) => SuccessCount++;
        public void NotifyFailure(ScheduledJob job, string reason) { }

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
