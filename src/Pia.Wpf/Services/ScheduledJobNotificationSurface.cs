using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
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
    private readonly IFlowService _flowService;
    private readonly ILocalizationService _localizationService;
    private readonly IWindowManagerService _windowManager;
    private readonly ILogger<ScheduledJobNotificationSurface> _logger;
    private bool _toastCallbackRegistered;

    public ScheduledJobNotificationSurface(
        IFlowService flowService,
        ILocalizationService localizationService,
        IWindowManagerService windowManager,
        ILogger<ScheduledJobNotificationSurface> logger)
    {
        _flowService = flowService;
        _localizationService = localizationService;
        _windowManager = windowManager;
        _logger = logger;

        // Register the toast activation callback eagerly. A toast that has been sitting
        // in Windows Action Center across app sessions only fires its callback if a
        // handler is wired up at app start — registering lazily on the first NotifySuccess
        // /NotifyFailure call would miss those clicks.
        EnsureToastActivationRegistered();
    }

    public void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle)
    {
        // T2-18 QUIET MODE, and THIS is the chokepoint on purpose: both producers of a success notification
        // (ScheduledJobBackgroundService's agent and research legs) come through here, so the flag is honoured
        // once instead of at each call site. It suppresses the PUSH, not the record — the run's chat is written
        // either way and the job row still carries LastFiredAt/LastResultEntryId, so a quiet monitor is
        // findable, just not announced.
        //
        // NOTIFYFAILURE DOES NOT CHECK THIS. A monitor that breaks silently is worse than one that is noisy:
        // "do not tell me when it worked" is not "hide it when it stops working".
        if (job.QuietOnSuccess)
        {
            _logger.LogInformation("Scheduled job {Id} succeeded quietly (notifications suppressed)", job.Id);
            return;
        }

        EnsureToastActivationRegistered();

        // Publish to Flow first (the canonical in-app surface, replacing the retired Border toast).
        _flowService.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Success,
            Source = FlowSource.ScheduledJob,
            Title = job.Name,
            Body = _localizationService["Flow_Job_Success"],
            DedupKey = job.Id.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenChatAction(chatId, _localizationService["Flow_Action_OpenChat"]),
            RequestDurable = true,
        });

        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_ScheduledResearch"])
                .AddText(_localizationService.Format("Notification_ScheduledResearch_Body", job.Name))
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_OpenChat"])
                    .AddArgument("action", "openChat")
                    .AddArgument("chatId", chatId.ToString())
                    .AddArgument("jobId", job.Id.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show success toast for job {Id}", job.Id);
        }
    }

    public void NotifyFailure(ScheduledJob job, string reason)
    {
        EnsureToastActivationRegistered();

        // Publish to Flow first. The failure reason can carry content, so it is never logged or stored;
        // a generic localized body is used. No chat was produced, so there is no deep-link action.
        _flowService.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Error,
            Source = FlowSource.ScheduledJob,
            Title = job.Name,
            Body = _localizationService["Flow_Job_Failure"],
            DedupKey = job.Id.ToString(),
            Lifetime = FlowLifetime.Persistent,
            RequestDurable = true,
        });

        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_ScheduledResearchFailed"])
                .AddText(_localizationService.Format("Notification_ScheduledResearchFailed_Body", job.Name))
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

                // None is what Wpf.Ui returns for the CLOSE button AND for Escape, so Skip has to sit on the
                // SECONDARY button: while it shared None, dismissing the dialog silently advanced the schedule
                // and logged a skip the user never chose. Null means "left unanswered", which the caller
                // already handles by leaving the occurrence due and re-offering it at next launch.
                tcs.TrySetResult(result switch
                {
                    ContentDialogResult.Primary => true,
                    ContentDialogResult.Secondary => false,
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

    // (BringMainWindowForward removed: success now opens the assistant chat via
    // IWindowManagerService.ShowAssistantChat, which foregrounds the assistant window itself.)

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

            if (!args.TryGetValue("action", out var action) || action != "openChat")
                return;

            args.TryGetValue("chatId", out var chatIdStr);
            args.TryGetValue("jobId", out var jobIdStr);

            _logger.LogInformation(
                "Scheduled-job toast activated chatId={ChatId} jobId={JobId}",
                chatIdStr,
                jobIdStr);

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (Guid.TryParse(chatIdStr, out var chatId) && chatId != Guid.Empty)
                        _windowManager.ShowAssistantChat(chatId);
                    else
                        _windowManager.ShowWindow(WindowMode.Assistant);
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

}
