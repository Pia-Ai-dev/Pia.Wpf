using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Velopack;

namespace Pia;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(v => AutostartService.EnableStatic())
            .OnAfterUpdateFastCallback(v => AutostartService.UpdatePathIfEnabled())
            .OnBeforeUninstallFastCallback(v => AutostartService.DisableStatic())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SetCurrentProcessExplicitAppUserModelID(string appID);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetCurrentProcessExplicitAppUserModelID("Pia.App");

        // Set shutdown mode to explicit (don't exit when window closes)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Initialize DI container
        await Bootstrapper.InitializeAsync();

        // Initialize localization — auto-detect from Windows locale on first run
        var localizationService = Bootstrapper.ServiceProvider.GetRequiredService<ILocalizationService>();
        var earlySettings = await Bootstrapper.ServiceProvider.GetRequiredService<ISettingsService>().GetSettingsAsync();

        if (!earlySettings.HasCompletedFirstRunWizard && earlySettings.UiLanguage == TargetLanguage.EN)
        {
            var systemCulture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var detectedLanguage = systemCulture switch
            {
                "de" => TargetLanguage.DE,
                "fr" => TargetLanguage.FR,
                _ => TargetLanguage.EN
            };
            localizationService.SetLanguage(detectedLanguage);
        }
        else
        {
            localizationService.SetLanguage(earlySettings.UiLanguage);
        }

        // Set up global exception handling FIRST
        DispatcherUnhandledException += (sender, args) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        // Ensure built-in Pia Cloud provider exists, collapse any leftover sync
        // duplicates, and heal stale mode-default references (non-critical).
        try
        {
            var providerService = Bootstrapper.ServiceProvider.GetRequiredService<IProviderService>();
            var startupLogger = Bootstrapper.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ProviderStartup");

            await providerService.EnsureBuiltInProviderAsync();
            await providerService.ConsolidateLocalDuplicatesAsync();
            await providerService.RepairModeDefaultsAsync();

            var providers = await providerService.GetProvidersAsync();
            var startupSettings = await Bootstrapper.ServiceProvider
                .GetRequiredService<ISettingsService>().GetSettingsAsync();
            startupSettings.ModeProviderDefaults.TryGetValue(WindowMode.Optimize, out var optId);
            startupSettings.ModeProviderDefaults.TryGetValue(WindowMode.Assistant, out var asstId);
            var hasPiaCloud = providers.Any(p => p.Id == ProviderService.PiaCloudProviderId);

            startupLogger.LogInformation(
                "Provider startup decision: providers={Count} (PiaCloud={HasPiaCloud}), modeDefaults Optimize={OptId} Assistant={AsstId}, useSame={UseSame}",
                providers.Count, hasPiaCloud, optId, asstId, startupSettings.UseSameProviderForAllModes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to ensure built-in provider: {ex.Message}");
            // App can still function; user can add providers manually
        }

        var trayIconService = Bootstrapper.ServiceProvider.GetRequiredService<ITrayIconService>();
        trayIconService.Initialize();

        var settingsService = Bootstrapper.ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = await settingsService.GetSettingsAsync();

        // Initialize assistant files folder default on first run (or after upgrade).
        // Once the user clears the path the empty value sticks — we only seed it when it's missing.
        if (string.IsNullOrWhiteSpace(settings.AssistantFilesFolder))
        {
            // Shared with SensitivePathGuard's carve-out — both must use the same value or the
            // guard re-blocks the whole workdir (see AssistantWorkspace).
            var defaultFolder = Pia.Infrastructure.AssistantWorkspace.DefaultWorkdir;
            try
            {
                System.IO.Directory.CreateDirectory(defaultFolder);
                settings.AssistantFilesFolder = defaultFolder;
                await settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create default assistant files folder: {ex.Message}");
            }
        }

        // Sync autostart registry state with setting (covers existing installs upgrading to this version)
        var autostartService = Bootstrapper.ServiceProvider.GetRequiredService<IAutostartService>();
        if (settings.LaunchAtStartup && !autostartService.IsEnabled())
            autostartService.Enable();
        else if (!settings.LaunchAtStartup && autostartService.IsEnabled())
            autostartService.Disable();

        if (!settings.HasCompletedFirstRunWizard)
        {
            await ShowFirstRunWizardAsync();
            settings = await settingsService.GetSettingsAsync();
        }

        if (!settings.StartMinimized)
        {
            var windowManager = Bootstrapper.ServiceProvider.GetRequiredService<IWindowManagerService>();
            windowManager.ShowWindow(settings.DefaultWindowMode);
        }

        // Force the scheduled-job notification surface to attach its toast activation
        // handler immediately, so toasts left in Action Center across app sessions still
        // route correctly when clicked.
        _ = Bootstrapper.ServiceProvider.GetRequiredService<IScheduledJobNotificationSurface>();

        // Load persisted durable Flow items before the pollers run (the todo poller re-validates against them).
        var flowService = Bootstrapper.ServiceProvider.GetRequiredService<Services.Flow.IFlowService>();
        await flowService.LoadAsync();

        // Start background services
        var reminderService = Bootstrapper.ServiceProvider.GetRequiredService<ReminderBackgroundService>();
        await reminderService.StartAsync(CancellationToken.None);

        var scheduledJobService = Bootstrapper.ServiceProvider.GetRequiredService<ScheduledJobBackgroundService>();
        await scheduledJobService.StartAsync(CancellationToken.None);

        var chatSyncService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatSyncService>();
        await chatSyncService.StartAsync(CancellationToken.None);

        var chatRetentionService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatRetentionService>();
        await chatRetentionService.StartAsync(CancellationToken.None);

        var todoDeadlineService = Bootstrapper.ServiceProvider.GetRequiredService<Services.Flow.TodoDeadlineBackgroundService>();
        await todoDeadlineService.StartAsync(CancellationToken.None);

        // Initialize persisted MCP plugins from local database
        var pluginService = Bootstrapper.ServiceProvider.GetRequiredService<IPluginService>();
        _ = pluginService.InitializePersistedPluginsAsync();

        // Start background sync if user is logged in
        var authService = Bootstrapper.ServiceProvider.GetRequiredService<IAuthService>();
        if (authService.IsLoggedIn)
        {
            var syncService = Bootstrapper.ServiceProvider.GetRequiredService<ISyncClientService>();
            syncService.StartBackgroundSync();
        }

        // Silently check for updates in the background
        _ = CheckForUpdateOnStartupAsync();

        // Periodically re-check for updates (randomized 4–6 hour interval)
        _ = StartPeriodicUpdateCheckAsync();

        // Pre-download embedding model in background
        _ = EnsureEmbeddingModelAsync();
    }

    private async Task StartPeriodicUpdateCheckAsync()
    {
        var updateService = Bootstrapper.ServiceProvider.GetRequiredService<IUpdateService>();

        while (!updateService.IsUpdateReady)
        {
            var delayMinutes = RandomNumberGenerator.GetInt32(240, 361); // 4–6 hours
            System.Diagnostics.Debug.WriteLine($"Next update check in {delayMinutes} minutes");
            await Task.Delay(TimeSpan.FromMinutes(delayMinutes));
            await CheckForUpdateOnStartupAsync();
        }
    }

    private async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            var settingsService = Bootstrapper.ServiceProvider.GetRequiredService<ISettingsService>();
            var settings = await settingsService.GetSettingsAsync();
            if (!settings.AutoUpdateEnabled)
                return;

            var updateService = Bootstrapper.ServiceProvider.GetRequiredService<IUpdateService>();
            await updateService.CheckAndDownloadUpdateAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    private async Task ShowFirstRunWizardAsync()
    {
        using var scope = Bootstrapper.ServiceProvider.CreateScope();
        var wizard = scope.ServiceProvider.GetRequiredService<Views.FirstRunWizardWindow>();

        // ShowDialog blocks until the wizard is closed
        // If user closes without completing (X button), treat as skip
        var result = wizard.ShowDialog();
        if (result != true)
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var settings = await settingsService.GetSettingsAsync();
            if (!settings.HasCompletedFirstRunWizard)
            {
                settings.HasCompletedFirstRunWizard = true;
                await settingsService.SaveSettingsAsync(settings);
            }
        }
    }

    private async Task EnsureEmbeddingModelAsync()
    {
        try
        {
            var embeddingService = Bootstrapper.ServiceProvider.GetRequiredService<IEmbeddingService>();
            if (embeddingService.IsModelAvailable)
            {
                CleanupOldEmbeddingModel();
                return;
            }

            await embeddingService.DownloadModelAsync();
            CleanupOldEmbeddingModel();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Embedding model download failed: {ex.Message}");
        }
    }

    private static void CleanupOldEmbeddingModel()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldModelPath = System.IO.Path.Combine(localAppData, "Pia", "Models", "Embeddings", "all-MiniLM-L6-v2.onnx");
            if (System.IO.File.Exists(oldModelPath))
                System.IO.File.Delete(oldModelPath);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);

        // Stop background sync
        var syncService = Bootstrapper.ServiceProvider.GetRequiredService<ISyncClientService>();
        syncService.StopBackgroundSync();

        var reminderService = Bootstrapper.ServiceProvider.GetRequiredService<ReminderBackgroundService>();
        await reminderService.StopAsync(CancellationToken.None);

        var scheduledJobService = Bootstrapper.ServiceProvider.GetRequiredService<ScheduledJobBackgroundService>();
        await scheduledJobService.StopAsync(CancellationToken.None);

        var chatRetentionService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatRetentionService>();
        await chatRetentionService.StopAsync(CancellationToken.None);

        var todoDeadlineService = Bootstrapper.ServiceProvider.GetRequiredService<Services.Flow.TodoDeadlineBackgroundService>();
        await todoDeadlineService.StopAsync(CancellationToken.None);

        var chatSyncService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatSyncService>();
        await chatSyncService.StopAsync(CancellationToken.None);

        var windowManager = Bootstrapper.ServiceProvider.GetRequiredService<IWindowManagerService>();
        windowManager.CloseAndDisposeAll();
    }
}
