using System.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ReminderBackgroundService : BackgroundService
{
    private readonly IReminderService _reminderService;
    private readonly INotificationService _notificationService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ReminderBackgroundService> _logger;
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _cleanupAge = TimeSpan.FromDays(7);
    private bool _toastCallbackRegistered;

    public ReminderBackgroundService(
        IReminderService reminderService,
        INotificationService notificationService,
        ILocalizationService localizationService,
        ILogger<ReminderBackgroundService> logger)
    {
        _reminderService = reminderService;
        _notificationService = notificationService;
        _localizationService = localizationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderBackgroundService started");

        RegisterToastCallbacks();

        using var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAndFireRemindersAsync();
                await _reminderService.CleanupCompletedAsync(_cleanupAge);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking reminders");
            }
        }
    }

    private async Task CheckAndFireRemindersAsync()
    {
        var dueReminders = await _reminderService.GetDueRemindersAsync();

        foreach (var reminder in dueReminders)
        {
            _logger.LogInformation("Firing reminder {Id}: {Description}", reminder.Id, reminder.Description);

            try
            {
                ShowWindowsToast(reminder);
                ShowInAppToast(reminder);
                await _reminderService.DismissAsync(reminder.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fire reminder {Id}", reminder.Id);
            }
        }
    }

    private void ShowWindowsToast(Reminder reminder)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_Reminder"])
                .AddText(reminder.Description)
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_Dismiss"])
                    .AddArgument("action", "dismiss")
                    .AddArgument("reminderId", reminder.Id.ToString()))
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_Snooze"])
                    .AddArgument("action", "snooze")
                    .AddArgument("reminderId", reminder.Id.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show Windows toast for reminder {Id}", reminder.Id);
        }
    }

    private void ShowInAppToast(Reminder reminder)
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _notificationService.ShowToast(_localizationService.Format("Notification_ReminderInApp", reminder.Description));
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show in-app toast for reminder {Id}", reminder.Id);
        }
    }

    private void RegisterToastCallbacks()
    {
        if (_toastCallbackRegistered) return;

        try
        {
            ToastNotificationManagerCompat.OnActivated += async toastArgs =>
            {
                var args = ToastArguments.Parse(toastArgs.Argument);

                if (!args.TryGetValue("reminderId", out var reminderIdStr) ||
                    !Guid.TryParse(reminderIdStr, out var reminderId))
                    return;

                if (!args.TryGetValue("action", out var action))
                    return;

                try
                {
                    switch (action)
                    {
                        case "dismiss":
                            // DismissAsync was already called when the reminder fired,
                            // so clicking Dismiss is just acknowledging
                            _logger.LogInformation("User dismissed reminder {Id} via toast", reminderId);
                            break;

                        case "snooze":
                            await _reminderService.SnoozeAsync(reminderId, TimeSpan.FromMinutes(10));
                            _logger.LogInformation("User snoozed reminder {Id} for 10 minutes via toast", reminderId);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling toast action for reminder {Id}", reminderId);
                }
            };

            _toastCallbackRegistered = true;
            _logger.LogInformation("Toast notification callbacks registered");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register toast notification callbacks");
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
