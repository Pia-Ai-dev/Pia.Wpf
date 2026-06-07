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

        // Initialize ViewModelLocator with root service provider (fallback for design-time)
        ViewModelLocator.Initialize(_serviceProvider);
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

            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pia", "Logs");
            Directory.CreateDirectory(logDirectory);

            builder.AddFile(Path.Combine(logDirectory, "pia.log"), options =>
            {
                options.Append = true;
                options.MinLevel = IsDevMode ? LogLevel.Debug : LogLevel.Information;
                options.FileSizeLimitBytes = 10 * 1024 * 1024; // 10 MB per file
                options.MaxRollingFiles = 7;                    // Keep 7 days
                options.FormatLogFileName = name =>
                {
                    var ext = Path.GetExtension(name);
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    var dir = Path.GetDirectoryName(name);
                    return Path.Combine(dir!, $"{baseName}-{DateTime.Now:yyyy-MM-dd}{ext}");
                };
            });
        });

        // Infrastructure
        services.AddSingleton<SqliteContext>();
        services.AddSingleton<VaultPathProvider>();
        services.AddSingleton<MarkdownVaultParser>();
        services.AddSingleton<IVaultStore>(sp => new VaultStore(
            sp.GetRequiredService<VaultPathProvider>().VaultRoot,
            sp.GetRequiredService<MarkdownVaultParser>()));
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
        services.AddScoped<ISnackbarService, SnackbarService>();
        services.AddScoped<IDialogOverlayService, DialogOverlayService>();

        // UI abstractions that keep System.Windows out of ViewModels
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ICollectionViewService, CollectionViewService>();

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
        services.AddSingleton<IIngestExtractor, Pia.Services.Wiki.AiIngestExtractionService>();
        services.AddSingleton<IIngestService, Pia.Services.Wiki.IngestService>();
        services.AddSingleton<ILintService, Pia.Services.Wiki.LintService>();
        services.AddSingleton<Pia.Services.Sync.SectionMergeEngine>();
        services.AddSingleton<Pia.Infrastructure.Sync.SyncBaseStore>();
        services.AddSingleton<IVaultSyncService, Pia.Services.Sync.VaultSyncService>();
        services.AddSingleton<Pia.Services.Migration.MemoryJsonRenderer>();
        services.AddSingleton<IVaultMigrationRunner, Pia.Services.Migration.VaultMigrationRunner>();
        services.AddSingleton<VaultWatcher>();
        services.AddSingleton<IMemoryToolHandler, MemoryToolHandler>();
        services.AddSingleton<IIngestToolHandler, IngestToolHandler>();
        services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<IScheduledJobService, ScheduledJobService>();
        services.AddSingleton<IScheduledResearchProviderResolver, ScheduledResearchProviderResolver>();
        services.AddSingleton<IScheduledJobNotificationSurface, ScheduledJobNotificationSurface>();
        services.AddSingleton<IReminderToolHandler, ReminderToolHandler>();
        services.AddSingleton<IScheduledJobToolHandler, ScheduledJobToolHandler>();
        services.AddSingleton<IResearchHistoryToolHandler, ResearchHistoryToolHandler>();
        services.AddSingleton<IKanbanColumnService, KanbanColumnService>();
        services.AddSingleton<ITodoService, TodoService>();
        services.AddSingleton<ITodoToolHandler, TodoToolHandler>();
        services.AddSingleton<IFilesToolHandler, FilesToolHandler>();
        services.AddSingleton<Pia.Services.Plugins.TrustedCertificateCacheService>();
        services.AddSingleton<Pia.Services.Plugins.CabManagerService>();
        services.AddSingleton<IPluginIconLoader, Pia.Services.Plugins.PluginIconLoaderService>();
        services.AddSingleton<IPluginService, Pia.Services.Plugins.PluginService>();
        services.AddSingleton<IAutocompleteService, AutocompleteService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ITemplateService, TemplateService>();
        services.AddSingleton<IPersonaService, PersonaService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IResearchHistoryService, ResearchHistoryService>();
        services.AddSingleton<IAssistantChatService, AssistantChatService>();
        // Assistant turn collaborators (extracted from AssistantViewModel).
        services.AddTransient<IAssistantPromptComposer, AssistantPromptComposer>();
        services.AddTransient<IChatTitleService, ChatTitleService>();
        services.AddScoped<IActionCardBuilder, ActionCardBuilder>();
        services.AddTransient<IResearchExportService, ResearchExportService>();
        services.AddSingleton<IWindowTrackingService, WindowTrackingService>();
        services.AddSingleton<INativeHotkeyServiceFactory, NativeHotkeyServiceFactory>();
        services.AddSingleton<ISelectedTextService, SelectedTextService>();
        services.AddSingleton<IFastPathOptimizer, FastPathOptimizerService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddSingleton<IWindowManagerService, WindowManagerService>();
        services.AddSingleton<IAudioRecordingService, AudioRecordingService>();
        services.AddSingleton<ITranscriptionService, TranscriptionService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<Services.Interfaces.IThemeService, Services.ThemeService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<ITtsService, TtsService>();

        // Privacy / PII tokenization
        services.AddSingleton<IPiiDetector, StructuredPiiDetector>();
        services.AddScoped<ITokenMapService, TokenMapService>();

        // E2EE services
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<IDeviceKeyService, DeviceKeyService>();
        services.AddSingleton<IE2EEService, E2EEService>();
        services.AddSingleton<IRecoveryCodeService, RecoveryCodeService>();
        services.AddSingleton<IDeviceManagementService, DeviceManagementService>();

        // Sync services
        services.AddSingleton<SyncDeleteTrackerService>(sp =>
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pia");
            var logger = sp.GetRequiredService<ILogger<SyncDeleteTrackerService>>();
            return new SyncDeleteTrackerService(dataDirectory, logger);
        });
        services.AddSingleton<SyncMapper>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ISyncClientService, SyncClientService>();

        // Cloud capability probe + assistant-chat sync (singletons)
        services.AddSingleton<ICloudCapabilityService, CloudCapabilityService>();
        services.AddSingleton<AssistantChatSyncService>();

        // Background services
        services.AddSingleton<ReminderBackgroundService>();
        services.AddSingleton<ScheduledJobBackgroundService>();
        services.AddSingleton<AssistantChatRetentionService>();

        // Auto-update
        services.AddSingleton<IUpdateService, UpdateService>();

        // Autostart
        services.AddSingleton<IAutostartService, AutostartService>();

        // Services - Scoped (per-window)
        services.AddScoped<Navigation.INavigationService, Navigation.NavigationService>();
        services.AddScoped<IDialogService, DialogService>();
        services.AddScoped<ITextOptimizationService, TextOptimizationService>();
        services.AddScoped<IResearchService, ResearchService>();
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
        services.AddScoped<ResearchViewModel>();
        services.AddScoped<ResearchHistoryViewModel>();
        services.AddScoped<AssistantHistoryViewModel>();
        services.AddScoped<MemoryViewModel>();
        services.AddScoped<RemindersViewModel>();
        services.AddScoped<TodoViewModel>();
        services.AddScoped<DeviceManagementViewModel>();
        services.AddScoped<E2EEOnboardingViewModel>();
        services.AddScoped<E2EESetupStepViewModel>();

        // First Run Wizard
        services.AddTransient<FirstRunWizardViewModel>();

        // Windows - Transient (created by WindowManagerService from scoped provider)
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.FirstRunWizardWindow>();
    }
}
