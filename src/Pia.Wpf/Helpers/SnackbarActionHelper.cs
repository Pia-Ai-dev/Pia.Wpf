using System.Windows;
using System.Windows.Documents;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Helpers;

/// <summary>
/// Constructs a Snackbar with an inline hyperlink action.
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
        // WPF-UI 4.2.0 verified signatures:
        // Snackbar.Snackbar(SnackbarPresenter), Snackbar.Show(),
        // ISnackbarService.GetSnackbarPresenter(), Snackbar.Content : object.
        var presenter = snackbarService.GetSnackbarPresenter();
        if (presenter is null)
            return;

        var content = BuildContent(message, actionText, onAction);

        var snackbar = new Snackbar(presenter)
        {
            Title = title,
            Content = content,
            Appearance = appearance,
            Timeout = timeout,
        };

        snackbar.Show();
    }

    private static FrameworkElement BuildContent(string message, string actionText, Action onAction)
    {
        var textBlock = new System.Windows.Controls.TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        textBlock.Inlines.Add(new Run(message));
        textBlock.Inlines.Add(new Run("  "));

        var hyperlink = new Hyperlink(new Run(actionText));
        hyperlink.Click += (_, _) =>
        {
            try
            {
                onAction();
            }
            catch
            {
                // Action errors are surfaced by the caller's logging path; swallow here so
                // an exception in user-supplied code does not crash the snackbar host.
            }
        };
        textBlock.Inlines.Add(hyperlink);

        return textBlock;
    }
}
