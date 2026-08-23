using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pia.Controls.AssistantHistory;

public partial class PiaAssistantChatRowContent : UserControl
{
    /// <summary>
    /// Optional per-row delete command (hover trash bin that replaces the date). Left unset by hosts
    /// that don't offer delete — the trash then stays hidden. The command parameter is the row's own
    /// DataContext (the chat/chip item VM), so each host binds a command matching its item type.
    /// </summary>
    public static readonly DependencyProperty DeleteCommandProperty =
        DependencyProperty.Register(nameof(DeleteCommand), typeof(ICommand), typeof(PiaAssistantChatRowContent));

    public ICommand? DeleteCommand
    {
        get => (ICommand?)GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    /// <summary>
    /// Optional per-row open command, on the same hover strip as the delete one. A host that offers delete
    /// without this leaves a row that a script can destroy by id but not open by one.
    /// </summary>
    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(PiaAssistantChatRowContent));

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public PiaAssistantChatRowContent() => InitializeComponent();
}
