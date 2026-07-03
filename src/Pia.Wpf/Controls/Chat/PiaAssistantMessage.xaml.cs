using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.Models;

namespace Pia.Controls.Chat;

public partial class PiaAssistantMessage : UserControl
{
    public static readonly DependencyProperty CopyCommandProperty =
        DependencyProperty.Register(nameof(CopyCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty SpeakCommandProperty =
        DependencyProperty.Register(nameof(SpeakCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty RegenerateCommandProperty =
        DependencyProperty.Register(nameof(RegenerateCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty RegenerateStyledCommandProperty =
        DependencyProperty.Register(nameof(RegenerateStyledCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty ExportCommandProperty =
        DependencyProperty.Register(nameof(ExportCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty RateCommandProperty =
        DependencyProperty.Register(nameof(RateCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty SuggestionCommandProperty =
        DependencyProperty.Register(nameof(SuggestionCommand), typeof(ICommand), typeof(PiaAssistantMessage));
    public static readonly DependencyProperty ManageToolPermissionsCommandProperty =
        DependencyProperty.Register(nameof(ManageToolPermissionsCommand), typeof(ICommand), typeof(PiaAssistantMessage));

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

    public ICommand? SuggestionCommand
    {
        get => (ICommand?)GetValue(SuggestionCommandProperty);
        set => SetValue(SuggestionCommandProperty, value);
    }

    public ICommand? ManageToolPermissionsCommand
    {
        get => (ICommand?)GetValue(ManageToolPermissionsCommandProperty);
        set => SetValue(ManageToolPermissionsCommandProperty, value);
    }

    public event EventHandler<PiiKeywordRequest>? AddToPiiRequested;

    public PiaAssistantMessage() => InitializeComponent();

    private void Markdown_AddToPiiRequested(object? sender, PiiKeywordRequest e)
        => AddToPiiRequested?.Invoke(this, e);
}
