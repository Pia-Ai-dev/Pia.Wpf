using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Models;
using Pia.ViewModels.Models;

namespace Pia.Controls.Chat;

public partial class PiaAnswerToolbar : UserControl
{
    public static readonly DependencyProperty CopyCommandProperty =
        DependencyProperty.Register(nameof(CopyCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty SpeakCommandProperty =
        DependencyProperty.Register(nameof(SpeakCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty RegenerateCommandProperty =
        DependencyProperty.Register(nameof(RegenerateCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty RegenerateStyledCommandProperty =
        DependencyProperty.Register(nameof(RegenerateStyledCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
    public static readonly DependencyProperty ExportCommandProperty =
        DependencyProperty.Register(nameof(ExportCommand), typeof(ICommand), typeof(PiaAnswerToolbar));
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

    public ICommand? RegenerateStyledCommand
    {
        get => (ICommand?)GetValue(RegenerateStyledCommandProperty);
        set => SetValue(RegenerateStyledCommandProperty, value);
    }

    public ICommand? ExportCommand
    {
        get => (ICommand?)GetValue(ExportCommandProperty);
        set => SetValue(ExportCommandProperty, value);
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

    /// <summary>Opens the regenerate-style menu anchored to the caret button.</summary>
    private void OnRegenerateMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    /// <summary>
    /// A regenerate-style menu item was chosen. A ContextMenu is a separate NameScope, so the menu
    /// items can't bind to the toolbar's command/parameter directly; resolve both here from the item's
    /// Tag (the style) plus the toolbar's CommandParameter (the message) and invoke the styled command.
    /// </summary>
    private void OnRegenerateStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RegenerateStyle style }) return;
        if (CommandParameter is not AssistantMessage message) return;

        var request = new RegenerateRequest(message, style);
        if (RegenerateStyledCommand?.CanExecute(request) == true)
            RegenerateStyledCommand.Execute(request);
    }

    private void OnRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction }) return;
        if (CommandParameter is not AssistantMessage message) return;

        var request = new AnswerRatingRequest(message, Positive: direction == "Up");
        if (RateCommand?.CanExecute(request) == true)
            RateCommand.Execute(request);
    }
}
