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

    private static readonly DependencyPropertyKey StatsSummaryKey =
        DependencyProperty.RegisterReadOnly(nameof(StatsSummary), typeof(string), typeof(PiaAnswerToolbar),
            new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty StatsSummaryProperty = StatsSummaryKey.DependencyProperty;

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

    public string StatsSummary => (string)GetValue(StatsSummaryProperty);

    public PiaAnswerToolbar() => InitializeComponent();

    private static void OnStatsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (PiaAnswerToolbar)d;
        var stats = e.NewValue as AnswerStats;
        bar.SetValue(StatsSummaryKey, stats?.Summary ?? string.Empty);
    }
}
