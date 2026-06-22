using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
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

    /// <summary>
    /// Shows a deliberately quiet, neutral snackbar (no Success/Caution/Danger/Info
    /// fill) with a small state glyph and an inline action link — for low-stakes,
    /// ambient notifications such as a background chat finishing while the window is
    /// focused. The neutral chrome is set as local values (resource references) so it
    /// overrides the WPF-UI appearance Style triggers and stays on the Pia surface
    /// tokens, flipping with light/dark.
    /// </summary>
    public static void ShowSubtleWithAction(
        SnackbarPresenter presenter,
        string title,
        string message,
        string actionText,
        Action onAction,
        SymbolRegular icon,
        Brush? iconBrush,
        TimeSpan timeout)
    {
        var snackbar = new Snackbar(presenter)
        {
            Title = title,
            // Secondary keeps the close button/chrome neutral; the brushes below
            // (local values) win over the appearance Style triggers.
            Appearance = ControlAppearance.Secondary,
            Timeout = timeout,
        };

        snackbar.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SurfaceBrush");
        snackbar.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "BorderBrush_");
        snackbar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextDefaultBrush");
        snackbar.SetResourceReference(Snackbar.ContentForegroundProperty, "TextMutedBrush");

        var iconControl = new SymbolIcon { Symbol = icon, FontSize = 24 };
        if (iconBrush is not null)
            iconControl.Foreground = iconBrush;
        snackbar.Icon = iconControl;

        snackbar.Content = BuildSubtleContent(message, actionText, () =>
        {
            // Dismiss instantly on click for immediate feedback (mirrors ShowWithAction).
            _ = presenter.HideCurrent();
            onAction();
        });

        snackbar.Show();
    }

    private static FrameworkElement BuildSubtleContent(string message, string actionText, Action onAction)
    {
        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        });

        // "Open" sits on its own line below the message (the chosen "quiet card" layout).
        var linkHost = new System.Windows.Controls.TextBlock { Margin = new Thickness(0, 3, 0, 0) };
        var hyperlink = new Hyperlink(new Run(actionText))
        {
            FontWeight = FontWeights.SemiBold,
        };
        // App accent makes the link read as the actionable affordance on the neutral card.
        hyperlink.SetResourceReference(Hyperlink.ForegroundProperty, "AccentFillColorDefaultBrush");
        hyperlink.Click += (_, _) =>
        {
            try
            {
                onAction();
            }
            catch
            {
                // Swallow user-action errors so the snackbar host can't crash (see ShowWithAction).
            }
        };
        linkHost.Inlines.Add(hyperlink);
        stack.Children.Add(linkHost);

        return stack;
    }
}
