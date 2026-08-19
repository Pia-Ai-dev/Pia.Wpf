using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;
using Pia.Infrastructure;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Paths;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Services.Scheduling;
using Pia.ViewModels;
using Wpf.Ui;

namespace Pia;

public static class Bootstrapper
{
    public const string DefaultProductionServerUrl = "https://cloud.pia-ai.de";
    public const string ServerUrlEnvVar = "PIA_CLOUD_SERVER_URL";

    // Dev-only hooks (see DebugFileAudioCaptureService): when set, the corresponding transcription
    // service is wired to decode this recorded meeting file instead of live mic/loopback/Teams
    // audio, so the real overlay UI can be exercised against a recording. DEBUG builds only.
    public const string DebugDirectTranscriptionAudioFileEnvVar = "PIA_DEBUG_DIRECT_TRANSCRIPTION_AUDIO_FILE";
    public const string DebugMeetingAttendeeAudioFileEnvVar = "PIA_DEBUG_MEETING_ATTENDEE_AUDIO_FILE";

    public static string ProductionServerUrl =>
        Environment.GetEnvironmentVariable(ServerUrlEnvVar) is { Length: > 0 } envUrl
            ? envUrl
            : DefaultProductionServerUrl;

#if DEBUG
    public static bool IsDevMode => true;
#else
    public static bool IsDevMode => false;
#endif

    private static IServiceProvider? _serviceProvider;

    public static IServiceProvider ServiceProvider => _serviceProvider
        ?? throw new InvalidOperationException("Bootstrapper not initialized. Call Initialize() first.");

    public static async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        var options = new ServiceProviderOptions();
#if DEBUG
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
#endif

        _serviceProvider = services.BuildServiceProvider(options);

        var bootstrapLogger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrapper");

        // Effective, not configured: the whole point is to make an overridden run visibly distinguishable in a
        // log a user attaches to a support request.
        bootstrapLogger.LogInformation(
            "Data directories: Roaming={Roaming}, Local={Local}, Overridden={Overridden}",
            PiaPaths.RoamingDataDirectory, PiaPaths.LocalDataDirectory, PiaPaths.IsOverridden);

        var envServerUrl = Environment.GetEnvironmentVariable(ServerUrlEnvVar);
#if DEBUG
        bootstrapLogger.LogInformation(
            "Server URL resolution: IsDevMode={IsDevMode}, {EnvVar}={EnvValue}, EffectiveProductionServerUrl={Effective}",
            IsDevMode, ServerUrlEnvVar, envServerUrl ?? "(unset)", ProductionServerUrl);
#else
        bootstrapLogger.LogInformation("Server URL resolution: IsDevMode={IsDevMode}", IsDevMode);
#endif

        // Resolve enforcement up-front so both branches can short-circuit cleanly.
        // Enterprise policy.enforce.serverUrl always wins over both the hardcoded production URL
        // and the PIA_CLOUD_SERVER_URL dev override (precedence chain in docs/preset-settings.md).
        var policyService = _serviceProvider.GetRequiredService<IPolicyService>();
        await policyService.GetPolicyAsync();
        var serverUrlEnforced = policyService.IsEnforced(nameof(AppSettings.ServerUrl));

