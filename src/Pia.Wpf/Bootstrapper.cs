using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NReco.Logging.File;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Wpf.Ui;

namespace Pia;

public static class Bootstrapper
{
    public const string ProductionServerUrl = "https://cloud.pia-ai.de";

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

        // In production, enforce the hardcoded server URL
        if (!IsDevMode)
        {
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            var settings = await settingsService.GetSettingsAsync();
            if (settings.ServerUrl != ProductionServerUrl)
            {
                settings.ServerUrl = ProductionServerUrl;
                settings.TrustSelfSignedCertificates = false;
                await settingsService.SaveSettingsAsync(settings);
            }
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
            builder.SetMinimumLevel(LogLevel.Information);

            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pia", "Logs");
            Directory.CreateDirectory(logDirectory);

            builder.AddFile(Path.Combine(logDirectory, "pia.log"), options =>
            {
                options.Append = true;
                options.MinLevel = LogLevel.Information;
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
        services.AddSingleton<DpapiHelper>();

        // HttpClient Factory for managed HTTP connections
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new HttpClientHandler();
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

        // AI Client (decorator applies PII tokenization transparently)
        services.AddTransient<AiClientService>();
        services.AddTransient<IAiClientService>(sp =>
            new TokenizingAiClientService(
                sp.GetRequiredService<AiClientService>(),
                sp,
                sp.GetRequiredService<ISettingsService>()));

        // Services - Singleton (shared across all windows)
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IMemoryToolHandler, MemoryToolHandler>();
        services.AddSingleton<IReminderService, ReminderService>();
        services.AddSingleton<IReminderToolHandler, ReminderToolHandler>();
        services.AddSingleton<ITodoService, TodoService>();
        services.AddSingleton<ITodoToolHandler, TodoToolHandler>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ITemplateService, TemplateService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IResearchHistoryService, ResearchHistoryService>();
        services.AddTransient<IResearchExportService, ResearchExportService>();
        services.AddSingleton<IWindowTrackingService, WindowTrackingService>();
        services.AddSingleton<INativeHotkeyServiceFactory, NativeHotkeyServiceFactory>();
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
        services.AddSingleton<SyncMapper>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<ISyncClientService, SyncClientService>();

        // Background services
        services.AddSingleton<ReminderBackgroundService>();

        // Auto-update
        services.AddSingleton<IUpdateService, UpdateService>();

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
        services.AddScoped<MemoryViewModel>();
        services.AddScoped<RemindersViewModel>();
        services.AddScoped<TodoViewModel>();
        services.AddScoped<DeviceManagementViewModel>();
        services.AddScoped<E2EEOnboardingViewModel>();

        // First Run Wizard
        services.AddTransient<FirstRunWizardViewModel>();

        // Windows - Transient (created by WindowManagerService from scoped provider)
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.FirstRunWizardWindow>();
    }
}
