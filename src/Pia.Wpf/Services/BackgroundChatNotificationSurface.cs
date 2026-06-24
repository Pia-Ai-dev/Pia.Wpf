using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Pia.Converters;
using Pia.Helpers;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Services;

/// <summary>
/// Singleton notification surface for background (non-active) assistant chats.
/// When a backgrounded chat reaches WaitingForTool / Completed / Error it surfaces
/// the update one of two ways: a quiet in-app snackbar when the assistant window is
/// the foreground window (the user is already looking at Pia), otherwise a Windows
/// toast (with a clickable "open chat" button) so the update still reaches them while
/// they work elsewhere. Either way, opening activates that chat inside the single
/// assistant window via <see cref="IWindowManagerService.ShowAssistantChat"/> — never
/// a second window, never <c>BringMainWindowForward</c> (which could foreground a
/// different mode). Modeled on <see cref="ScheduledJobNotificationSurface"/>.
/// </summary>
public sealed class BackgroundChatNotificationSurface : IBackgroundChatNotifier
{
    private const string ActionKey = "action";
    private const string OpenChatActionArg = "openChat";
    private const string ChatIdKey = "chatId";

    // Reuse the canonical chat-state visual language (glyph + accent) so the snackbar
    // matches the inline chat-state badge instead of inventing a second mapping.
    private static readonly ChatStateToGlyphConverter GlyphConverter = new();
    private static readonly ChatStateToBrushConverter FgBrushConverter =
        new() { Kind = ChatStateToBrushConverter.ChatStateBrushKind.Foreground };

    private readonly IFlowService _flowService;
    private readonly IWindowManagerService _windowManager;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<BackgroundChatNotificationSurface> _logger;
    private bool _toastCallbackRegistered;

    public BackgroundChatNotificationSurface(
        IFlowService flowService,
        IWindowManagerService windowManager,
        ILocalizationService localizationService,
        ILogger<BackgroundChatNotificationSurface> logger)
    {
        _flowService = flowService;
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

        // Publish to Flow first (the canonical in-app surface that replaces the retired Border toast)
        // so a throw in the Windows-toast path can never lose the item.
        PublishFlowItem(chatId, displayTitle, state);

        var body = _localizationService.Format(bodyKey, displayTitle);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ShowToastNotification(chatId, body);
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            // When the assistant window is the foreground window the user is already
            // looking at Pia, so a quiet in-app snackbar is enough — and avoids pushing
            // a duplicate into Action Center. Otherwise fall back to the OS toast so the
            // update still reaches them while they're working elsewhere.
            if (_windowManager.IsInForeground(WindowMode.Assistant)
                && TryFindForegroundSnackbarPresenter() is { } presenter)
            {
                try
                {
                    var icon = (SymbolRegular)GlyphConverter.Convert(
                        state, typeof(SymbolRegular), null!, CultureInfo.InvariantCulture);
                    var iconBrush = FgBrushConverter.Convert(
                        state, typeof(Brush), null!, CultureInfo.InvariantCulture) as Brush;

                    SnackbarActionHelper.ShowSubtleWithAction(
                        presenter,
                        _localizationService["Notification_BackgroundChat_Title"],
                        body,
                        _localizationService["Notification_OpenChat"],
                        () => _windowManager.ShowAssistantChat(chatId),
                        icon,
                        iconBrush,
                        ResolveTimeout(state));
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to show in-app background-chat snackbar for {ChatId}; falling back to toast",
                        chatId);
                }
            }

            ShowToastNotification(chatId, body);
        });
    }

    private void ShowToastNotification(Guid chatId, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(_localizationService["Notification_BackgroundChat_Title"])
                .AddText(body)
                .AddButton(new ToastButton()
                    .SetContent(_localizationService["Notification_OpenChat"])
                    .AddArgument(ActionKey, OpenChatActionArg)
                    .AddArgument(ChatIdKey, chatId.ToString()))
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show background-chat toast for {ChatId}", chatId);
        }
    }

    private static TimeSpan ResolveTimeout(ChatState state) => state switch
    {
        // Action-needed states linger longer than a plain completion.
        ChatState.WaitingForTool => TimeSpan.FromSeconds(12),
        ChatState.Error => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(6),
    };

    /// <summary>
    /// Finds the <c>RootSnackbarPresenter</c> on the currently active window. Each
    /// window owns its own presenter (scoped <c>ISnackbarService</c>); the
    /// <see cref="IWindowManagerService.IsInForeground"/> gate at the call site means
    /// the active window here is the assistant window. Mirrors <c>FindDialogHost</c>
    /// in <see cref="ScheduledJobNotificationSurface"/>.
    /// </summary>
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

    private void PublishFlowItem(Guid chatId, string displayTitle, ChatState state)
    {
        var flowBodyKey = state switch
        {
            ChatState.WaitingForTool => "Flow_BgChat_WaitingForTool",
            ChatState.Completed => "Flow_BgChat_Completed",
            ChatState.Error => "Flow_BgChat_Error",
            _ => string.Empty,
        };

        _flowService.Publish(new FlowItemDraft
        {
            Severity = FlowSeverityMapper.FromChatState(state),
            Source = FlowSource.BackgroundChat,
            Title = displayTitle,
            Body = _localizationService[flowBodyKey],
            DedupKey = chatId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenChatAction(chatId, _localizationService["Flow_Action_OpenChat"]),
            RequestDurable = true,
        });
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
            if (!args.TryGetValue(ActionKey, out var action) || action != OpenChatActionArg)
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
