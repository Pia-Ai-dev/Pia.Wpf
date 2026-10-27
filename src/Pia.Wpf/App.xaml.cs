using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pia.Behaviors;
using Pia.Helpers;
using Pia.Models;
using Pia.Paths;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.LocalDataReset;
using Velopack;

namespace Pia;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static int _restartRequested;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(v => AutostartService.EnableStatic())
            .OnAfterUpdateFastCallback(v => AutostartService.UpdatePathIfEnabled())
            .OnBeforeUninstallFastCallback(v =>
            {
                AutostartService.DisableStatic();
                // The uninstaller only removes the app directory, and the browser download lives
                // outside it — left behind it is an unpatched Chromium nobody will ever update.
                Services.MeetingAttendee.ChromiumProvisioner.DeleteDownloadCache();
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();

        if (Volatile.Read(ref _restartRequested) != 0)
            Relaunch();
    }

    /// <summary>Asks <see cref="Main"/> to spawn a replacement once the message loop has drained.</summary>
    internal static void RequestRestart() => Volatile.Write(ref _restartRequested, 1);

    /// <summary>The only place Pia re-launches itself. No args: only Velopack reads <see cref="Main"/>'s.</summary>
    private static void Relaunch()
    {
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });

                // Immediately, so two instances never overlap on one profile for long: settings.json is
                // written from an in-memory cache with no cross-process guard.
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to relaunch Pia: {ex.Message}");
            }
        }

        MessageBox.Show(
            Localization.LocalizationSource.Instance["Restart_ManualRelaunchRequired"],
            Localization.LocalizationSource.Instance["Restart_ManualRelaunchRequired_Title"],
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SetCurrentProcessExplicitAppUserModelID(string appID);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetCurrentProcessExplicitAppUserModelID(Helpers.ToastAppIdentity.Aumid);

        // Before the first toast: the toolkit derives the notification header from the process name.
        Helpers.ToastAppIdentity.Ensure();

        // A class handler, not a style: the resource lookup for an implicit ScrollBar style would have to
        // beat WPF-UI's, and ThemeService already swaps entries in Application.Resources.MergedDictionaries.
        ScrollBarAutoFadeBehavior.Install();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Nothing a virtual-file drop wrote outlives the run that read it.
        Helpers.ShellDropCache.Clear();

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


        var autostartService = Bootstrapper.ServiceProvider.GetRequiredService<IAutostartService>();
        if (settings.LaunchAtStartup && !autostartService.IsEnabled())
            autostartService.Enable();
        else if (!settings.LaunchAtStartup && autostartService.IsEnabled())
            autostartService.Disable();

        if (!settings.HasCompletedFirstRunWizard)
        {
            await ShowFirstRunWizardAsync();
            if (!ShouldContinueStartup(Volatile.Read(ref _restartRequested) != 0)) return;
            settings = await settingsService.GetSettingsAsync();
        }

        if (!settings.StartMinimized)
        {
            var windowManager = Bootstrapper.ServiceProvider.GetRequiredService<IWindowManagerService>();
            windowManager.ShowWindow(settings.DefaultWindowMode);
        }

        // Resolved eagerly so toasts left in Action Center from an earlier session still route when clicked.
        _ = Bootstrapper.ServiceProvider.GetRequiredService<IScheduledJobNotificationSurface>();

        // Attach the agent-run notification surface eagerly so it subscribes to RunChanged at startup.
        _ = Bootstrapper.ServiceProvider.GetRequiredService<IAgentRunNotificationSurface>();

        // Same reason: nothing else resolves it, and it must be subscribed before the first drain pass.
        _ = Bootstrapper.ServiceProvider.GetRequiredService<IAssignmentNotificationSurface>();

        // Settle runs a crash left non-terminal before the scheduler can start new ones; the workspace sweep
        // runs in the background so startup never waits on disk I/O.
        try
        {
            var agentRunService = Bootstrapper.ServiceProvider.GetRequiredService<IAgentRunService>();
            await agentRunService.FailInterruptedRunsAsync(CancellationToken.None);

            var headlessRunLauncher = Bootstrapper.ServiceProvider.GetRequiredService<IHeadlessRunLauncher>();
            _ = headlessRunLauncher.RunStartupSweepAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Headless run startup recovery failed: {ex.Message}");
        }

        // Books firings whose run settled after the process died: after the sweep above settles crashed runs,
        // before the scheduler writes job rows. Own try/catch, so a fault here skips nothing else.
        try
        {
            var firingReconciler = Bootstrapper.ServiceProvider.GetRequiredService<IScheduledFiringReconciler>();
            await firingReconciler.ReconcileAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Scheduled firing reconcile failed: {ex.Message}");
        }

        // Before the scheduler can recompute NextFireAt off today's date and relocate these jobs again.
        try
        {
            var jobService = Bootstrapper.ServiceProvider.GetRequiredService<IScheduledJobService>();
            await jobService.BackfillRecurrenceDaysAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Scheduled job recurrence backfill failed: {ex.Message}");
        }

        // Load persisted durable Flow items before the pollers run (the todo poller re-validates against them).
        var flowService = Bootstrapper.ServiceProvider.GetRequiredService<Services.Flow.IFlowService>();
        await flowService.LoadAsync();

        var reminderService = Bootstrapper.ServiceProvider.GetRequiredService<ReminderBackgroundService>();
        await reminderService.StartAsync(CancellationToken.None);

        var scheduledJobService = Bootstrapper.ServiceProvider.GetRequiredService<ScheduledJobBackgroundService>();
        await scheduledJobService.StartAsync(CancellationToken.None);

        // A clear that failed may have left the old account signed in; nothing account-bound may run until it lands.
        var resetPending = LocalDataResetMarker.IsPending;
        if (!resetPending)
        {
            var chatSyncService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatSyncService>();
            await chatSyncService.StartAsync(CancellationToken.None);

            var chatRetentionService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatRetentionService>();
            await chatRetentionService.StartAsync(CancellationToken.None);
        }

        var consentRetentionService = Bootstrapper.ServiceProvider
            .GetRequiredService<Services.Consent.ConsentRetentionBackgroundService>();
        await consentRetentionService.StartAsync(CancellationToken.None);

        var todoDeadlineService = Bootstrapper.ServiceProvider.GetRequiredService<Services.Flow.TodoDeadlineBackgroundService>();
        await todoDeadlineService.StartAsync(CancellationToken.None);

        // Its startup pass stores assignments that finished while the app was closed, before the server's
        // retention drops their plaintext.
        if (!resetPending)
        {
            var AssignmentDrainService = Bootstrapper.ServiceProvider
                .GetRequiredService<Services.Operators.AssignmentDrainService>();
            await AssignmentDrainService.StartAsync(CancellationToken.None);
        }

        var pluginService = Bootstrapper.ServiceProvider.GetRequiredService<IPluginService>();
        _ = pluginService.InitializePersistedPluginsAsync();

        var authService = Bootstrapper.ServiceProvider.GetRequiredService<IAuthService>();
        if (authService.IsLoggedIn && !resetPending)
        {
            var syncService = Bootstrapper.ServiceProvider.GetRequiredService<ISyncClientService>();
            RepairThenSyncAsync(syncService).SafeFireAndForget(
                Bootstrapper.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SyncRepair"));
        }

        _ = CheckForUpdateOnStartupAsync();

        // Randomized 4–6 hour interval.
        _ = StartPeriodicUpdateCheckAsync();

        _ = EnsureEmbeddingModelAsync();

        // Off the UI thread, since these probes spawn where.exe or read the registry; each caches its result so
        // the first file chip or vault header renders without the delay.
        _ = Task.Run(() =>
        {
            _ = GitLocator.IsAvailable;
            _ = VsCodeLauncher.IsAvailable;
            VsCodeLauncher.TryGetIcon();
            _ = ObsidianLauncher.IsAvailable;
            ObsidianLauncher.TryGetIcon();
        });
    }

    /// <summary>
    /// Heals rows an older build blanked before the E2EE pull guard existed, then starts sync.
    /// </summary>
    /// <remarks>
    /// Sequential on purpose: SyncNowAsync takes the sync lock non-blockingly, so a repair racing the
    /// first background cycle would silently lose it and leave the rows blank. The repair returns
    /// immediately on a healthy profile, so this does not delay sync in the normal case, and the timer
    /// starts even if it throws.
    /// </remarks>
    private static async Task RepairThenSyncAsync(ISyncClientService syncService)
    {
        try
        {
            await syncService.RepairBlankedSyncRowsAsync();
        }
        finally
        {
            syncService.StartBackgroundSync();
        }
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

        var result = wizard.ShowDialog();
        if (ShouldMarkWizardCompletedOnClose(result, LocalDataResetMarker.IsPending))
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

    internal static bool ShouldContinueStartup(bool restartRequested) => !restartRequested;

    internal static bool ShouldMarkWizardCompletedOnClose(bool? dialogResult, bool resetPending) =>
        dialogResult != true && !resetPending;

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
            var oldModelPath = System.IO.Path.Combine(
                PiaPaths.ModelsDirectory, "Embeddings", "all-MiniLM-L6-v2.onnx");
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

        var consentRetentionService = Bootstrapper.ServiceProvider
            .GetRequiredService<Services.Consent.ConsentRetentionBackgroundService>();
        await consentRetentionService.StopAsync(CancellationToken.None);

        var todoDeadlineService = Bootstrapper.ServiceProvider.GetRequiredService<Services.Flow.TodoDeadlineBackgroundService>();
        await todoDeadlineService.StopAsync(CancellationToken.None);

        // Before the chat sync worker stops, so an artifact this pass stores locally still gets pushed.
        var AssignmentDrainService = Bootstrapper.ServiceProvider
            .GetRequiredService<Services.Operators.AssignmentDrainService>();
        await AssignmentDrainService.StopAsync(CancellationToken.None);

        var chatSyncService = Bootstrapper.ServiceProvider.GetRequiredService<AssistantChatSyncService>();
        await chatSyncService.StopAsync(CancellationToken.None);

        // G-4: cancel + bounded-await in-flight headless runs so none is left Running at exit.
        var headlessRunLauncher = Bootstrapper.ServiceProvider.GetRequiredService<IHeadlessRunLauncher>();
        await headlessRunLauncher.StopAsync(CancellationToken.None);

        var windowManager = Bootstrapper.ServiceProvider.GetRequiredService<IWindowManagerService>();
        windowManager.CloseAndDisposeAll();
    }
}
