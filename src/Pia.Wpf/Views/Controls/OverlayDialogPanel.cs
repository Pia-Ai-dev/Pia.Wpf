using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pia.Views.Controls;

public enum OverlayDialogResult
{
    Primary,
    Secondary,
    Close
}

[TemplatePart(Name = "PART_PrimaryButton", Type = typeof(Wpf.Ui.Controls.Button))]
[TemplatePart(Name = "PART_SecondaryButton", Type = typeof(Wpf.Ui.Controls.Button))]
[TemplatePart(Name = "PART_CloseButton", Type = typeof(Wpf.Ui.Controls.Button))]
public class OverlayDialogPanel : ContentControl
{
    public static readonly DependencyProperty MaxPanelWidthProperty =
        DependencyProperty.Register(nameof(MaxPanelWidth), typeof(double), typeof(OverlayDialogPanel),
            new PropertyMetadata(480.0));

    public static readonly DependencyProperty PrimaryButtonTextProperty =
        DependencyProperty.Register(nameof(PrimaryButtonText), typeof(string), typeof(OverlayDialogPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryButtonTextProperty =
        DependencyProperty.Register(nameof(SecondaryButtonText), typeof(string), typeof(OverlayDialogPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CloseButtonTextProperty =
        DependencyProperty.Register(nameof(CloseButtonText), typeof(string), typeof(OverlayDialogPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsPrimaryButtonEnabledProperty =
        DependencyProperty.Register(nameof(IsPrimaryButtonEnabled), typeof(bool), typeof(OverlayDialogPanel),
            new PropertyMetadata(true));

    public event Action<object>? ResultChosen;

    public double MaxPanelWidth
    {
        get => (double)GetValue(MaxPanelWidthProperty);
        set => SetValue(MaxPanelWidthProperty, value);
    }

    public string? PrimaryButtonText
    {
        get => (string?)GetValue(PrimaryButtonTextProperty);
        set => SetValue(PrimaryButtonTextProperty, value);
    }

    public string? SecondaryButtonText
    {
        get => (string?)GetValue(SecondaryButtonTextProperty);
        set => SetValue(SecondaryButtonTextProperty, value);
    }

    public string? CloseButtonText
    {
        get => (string?)GetValue(CloseButtonTextProperty);
        set => SetValue(CloseButtonTextProperty, value);
    }

    public bool IsPrimaryButtonEnabled
    {
        get => (bool)GetValue(IsPrimaryButtonEnabledProperty);
        set => SetValue(IsPrimaryButtonEnabledProperty, value);
    }

    static OverlayDialogPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(OverlayDialogPanel),
            new FrameworkPropertyMetadata(typeof(OverlayDialogPanel)));
        FocusableProperty.OverrideMetadata(typeof(OverlayDialogPanel),
            new FrameworkPropertyMetadata(true));
        KeyboardNavigation.TabNavigationProperty.OverrideMetadata(typeof(OverlayDialogPanel),
            new FrameworkPropertyMetadata(KeyboardNavigationMode.Cycle));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_PrimaryButton") is Wpf.Ui.Controls.Button primaryBtn)
            primaryBtn.Click += (_, _) => RaiseResultChosen(OverlayDialogResult.Primary);

        if (GetTemplateChild("PART_SecondaryButton") is Wpf.Ui.Controls.Button secondaryBtn)
            secondaryBtn.Click += (_, _) => RaiseResultChosen(OverlayDialogResult.Secondary);

        if (GetTemplateChild("PART_CloseButton") is Wpf.Ui.Controls.Button closeBtn)
            closeBtn.Click += (_, _) => RaiseResultChosen(OverlayDialogResult.Close);
    }

    public virtual void OnEscapePressed()
    {
        RaiseResultChosen(OverlayDialogResult.Close);
    }

    protected void RaiseResultChosen(object result)
    {
        ResultChosen?.Invoke(result);
    }
}
