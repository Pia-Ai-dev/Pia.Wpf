using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
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
