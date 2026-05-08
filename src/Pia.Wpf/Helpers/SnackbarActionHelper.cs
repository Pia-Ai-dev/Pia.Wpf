using System.Windows;
using System.Windows.Data;
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
        // ISnackbarService.GetSnackbarPresenter(), Snackbar.Content : object,
        // SnackbarPresenter.HideCurrent(), Snackbar.ContentForeground : Brush.
        var presenter = snackbarService.GetSnackbarPresenter();
        if (presenter is null)
            return;

        var snackbar = new Snackbar(presenter)
        {
            Title = title,
            Appearance = appearance,
            Timeout = timeout,
        };

        snackbar.Content = BuildContent(snackbar, message, actionText, () =>
        {
            // Dismiss the snackbar instantly when the user clicks the action so they
            // get immediate visual feedback that the click registered.
            _ = presenter.HideCurrent();
            onAction();
        });

        snackbar.Show();
    }

    private static FrameworkElement BuildContent(Snackbar snackbar, string message, string actionText, Action onAction)
    {
        var textBlock = new System.Windows.Controls.TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        };
        textBlock.Inlines.Add(new Run(message));
        textBlock.Inlines.Add(new Run("  "));

        var hyperlink = new Hyperlink(new Run(actionText))
        {
            FontWeight = FontWeights.SemiBold,
        };

        // Bind the link to the snackbar's appearance-aware ContentForeground so it
        // adapts to Caution/Danger/Info backgrounds instead of using the default
        // Hyperlink blue (which clashes with the orange Caution background).
        hyperlink.SetBinding(Hyperlink.ForegroundProperty, new Binding(nameof(Snackbar.ContentForeground))
        {
            Source = snackbar,
        });

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
