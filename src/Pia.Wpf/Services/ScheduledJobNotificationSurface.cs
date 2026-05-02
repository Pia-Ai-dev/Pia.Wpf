using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Views.Dialogs;
using Wpf.Ui.Controls;

namespace Pia.Services;

/// <summary>
/// Concrete UI hub for scheduled-job notifications. Shows Windows toasts on
/// success/failure (mirroring <see cref="ReminderBackgroundService"/>'s pattern)
/// and a <see cref="MissedScheduledJobDialog"/> when the background service
/// asks the user about a missed run.
/// </summary>
/// <remarks>
/// Registered as a singleton, so it cannot directly depend on the scoped
/// <c>IContentDialogService</c>. Instead, it walks the active <see cref="MainWindow"/>
/// to find the <c>RootContentDialogPresenter</c> at dialog-show time. Toast
/// activation routing brings the main window forward and opens the Research
/// view in the active window mode (a richer "navigate to specific entry" hub
/// is out of scope for v1; the window surfaces the user's history alongside).
/// </remarks>
public sealed class ScheduledJobNotificationSurface : IScheduledJobNotificationSurface
{
    private readonly INotificationService _notificationService;
    private readonly ILocalizationService _localizationService;
    private readonly IWindowManagerService _windowManager;
    private readonly ILogger<ScheduledJobNotificationSurface> _logger;
    private bool _toastCallbackRegistered;

    public ScheduledJobNotificationSurface(
        INotificationService notificationService,
        ILocalizationService localizationService,
        IWindowManagerService windowManager,
        ILogger<ScheduledJobNotificationSurface> logger)
    {
        _notificationService = notificationService;
        _localizationService = localizationService;
        _windowManager = windowManager;
        _logger = logger;

        // Register the toast activation callback eagerly. A toast that has been sitting
        // in Windows Action Center across app sessions only fires its callback if a
        // handler is wired up at app start — registering lazily on the first NotifySuccess
        // /NotifyFailure call would miss those clicks.
        EnsureToastActivationRegistered();
    }

    public void NotifySuccess(ScheduledJob job, ResearchHistoryEntry entry)
    {
        EnsureToastActivationRegistered();

        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_ScheduledResearch"])
                .AddText(_localizationService.Format("Notification_ScheduledResearch_Body", job.Name))
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_OpenBriefing"])
                    .AddArgument("action", "openBriefing")
                    .AddArgument("entryId", entry.Id.ToString())
                    .AddArgument("jobId", job.Id.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show success toast for job {Id}", job.Id);
        }

        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _notificationService.ShowToast(
                    _localizationService.Format("Notification_ScheduledResearchInApp", job.Name));
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show in-app toast for job {Id}", job.Id);
        }
    }

    public void NotifyFailure(ScheduledJob job, Guid resultEntryId, string reason)
    {
        EnsureToastActivationRegistered();

        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_ScheduledResearchFailed"])
                .AddText(_localizationService.Format("Notification_ScheduledResearchFailed_Body", job.Name))
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_OpenBriefing"])
                    .AddArgument("action", "openBriefing")
                    .AddArgument("entryId", resultEntryId.ToString())
                    .AddArgument("jobId", job.Id.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show failure toast for job {Id}", job.Id);
        }
    }

    public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
    {
        var tcs = new TaskCompletionSource<bool?>();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            tcs.TrySetResult(null);
            return tcs.Task;
        }

        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var dialogHost = FindDialogHost();
                if (dialogHost is null)
                {
                    _logger.LogWarning(
                        "No ContentDialogHost available for missed-run dialog (job {Id}); skipping.",
                        job.Id);
                    tcs.TrySetResult(null);
                    return;
                }

                var body = _localizationService.Format(
                    "MissedRun_Dialog_Body",
                    job.Name,
                    scheduledFireAt.ToString("g"));
                var dialog = new MissedScheduledJobDialog(dialogHost, body);
                var result = await dialog.ShowAsync();

                tcs.TrySetResult(result switch
                {
                    ContentDialogResult.Primary => true,
                    ContentDialogResult.Secondary => false,
                    ContentDialogResult.None => false,
                    _ => null
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show missed-run dialog for job {Id}", job.Id);
                tcs.TrySetResult(null);
            }
        });

        return tcs.Task;
    }

    /// <summary>
    /// Finds the <see cref="ContentDialogHost"/> on the currently active
    /// <see cref="MainWindow"/>. Falls back to scanning <see cref="Application.Windows"/>
    /// if no main window is set.
    /// </summary>
    private static ContentDialogHost? FindDialogHost()
    {
        if (Application.Current is null) return null;

        if (Application.Current.MainWindow is { } mw &&
            mw.FindName("RootContentDialogPresenter") is ContentDialogHost host)
        {
            return host;
        }

        foreach (Window w in Application.Current.Windows)
        {
            if (w.FindName("RootContentDialogPresenter") is ContentDialogHost found)
                return found;
        }

        return null;
    }

    private void EnsureToastActivationRegistered()
    {
        if (_toastCallbackRegistered) return;

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _toastCallbackRegistered = true;
            _logger.LogInformation("Scheduled-job toast activation handler registered");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register scheduled-job toast activation handler");
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);

            if (!args.TryGetValue("action", out var action) || action != "openBriefing")
                return;

            args.TryGetValue("entryId", out var entryIdStr);
            args.TryGetValue("jobId", out var jobIdStr);

            _logger.LogInformation(
                "Scheduled-job toast activated entryId={EntryId} jobId={JobId}",
                entryIdStr,
                jobIdStr);

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // v1: bring a Research window forward. A richer "navigate to specific
                    // entry" hub is out of scope; the user lands in Research where they
                    // can find the entry in history.
                    _windowManager.ShowWindow(WindowMode.Research);
                    BringMainWindowForward();
                }
                catch (Exception inner)
                {
                    _logger.LogWarning(inner, "Failed to route scheduled-job toast activation to UI");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling scheduled-job toast activation");
        }
    }

    private static void BringMainWindowForward()
    {
        if (Application.Current?.MainWindow is null) return;
        if (Application.Current.MainWindow.WindowState == WindowState.Minimized)
            Application.Current.MainWindow.WindowState = WindowState.Normal;
        Application.Current.MainWindow.Activate();
    }
}
