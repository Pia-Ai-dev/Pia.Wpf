using Pia.Services.Flow;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Helpers;

/// <summary>
/// Routes a "show with inline action" request into Flow as an <c>ActionRequired</c> item carrying the
/// <c>onAction</c> callback (design §7). The WPF-UI action snackbar is retired; Flow renders the peek.
/// The registered <see cref="ISnackbarService"/> is Pia's Flow snackbar service, which implements
/// <see cref="IFlowActionPublisher"/> — so existing call sites pass the service unchanged.
/// </summary>
public static class SnackbarActionHelper
{
    public static void ShowWithAction(
        ISnackbarService snackbarService,
        string title,
        string message,
        string actionText,
        Action onAction,
        ControlAppearance appearance,
        TimeSpan timeout)
    {
        if (snackbarService is IFlowActionPublisher publisher)
            publisher.PublishAction(title, message, actionText, onAction, appearance, timeout);
    }
}