        if (!IsDevMode)
        {
            if (serverUrlEnforced)
            {
                bootstrapLogger.LogInformation(
                    "ServerUrl is enforced by enterprise policy; skipping production URL write");
            }
            else
            {
                // ProductionServerUrl honors the PIA_CLOUD_SERVER_URL env var override (for dev/staging).
                var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
                var settings = await settingsService.GetSettingsAsync();
                if (settings.ServerUrl != ProductionServerUrl)
                {
                    settings.ServerUrl = ProductionServerUrl;
                    settings.TrustSelfSignedCertificates = false;
                    await settingsService.SaveSettingsAsync(settings);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(envServerUrl))
        {
            if (serverUrlEnforced)
            {
                bootstrapLogger.LogInformation(
                    "{EnvVar} is set but ServerUrl is enforced by enterprise policy; env var override ignored",
                    ServerUrlEnvVar);
            }
            else
            {
                // In dev mode, apply the PIA_CLOUD_SERVER_URL env var override if set,
                // overriding whatever URL was previously saved via the Account Settings UI.
                var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
                var settings = await settingsService.GetSettingsAsync();
                settings.TrustSelfSignedCertificates = true;
                if (settings.ServerUrl != envServerUrl)
                {
                    bootstrapLogger.LogInformation(
                        "Applying {EnvVar} override to settings.ServerUrl (was {Old}, now {New})",
                        ServerUrlEnvVar, SafeUrl.Format(settings.ServerUrl), SafeUrl.Format(envServerUrl));
                    settings.ServerUrl = envServerUrl;
                    await settingsService.SaveSettingsAsync(settings);
                }
            }
        }

        // Derive the vault root from the (relocatable) assistant files folder and run the one-shot
        // in-place vault nesting BEFORE scaffolding/migration/watcher bind to the vault path.
        await InitializeAssistantFoldersAsync(_serviceProvider, bootstrapLogger);

        // Scaffold the §1 vault layout so a fresh install has sources/ and a default AGENTS.md before
        // anything reads or migrates into the vault. Idempotent and never overwrites a co-evolved
        // AGENTS.md; like migration and the watcher it must NEVER block startup, so guard and continue.
        try
        {
            await _serviceProvider.GetRequiredService<Pia.Services.Wiki.VaultSchemaService>()
                .EnsureScaffoldingAsync();
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogWarning(ex, "Vault scaffolding failed; sources/ and AGENTS.md may be missing this session");
        }

        // Migrate the legacy Memories table into the on-disk vault (one-shot, idempotent, guarded by
        // AppSettings.VaultVersion + a populated-vault cross-device check). Migration must NEVER block
        // startup: on failure the legacy table remains the fallback, so log a warning and continue.
        try
        {
            var migrationReport = await _serviceProvider.GetRequiredService<IVaultMigrationRunner>().RunAsync();
            if (!migrationReport.Skipped)
            {
                bootstrapLogger.LogInformation(
                    "Vault migration ran: {Rows} row(s) -> {Records} record(s), {Archived} archived",
                    migrationReport.RowsMigrated, migrationReport.RecordsWritten, migrationReport.Archived);
            }
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogWarning(ex, "Vault migration failed; legacy memory table remains the fallback");
        }

        // Reconcile the index against the vault on disk BEFORE the watcher goes live. The watcher is
        // change-only (it never scans existing files), so without this a cold index — e.g. after the
        // embedding model is first installed — never fills in, and files created while the app was
        // closed stay invisible to recall. Additive + content-hash idempotent (cheap after the first
        // run) and must precede Start() because the shared SQLite connection is single-threaded. Never
        // blocks startup on failure.
        try
        {
            await _serviceProvider.GetRequiredService<IVaultIndexer>().ReconcileAsync();
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogWarning(ex, "Vault reconcile failed; existing files may not be indexed until they change");
        }

        // Start the vault file-watcher on the default root so external edits (and Pia's own writes)
        // flow into the index. Start() creates the root dir if absent, so this never throws on a
        // fresh install; guard anyway so a watcher failure cannot block app startup.
        try
        {
            _serviceProvider.GetRequiredService<VaultWatcher>().Start();
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogWarning(ex, "Failed to start vault watcher; vault edits won't auto-index this session");
        }

        // One-time ingest migration: wipe every topic page + clear ingest state, then let reconcile rebuild
        // fresh. The hash gate would otherwise no-op the re-ingest (deleted pages + surviving state rows =
        // unchanged hashes = nothing rebuilt). Runs AFTER the watcher is live (it de-indexes the deletions)
        // and BEFORE auto-ingest reconcile. Runs once per bump, gated by AppSettings.IngestSchemaVersion.
        // Guarded so a migration failure never blocks startup.
        //   v1: initial synthesis-pipeline topic-page format.
        //   v2: scope tightening — charter no longer feeds memory/profile.md and ingest is sources/-only,
        //       so re-synthesis strips personal/profile content that had leaked into topic bodies.
        const int currentIngestSchemaVersion = 2;
        try
        {
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            var settings = await settingsService.GetSettingsAsync();
            if (settings.IngestSchemaVersion < currentIngestSchemaVersion)
            {
                var store = _serviceProvider.GetRequiredService<IVaultStore>();
                var index = _serviceProvider.GetRequiredService<Pia.Services.Wiki.VaultIndexService>();
                var pages = await store.EnumerateAsync("memory/topics/*.md");
                foreach (var page in pages)
                {
                    var relative = page.Replace('\\', '/');
                    await store.DeleteAsync(relative);
                    await index.RemoveEntryAsync(relative);
                }

                await _serviceProvider.GetRequiredService<Pia.Services.Wiki.IngestStateStore>().ClearAllAsync();
                settings.IngestSchemaVersion = currentIngestSchemaVersion;
                await settingsService.SaveSettingsAsync(settings);
                bootstrapLogger.LogInformation(
                    "Ingest synthesis migration: cleared {Pages} topic page(s) and ingest state; sources will re-synthesize",
                    pages.Count);
            }
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogWarning(ex, "Ingest synthesis migration failed; topic pages may retain the old format until re-ingested");
        }

        // Auto-ingest starts AFTER the vault watcher: recall indexing of Pia's own page writes happens
        // only via the live watcher, so ingest-written topic pages must land while it is running. The
        // reconcile scan runs on the service's own background queue — startup is never blocked on LLM work.
        try
        {
            await _serviceProvider.GetRequiredService<Pia.Services.Wiki.AutoIngestService>().StartAsync();
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogWarning(ex, "Failed to start auto-ingest; sources won't auto-compile this session");
        }

        // Initialize ViewModelLocator with root service provider (fallback for design-time)
        ViewModelLocator.Initialize(_serviceProvider);
    }

    /// <summary>
    /// Seeds the default assistant files folder on first run, points the vault root at
    /// <c>&lt;folder&gt;\Vault</c>, and runs the one-shot in-place migration that nests an existing
    /// user's legacy <c>%LOCALAPPDATA%\Pia\Vault</c> under their folder. Runs BEFORE scaffolding /
    /// migration / the watcher so they all bind to the nested vault. Guarded so a failure never blocks
    /// startup; idempotent via the layout-version marker + the derived-vault existence guard.
    /// </summary>
    private static async Task InitializeAssistantFoldersAsync(IServiceProvider sp, ILogger logger)
    {
        var settingsService = sp.GetRequiredService<ISettingsService>();
        var settings = await settingsService.GetSettingsAsync();
        var paths = sp.GetRequiredService<VaultPathProvider>();

        // Seed the default folder on first run (creating it + the Vault subfolder).
        var folder = settings.AssistantFilesFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AssistantWorkspace.DefaultRoot;
            try
            {
                Directory.CreateDirectory(AssistantWorkspace.VaultRootFor(folder));
                settings.AssistantFilesFolder = folder;
                await settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to seed default assistant folder"); }
        }

        // Point the vault root at <folder>\Vault BEFORE scaffolding/migration/watcher run.
        paths.SetRoot(AssistantWorkspace.VaultRootFor(folder!));

        // One-shot in-place nesting: move legacy %LOCALAPPDATA%\Pia\Vault under the folder.
        if (settings.AssistantFolderLayoutVersion < 1)
        {
            var legacyVault = Path.Combine(PiaPaths.LocalDataDirectory, "Vault");
            var derivedVault = AssistantWorkspace.VaultRootFor(folder!);
            try
            {
                if (Directory.Exists(legacyVault) &&
                    !string.Equals(Path.GetFullPath(legacyVault), Path.GetFullPath(derivedVault),
                                   StringComparison.OrdinalIgnoreCase) &&
                    !Directory.Exists(derivedVault))
                {
                    var result = await SafeDirectoryMove.MoveAsync(
                        legacyVault, derivedVault, progress: null, CancellationToken.None);
                    logger.LogInformation("In-place vault nesting: {Outcome}", result.Outcome);
                }
                settings.AssistantFolderLayoutVersion = 1;
                await settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex) { logger.LogWarning(ex, "In-place vault nesting failed; will retry next start"); }
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<AutoUpdateOptions>(configuration.GetSection(AutoUpdateOptions.SectionName));

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(IsDevMode ? LogLevel.Debug : LogLevel.Information);

            var logDirectory = Path.Combine(PiaPaths.LocalDataDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);

            var fileOptions = new FileLoggerOptions
            {
                Append = true,
                MinLevel = IsDevMode ? LogLevel.Debug : LogLevel.Information,
                FileSizeLimitBytes = 10 * 1024 * 1024, // 10 MB per file
                MaxRollingFiles = 7,                    // Keep 7 days
                FormatLogFileName = name =>
                {
                    var ext = Path.GetExtension(name);
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    var dir = Path.GetDirectoryName(name);
                    return Path.Combine(dir!, $"{baseName}-{DateTime.Now:yyyy-MM-dd}{ext}");
                },
            };

            // T2-18: registered by hand rather than through NReco's AddFile extension, because the provider is
            // WRAPPED TWICE. NReco has no scope support whatsoever (no ISupportExternalScope, no
            // IExternalScopeProvider, and FormatLogEntry cannot reach one), so ILogger.BeginScope(runId) would
            // compile and be discarded before it reached the file a user attaches to a support request —
            // ScopeRenderingLoggerProvider is what makes the run/step scope visible there. LogMessageCapLoggerProvider
            // is the release-only length backstop (defence in depth; the load-bearing mechanism is still the
            // compile-time erasure of the Sensitive* family — 17-trust-model.md §4).
            //
            // ORDER: scope OUTSIDE cap, so the scope prefix is inside the capped text and survives truncation
            // (which keeps the head) — a capped line still says which run it belongs to.
            builder.Services.AddSingleton<ILoggerProvider>(_ => new ScopeRenderingLoggerProvider(
                new LogMessageCapLoggerProvider(
                    new FileLoggerProvider(Path.Combine(logDirectory, "pia.log"), fileOptions))));
        });

        // Infrastructure
        services.AddSingleton<SqliteContext>();
        services.AddSingleton<VaultPathProvider>();
        services.AddSingleton<MarkdownVaultParser>();
        services.AddSingleton<Pia.Infrastructure.Vault.IVaultWriteGate, Pia.Infrastructure.Vault.VaultWriteGate>();
        services.AddSingleton<IVaultStore>(sp => new VaultStore(
            sp.GetRequiredService<VaultPathProvider>(),
            sp.GetRequiredService<MarkdownVaultParser>(),
            sp.GetRequiredService<Pia.Infrastructure.Vault.IVaultWriteGate>()));
        services.AddSingleton<DpapiHelper>();
        services.AddTransient<HttpLoggingHandler>();
        services.AddTransient<RateLimitRetryHandler>();

        // HttpClient Factory for managed HTTP connections
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.AddHttpMessageHandler<RateLimitRetryHandler>();
            builder.AddHttpMessageHandler<HttpLoggingHandler>();
            builder.ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All
                };
                var settingsService = sp.GetService<ISettingsService>();
                if (settingsService != null)
                {
                    var settings = settingsService.GetSettingsAsync()
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    if (settings.TrustSelfSignedCertificates)
                    {
                        handler.ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }
                }
                return handler;
            });
        });

        // WPF-UI Services - Scoped (per-window)
        services.AddScoped<IContentDialogService, ContentDialogService>();
        // Snackbars are funneled into Flow instead of the WPF-UI slide-in (design §7). FlowSnackbarService
        // implements the WPF-UI ISnackbarService so all ~85 producer call sites are captured untouched.
        services.AddScoped<ISnackbarService, Services.Flow.FlowSnackbarService>();
        services.AddScoped<IDialogOverlayService, DialogOverlayService>();

        // UI abstractions that keep System.Windows out of ViewModels
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ICollectionViewService, CollectionViewService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();

        // Color-emoji renderer (OS Direct2D/DirectWrite/WIC). Same instance the static accessor and
        // XAML controls use, so the bitmap cache is shared.
        services.AddSingleton(Pia.Emoji.EmojiImageRenderer.Shared);

        // AI provider handlers (one per AiProviderType) + registry
        services.AddSingleton<IAiProviderHandler, OpenAiProviderHandler>();
        services.AddSingleton<IAiProviderHandler, AzureOpenAiProviderHandler>();
        services.AddSingleton<IAiProviderHandler, OllamaProviderHandler>();
        services.AddSingleton<IAiProviderHandler, MistralProviderHandler>();
        services.AddSingleton<IAiProviderHandler, OpenRouterProviderHandler>();
        services.AddSingleton<IAiProviderHandler, OpenAiCompatibleProviderHandler>();
        services.AddSingleton<IAiProviderHandler, VLlmProviderHandler>();
        services.AddSingleton<IAiProviderHandler, PiaCloudProviderHandler>();
        services.AddSingleton<AiProviderHandlerResolver>();

        // T1-2: the per-provider request bound. SINGLETON while AiClientService is transient — the keyed
        // semaphore is the device-wide bound, so a per-request instance would throttle nothing.
        services.AddSingleton<IProviderRequestThrottle, ProviderRequestThrottle>();

        // AI Client (decorator applies PII tokenization transparently)
        services.AddTransient<AiClientService>();
        services.AddTransient<IAiClientService>(sp =>
            new TokenizingAiClientService(
                sp.GetRequiredService<AiClientService>(),
                sp,
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<ILogger<TokenizingAiClientService>>()));

        // Follow-up suggestions (uses IAiClientService internally — transient)
        services.AddTransient<ISuggestionService, SuggestionService>();

        // Enterprise policy
        services.AddSingleton<IPolicyService, PolicyService>();

        // Services - Singleton (shared across all windows)
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IVaultIndexer, VaultIndexer>();
        services.AddSingleton<ISectionUpsertService, SectionUpsertService>();
        services.AddSingleton<Pia.Services.Wiki.VaultIndexService>();
        services.AddSingleton<Pia.Services.Wiki.VaultLogService>();
        services.AddSingleton<Pia.Services.Wiki.VaultSchemaService>();
        services.AddSingleton<Pia.Services.Wiki.VaultCharterService>();
        services.AddSingleton<IIngestExtractor, Pia.Services.Wiki.AiIngestExtractionService>();
        services.AddSingleton<IIngestSynthesizer, Pia.Services.Wiki.AiIngestSynthesisService>();
        services.AddSingleton<IIngestService, Pia.Services.Wiki.IngestService>();
        services.AddSingleton(sp => new Pia.Services.Wiki.IngestStateStore(
            sp.GetRequiredService<SqliteContext>().ConnectionString));
        services.AddSingleton<Pia.Services.Wiki.AutoIngestService>();
        services.AddSingleton<IIngestScheduler>(sp => sp.GetRequiredService<Pia.Services.Wiki.AutoIngestService>());
        services.AddSingleton<IVaultSourcesService, Pia.Services.Wiki.VaultSourcesService>();
        services.AddSingleton<ILintService, Pia.Services.Wiki.LintService>();
        services.AddSingleton<Pia.Services.Sync.SectionMergeEngine>();
        services.AddSingleton<Pia.Infrastructure.Sync.SyncBaseStore>();
        services.AddSingleton<IVaultSyncService, Pia.Services.Sync.VaultSyncService>();
        services.AddSingleton<Pia.Services.Migration.MemoryJsonRenderer>();
        services.AddSingleton<IVaultMigrationRunner, Pia.Services.Migration.VaultMigrationRunner>();
        services.AddSingleton<VaultWatcher>();
        services.AddSingleton<IAssistantFolderRelocationService, AssistantFolderRelocationService>();
        services.AddSingleton<IMemoryToolHandler, MemoryToolHandler>();
        services.AddSingleton<IIngestToolHandler, IngestToolHandler>();
        services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<IScheduledJobService, ScheduledJobService>();
        services.AddSingleton<IScheduledResearchProviderResolver, ScheduledResearchProviderResolver>();
        // Startup-only (App.xaml.cs), between the crash sweep and the scheduler — see the interface for why
        // that position is load-bearing in both directions.
        services.AddSingleton<IScheduledFiringReconciler, ScheduledFiringReconciler>();
        services.AddSingleton<IScheduledJobNotificationSurface, ScheduledJobNotificationSurface>();
        // Terminal agent-run Flow notifications (R18/G3). Eager-resolved at startup (App.xaml.cs).
        services.AddSingleton<IAgentRunNotificationSurface, AgentRunNotificationSurface>();
        services.AddSingleton<IBackgroundChatNotifier, BackgroundChatNotificationSurface>();
        services.AddSingleton<IReminderToolHandler, ReminderToolHandler>();
        services.AddSingleton<IScheduledJobToolHandler, ScheduledJobToolHandler>();
        services.AddSingleton<IKanbanColumnService, KanbanColumnService>();
        services.AddSingleton<ITodoService, TodoService>();
        services.AddSingleton<ITodoToolHandler, TodoToolHandler>();
        services.AddSingleton<IFileStalenessStore, FileStalenessStore>();
        services.AddSingleton<IFilesToolHandler, FilesToolHandler>();
        services.AddSingleton<Pia.Helpers.IGitProcessRunner, Pia.Helpers.GitProcessRunner>();
        services.AddSingleton<IGitToolHandler, GitToolHandler>();
        services.AddSingleton<IWorkingDirectoryService, WorkingDirectoryService>();
        services.AddSingleton<Pia.Services.Plugins.TrustedCertificateCacheService>();
        services.AddSingleton<Pia.Services.Plugins.CabManagerService>();
        services.AddSingleton<IPluginIconLoader, Pia.Services.Plugins.PluginIconLoaderService>();
        services.AddSingleton<IPluginService, Pia.Services.Plugins.PluginService>();
        services.AddSingleton<IAutocompleteService, AutocompleteService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        // hermes #15: the session grant tier. SINGLETON is the scope — its lifetime IS the "session" the
        // button names, and a second instance would be a second session with its own answers. Registered
        // BEFORE ToolPermissionService, which consults it (and whose ctor resolves it).
        services.AddSingleton<ISessionToolGrantStore, SessionToolGrantStore>();
        services.AddSingleton<IToolPermissionService, ToolPermissionService>();
        services.AddSingleton<ITemplateService, TemplateService>();
        services.AddSingleton<IPersonaService, PersonaService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IAssistantChatService, AssistantChatService>();
        services.AddSingleton<IAgentRunService, AgentRunService>();
        // The per-run audit timeline (Batch 03). Singleton for the same reason AgentRunService is: it owns a
        // dedicated SQLite connection and a per-run Seq allocator, and a second instance would allocate
        // colliding sequence numbers for the same run.
        services.AddSingleton<IAgentTimelineService, AgentTimelineService>();
        // The run panel live-updates its tool-activity section through this broker (T2-G1's first consumer).
        // Further observers (OTel exporter, file trace) still register ADDITIVELY right here; never TryAdd,
        // which would let the first registration silently exclude the second.
        services.AddSingleton<RunTimelineWatcher>();
        services.AddSingleton<ITimelineWatcher>(sp => sp.GetRequiredService<RunTimelineWatcher>());
        services.AddSingleton<IRunObserver>(sp => sp.GetRequiredService<RunTimelineWatcher>());
        // Assistant turn collaborators (extracted from AssistantViewModel).
        services.AddTransient<IAssistantPromptComposer, AssistantPromptComposer>();
        services.AddTransient<IChatTitleService, ChatTitleService>();
        // Headless background assistant-turn runner. Transient (resolved per-run from a
        // fresh scope by the scheduled-job service) so its transient AI-client decorator
        // doesn't cache tokenization state across runs.
        // The concrete type is registered too: HeadlessTurnExecutor depends on it directly for
        // RunExchangeAsync (per-step), which is not on the single-turn interface.
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<IBackgroundAssistantTurnRunner>(sp => sp.GetRequiredService<BackgroundAssistantTurnRunner>());
        // Agent orchestration loop (1.2, §13.10). Planner + orchestrator are transient/stateless per
        // call; the headless executor is resolved inside a fresh per-run DI scope. The live executor
        // is NOT registered — ChatSessionManager new's it on the UI thread bound to the session.
        services.AddTransient<IAgentPlanner, AgentPlanner>();
        services.AddTransient<IAgentVerifier, AgentVerifier>();
        services.AddTransient<AgentRunOrchestrator>();
        services.AddTransient<HeadlessTurnExecutor>();
        // Batch 07 G6: per-step persona/provider/prompt resolution and the assignable-persona roster.
        // TRANSIENT and concrete. Transient because it memoizes the composed system prompt per persona id
        // for the life of ONE run — a singleton would pin a stale prompt across a persona edit or a roster
        // change until the app restarted, silently. Concrete because an interface here would buy nothing:
        // every consumer wants the real memoizing behaviour, and the executor tests construct it directly.
        services.AddTransient<StepPersonaResolver>();
        // ...and a FACTORY, because "transient" is only per-RUN where something resolves per run.
        // HeadlessTurnExecutor does (the launcher builds a fresh scope per run and per resume) and takes the
        // resolver directly. Its two other consumers do NOT: ChatSessionManager is Scoped, i.e. one instance
        // per WINDOW, and the AgentPlanner it reaches through its AgentRunOrchestrator is resolved once into
        // that same scope. Injecting the resolver into either would pin ONE memo cache — and therefore one
        // roster snapshot, one composed prompt per persona, and one degraded-id set — for as long as the
        // window is open, which is the exact staleness the transient registration above exists to avoid: a
        // user who configures the roster in Settings would see no specialists until the app restarted.
        // Both invoke this per run instead. All five dependencies are singletons (or transients over
        // singletons), so resolving from the root provider is safe with ValidateScopes on.
        services.AddSingleton<Func<StepPersonaResolver>>(sp => sp.GetRequiredService<StepPersonaResolver>);
        // Headless "Run in background" / scheduled-AgentTask launcher (§17.1/17.5). Singleton: owns the
        // shared concurrency cap, shutdown token, and per-run workspace cleanup map. One instance also
        // serves IAgentRunResumeService (budget-pause resume re-launches through this same machinery).
        // A2: the launch-bracket index of runs that are actually executing. Singleton and shared by both
        // brackets (this launcher and BackgroundAssistantTurnRunner) and by every window's ChatSessionManager,
        // which reads it synchronously when a chat is activated. Holds no state that outlives a run.
        services.AddSingleton<IExecutingRunStore, ExecutingRunStore>();
        // Batch 08 D1: the per-dispatch cancel-sink + pause-intent registry that lets a run's own loop tell a
        // USER PAUSE from a Stop. SINGLETON beside the index above and for the same reason — it is written by
        // the launcher's two dispatches and by every window's ChatSessionManager, and read by the loop running
        // in a per-run scope, so a scoped registration would give the pause command and the loop two different
        // maps. Holds no state that outlives a dispatch.
        services.AddSingleton<IRunSteeringStore, RunSteeringStore>();
        // The command surface over that registry. Never writes a run row: the row moves to Paused from inside
        // the loop, through the CAS, after the aborted step has been given back to the plan.
        services.AddSingleton<IAgentRunSteeringService, AgentRunSteeringService>();
        // Batch 06 G3: owns both workspace provisioning modes (git worktree when the source root is a repo,
        // else a bounded copy) and the symmetric teardown each needs. SINGLETON, like the launcher that
        // consumes it: a scoped registration would give two dispatches of one run different metadata readers
        // for the same workspace.
        services.AddSingleton<IRunWorkspaceService, RunWorkspaceService>();
        services.AddSingleton<HeadlessRunLauncher>();
        services.AddSingleton<IHeadlessRunLauncher>(sp => sp.GetRequiredService<HeadlessRunLauncher>());
        services.AddSingleton<IAgentRunResumeService>(sp => sp.GetRequiredService<HeadlessRunLauncher>());
        services.AddScoped<IActionCardBuilder, ActionCardBuilder>();
        services.AddSingleton<IMarkdownExportService, MarkdownExportService>();
        services.AddSingleton<IWindowTrackingService, WindowTrackingService>();
        services.AddSingleton<INativeHotkeyServiceFactory, NativeHotkeyServiceFactory>();
        services.AddSingleton<ISelectedTextService, SelectedTextService>();
        services.AddSingleton<IFastPathOptimizer, FastPathOptimizerService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddSingleton<IWindowManagerService, WindowManagerService>();
        services.AddSingleton<IAudioRecordingService, AudioRecordingService>();
        services.AddSingleton<ITranscriptionService, TranscriptionService>();

        // Meeting attendee (automated browser join + STT). The orchestrator constructs its own
        // IMeetingSession (TeamsMeetingSession) at runtime with the provisioned Chromium path, so
        // IMeetingSession is intentionally NOT container-registered (no parameterless seam).
        services.AddSingleton<Services.MeetingAttendee.IBrowserProvisioner, Services.MeetingAttendee.ChromiumProvisioner>();
        services.AddSingleton<Services.MeetingAttendee.IDefaultBrowserResolver, Services.MeetingAttendee.DefaultBrowserResolver>();
