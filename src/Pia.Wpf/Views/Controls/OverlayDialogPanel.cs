using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Wpf.Ui.Controls;

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

    /// <summary>The panel measures to its content, so a dialog that opens on an empty text box collapses to
    /// this. Raise it for one that should open at a usable size rather than grow into one as it is typed in —
    /// but keep it inside MainWindow's 600px MinWidth: the body scroller is vertical only, so a panel wider
    /// than the window is clipped with no way to reach the button row.</summary>
    public static readonly DependencyProperty MinPanelWidthProperty =
        DependencyProperty.Register(nameof(MinPanelWidth), typeof(double), typeof(OverlayDialogPanel),
            new PropertyMetadata(320.0));

    /// <summary>Unbounded by default, so a panel that already fits keeps growing as it did.</summary>
    public static readonly DependencyProperty MaxPanelHeightProperty =
        DependencyProperty.Register(nameof(MaxPanelHeight), typeof(double), typeof(OverlayDialogPanel),
            new PropertyMetadata(double.PositiveInfinity));

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

    public static readonly DependencyProperty IsSecondaryButtonEnabledProperty =
        DependencyProperty.Register(nameof(IsSecondaryButtonEnabled), typeof(bool), typeof(OverlayDialogPanel),
            new PropertyMetadata(true));

    public static readonly DependencyProperty PrimaryButtonIconProperty =
        DependencyProperty.Register(nameof(PrimaryButtonIcon), typeof(IconElement), typeof(OverlayDialogPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryButtonIconProperty =
        DependencyProperty.Register(nameof(SecondaryButtonIcon), typeof(IconElement), typeof(OverlayDialogPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CloseButtonIconProperty =
        DependencyProperty.Register(nameof(CloseButtonIcon), typeof(IconElement), typeof(OverlayDialogPanel),
            new PropertyMetadata(null));

    public event Action<object>? ResultChosen;

    public double MaxPanelWidth
    {
        get => (double)GetValue(MaxPanelWidthProperty);
        set => SetValue(MaxPanelWidthProperty, value);
    }

    public double MinPanelWidth
    {
        get => (double)GetValue(MinPanelWidthProperty);
        set => SetValue(MinPanelWidthProperty, value);
    }

    public double MaxPanelHeight
    {
        get => (double)GetValue(MaxPanelHeightProperty);
        set => SetValue(MaxPanelHeightProperty, value);
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

    public bool IsSecondaryButtonEnabled
    {
        get => (bool)GetValue(IsSecondaryButtonEnabledProperty);
        set => SetValue(IsSecondaryButtonEnabledProperty, value);
    }

    public IconElement? PrimaryButtonIcon
    {
        get => (IconElement?)GetValue(PrimaryButtonIconProperty);
        set => SetValue(PrimaryButtonIconProperty, value);
    }

    public IconElement? SecondaryButtonIcon
    {
        get => (IconElement?)GetValue(SecondaryButtonIconProperty);
        set => SetValue(SecondaryButtonIconProperty, value);
    }

    public IconElement? CloseButtonIcon
    {
        get => (IconElement?)GetValue(CloseButtonIconProperty);
        set => SetValue(CloseButtonIconProperty, value);
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

    protected virtual void RaiseResultChosen(object result)
    {
        ResultChosen?.Invoke(result);
    }
}
