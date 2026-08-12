using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Helpers;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Wpf.Ui.Controls;

namespace Pia.Services;

/// <summary>Announces a finished background assignment: a Flow item always, plus a snackbar when the
/// assistant window is in front and a Windows toast when it is not.</summary>
public sealed class AssignmentNotificationSurface : IAssignmentNotificationSurface
{
    private const string ActionKey = "action";
    private const string OpenChatActionArg = "openAssignmentChat";
    private const string ChatIdKey = "chatId";

    private readonly IFlowService _flowService;
    private readonly IWindowManagerService _windowManager;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<AssignmentNotificationSurface> _logger;
    private bool _toastCallbackRegistered;

    /// <param name="orchestrator">The concrete type, so this shares the instance the drain worker raises
    /// <see cref="AssignmentRunOrchestrator.Completed"/> on.</param>
    public AssignmentNotificationSurface(
        AssignmentRunOrchestrator orchestrator,
        IFlowService flowService,
        IWindowManagerService windowManager,
        ILocalizationService localizationService,
        ILogger<AssignmentNotificationSurface> logger)
    {
        _flowService = flowService;
        _windowManager = windowManager;
        _localizationService = localizationService;
        _logger = logger;

        orchestrator.Completed += OnCompleted;

        // Eager, so a toast left in Action Center across sessions still routes when it is finally clicked.
        EnsureToastActivationRegistered();
    }

    private void OnCompleted(object? sender, AssignmentCompleted completed) => Handle(completed);

    internal void Handle(AssignmentCompleted completed)
    {
        _logger.LogInformation(
            "Assignment {AssignmentId} came back into chat {ChatId} (succeeded {Succeeded}).",
            completed.AssignmentId, completed.ChatId, completed.Succeeded);

        // Flow first, so a throw on the way to the OS toast cannot lose the item.
        PublishFlowItem(completed);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return; // No UI to raise anything on; Flow already carries it.

        dispatcher.InvokeAsync(() =>
        {
            if (_windowManager.IsInForeground(WindowMode.Assistant)
                && TryFindForegroundSnackbarPresenter() is { } presenter)
            {
                try
                {
                    SnackbarActionHelper.ShowSubtleWithAction(
                        presenter,
                        _localizationService["Notification_Assignment_Title"],
                        NotificationBody(completed.Succeeded),
                        _localizationService["Notification_OpenChat"],
                        () => _windowManager.ShowAssistantChat(completed.ChatId),
                        SymbolRegular.Rocket24,
                        null,
                        TimeSpan.FromSeconds(completed.Succeeded ? 8 : 12));
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to show the in-app snackbar for assignment {AssignmentId}; falling back to a toast",
                        completed.AssignmentId);
                }
            }

            ShowToastNotification(completed);
        });
    }

    private void PublishFlowItem(AssignmentCompleted completed)
    {
        _flowService.Publish(new FlowItemDraft
        {
            Severity = completed.Succeeded ? FlowSeverity.Success : FlowSeverity.Error,
            Source = FlowSource.Assignment,
            Title = _localizationService["Flow_Assignment_Title"],
            Body = completed.Succeeded
                ? _localizationService["Flow_Assignment_Completed"]
                : _localizationService["Flow_Assignment_Unfinished"],
            DedupKey = completed.AssignmentId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenChatAction(completed.ChatId, _localizationService["Flow_Action_OpenChat"]),
            RequestDurable = true,
        });
    }

    // Cancelled runs arrive here as not-succeeded too, so the wording must fit a stop the user asked for.
    private string NotificationBody(bool succeeded) => succeeded
        ? _localizationService["Notification_Assignment_Completed"]
        : _localizationService["Notification_Assignment_Unfinished"];

    private void ShowToastNotification(AssignmentCompleted completed)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_Assignment_Title"])
                .AddText(NotificationBody(completed.Succeeded))
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_OpenChat"])
                    .AddArgument(ActionKey, OpenChatActionArg)
                    .AddArgument(ChatIdKey, completed.ChatId.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show the toast for assignment {AssignmentId}", completed.AssignmentId);
        }
    }

    private static SnackbarPresenter? TryFindForegroundSnackbarPresenter()
    {
        if (Application.Current is null) return null;

        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsActive && w.FindName("RootSnackbarPresenter") is SnackbarPresenter presenter)
                return presenter;
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register the assignment toast activation handler");
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue(ActionKey, out var action) || action != OpenChatActionArg)
                return;

            args.TryGetValue(ChatIdKey, out var chatIdStr);
            _logger.LogInformation("Assignment toast activated chatId={ChatId}", chatIdStr);

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
                    _logger.LogWarning(inner, "Failed to route the assignment toast activation to the UI");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling an assignment toast activation");
        }
    }
}