#if DEBUG
        var debugMeetingAttendeeAudioFile = Environment.GetEnvironmentVariable(DebugMeetingAttendeeAudioFileEnvVar);
        if (!string.IsNullOrEmpty(debugMeetingAttendeeAudioFile))
        {
            services.AddSingleton<Services.MeetingAttendee.IMeetingAttendeeService>(sp =>
                new Services.MeetingAttendee.MeetingAttendeeService(
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<ILoggerFactory>(),
                    provisionChromium: (_, _) => Task.FromResult(string.Empty),
                    createTranscription: Services.MeetingAttendee.MeetingAttendeeService.CreateProductionTranscriptionFactory(
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<IHttpClientFactory>(),
                        sp.GetRequiredService<ILoggerFactory>()),
                    sessionFactory: _ => new Services.MeetingAttendee.DebugNoOpMeetingSession(),
                    audioSourceFactory: (_, _) => new Services.LiveTranscription.DebugFileAudioCaptureService(
                        debugMeetingAttendeeAudioFile,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Services.LiveTranscription.DebugFileAudioCaptureService>()),
                    engineServiceFactory: Services.MeetingAttendee.MeetingAttendeeService.CreateEngineServiceFactory(
                        sp.GetRequiredService<ILoggerFactory>())));
        }
        else
        {
            services.AddSingleton<Services.MeetingAttendee.IMeetingAttendeeService, Services.MeetingAttendee.MeetingAttendeeService>();
        }
