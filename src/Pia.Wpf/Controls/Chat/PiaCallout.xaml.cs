using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Pia.Controls.Chat;

public enum CalloutKind { Info, Tip, Warn, Success }

public partial class PiaCallout : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(CalloutKind), typeof(PiaCallout),
            new PropertyMetadata(CalloutKind.Info, OnKindChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PiaCallout),
            new PropertyMetadata(null));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(string), typeof(PiaCallout),
            new PropertyMetadata(null));

    private static readonly DependencyPropertyKey AccentBrushKey =
        DependencyProperty.RegisterReadOnly(nameof(AccentBrush), typeof(Brush), typeof(PiaCallout),
            new PropertyMetadata(null));
    public static readonly DependencyProperty AccentBrushProperty = AccentBrushKey.DependencyProperty;

    private static readonly DependencyPropertyKey SoftBrushKey =
        DependencyProperty.RegisterReadOnly(nameof(SoftBrush), typeof(Brush), typeof(PiaCallout),
            new PropertyMetadata(null));
    public static readonly DependencyProperty SoftBrushProperty = SoftBrushKey.DependencyProperty;

    private static readonly DependencyPropertyKey IconSymbolKey =
        DependencyProperty.RegisterReadOnly(nameof(IconSymbol), typeof(SymbolRegular), typeof(PiaCallout),
            new PropertyMetadata(SymbolRegular.Info24));
    public static readonly DependencyProperty IconSymbolProperty = IconSymbolKey.DependencyProperty;

    public CalloutKind Kind
    {
        get => (CalloutKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Body
    {
        get => (string?)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public Brush? AccentBrush => (Brush?)GetValue(AccentBrushProperty);
    public Brush? SoftBrush => (Brush?)GetValue(SoftBrushProperty);
    public SymbolRegular IconSymbol => (SymbolRegular)GetValue(IconSymbolProperty);

    public PiaCallout()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyKind();
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PiaCallout)d).ApplyKind();

    private void ApplyKind()
    {
        (string accentKey, string softKey, SymbolRegular icon) = Kind switch
        {
            CalloutKind.Tip     => ("PiaAccentBrush", "PiaAccentSoftBrush", SymbolRegular.Lightbulb24),
            CalloutKind.Warn    => ("WarnBrush",      "WarnSoftBrush",      SymbolRegular.Warning24),
            CalloutKind.Success => ("PiaSuccessBrush", "SuccessSoftBrush",  SymbolRegular.Checkmark24),
            _                   => ("PiaAccentBrush", "PiaAccentSoftBrush", SymbolRegular.Info24),
        };

        SetValue(AccentBrushKey, TryFindResource(accentKey) as Brush);
        SetValue(SoftBrushKey,   TryFindResource(softKey)   as Brush);
        SetValue(IconSymbolKey,  icon);
    }
}
