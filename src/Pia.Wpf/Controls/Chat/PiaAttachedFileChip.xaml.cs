using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pia.Controls.Chat;

/// <summary>
/// Chip for a file the user attached to their own message. Unlike <see cref="PiaFileChip"/> it holds no
/// absolute path: only a file saved into the assistant-files sandbox can be reopened, and one that was
/// not renders as an inert name pill rather than a button that cannot work.
/// </summary>
public partial class PiaAttachedFileChip : UserControl
{
    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(nameof(FileName), typeof(string), typeof(PiaAttachedFileChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SavedRelativePathProperty =
        DependencyProperty.Register(nameof(SavedRelativePath), typeof(string), typeof(PiaAttachedFileChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(PiaAttachedFileChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RevealCommandProperty =
        DependencyProperty.Register(nameof(RevealCommand), typeof(ICommand), typeof(PiaAttachedFileChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(PiaAttachedFileChip),
            new PropertyMetadata(null));

    public string? FileName
    {
        get => (string?)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    public string? SavedRelativePath
    {
        get => (string?)GetValue(SavedRelativePathProperty);
        set => SetValue(SavedRelativePathProperty, value);
    }

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ICommand? RevealCommand
    {
        get => (ICommand?)GetValue(RevealCommandProperty);
        set => SetValue(RevealCommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public PiaAttachedFileChip() => InitializeComponent();
}