#else
        services.AddSingleton<Services.MeetingAttendee.IMeetingAttendeeService, Services.MeetingAttendee.MeetingAttendeeService>();
#endif

        // Direct transcription (in-session voice consent + live capture). Session-scoped consent
        // only (owner decision D-3/D-4): no persistent voice-profile store, no evidence retention worker.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Services.Consent.IConsentStateManager, Services.Consent.ConsentStateManager>();
        services.AddSingleton<Services.Consent.INamedConsentClassifier, Services.Consent.NamedConsentClassifier>();
        // Constructing this does NOT touch the disk: the assistant view is built at startup and
        // transitively resolves this singleton, so the file (and its directory) are created on the first
        // audit event instead — a launch that never opens direct transcription leaves nothing behind.
        services.AddSingleton<Services.Consent.IConsentAuditLog>(sp =>
            Services.Consent.JsonlConsentAuditLog.CreateForSession(
                sp.GetRequiredService<ILogger<Services.Consent.JsonlConsentAuditLog>>()));
        services.AddSingleton<Services.Consent.IConsentEvidenceStore>(sp => new Services.Consent.ConsentEvidenceStore(
            Services.Consent.ConsentEvidenceStore.DefaultRootDirectory,
            sp.GetRequiredService<DpapiHelper>(),
            sp.GetRequiredService<ILogger<Services.Consent.ConsentEvidenceStore>>()));
