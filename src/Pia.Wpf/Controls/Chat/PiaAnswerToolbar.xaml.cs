using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Models;

namespace Pia.Controls.Chat;

public partial class PiaAnswerToolbar : UserControl
{
    public static readonly DependencyProperty CopyCommandProperty =
        DependencyProperty.Register(nameof(CopyCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty SpeakCommandProperty =
        DependencyProperty.Register(nameof(SpeakCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty RegenerateCommandProperty =
        DependencyProperty.Register(nameof(RegenerateCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty RateCommandProperty =
        DependencyProperty.Register(nameof(RateCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty StatsProperty =
        DependencyProperty.Register(nameof(Stats), typeof(AnswerStats), typeof(PiaAnswerToolbar),
            new PropertyMetadata(null, OnStatsChanged));

    public static readonly DependencyProperty PersonaNameProperty =
        DependencyProperty.Register(nameof(PersonaName), typeof(string), typeof(PiaAnswerToolbar),
            new PropertyMetadata(null, OnPersonaNameChanged));

    private static readonly DependencyPropertyKey FooterSummaryKey =
        DependencyProperty.RegisterReadOnly(nameof(FooterSummary), typeof(string), typeof(PiaAnswerToolbar),
            new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty FooterSummaryProperty = FooterSummaryKey.DependencyProperty;

    public ICommand? CopyCommand
    {
        get => (ICommand?)GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    public ICommand? SpeakCommand
    {
        get => (ICommand?)GetValue(SpeakCommandProperty);
        set => SetValue(SpeakCommandProperty, value);
    }

    public ICommand? RegenerateCommand
    {
        get => (ICommand?)GetValue(RegenerateCommandProperty);
        set => SetValue(RegenerateCommandProperty, value);
    }

    public ICommand? RateCommand
    {
        get => (ICommand?)GetValue(RateCommandProperty);
        set => SetValue(RateCommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public AnswerStats? Stats
    {
        get => (AnswerStats?)GetValue(StatsProperty);
        set => SetValue(StatsProperty, value);
    }

    public string? PersonaName
    {
        get => (string?)GetValue(PersonaNameProperty);
        set => SetValue(PersonaNameProperty, value);
    }

    public string FooterSummary => (string)GetValue(FooterSummaryProperty);

    public PiaAnswerToolbar() => InitializeComponent();

    private static void OnStatsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PiaAnswerToolbar)d).RecomputeFooter();

    private static void OnPersonaNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PiaAnswerToolbar)d).RecomputeFooter();

    private void RecomputeFooter() =>
        SetValue(FooterSummaryKey, FooterSummaryFormatter.Compose(Stats, PersonaName));
}
