using Wpf.Ui.Controls;

namespace Pia.Services.Flow;

/// <summary>
/// Implemented by the Flow snackbar service so <see cref="Pia.Helpers.SnackbarActionHelper"/> can publish
/// an action-carrying item without a static service locator (it already receives the snackbar service).
/// </summary>
public interface IFlowActionPublisher
{
    void PublishAction(string title, string message, string actionText, Action onAction, ControlAppearance appearance, TimeSpan timeout);
}