#if DEBUG
        var debugDirectTranscriptionAudioFile = Environment.GetEnvironmentVariable(DebugDirectTranscriptionAudioFileEnvVar);
        if (!string.IsNullOrEmpty(debugDirectTranscriptionAudioFile))
        {
            services.AddSingleton<IDirectTranscriptionService>(sp =>
                new Services.LiveTranscription.DirectTranscriptionService(
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<ILoggerFactory>(),
                    sp.GetRequiredService<Services.Consent.IConsentStateManager>(),
                    sp.GetRequiredService<Services.Consent.INamedConsentClassifier>(),
                    sp.GetRequiredService<Services.Consent.IConsentAuditLog>(),
                    sp.GetRequiredService<Services.Consent.IConsentEvidenceStore>(),
                    createTranscription: Services.LiveTranscription.DirectTranscriptionService.CreateProductionTranscriptionFactory(
                        sp.GetRequiredService<ISettingsService>(),
                        sp.GetRequiredService<IHttpClientFactory>(),
                        sp.GetRequiredService<ILoggerFactory>()),
                    micSourceFactory: () => new Services.LiveTranscription.MicAudioCaptureService(
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Services.LiveTranscription.MicAudioCaptureService>()),
                    loopbackSourceFactory: () => new Services.LiveTranscription.DebugFileAudioCaptureService(
                        debugDirectTranscriptionAudioFile,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Services.LiveTranscription.DebugFileAudioCaptureService>()),
                    engineServiceFactory: Services.LiveTranscription.DirectTranscriptionService.CreateEngineServiceFactory(
                        sp.GetRequiredService<ILoggerFactory>())));
        }
        else
        {
            services.AddSingleton<IDirectTranscriptionService, Services.LiveTranscription.DirectTranscriptionService>();
        }
