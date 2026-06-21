using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Singleton notification surface for background (non-active) assistant chats.
/// Shows a Windows toast (with a clickable "open chat" button) and an in-app
/// toast when a backgrounded chat reaches WaitingForTool / Completed / Error.
/// Clicking the toast activates that chat inside the single assistant window via
/// <see cref="IWindowManagerService.ShowAssistantChat"/> — never a second window,
/// never <c>BringMainWindowForward</c> (which could foreground a different mode).
/// Modeled on <see cref="ScheduledJobNotificationSurface"/>.
/// </summary>
public sealed class BackgroundChatNotificationSurface : IBackgroundChatNotifier
{
    private const string ActionKey = "action";
    private const string OpenChatAction = "openChat";
    private const string ChatIdKey = "chatId";

    private readonly INotificationService _notificationService;
    private readonly IWindowManagerService _windowManager;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<BackgroundChatNotificationSurface> _logger;
    private bool _toastCallbackRegistered;

    public BackgroundChatNotificationSurface(
        INotificationService notificationService,
        IWindowManagerService windowManager,
        ILocalizationService localizationService,
        ILogger<BackgroundChatNotificationSurface> logger)
    {
        _notificationService = notificationService;
        _windowManager = windowManager;
        _localizationService = localizationService;
        _logger = logger;

        // Register eagerly so a toast sitting in Action Center across sessions still
        // fires its callback (same rationale as the scheduled-job surface).
        EnsureToastActivationRegistered();
    }

    public void NotifyStateChange(Guid chatId, string displayTitle, ChatState state)
    {
        if (!TryResolveBodyKey(state, out var bodyKey))
            return; // Running / Idle never notify.

        EnsureToastActivationRegistered();

        // id + enum only — never the title (CLAUDE.md). The title is shown, not logged.
        _logger.LogInformation("Background chat {ChatId} notify state {State}", chatId, state);

        var body = _localizationService.Format(bodyKey, displayTitle);

        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_BackgroundChat_Title"])
                .AddText(body)
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_OpenChat"])
                    .AddArgument(ActionKey, OpenChatAction)
                    .AddArgument(ChatIdKey, chatId.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show background-chat toast for {ChatId}", chatId);
        }

        try
        {
            Application.Current?.Dispatcher.Invoke(() => _notificationService.ShowToast(body));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show in-app background-chat toast for {ChatId}", chatId);
        }
    }

    private static bool TryResolveBodyKey(ChatState state, out string bodyKey)
    {
        bodyKey = state switch
        {
            ChatState.WaitingForTool => "Notification_BackgroundChat_WaitingForTool",
            ChatState.Completed => "Notification_BackgroundChat_Completed",
            ChatState.Error => "Notification_BackgroundChat_Error",
            _ => string.Empty,
        };
        return bodyKey.Length > 0;
    }

    private void EnsureToastActivationRegistered()
    {
        if (_toastCallbackRegistered) return;
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _toastCallbackRegistered = true;
            _logger.LogInformation("Background-chat toast activation handler registered");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register background-chat toast activation handler");
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            if (!args.TryGetValue(ActionKey, out var action) || action != OpenChatAction)
                return;

            args.TryGetValue(ChatIdKey, out var chatIdStr);
            _logger.LogInformation("Background-chat toast activated chatId={ChatId}", chatIdStr);

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
                    _logger.LogWarning(inner, "Failed to route background-chat toast activation to UI");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling background-chat toast activation");
        }
    }
}
