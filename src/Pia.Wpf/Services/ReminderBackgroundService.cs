using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Logging;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ReminderBackgroundService : BackgroundService
{
    private readonly IReminderService _reminderService;
    private readonly IFlowService _flowService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ReminderBackgroundService> _logger;
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _cleanupAge = TimeSpan.FromDays(7);
    private bool _toastCallbackRegistered;

    public ReminderBackgroundService(
        IReminderService reminderService,
        IFlowService flowService,
        ILocalizationService localizationService,
        ILogger<ReminderBackgroundService> logger)
    {
        _reminderService = reminderService;
        _flowService = flowService;
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
            _logger.LogInformation("Firing reminder {Id}", reminder.Id);
            _logger.SensitiveDebug("Firing reminder {Id} description: {Description}", reminder.Id, reminder.Description);

            try
            {
                ShowWindowsToast(reminder);
                PublishFlowItem(reminder);
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

    private void PublishFlowItem(Reminder reminder)
    {
        // Reminder text is shown (allowed) but never logged. The item persists in the rail until the
        // user snoozes or dismisses it; the underlying reminder is already DismissAsync'd by the fire loop.
        try
        {
            _flowService.Publish(new FlowItemDraft
            {
                Severity = FlowSeverity.ActionRequired,
                Source = FlowSource.Reminder,
                Title = reminder.Description,
                DedupKey = reminder.Id.ToString(),
                Lifetime = FlowLifetime.Persistent,
                // Reminder cards carry decisions (Snooze/Done) only; the nav Action stays null and the
                // decisions are re-derived on load from Source == Reminder + DedupKey (design §5).
                RequestDurable = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish Flow item for reminder {Id}", reminder.Id);
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