#else
        services.AddSingleton<IDirectTranscriptionService, Services.LiveTranscription.DirectTranscriptionService>();
#endif

        // In-app toasts are re-implemented over Flow (design §7), retiring the hand-rolled Border toast.
        services.AddSingleton<INotificationService, Services.Flow.FlowNotificationService>();

        // Flow — the persistent attention store (singleton) + its durable SQLite store.
        services.AddSingleton<Services.Flow.IFlowPersistenceStore, Services.Flow.FlowPersistenceStore>();
        services.AddSingleton<Services.Flow.IFlowService, Services.Flow.FlowService>();
        services.AddSingleton<Services.Interfaces.IThemeService, Services.ThemeService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();

        // The single place Application.Current.Dispatcher is read for a ViewModel marshal (Batch 12).
        // Singleton is safe and correct: it holds no state and re-reads Application.Current per call.
        services.AddSingleton<IUiDispatcher, UiDispatcherService>();
        services.AddSingleton<ITtsService, TtsService>();

        // Privacy / PII tokenization
        services.AddSingleton<IPiiDetector, StructuredPiiDetector>();
        services.AddScoped<ITokenMapService, TokenMapService>();
        // Per-session token-map factory: each ChatSession owns its own map so
        // concurrent background turns never share a PII namespace. All three
        // TokenMapService dependencies are singletons, so a fresh instance is safe.
        services.AddSingleton<Func<ITokenMapService>>(sp => () => new TokenMapService(
            sp.GetRequiredService<IPiiDetector>(),
            sp.GetRequiredService<IMemoryService>(),
            sp.GetRequiredService<ISettingsService>()));

        // E2EE services
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<IDeviceKeyService, DeviceKeyService>();
        services.AddSingleton<IE2EEService, E2EEService>();
        services.AddSingleton<IRecoveryCodeService, RecoveryCodeService>();
        services.AddSingleton<IDeviceManagementService, DeviceManagementService>();

        // Sync services
        services.AddSingleton<SyncDeleteTrackerService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SyncDeleteTrackerService>>();
            return new SyncDeleteTrackerService(PiaPaths.RoamingDataDirectory, logger);
        });
        services.AddSingleton<SyncMapper>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ISyncClientService, SyncClientService>();

        // Cloud capability probe + assistant-chat sync (singletons)
        services.AddSingleton<ICloudCapabilityService, CloudCapabilityService>();
        // Provider tool-calling capability probe for the Agent lever/suggestion (R10).
        services.AddSingleton<IProviderCapabilityService, ProviderCapabilityService>();
        services.AddSingleton<AssistantChatSyncService>();

        // Background services
        services.AddSingleton<ReminderBackgroundService>();
        services.AddSingleton<ScheduledJobBackgroundService>();
        // The SAME instance, not a second one: run-now must see the tick's own duplicate-dispatch state — the
        // missed-prompt dedup set, the bookkeeping lock and the in-flight dispatch list — which a
        // separately-constructed service would not share.
        services.AddSingleton<IScheduledJobRunner>(sp => sp.GetRequiredService<ScheduledJobBackgroundService>());
        services.AddSingleton<AssistantChatRetentionService>();
        services.AddSingleton<Services.Flow.TodoDeadlineBackgroundService>();

        // Background assignments — the one plane where content leaves the encrypted side. The consent log is a
        // singleton because its receipts are session-scoped evidence, and the coordinator is the only thing
        // that can send. Constructing the log touches no disk until the first record, like its speaker-consent
        // counterpart above.
        services.AddSingleton<Services.Operators.IAssignmentConsentStore>(sp =>
            Services.Operators.JsonlAssignmentConsentStore.CreateDefault(
                sp.GetRequiredService<ILogger<Services.Operators.JsonlAssignmentConsentStore>>()));
        services.AddSingleton<Services.Operators.IAssignmentApiClient, Services.Operators.AssignmentApiClient>();
        services.AddSingleton<Services.Operators.IAssignmentScopeResolver, Services.Operators.AssignmentScopeResolver>();
        services.AddSingleton<Services.Operators.IAssignmentPendingStore, Services.Operators.AssignmentPendingStore>();
        // The concrete type is registered too, so the notification surface can subscribe to the SAME
        // instance's Completed event that the drain worker drives.
        services.AddSingleton<Services.Operators.AssignmentRunOrchestrator>();
        services.AddSingleton<Services.Operators.IAssignmentRunOrchestrator>(sp =>
            sp.GetRequiredService<Services.Operators.AssignmentRunOrchestrator>());
        services.AddSingleton<Services.Operators.AssignmentDrainService>();
        services.AddSingleton<IAssignmentNotificationSurface, AssignmentNotificationSurface>();
        // Transient behind a factory: each open is a fresh affirmation, so no dialog may reuse the last one's
        // selection.
        services.AddTransient<AssignmentConsentViewModel>();
        services.AddSingleton<Func<AssignmentConsentViewModel>>(sp =>
            sp.GetRequiredService<AssignmentConsentViewModel>);

        // Auto-update
        services.AddSingleton<IUpdateService, UpdateService>();

        // Autostart
        services.AddSingleton<IAutostartService, AutostartService>();

        // Services - Scoped (per-window)
        // Chat-session manager: scoped per assistant window because it injects scoped
        // IActionCardBuilder + ITokenMapService (a singleton would be a captive dependency).
        services.AddScoped<ViewModels.Models.IChatSessionManager, ViewModels.Models.ChatSessionManager>();
        services.AddScoped<Navigation.INavigationService, Navigation.NavigationService>();
        services.AddScoped<IDialogService, DialogService>();
        services.AddScoped<ITextOptimizationService, TextOptimizationService>();
        services.AddScoped<IVoiceInputService, VoiceInputService>();

        // Services - Transient (no shared state)
        services.AddSingleton<IProviderService, ProviderService>();
        services.AddTransient<IOutputService, OutputService>();

        // ViewModels - Scoped (per-window, cached within scope)
        services.AddScoped<MainWindowViewModel>();
        services.AddScoped<OptimizeViewModel>();
        services.AddScoped<SettingsViewModel>();
        services.AddScoped<HistoryViewModel>();
        services.AddScoped<AssistantViewModel>();
        services.AddScoped<MeetingAttendeeViewModel>();
        services.AddScoped<DirectTranscriptionViewModel>();
        services.AddScoped<AssistantHistoryViewModel>();
        services.AddScoped<MemoryViewModel>();
        services.AddScoped<RoutinesViewModel>();
        services.AddScoped<RemindersViewModel>();
        services.AddScoped<AssignmentsViewModel>();
        services.AddScoped<TodoViewModel>();
        services.AddScoped<E2EEOnboardingViewModel>();
        services.AddScoped<E2EESetupStepViewModel>();
        services.AddScoped<ViewModels.Flow.FlowViewModel>();

        // First Run Wizard
        services.AddTransient<FirstRunWizardViewModel>();

        // Windows - Transient (created by WindowManagerService from scoped provider)
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.FirstRunWizardWindow>();
    }
}
